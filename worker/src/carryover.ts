/**
 * 旧周期分数结转清零：让"累计积分"回到只剩当前周期。
 *
 * 为什么需要它：客户端的总评用的是 `CumulativeTotals()`（所有周期相加），
 * 而老师的口径是"总评只看这个周期"。云端没有"总评"这个字段，唯一能左右它的
 * 就是 entries —— 于是在一个旧周期里给每个学生补一条等额负分，把那一桶抹平。
 *
 * 代价（用户已知并接受）：
 *   - 明细里每人会多一条「周期重置」的负分记录；
 *   - 旧周期的榜单/历史成绩单会变成 0。
 * 等客户端把口径改成"当前周期总分 + 当期考试"之后，这个开关可以关掉：
 *   wrangler secret/vars 里把 CARRYOVER_OLD_PERIODS 设成 "0" 即可，不用改代码。
 *
 * 幂等：抹平之后再算一次，那个周期的合计就是 0，不会再补第二条。
 */
import type { Env } from './types';
import { changeStmt, chunk, now } from './util';

/** 结转行的标记，明细里老师和排查都靠这两个字段认。 */
const LABEL = '周期重置';
const NOTE = '上一周期的分数已结转清零，不计入总评';
const OPERATOR_ID = 'carry-over';
const OPERATOR_NAME = '系统';

const INSERT = `INSERT INTO entries
  (id, class_id, batch_id, period_id, student_id, rule_id, rule_label, rule_delta_default,
   delta, source, note, note_template_id, capped, over_cap, operator_id, operator_name,
   identify_method, occurred_at, created_at, reverses_entry_id)
  VALUES (?, ?, ?, ?, ?, NULL, ?, NULL, ?, 'custom', ?, NULL, 0, 0, ?, ?, NULL, ?, ?, NULL)`;

export function carryOverEnabled(env: Env): boolean {
  return (env.CARRYOVER_OLD_PERIODS ?? '1') !== '0';
}

/** 抹平一个周期：每个学生补一条 -(该周期合计) 的负数。返回补了几条。 */
export async function carryOverPeriod(
  env: Env,
  classId: string,
  periodId: string,
  at = now(),
): Promise<number> {
  if (!carryOverEnabled(env)) return 0;

  const rows = await env.DB.prepare(
    `SELECT student_id AS student_id, SUM(delta) AS total
       FROM entries
      WHERE class_id = ? AND period_id = ?
      GROUP BY student_id
     HAVING SUM(delta) <> 0`,
  )
    .bind(classId, periodId)
    .all<{ student_id: string; total: number }>();

  const list = rows.results ?? [];
  if (list.length === 0) return 0;

  const batchId = `carry:${periodId}`;
  // 一条成立要写两行（流水 + changelog），按 40 人一批，别让单次 batch 过大
  for (const part of chunk(list, 40)) {
    const stmts: D1PreparedStatement[] = [];
    for (const r of part) {
      const id = crypto.randomUUID();
      stmts.push(
        env.DB.prepare(INSERT).bind(
          id,
          classId,
          batchId,
          periodId,
          r.student_id,
          LABEL,
          -r.total,
          NOTE,
          OPERATOR_ID,
          OPERATOR_NAME,
          at,
          at,
        ),
        changeStmt(env.DB, classId, 'entries', id, 'upsert', at),
      );
    }
    await env.DB.batch(stmts);
  }

  return list.length;
}

/** 抹平这个班所有"不是当前"的周期（换周期/重置时调，必须在 trimOldPeriods 之前）。 */
export async function carryOverOldPeriods(env: Env, classId: string, at = now()): Promise<number> {
  if (!carryOverEnabled(env)) return 0;

  const rows = await env.DB.prepare(
    `SELECT id FROM periods WHERE class_id = ? AND status <> 'active'`,
  )
    .bind(classId)
    .all<{ id: string }>();

  let n = 0;
  for (const p of rows.results ?? []) n += await carryOverPeriod(env, classId, p.id, at);
  return n;
}

/**
 * 这批流水落进了哪些"已经不是当前"的周期 —— 落进去了就要重新抹平。
 *
 * 典型场景：大屏在换周期之前记了几分、还没推上去，教室那边先点了换周期；
 * 大屏回来补推，那几分就悄悄加进旧周期的累计里，总评又鼓起来了。
 */
export async function carryOverTouched(env: Env, classId: string, periodIds: string[], at = now()): Promise<number> {
  if (!carryOverEnabled(env) || periodIds.length === 0) return 0;

  const ids = [...new Set(periodIds)];
  const rows = await env.DB.prepare(
    `SELECT id FROM periods WHERE class_id = ? AND status <> 'active' AND id IN (${ids.map(() => '?').join(',')})`,
  )
    .bind(classId, ...ids)
    .all<{ id: string }>();

  let n = 0;
  for (const p of rows.results ?? []) n += await carryOverPeriod(env, classId, p.id, at);
  return n;
}
