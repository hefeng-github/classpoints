/**
 * 班级总览与管理。
 *
 * 网页管理台原来只有设备白名单：班主任能决定"谁能进来"，却看不到这个班
 * 在云端到底存了什么 —— 有没有名单、流水攒了多少、几个周期、占多少变更。
 * 出了「重复建班」那种事（同一个班名在云端两个班，一个是空的），
 * 手上没有任何地方能确认哪个是空的、也没法把空的那个收掉。
 *
 * 所以这里补两块：
 *   总览 —— 学生 / 流水 / 周期 / 考试 / 设备 / 留存，一次查询取齐；
 *   管理 —— 改云端显示名、手动清理更早的周期、注销这个班（软删）。
 *
 * 三条边界，写在这里免得以后被"顺手加个功能"破坏：
 *   ① 注销只做软删（classes.deleted_at），业务数据一行不删 —— 删错了还能捞；
 *   ② 清理周期调的是 trimOldPeriods，保留窗口固定 KEEP_PERIODS，不接受前端传参；
 *   ③ 注销要求重新输入班级码。它是这个页面唯一不可撤销的动作，
 *      不能靠"我刚才点过登录"来证明自己没点错。
 */
import type { Env } from '../types';
import { HttpError, json, now, readJson, requireString } from '../util';
import { KEEP_PERIODS, trimOldPeriods } from '../trim';

const DAY = 86_400_000;

interface OverviewRow {
  id: string;
  name: string;
  code: string;
  join_mode: string | null;
  resetting: number | null;
  created_at: number;
}

type Sub = {
  key: string;
  sql: string;
  /** 和 sql 里的占位符一一对应，按顺序拼进 bind */
  args?: (string | number)[];
};

/**
 * 把一堆标量子查询拼成一条 SELECT。
 *
 * 为什么不写一条长 SQL：十几个 `?` 全靠人肉数顺序，改一行就要重数一遍；
 * 这里每个子查询带着自己的参数，拼装时按书写顺序收集，不会错位。
 */
function scalarQuery(subs: Sub[]): { sql: string; args: (string | number)[] } {
  const args: (string | number)[] = [];
  const parts = subs.map((s) => {
    if (s.args) args.push(...s.args);
    return `(${s.sql}) AS ${s.key}`;
  });
  return { sql: `SELECT ${parts.join(', ')}`, args };
}

export async function classOverview(env: Env, classId: string): Promise<Response> {
  const cls = await env.DB.prepare(
    'SELECT id, name, code, join_mode, resetting, created_at FROM classes WHERE id = ? AND deleted_at IS NULL',
  )
    .bind(classId)
    .first<OverviewRow>();
  if (!cls) throw new HttpError(404, 'class_not_found', '这个班不存在或已注销');

  const at = now();
  const { sql, args } = scalarQuery([
    { key: 'students', sql: 'SELECT count(*) FROM students WHERE class_id = ? AND deleted_at IS NULL AND active = 1', args: [classId] },
    { key: 'studentsRemoved', sql: 'SELECT count(*) FROM students WHERE class_id = ? AND deleted_at IS NOT NULL', args: [classId] },
    { key: 'members', sql: 'SELECT count(*) FROM members WHERE class_id = ? AND deleted_at IS NULL', args: [classId] },
    { key: 'rules', sql: 'SELECT count(*) FROM rules WHERE class_id = ? AND deleted_at IS NULL AND archived_at IS NULL', args: [classId] },
    { key: 'counters', sql: 'SELECT count(*) FROM counters WHERE class_id = ? AND deleted_at IS NULL', args: [classId] },
    { key: 'periods', sql: 'SELECT count(*) FROM periods WHERE class_id = ?', args: [classId] },
    { key: 'periodsArchived', sql: "SELECT count(*) FROM periods WHERE class_id = ? AND status = 'archived'", args: [classId] },
    { key: 'entries', sql: 'SELECT count(*) FROM entries WHERE class_id = ?', args: [classId] },
    { key: 'entriesReversed', sql: 'SELECT count(*) FROM entries WHERE class_id = ? AND reversed_at IS NOT NULL', args: [classId] },
    { key: 'entriesDay', sql: 'SELECT count(*) FROM entries WHERE class_id = ? AND created_at >= ?', args: [classId, at - DAY] },
    { key: 'entriesWeek', sql: 'SELECT count(*) FROM entries WHERE class_id = ? AND created_at >= ?', args: [classId, at - 7 * DAY] },
    { key: 'exams', sql: 'SELECT count(*) FROM exams WHERE class_id = ? AND deleted_at IS NULL', args: [classId] },
    { key: 'scores', sql: 'SELECT count(*) FROM exam_scores WHERE class_id = ?', args: [classId] },
    { key: 'ticks', sql: 'SELECT count(*) FROM counter_ticks WHERE class_id = ?', args: [classId] },
    { key: 'devices', sql: 'SELECT count(*) FROM devices WHERE class_id = ? AND revoked_at IS NULL', args: [classId] },
    { key: 'devicesLegacy', sql: 'SELECT count(*) FROM devices WHERE class_id = ? AND revoked_at IS NULL AND fingerprint IS NULL', args: [classId] },
    { key: 'approved', sql: "SELECT count(*) FROM device_grants WHERE class_id = ? AND status = 'approved'", args: [classId] },
    { key: 'pending', sql: "SELECT count(*) FROM device_grants WHERE class_id = ? AND status = 'pending'", args: [classId] },
    { key: 'changes', sql: 'SELECT count(*) FROM changelog WHERE class_id = ?', args: [classId] },
    { key: 'lastEntryAt', sql: 'SELECT coalesce(max(created_at), 0) FROM entries WHERE class_id = ?', args: [classId] },
    { key: 'lastChangeAt', sql: 'SELECT coalesce(max(at), 0) FROM changelog WHERE class_id = ?', args: [classId] },
    { key: 'lastDeviceAt', sql: 'SELECT coalesce(max(last_seen_at), 0) FROM devices WHERE class_id = ?', args: [classId] },
  ]);

  const row = await env.DB.prepare(sql)
    .bind(...args)
    .first<Record<string, number | null>>();
  const n = (key: string): number => Number(row?.[key] ?? 0) || 0;

  const current = await env.DB.prepare(
    `SELECT p.id, p.name, p.start_at,
            (SELECT count(*) FROM entries e WHERE e.class_id = p.class_id AND e.period_id = p.id) AS entries
       FROM periods p
      WHERE p.class_id = ? AND p.status = 'active'`,
  )
    .bind(classId)
    .first<{ id: string; name: string; start_at: number; entries: number }>();

  return json({
    class: {
      id: cls.id,
      name: cls.name,
      code: cls.code,
      createdAt: cls.created_at,
      joinMode: cls.join_mode === 'approval' ? 'approval' : 'open',
      resetting: cls.resetting === 1,
    },
    counts: {
      students: n('students'),
      studentsRemoved: n('studentsRemoved'),
      members: n('members'),
      rules: n('rules'),
      counters: n('counters'),
      periods: n('periods'),
      periodsArchived: n('periodsArchived'),
      entries: n('entries'),
      entriesReversed: n('entriesReversed'),
      entriesDay: n('entriesDay'),
      entriesWeek: n('entriesWeek'),
      exams: n('exams'),
      scores: n('scores'),
      ticks: n('ticks'),
      devices: n('devices'),
      devicesLegacy: n('devicesLegacy'),
      approved: n('approved'),
      pending: n('pending'),
      changes: n('changes'),
    },
    current: current
      ? { id: current.id, name: current.name, startAt: current.start_at, entries: current.entries }
      : null,
    // 保留窗口由服务端定，前端只负责显示与"能不能清"的判定
    keepPeriods: KEEP_PERIODS,
    activity: {
      lastEntryAt: n('lastEntryAt'),
      lastChangeAt: n('lastChangeAt'),
      lastDeviceAt: n('lastDeviceAt'),
      serverTime: at,
    },
  });
}

/** 改云端显示名。客户端里的班名在客户端设置里改，这里只影响管理台标题与 API 返回。 */
export async function renameClass(env: Env, request: Request, classId: string): Promise<Response> {
  const body = await readJson<{ name?: string }>(request);
  const name = requireString(body.name, 'name', 40).trim();
  if (name.length === 0) throw new HttpError(422, 'invalid_name', '班名不能为空');

  const res = await env.DB.prepare('UPDATE classes SET name = ? WHERE id = ? AND deleted_at IS NULL')
    .bind(name, classId)
    .run();
  if ((res.meta.changes ?? 0) === 0) throw new HttpError(404, 'class_not_found', '这个班不存在或已注销');
  return json({ ok: true, name });
}

/** 手动清理更早的周期。自动清理在每次 rollover 时做，这里给不愿意等的人一个按钮。 */
export async function trimClass(env: Env, classId: string): Promise<Response> {
  const count = async (): Promise<{ periods: number; entries: number; exams: number }> => {
    const r = await env.DB.prepare(
      `SELECT (SELECT count(*) FROM periods WHERE class_id = ?) AS periods,
              (SELECT count(*) FROM entries WHERE class_id = ?) AS entries,
              (SELECT count(*) FROM exams   WHERE class_id = ?) AS exams`,
    )
      .bind(classId, classId, classId)
      .first<{ periods: number; entries: number; exams: number }>();
    return { periods: r?.periods ?? 0, entries: r?.entries ?? 0, exams: r?.exams ?? 0 };
  };

  const before = await count();
  const { dropped } = await trimOldPeriods(env, classId);
  const after = await count();

  return json({
    ok: true,
    dropped: dropped.length,
    keep: KEEP_PERIODS,
    before,
    after,
  });
}

/**
 * 注销这个班：软删 + 撤销所有设备。
 *
 * 业务数据一行不删（tables 都还在，只是 classes.deleted_at 有值，所有入口都查它），
 * 所以"注销"是可恢复的 —— 需要时在 D1 里把 deleted_at 置空就行。
 * 要求重新输入班级码，是因为它会让全班设备当场掉线，且管理台登录态可能是半天前的。
 */
export async function closeClass(env: Env, request: Request, classId: string): Promise<Response> {
  const body = await readJson<{ code?: string }>(request);
  const code = requireString(body.code, 'code', 16).trim().toUpperCase();

  const cls = await env.DB.prepare('SELECT code FROM classes WHERE id = ? AND deleted_at IS NULL')
    .bind(classId)
    .first<{ code: string }>();
  if (!cls) throw new HttpError(404, 'class_not_found', '这个班不存在或已注销');
  if (cls.code !== code) {
    throw new HttpError(422, 'code_mismatch', '班级码不对，什么都没动');
  }

  const at = now();
  await env.DB.batch([
    // 先撤设备：云端已经注销的班不该再让任何机器续期
    env.DB.prepare('UPDATE devices SET revoked_at = ? WHERE class_id = ? AND revoked_at IS NULL').bind(at, classId),
    env.DB.prepare('UPDATE classes SET deleted_at = ? WHERE id = ?').bind(at, classId),
  ]);

  return json({ ok: true });
}
