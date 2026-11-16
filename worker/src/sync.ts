/**
 * 增量拉取。
 *
 * 游标用一个整数（changelog.seq），不用时间戳 —— 客户端时钟不可信，
 * 一台系统时间错乱的电脑会永久卡住或重复拉取。
 *
 * 返回的是"当前状态"，不是"变更日志"：一次拉取把所有被改过的行的最新样子给出去，
 * 客户端直接覆盖，不用重放补丁。
 */
import type { Env } from './types';
import { requireAuth } from './auth';
import { HttpError, chunk, json } from './util';

const ENTITIES = [
  'students', 'rules', 'members', 'periods', 'entries',
  'exams', 'exam_scores', 'schemes', 'scheme_weights', 'note_templates',
  'counters', 'counter_ticks',
] as const;

/**
 * 只追加的表：**不参与"取不回来就是被删了"的推断**。
 *
 * 事件打点没有任何删除路径，而且云端按周期清理时会成片删掉老周期 ——
 * 如果照常推墓碑，另一台设备拉到的"服务端说没有这行"会把它本地那份也删掉。
 * 那正是我们最不想要的：删的是云端副本，本地各留各的。
 */
const APPEND_ONLY = new Set<string>(['counter_ticks']);

type EntityName = (typeof ENTITIES)[number];

/** 每次拉多少条变更记录。500 条足够覆盖一天的流水，也远小于 D1 单次查询的行数上限。 */
const MAX_LIMIT = 500;

export async function pull(env: Env, request: Request): Promise<Response> {
  const claims = await requireAuth(env, request);
  const url = new URL(request.url);
  const sinceRaw = Number(url.searchParams.get('since') ?? '0');
  const since = Number.isFinite(sinceRaw) && sinceRaw > 0 ? Math.trunc(sinceRaw) : 0;
  const limitRaw = Number(url.searchParams.get('limit') ?? '200');
  const limit = Math.min(MAX_LIMIT, Math.max(1, Number.isFinite(limitRaw) ? Math.trunc(limitRaw) : 200));

  // class_id 是这里唯一的隔离依据。少了它，一个班的令牌能把整个库拉走。
  const changes = await env.DB.prepare(
    'SELECT seq, entity, row_id, op FROM changelog WHERE class_id = ? AND seq > ? ORDER BY seq ASC LIMIT ?',
  ).bind(claims.cid, since, limit).all<{ seq: number; entity: string; row_id: string; op: string }>();

  const rows = changes.results ?? [];
  const next = rows.length > 0 ? rows[rows.length - 1].seq : since;
  const hasMore = rows.length === limit;

  // 按实体归并 id：一次查询取回几十行，而不是一行一次
  const ids = new Map<EntityName, Set<string>>();
  for (const name of ENTITIES) ids.set(name, new Set());
  for (const r of rows) {
    const set = ids.get(r.entity as EntityName);
    if (set) set.add(r.row_id);
  }

  const out: Record<string, unknown[]> = {};
  for (const name of ENTITIES) out[name] = [];

  const fetchById = async (entity: EntityName, idColumn: (ph: string) => string) => {
    const list = [...(ids.get(entity) ?? [])];
    if (list.length === 0) return;
    for (const part of chunk(list, 80)) {
      const ph = part.map(() => '?').join(',');
      // 两道锁：实体自带的分组列（id / exam_id||':'||student_id / scheme_id）+ class_id
      const res = await env.DB.prepare(`SELECT * FROM ${entity} WHERE class_id = ? AND (${idColumn(ph)})`)
        .bind(claims.cid, ...part)
        .all();
      out[entity].push(...(res.results ?? []));
    }
  };

  await fetchById('students', (ph) => `id IN (${ph})`);
  await fetchById('rules', (ph) => `id IN (${ph})`);
  await fetchById('members', (ph) => `id IN (${ph})`);
  await fetchById('note_templates', (ph) => `id IN (${ph})`);
  await fetchById('counters', (ph) => `id IN (${ph})`);
  await fetchById('counter_ticks', (ph) => `id IN (${ph})`);
  await fetchById('periods', (ph) => `id IN (${ph})`);
  await fetchById('entries', (ph) => `id IN (${ph})`);
  await fetchById('exams', (ph) => `id IN (${ph})`);
  await fetchById('schemes', (ph) => `id IN (${ph})`);
  await fetchById('exam_scores', (ph) => `exam_id || ':' || student_id IN (${ph})`);
  await fetchById('scheme_weights', (ph) => `scheme_id IN (${ph})`);

  // 取不回来的行 = 服务端真的没有了（硬删或整批重导覆盖）。
  // 客户端要据此把本地的也删掉，否则会一直留着一份幽灵数据。
  const deleted: Record<string, string[]> = {};
  for (const name of ENTITIES) {
    if (APPEND_ONLY.has(name)) continue;
    const wanted = [...(ids.get(name) ?? [])];
    if (wanted.length === 0) continue;
    const present = new Set<string>();
    for (const row of out[name] as Record<string, unknown>[]) {
      if (name === 'exam_scores') present.add(`${row.exam_id}:${row.student_id}`);
      else if (name === 'scheme_weights') present.add(String(row.scheme_id));
      else present.add(String(row.id));
    }
    const gone = wanted.filter((id) => !present.has(id));
    if (gone.length > 0) deleted[name] = gone;
  }

  return json({
    ...out,
    deleted,
    next,
    hasMore,
    since,
    serverTime: Date.now(),
    // 客户端每轮拉取都会看到这个标记：一直 true = 还在等通过；
    // 哪一轮突然变 false，就是班主任点了通过，界面当场恢复写权限。
    readOnly: claims.ro === true,
  });
}

/** 重置令牌期间，普通写入一律 423 —— 别让一台设备的写入落到已经归档的周期里。 */
export async function assertNotResetting(env: Env, classId: string): Promise<void> {
  const row = await env.DB.prepare('SELECT resetting FROM classes WHERE id = ?')
    .bind(classId)
    .first<{ resetting: number }>();
  if (row?.resetting === 1) {
    throw new HttpError(423, 'resetting', '这个班正在重置周期，稍等几十秒再试');
  }
}
