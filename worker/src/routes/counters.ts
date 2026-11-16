/**
 * 事件打点：只追加的事实表，和流水（entries）是同一类东西。
 *
 * 幂等靠约束：主键 (class_id, id)，配 INSERT OR IGNORE。客户端重传一次不会多加一次数。
 * 一批一个事务：D1 的 batch 就是事务，也只算一次查询配额。
 *
 * 为什么次数永远现算 SUM(value)、而不是在云端存一个"当前次数"：
 * 存累计值就要处理"两台设备同时 +1"的合并（要么加锁、要么后写覆盖前写），
 * 而只追加的话两次 +1 各自是一条行，加起来天然是 2。
 */
import type { Env } from '../types';
import { requireAuth } from '../auth';
import { HttpError, changeStmt, intOrNull, json, now, optionalString, readJson, requireString } from '../util';

/** 客户端 outbox 里的 tick 对象的 camelCase 形状 */
interface TickDto {
  id?: string;
  counterId?: string;
  studentId?: string;
  periodId?: string;
  value?: number;
  note?: string | null;
  operatorId?: string;
  operatorName?: string;
  occurredAt?: number;
  createdAt?: number;
}

const INSERT = `INSERT OR IGNORE INTO counter_ticks
  (id, class_id, counter_id, student_id, period_id, value, note, operator_id, operator_name, occurred_at, created_at)
  VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`;

/**
 * 打点引用的三样东西必须先在云端存在。
 * 不先查的话，缺引用会让整批变成 500，客户端当成"网络抖了"一轮轮退避 ——
 * 数据没丢但永远推不上来，界面上只有一句「待同步 N 条」。
 */
async function requireKnownRefs(env: Env, classId: string, list: TickDto[]): Promise<void> {
  const check = async (table: string, key: keyof TickDto, code: string, what: string) => {
    const ids = [...new Set(list.map((t) => t[key]).filter((v): v is string => typeof v === 'string' && v.length > 0))];
    if (ids.length === 0) return;
    const rows = await env.DB.prepare(
      `SELECT id FROM ${table} WHERE class_id = ? AND id IN (${ids.map(() => '?').join(',')})`,
    )
      .bind(classId, ...ids)
      .all<{ id: string }>();
    const have = new Set((rows.results ?? []).map((r) => r.id));
    const missing = ids.filter((id) => !have.has(id));
    if (missing.length > 0) {
      throw new HttpError(422, code, `云端还没有这个${what}（${missing[0]}），等它同步上去后这批计数会自动补推`);
    }
  };

  await check('periods', 'periodId', 'unknown_period', '周期');
  await check('students', 'studentId', 'unknown_student', '学生');
  await check('counters', 'counterId', 'unknown_counter', '事件');
}

function bindTick(env: Env, classId: string, t: TickDto): D1PreparedStatement {
  const id = requireString(t.id, 'id', 40);
  const value = intOrNull(t.value);
  if (value === null || value === 0) {
    throw new HttpError(422, 'invalid_value', 'value 必须是非零整数');
  }
  // 合理性上限：真实场景里只会有 ±1（改次数也只是补一个差值），
  // 四位以上必然是客户端算错了，宁可拒收也别让统计变得没法解释。
  if (Math.abs(value) > 999) {
    throw new HttpError(422, 'value_too_large', '单次计数的变化量超过 999');
  }
  const at = intOrNull(t.occurredAt) ?? now();

  return env.DB.prepare(INSERT).bind(
    id,
    classId,
    requireString(t.counterId, 'counterId', 40),
    requireString(t.studentId, 'studentId', 40),
    requireString(t.periodId, 'periodId', 40),
    value,
    optionalString(t.note),
    requireString(t.operatorId, 'operatorId', 40),
    requireString(t.operatorName, 'operatorName', 40),
    at,
    intOrNull(t.createdAt) ?? at,
  );
}

export async function postTicks(env: Env, request: Request): Promise<Response> {
  const claims = await requireAuth(env, request);
  const body = await readJson<{ ticks?: TickDto[] } | TickDto[]>(request);
  const list = Array.isArray(body) ? body : body.ticks ?? [];
  if (list.length === 0) throw new HttpError(422, 'empty_batch', '至少要一条记录');
  if (list.length > 100) throw new HttpError(422, 'batch_too_large', '一批最多 100 条，请分批提交');

  await requireKnownRefs(env, claims.cid, list);

  const at = now();
  const stmts: D1PreparedStatement[] = [];
  const insertAt: number[] = [];
  for (const t of list) {
    insertAt.push(stmts.length);
    stmts.push(bindTick(env, claims.cid, t));
    stmts.push(changeStmt(env.DB, claims.cid, 'counter_ticks', requireString(t.id, 'id', 40), 'upsert', at));
  }

  const results = await env.DB.batch(stmts);
  // 每条插入后面跟着一条 changelog，所以先记下插入语句的下标再数 changes
  const inserted = insertAt.filter((i) => (results[i].meta?.changes ?? 0) > 0).length;

  return json({ accepted: list.length, inserted, duplicates: list.length - inserted }, 200);
}
