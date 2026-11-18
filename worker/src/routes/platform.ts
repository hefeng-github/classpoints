/**
 * 平台管理员：跨班级的那一个视角。
 *
 * 班里那套管理台（routes/overview.ts + grants.ts）要求"你得先知道班级码 + 班主任密码"。
 * 这对班主任是对的，但它回答不了另一类问题：
 *   —— 这台部署上一共有几个班？哪个是空的、哪个是重复建的？
 *   —— 某个老师的机器全掉了，但他人不在学校，谁去把那个班收掉？
 *   —— 迁移/排查时要知道"云端到底存了什么"。
 *
 * 所以这里加一个**部署级**的管理员：口令在环境变量 PLATFORM_PIN 里，一个就能看全部班级。
 * 它不进任何班级的业务数据，只做四件事：看列表、改名、清旧周期、注销/恢复。
 * 刻意没给"读流水明细""看学生名单"这类能力 —— 平台管理员是运维，不是班主任。
 *
 * 三条边界：
 *   ① 没配 PLATFORM_PIN 就整块关闭（503），不做一个"空口令可以进"的默认；
 *   ② 令牌带 plat=true，且**不能**用在班级接口上（见 auth.requireAuth）；
 *   ③ 注销仍要重新输入班级码 —— 它会让全班设备当场掉线，点错一次够老师折腾一天。
 */
import type { Env } from '../types';
import { HttpError, json, now, readJson, requireString, safeEqual } from '../util';
import { accessTtlSec, signToken } from '../auth';
import { assertNotBlocked, clear, fingerprint, record } from '../guard';
import { KEEP_PERIODS, trimOldPeriods } from '../trim';

const SCOPE = 'platform';

/** 没配口令就别开门。空口令默认放行是最糟的一种"方便"。 */
function assertEnabled(env: Env): string {
  const pin = (env.PLATFORM_PIN ?? '').trim();
  if (pin.length < 8) {
    throw new HttpError(
      503,
      'platform_disabled',
      '这台部署没有配置平台管理员口令，请在服务端执行 wrangler secret put PLATFORM_PIN（至少 8 位）',
    );
  }
  return pin;
}

/**
 * 平台登录。和班级管理台登录同一套限流思路，但门槛更严：
 * 一个口令能看所有班，被猜中的代价比单个班大得多。
 */
export async function platformSession(env: Env, request: Request): Promise<Response> {
  const pin = assertEnabled(env);
  const fp = await fingerprint(request, env);
  await assertNotBlocked(env, SCOPE, fp);

  const body = await readJson<{ pin?: string }>(request);
  const given = requireString(body.pin, 'pin', 128);

  if (!safeEqual(given, pin)) {
    await record(env, SCOPE, fp);
    throw new HttpError(401, 'bad_platform_pin', '平台管理员口令不对');
  }

  await clear(env, SCOPE, fp);
  // cid 留空：平台令牌不对应任何班，班级接口会因为"查不到这个班"而自然拒绝，
  // 但真正拦住它的是 requireAuth 里那条 plat 判断。
  const access = await signToken(
    env,
    { sid: 'platform', cid: '', kind: 'desk', role: 'owner', mid: null, plat: true },
    accessTtlSec(env),
  );
  return json({ access, kind: 'platform' });
}

interface ClassListRow {
  id: string;
  name: string;
  code: string;
  join_mode: string | null;
  resetting: number | null;
  created_at: number;
  deleted_at: number | null;
  students: number;
  entries: number;
  exams: number;
  devices: number;
  pending: number;
  periods: number;
  last_entry_at: number | null;
  last_device_at: number | null;
}

/**
 * 所有班级一屏看完。
 *
 * 排序刻意按"有没有在用"：刚记过分的排前面，其次是刚建的，注销的沉到最后 ——
 * 排查时最想先看到的就是"哪几个班是真的活的"。
 */
export async function listClasses(env: Env, request: Request): Promise<Response> {
  const url = new URL(request.url);
  const q = (url.searchParams.get('q') ?? '').trim();
  const includeClosed = url.searchParams.get('closed') === '1';
  const like = `%${q}%`;

  const rows = await env.DB.prepare(
    `SELECT c.id, c.name, c.code, c.join_mode, c.resetting, c.created_at, c.deleted_at,
            (SELECT count(*) FROM students s
              WHERE s.class_id = c.id AND s.deleted_at IS NULL AND s.active = 1) AS students,
            (SELECT count(*) FROM entries e WHERE e.class_id = c.id) AS entries,
            (SELECT count(*) FROM exams x WHERE x.class_id = c.id AND x.deleted_at IS NULL) AS exams,
            (SELECT count(*) FROM devices d WHERE d.class_id = c.id AND d.revoked_at IS NULL) AS devices,
            (SELECT count(*) FROM device_grants g WHERE g.class_id = c.id AND g.status = 'pending') AS pending,
            (SELECT count(*) FROM periods p WHERE p.class_id = c.id) AS periods,
            (SELECT max(e.created_at) FROM entries e WHERE e.class_id = c.id) AS last_entry_at,
            (SELECT max(d.last_seen_at) FROM devices d WHERE d.class_id = c.id) AS last_device_at
       FROM classes c
      WHERE (? = 1 OR c.deleted_at IS NULL)
        AND (? = '' OR c.code LIKE ? OR c.name LIKE ?)
      ORDER BY (c.deleted_at IS NULL) DESC, coalesce(
        (SELECT max(e.created_at) FROM entries e WHERE e.class_id = c.id),
        c.created_at) DESC
      LIMIT 200`,
  )
    .bind(includeClosed ? 1 : 0, q, like, like)
    .all<ClassListRow>();

  const list = (rows.results ?? []).map((r) => ({
    id: r.id,
    name: r.name,
    code: r.code,
    joinMode: r.join_mode === 'approval' ? 'approval' : 'open',
    resetting: r.resetting === 1,
    createdAt: r.created_at,
    closedAt: r.deleted_at,
    students: r.students,
    entries: r.entries,
    exams: r.exams,
    devices: r.devices,
    pending: r.pending,
    periods: r.periods,
    lastEntryAt: r.last_entry_at ?? 0,
    lastDeviceAt: r.last_device_at ?? 0,
  }));

  const totals = await env.DB.prepare(
    `SELECT (SELECT count(*) FROM classes) AS classes,
            (SELECT count(*) FROM classes WHERE deleted_at IS NULL) AS open,
            (SELECT count(*) FROM students WHERE deleted_at IS NULL AND active = 1) AS students,
            (SELECT count(*) FROM entries) AS entries,
            (SELECT count(*) FROM devices WHERE revoked_at IS NULL) AS devices`,
  ).first<Record<string, number | null>>();

  return json({
    classes: list,
    totals: {
      classes: Number(totals?.classes ?? 0),
      open: Number(totals?.open ?? 0),
      students: Number(totals?.students ?? 0),
      entries: Number(totals?.entries ?? 0),
      devices: Number(totals?.devices ?? 0),
    },
    keepPeriods: KEEP_PERIODS,
    serverTime: now(),
  });
}

/** 单个班的详情（注销的班也能看，便于确认哪个空班可以恢复/彻底收掉）。 */
export async function classDetail(env: Env, classId: string): Promise<Response> {
  const cls = await env.DB.prepare(
    `SELECT id, name, code, join_mode, resetting, created_at, deleted_at,
            (SELECT count(*) FROM students WHERE class_id = classes.id AND deleted_at IS NULL AND active = 1) AS students,
            (SELECT count(*) FROM entries WHERE class_id = classes.id) AS entries,
            (SELECT count(*) FROM periods WHERE class_id = classes.id) AS periods,
            (SELECT count(*) FROM exams WHERE class_id = classes.id AND deleted_at IS NULL) AS exams,
            (SELECT count(*) FROM devices WHERE class_id = classes.id AND revoked_at IS NULL) AS devices,
            (SELECT count(*) FROM device_grants WHERE class_id = classes.id AND status = 'pending') AS pending,
            (SELECT count(*) FROM changelog WHERE class_id = classes.id) AS changes,
            (SELECT max(created_at) FROM entries WHERE class_id = classes.id) AS last_entry_at
       FROM classes WHERE id = ?`,
  )
    .bind(classId)
    .first<Record<string, string | number | null>>();

  if (!cls) throw new HttpError(404, 'class_not_found', '没有这个班');

  return json({
    class: {
      id: String(cls.id),
      name: String(cls.name),
      code: String(cls.code),
      joinMode: cls.join_mode === 'approval' ? 'approval' : 'open',
      resetting: Number(cls.resetting ?? 0) === 1,
      createdAt: Number(cls.created_at ?? 0),
      closedAt: cls.deleted_at === null ? null : Number(cls.deleted_at),
    },
    counts: {
      students: Number(cls.students ?? 0),
      entries: Number(cls.entries ?? 0),
      periods: Number(cls.periods ?? 0),
      exams: Number(cls.exams ?? 0),
      devices: Number(cls.devices ?? 0),
      pending: Number(cls.pending ?? 0),
      changes: Number(cls.changes ?? 0),
    },
    lastEntryAt: Number(cls.last_entry_at ?? 0),
  });
}

export async function renameAnyClass(env: Env, request: Request, classId: string): Promise<Response> {
  const body = await readJson<{ name?: string }>(request);
  const name = requireString(body.name, 'name', 40).trim();
  if (name.length === 0) throw new HttpError(422, 'invalid_name', '班名不能为空');

  const res = await env.DB.prepare('UPDATE classes SET name = ? WHERE id = ?').bind(name, classId).run();
  if ((res.meta.changes ?? 0) === 0) throw new HttpError(404, 'class_not_found', '没有这个班');
  return json({ ok: true, name });
}

export async function trimAnyClass(env: Env, classId: string): Promise<Response> {
  const exists = await env.DB.prepare('SELECT 1 AS ok FROM classes WHERE id = ?').bind(classId).first<{ ok: number }>();
  if (!exists) throw new HttpError(404, 'class_not_found', '没有这个班');

  const before = await countClass(env, classId);
  const { dropped } = await trimOldPeriods(env, classId);
  const after = await countClass(env, classId);
  return json({ ok: true, dropped: dropped.length, keep: KEEP_PERIODS, before, after });
}

async function countClass(env: Env, classId: string): Promise<{ periods: number; entries: number; exams: number }> {
  const r = await env.DB.prepare(
    `SELECT (SELECT count(*) FROM periods WHERE class_id = ?) AS periods,
            (SELECT count(*) FROM entries WHERE class_id = ?) AS entries,
            (SELECT count(*) FROM exams   WHERE class_id = ?) AS exams`,
  )
    .bind(classId, classId, classId)
    .first<{ periods: number; entries: number; exams: number }>();
  return { periods: r?.periods ?? 0, entries: r?.entries ?? 0, exams: r?.exams ?? 0 };
}

/**
 * 吊销这个班的全部设备（不注销班级）。
 *
 * 比注销轻一档：老师的机器丢了、或者整批设备需要重新配对时用这个。
 * 班级码和云端数据都留着，重新配对就能接上。
 */
export async function revokeClassDevices(env: Env, classId: string): Promise<Response> {
  const at = now();
  const res = await env.DB.prepare(
    'UPDATE devices SET revoked_at = ? WHERE class_id = ? AND revoked_at IS NULL',
  )
    .bind(at, classId)
    .run();
  return json({ ok: true, revoked: res.meta.changes ?? 0 });
}

/** 注销：软删 + 撤设备。要求重输班级码，理由同班里那套。 */
export async function closeAnyClass(env: Env, request: Request, classId: string): Promise<Response> {
  const body = await readJson<{ code?: string }>(request);
  const code = requireString(body.code, 'code', 16).trim().toUpperCase();

  const cls = await env.DB.prepare('SELECT code FROM classes WHERE id = ? AND deleted_at IS NULL')
    .bind(classId)
    .first<{ code: string }>();
  if (!cls) throw new HttpError(404, 'class_not_found', '这个班不存在或已注销');
  if (cls.code !== code) throw new HttpError(422, 'code_mismatch', '班级码不对，什么都没动');

  const at = now();
  await env.DB.batch([
    env.DB.prepare('UPDATE devices SET revoked_at = ? WHERE class_id = ? AND revoked_at IS NULL').bind(at, classId),
    env.DB.prepare('UPDATE classes SET deleted_at = ? WHERE id = ?').bind(at, classId),
  ]);
  return json({ ok: true });
}

/** 恢复：把 deleted_at 清掉，班级码重新可用。设备不自动恢复，需要各自重新配对。 */
export async function restoreAnyClass(env: Env, classId: string): Promise<Response> {
  const res = await env.DB.prepare('UPDATE classes SET deleted_at = NULL WHERE id = ? AND deleted_at IS NOT NULL')
    .bind(classId)
    .run();
  if ((res.meta.changes ?? 0) === 0) {
    throw new HttpError(409, 'not_closed', '这个班没有注销，或者不存在');
  }
  return json({ ok: true });
}
