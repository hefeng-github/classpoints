/**
 * 班级与配对。
 * 向导第 1 步只调用一次 /classes —— 这个端点最容易在接口表里被漏掉，
 * 因为整个系统只用到它一次。
 */
import type { Claims, Env, Role } from '../types';
import {
  accessTtlSec,
  asRole,
  hashPassword,
  hashPinPhc,
  refreshTtlSec,
  requireAuth,
  sha256Hex,
  signToken,
  verifyMemberPin,
  verifyPassword,
} from '../auth';
import {
  HttpError,
  changeStmt,
  classCode,
  json,
  now,
  optionalString,
  randomHex,
  readJson,
  requireString,
  safeEqual,
  uuidv7,
} from '../util';
import { assertNotBlocked, clear, fingerprint, record } from '../guard';

interface CreateClassBody {
  name?: string;
  ownerName?: string;
  ownerPin?: string;
  recoveryCode?: string;
  kind?: 'board' | 'desk';
  deviceLabel?: string;
}

interface PairBody {
  code?: string;
  secret?: string;
  kind?: 'board' | 'desk';
  deviceLabel?: string;
}

async function issueDevice(
  env: Env,
  classId: string,
  kind: 'board' | 'desk',
  role: Role,
  memberId: string | null,
  label: string | null,
): Promise<{ deviceId: string; access: string; refresh: string; accessExpiresAt: number; refreshExpiresAt: number }> {
  const deviceId = uuidv7();
  const refresh = randomHex(32);
  const at = now();
  const refreshExpiresAt = at + refreshTtlSec(env) * 1000;

  await env.DB.batch([
    // role 和 member_id 要落库：refresh 时得原样换回去，否则班委的令牌刷一次就变回班主任
    env.DB.prepare(
      `INSERT INTO devices (id, class_id, kind, label, refresh_hash, created_at, last_seen_at, expires_at, role, member_id)
       VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`,
    ).bind(deviceId, classId, kind, label, await sha256Hex(refresh), at, at, refreshExpiresAt, role, memberId),
  ]);

  const access = await signToken(env, { sid: deviceId, cid: classId, kind, role, mid: memberId }, accessTtlSec(env));
  return { deviceId, access, refresh, accessExpiresAt: at + accessTtlSec(env) * 1000, refreshExpiresAt };
}

export async function createClass(env: Env, request: Request): Promise<Response> {
  // 建班是整个服务端唯一"不需要任何凭据就能写"的接口，所以它必须是最窄的那道门：
  // 先限流（连错都算不上，纯粹是次数），再验部署口令，最后看班级数够不够放。
  const fp = await fingerprint(request, env);
  await assertNotBlocked(env, 'create', fp);
  await record(env, 'create', fp);

  if (env.BOOTSTRAP_SECRET) {
    const got = request.headers.get('x-bootstrap-secret') ?? '';
    if (!safeEqual(got, env.BOOTSTRAP_SECRET)) {
      throw new HttpError(403, 'bootstrap_required', '这台服务器需要建班口令，请向部署者索取');
    }
  }

  const maxClasses = Number(env.MAX_CLASSES || '20');
  const existing = await env.DB.prepare('SELECT count(*) AS n FROM classes WHERE deleted_at IS NULL').first<{
    n: number;
  }>();
  if ((existing?.n ?? 0) >= maxClasses) {
    throw new HttpError(409, 'too_many_classes', `这个部署最多放 ${maxClasses} 个班，已经满了`);
  }

  const body = await readJson<CreateClassBody>(request);
  const name = requireString(body.name, 'name', 60);
  const ownerName = optionalString(body.ownerName, 30) ?? '班主任';
  const pin = requireString(body.ownerPin, 'ownerPin', 64);
  if (pin.length < 6) throw new HttpError(422, 'weak_pin', '班主任密码至少 6 位');

  const classId = uuidv7();
  const ownerId = uuidv7();
  const code = classCode(8);
  const at = now();

  const ownerPin = await hashPassword(env, pin);
  // 成员行里存的是**客户端能解**的格式：这一列会被同步下去，直接变成班主任
  // 在那台机器上校验管理密码的依据。用班级密码那套 hex 的话，一同步就废了。
  const memberPin = await hashPinPhc(pin);
  const recovery = body.recoveryCode ? await hashPassword(env, body.recoveryCode) : null;

  await env.DB.batch([
    env.DB.prepare(
      `INSERT INTO classes (id, name, code, owner_pin, owner_salt, recovery_hash, recovery_salt, created_at)
       VALUES (?, ?, ?, ?, ?, ?, ?, ?)`,
    ).bind(classId, name, code, ownerPin.hash, ownerPin.salt, recovery?.hash ?? null, recovery?.salt ?? null, at),
    env.DB.prepare(
      `INSERT INTO members (id, class_id, display_name, role, pin_hash, created_at, rev, updated_at)
       VALUES (?, ?, ?, 'owner', ?, ?, 1, ?)`,
    ).bind(ownerId, classId, ownerName, memberPin, at, at),
    changeStmt(env.DB, classId, 'members', ownerId, 'upsert', at),
  ]);

  const tokens = await issueDevice(env, classId, body.kind ?? 'board', 'owner', ownerId, body.deviceLabel ?? null);
  // role 和 /pair 的响应保持同一个形状：调用方不该为了读一个字段而区分两个端点
  return json({ classId, code, ownerId, role: 'owner', memberId: ownerId, ...tokens }, 201);
}

interface PairIdentity {
  role: Role;
  memberId: string | null;
}

interface ClassRow {
  id: string;
  owner_pin: string;
  owner_salt: string;
  recovery_hash: string | null;
  recovery_salt: string | null;
}

/**
 * 口令 → 身份。**输谁的密码，就是谁**。
 *
 * 顺序有讲究：先试班主任密码（一次 PBKDF2），再试恢复码，最后才扫成员口令。
 * 前两条是"班级级"凭据，一验就命中；成员扫描是唯一可能很贵的操作（每人一次
 * PBKDF2，轮数由客户端决定），所以放最后，并且用 PAIR_MEMBER_SCAN 封顶 ——
 * 一个故意塞几百个成员的班，不能把配对接口变成 CPU 炸弹。
 *
 * 排序也在省事：owner 排最前，班主任那条成员记录通常第一轮就命中。
 */
async function resolveIdentity(env: Env, cls: ClassRow, secret: string): Promise<PairIdentity | null> {
  if (await verifyPassword(env, secret, cls.owner_pin, cls.owner_salt)) {
    // 用班主任密码配的设备不绑具体成员：换班主任是换密码，不是换成员行
    return { role: 'owner', memberId: null };
  }
  if (cls.recovery_hash && cls.recovery_salt) {
    if (await verifyPassword(env, secret, cls.recovery_hash, cls.recovery_salt)) {
      return { role: 'owner', memberId: null };
    }
  }

  const cap = Math.max(1, Math.min(500, Number(env.PAIR_MEMBER_SCAN || '120')));
  const members = await env.DB.prepare(
    `SELECT id, role, pin_hash FROM members
      WHERE class_id = ? AND deleted_at IS NULL AND revoked_at IS NULL AND pin_hash IS NOT NULL
      ORDER BY CASE role WHEN 'owner' THEN 0 WHEN 'co_owner' THEN 1 ELSE 2 END, created_at
      LIMIT ?`,
  )
    .bind(cls.id, cap)
    .all<{ id: string; role: string; pin_hash: string }>();

  for (const m of members.results ?? []) {
    if (await verifyMemberPin(secret, m.pin_hash)) {
      return { role: asRole(m.role), memberId: m.id };
    }
  }
  return null;
}

/** 班级码 + 密码（或恢复码、或成员口令）→ 一对 token。 */
export async function pair(env: Env, request: Request): Promise<Response> {
  const fp = await fingerprint(request, env);
  await assertNotBlocked(env, 'pair', fp);

  const body = await readJson<PairBody>(request);
  const code = requireString(body.code, 'code', 16).trim().toUpperCase();
  const secret = requireString(body.secret, 'secret', 64);

  const row = await env.DB.prepare(
    'SELECT id, owner_pin, owner_salt, recovery_hash, recovery_salt FROM classes WHERE code = ? AND deleted_at IS NULL',
  )
    .bind(code)
    .first<ClassRow>();

  if (!row) {
    await record(env, 'pair', fp);
    throw new HttpError(404, 'class_not_found', '班级码不对，或者这个班已经注销');
  }

  const identity = await resolveIdentity(env, row, secret);
  // 统一的失败话术：不告诉对方"密码对但恢复码错"这种信息
  if (!identity) {
    await record(env, 'pair', fp);
    throw new HttpError(401, 'bad_secret', '密码不对');
  }

  await clear(env, 'pair', fp);
  const tokens = await issueDevice(
    env,
    row.id,
    body.kind ?? 'desk',
    identity.role,
    identity.memberId,
    body.deviceLabel ?? null,
  );
  // role 回给客户端：界面要据此决定配对完是"进了管理模式"还是"只能记分"
  return json({ classId: row.id, code, role: identity.role, memberId: identity.memberId, ...tokens });
}

/** 静默续期。refresh 一次一换（rotate），旧 token 立即作废。 */
export async function refresh(env: Env, request: Request): Promise<Response> {
  const fp = await fingerprint(request, env);
  await assertNotBlocked(env, 'refresh', fp);

  const body = await readJson<{ refresh?: string }>(request);
  const token = requireString(body.refresh, 'refresh', 128);
  const hash = await sha256Hex(token);

  const device = await env.DB.prepare(
    `SELECT d.id, d.class_id, d.kind, d.expires_at, d.revoked_at, d.role, d.member_id
       FROM devices d WHERE d.refresh_hash = ?`,
  )
    .bind(hash)
    .first<{
      id: string;
      class_id: string;
      kind: 'board' | 'desk';
      expires_at: number;
      revoked_at: number | null;
      role: string | null;
      member_id: string | null;
    }>();

  if (!device || device.revoked_at || device.expires_at < now()) {
    await record(env, 'refresh', fp);
    // 三种失败原因分开报，但都不带任何可用信息：客户端只需要知道"重新配对"
    if (!device) throw new HttpError(401, 'bad_refresh', '设备凭据无效，请重新配对');
    if (device.revoked_at) throw new HttpError(401, 'device_revoked', '这台设备已被撤销');
    throw new HttpError(401, 'refresh_expired', '设备凭据已过期，请重新配对');
  }

  // 身份以"现在"为准，不照抄签发时的快照：
  // 成员被删、被撤销、或者被从班长降成班委，下一轮续期就该跟着变。
  // 不这么做的话，撤一个班长等于要等他手上的令牌 12 小时自然过期。
  let role = asRole(device.role);
  let memberId = device.member_id ?? null;
  if (memberId) {
    const member = await env.DB.prepare(
      'SELECT role, deleted_at, revoked_at FROM members WHERE id = ? AND class_id = ?',
    )
      .bind(memberId, device.class_id)
      .first<{ role: string; deleted_at: number | null; revoked_at: number | null }>();

    if (!member || member.deleted_at || member.revoked_at) {
      // 顺手把这台设备也撤掉：下次不再是一个"刷新失败"，而是明确的"这个身份没了"
      await env.DB.prepare('UPDATE devices SET revoked_at = ? WHERE id = ? AND class_id = ?')
        .bind(now(), device.id, device.class_id)
        .run();
      await record(env, 'refresh', fp);
      throw new HttpError(401, 'member_revoked', '这个身份已被撤销，请用当前的口令重新配对');
    }
    role = asRole(member.role);
  }

  await clear(env, 'refresh', fp);

  const next = randomHex(32);
  const at = now();
  const expiresAt = at + refreshTtlSec(env) * 1000;
  await env.DB.batch([
    env.DB.prepare('UPDATE devices SET refresh_hash = ?, last_seen_at = ?, expires_at = ?, role = ? WHERE id = ?')
      .bind(await sha256Hex(next), at, expiresAt, role, device.id),
  ]);

  const claims: Omit<Claims, 'iat' | 'exp'> = {
    sid: device.id,
    cid: device.class_id,
    kind: device.kind,
    role,
    mid: memberId,
  };
  return json({
    access: await signToken(env, claims, accessTtlSec(env)),
    refresh: next,
    role,
    accessExpiresAt: at + accessTtlSec(env) * 1000,
    refreshExpiresAt: expiresAt,
  });
}

/** 换班主任：撤销当前设备。撤销后 refresh 立刻失效，access 到期自然失效。 */
export async function signOut(env: Env, request: Request): Promise<Response> {
  const claims = await requireAuth(env, request);
  await env.DB.prepare('UPDATE devices SET revoked_at = ? WHERE id = ? AND class_id = ?')
    .bind(now(), claims.sid, claims.cid)
    .run();
  return json({ ok: true });
}

/** 唤醒探测：一体机休眠后 TCP 是假活的，客户端每次激活发一个 /ping，2 秒超时重建连接。 */
export async function ping(env: Env, request: Request): Promise<Response> {
  await requireAuth(env, request);
  const row = await env.DB.prepare('SELECT 1 AS ok').first<{ ok: number }>();
  return json({ ok: row?.ok === 1, serverTime: now() });
}
