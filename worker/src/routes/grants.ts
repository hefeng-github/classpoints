/**
 * 设备白名单。
 *
 * 解决的问题很具体：班级码和口令是"给班里人的"，一旦发给班委就收不回来。
 * 谁拿到它，在他自己电脑上装一份客户端，就能读走全班名单、每一笔流水、
 * 甚至把成绩改掉。口令强度顶不住这种"知情者"。
 *
 * 所以多加一道按机器算的门：口令对了之后，还要看这台机器的特征码在不在
 * 班主任批准过的名单里。特征码由客户端本地生成（MachineGuid + 机器名的
 * SHA-256），重装系统才会变，学生改不了。
 *
 * 它不是强认证 —— 伪造一个特征码比抄一个班级码贵不了多少。真正的价值是
 * **把"谁进得来"变成一件需要人点头的事**，并且留下记录：哪台机器、什么时候、
 * 试了几次。默认仍然是 open（老班不受影响），班主任在网页上按一下才切换。
 */
import type { Env } from '../types';
import { HttpError, json, now, optionalString, readJson, uuidv7 } from '../util';

/** 客户端固定 20 位小写十六进制；放宽到 16–64 是为了以后换算法不用改协议。 */
const FINGERPRINT = /^[0-9a-f]{16,64}$/;

/** 每个班最多挂多少条待审 —— 防止有人拿这个端点往库里灌行。 */
const MAX_PENDING = 200;

/** 去掉连字符和空格、转小写。格式不对返回 null（不抛，调用方自己决定怎么处理）。 */
export function normalizeFingerprint(raw: unknown): string | null {
  if (typeof raw !== 'string') return null;
  const v = raw.replace(/[\s-]/g, '').toLowerCase();
  return FINGERPRINT.test(v) ? v : null;
}

/** 展示成 4 位一段，老师照着念、照着抄都不会串行。 */
export function displayFingerprint(fp: string): string {
  return (fp.match(/.{1,4}/g) ?? [fp]).join('-').toUpperCase();
}

export type GateResult =
  | { kind: 'ok' }
  | { kind: 'pending'; label: string | null }
  | { kind: 'blocked' }
  | { kind: 'queue_full' };

/**
 * 配对时的闸门。只在 join_mode = 'approval' 的班上调用。
 *
 * 三种结果对应三件不同的事：
 *   ok        名单里有它，且是 approved —— 放行，顺手记一次"最近出现"
 *   pending   第一次来，或者上次的点还没批 —— 落一条待审，**不签令牌**
 *   blocked   被明确拒绝过 —— 连待审都不落，避免被拒的机器反复刷列表
 */
export async function gateDevice(
  env: Env,
  classId: string,
  fingerprint: string,
  label: string | null,
  role: string,
  memberId: string | null,
): Promise<GateResult> {
  const at = now();
  const existing = await env.DB.prepare(
    'SELECT id, status, label FROM device_grants WHERE class_id = ? AND fingerprint = ?',
  )
    .bind(classId, fingerprint)
    .first<{ id: string; status: string; label: string | null }>();

  if (existing?.status === 'approved') {
    await env.DB.prepare(
      `UPDATE device_grants SET attempts = attempts + 1, last_seen_at = ?, label = coalesce(?, label),
              role = ?, member_id = ? WHERE id = ?`,
    )
      .bind(at, label, role, memberId, existing.id)
      .run();
    return { kind: 'ok' };
  }

  if (existing?.status === 'blocked') return { kind: 'blocked' };

  if (existing) {
    // 已经在待审队列里：刷新次数与设备名就够了，不新增行
    await env.DB.prepare(
      `UPDATE device_grants SET attempts = attempts + 1, last_seen_at = ?, label = coalesce(?, label),
              role = ?, member_id = ? WHERE id = ?`,
    )
      .bind(at, label, role, memberId, existing.id)
      .run();
    return { kind: 'pending', label: existing.label ?? label };
  }

  const pending = await env.DB.prepare(
    "SELECT count(*) AS n FROM device_grants WHERE class_id = ? AND status = 'pending'",
  )
    .bind(classId)
    .first<{ n: number }>();
  if ((pending?.n ?? 0) >= MAX_PENDING) return { kind: 'queue_full' };

  await env.DB.prepare(
    `INSERT INTO device_grants (id, class_id, fingerprint, label, status, role, member_id, attempts, created_at, last_seen_at)
     VALUES (?, ?, ?, ?, 'pending', ?, ?, 1, ?, ?)`,
  )
    .bind(uuidv7(), classId, fingerprint, label, role, memberId, at, at)
    .run();

  return { kind: 'pending', label };
}

// ---------------- 管理端 ----------------

export interface GrantView {
  id: string;
  fingerprint: string;
  display: string;
  label: string | null;
  status: 'pending' | 'approved' | 'blocked';
  role: string | null;
  memberId: string | null;
  note: string | null;
  attempts: number;
  createdAt: number;
  lastSeenAt: number | null;
  decidedAt: number | null;
}

interface GrantRow {
  id: string;
  fingerprint: string;
  label: string | null;
  status: 'pending' | 'approved' | 'blocked';
  role: string | null;
  member_id: string | null;
  note: string | null;
  attempts: number;
  created_at: number;
  last_seen_at: number | null;
  decided_at: number | null;
}

const toView = (r: GrantRow): GrantView => ({
  id: r.id,
  fingerprint: r.fingerprint,
  display: displayFingerprint(r.fingerprint),
  label: r.label,
  status: r.status,
  role: r.role,
  memberId: r.member_id,
  note: r.note,
  attempts: r.attempts,
  createdAt: r.created_at,
  lastSeenAt: r.last_seen_at,
  decidedAt: r.decided_at,
});

export async function listGrants(env: Env, classId: string): Promise<Response> {
  const cls = await env.DB.prepare('SELECT name, code, join_mode FROM classes WHERE id = ? AND deleted_at IS NULL')
    .bind(classId)
    .first<{ name: string; code: string; join_mode: string }>();
  if (!cls) throw new HttpError(404, 'class_not_found', '这个班不存在或已注销');

  const rows = await env.DB.prepare(
    `SELECT id, fingerprint, label, status, role, member_id, note, attempts, created_at, last_seen_at, decided_at
       FROM device_grants WHERE class_id = ?
      ORDER BY CASE status WHEN 'pending' THEN 0 WHEN 'approved' THEN 1 ELSE 2 END, created_at DESC
      LIMIT 500`,
  )
    .bind(classId)
    .all<GrantRow>();

  const devices = await env.DB.prepare(
    `SELECT count(*) AS n,
            sum(CASE WHEN fingerprint IS NULL THEN 1 ELSE 0 END) AS legacy
       FROM devices WHERE class_id = ? AND revoked_at IS NULL`,
  )
    .bind(classId)
    .first<{ n: number; legacy: number | null }>();

  return json({
    classId,
    className: cls.name,
    code: cls.code,
    joinMode: cls.join_mode === 'approval' ? 'approval' : 'open',
    activeDevices: devices?.n ?? 0,
    // 升级前配对的老设备没有特征码，白名单管不到它们 —— 网页上要能说清这件事，
    // 否则老师会问"我这里明明有 4 台在用，白名单怎么是空的"。
    legacyDevices: devices?.legacy ?? 0,
    grants: (rows.results ?? []).map(toView),
  });
}

/** 撤销某台机器上所有还没失效的 refresh —— 网页点「撤销」之后，它下次续期就断。 */
async function revokeDevices(env: Env, classId: string, fingerprint: string): Promise<void> {
  await env.DB.prepare('UPDATE devices SET revoked_at = ? WHERE class_id = ? AND fingerprint = ? AND revoked_at IS NULL')
    .bind(now(), classId, fingerprint)
    .run();
}

export async function decideGrant(
  env: Env,
  classId: string,
  id: string,
  action: 'approve' | 'reject' | 'revoke',
  by: string,
): Promise<Response> {
  const row = await env.DB.prepare('SELECT id, fingerprint, status FROM device_grants WHERE id = ? AND class_id = ?')
    .bind(id, classId)
    .first<{ id: string; fingerprint: string; status: string }>();
  if (!row) throw new HttpError(404, 'grant_not_found', '这条记录不存在');

  const at = now();
  const status = action === 'approve' ? 'approved' : 'blocked';
  await env.DB.prepare('UPDATE device_grants SET status = ?, decided_at = ?, decided_by = ? WHERE id = ? AND class_id = ?')
    .bind(status, at, by, row.id, classId)
    .run();

  // 通过时不动作；拒绝和撤销都要立刻掐断已经发出去的令牌
  if (status === 'blocked') await revokeDevices(env, classId, row.fingerprint);

  return json({ ok: true, id: row.id, status });
}

/** 手动加一台：老师提前把自己办公室的机器记进白名单，那台机器第一次就能进。 */
export async function addGrant(env: Env, request: Request, classId: string, by: string): Promise<Response> {
  const body = await readJson<{ fingerprint?: string; label?: string; note?: string }>(request);
  const fp = normalizeFingerprint(body.fingerprint);
  if (!fp) throw new HttpError(422, 'bad_fingerprint', '特征码是 20 位十六进制（界面上是 4 位一段），请照抄客户端显示的');

  const at = now();
  const id = uuidv7();
  await env.DB.prepare(
    `INSERT INTO device_grants (id, class_id, fingerprint, label, status, note, attempts, created_at, decided_at, decided_by)
     VALUES (?, ?, ?, ?, 'approved', ?, 0, ?, ?, ?)
     ON CONFLICT(class_id, fingerprint) DO UPDATE SET
       status = 'approved', label = coalesce(excluded.label, label), note = coalesce(excluded.note, note),
       decided_at = excluded.decided_at, decided_by = excluded.decided_by`,
  )
    .bind(id, classId, fp, optionalString(body.label, 60), optionalString(body.note, 200), at, at, by)
    .run();

  const saved = await env.DB.prepare('SELECT id FROM device_grants WHERE class_id = ? AND fingerprint = ?')
    .bind(classId, fp)
    .first<{ id: string }>();
  return json({ ok: true, id: saved?.id ?? id, fingerprint: fp });
}

/** 彻底删掉一条：下次这台机器再来，会重新进待审（而不是直接被拒）。 */
export async function removeGrant(env: Env, classId: string, id: string): Promise<Response> {
  const row = await env.DB.prepare('SELECT fingerprint FROM device_grants WHERE id = ? AND class_id = ?')
    .bind(id, classId)
    .first<{ fingerprint: string }>();
  if (!row) throw new HttpError(404, 'grant_not_found', '这条记录不存在');

  await env.DB.prepare('DELETE FROM device_grants WHERE id = ? AND class_id = ?').bind(id, classId).run();
  await revokeDevices(env, classId, row.fingerprint);
  return json({ ok: true });
}

export async function setJoinMode(env: Env, request: Request, classId: string): Promise<Response> {
  const body = await readJson<{ joinMode?: string }>(request);
  const mode = body.joinMode === 'approval' ? 'approval' : body.joinMode === 'open' ? 'open' : null;
  if (!mode) throw new HttpError(422, 'bad_join_mode', 'joinMode 只能是 open 或 approval');

  await env.DB.prepare('UPDATE classes SET join_mode = ? WHERE id = ? AND deleted_at IS NULL').bind(mode, classId).run();

  // 开审核的一刻，把"已经在这台班上用着的设备"先登记成通过。
  // 这一步是必须的：这些机器本来就是班主任亲手配进来的，开个开关不该让全班掉线。
  // 漏了这一步，老师点完开关，教室里几台机器会陆续续期失败，看起来像服务器坏了。
  if (mode === 'approval') {
    const rows = await env.DB.prepare(
      `SELECT fingerprint, max(last_seen_at) AS seen, max(label) AS label
         FROM devices
        WHERE class_id = ? AND fingerprint IS NOT NULL AND revoked_at IS NULL
        GROUP BY fingerprint
        LIMIT 90`,
    )
      .bind(classId)
      .all<{ fingerprint: string; seen: number | null; label: string | null }>();

    const at = now();
    const batch = (rows.results ?? []).map((r) =>
      env.DB.prepare(
        `INSERT INTO device_grants (id, class_id, fingerprint, label, status, attempts, created_at, last_seen_at, decided_at, decided_by)
         VALUES (?, ?, ?, ?, 'approved', 0, ?, ?, ?, 'existing-device')
         ON CONFLICT(class_id, fingerprint) DO NOTHING`,
      ).bind(uuidv7(), classId, r.fingerprint, r.label, at, r.seen ?? at, at),
    );
    if (batch.length) await env.DB.batch(batch);
  }

  return json({ ok: true, joinMode: mode });
}
