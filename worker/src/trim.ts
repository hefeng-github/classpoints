/**
 * 云端明细的保留窗口。
 *
 * 周期重置（rollover）只把旧周期标成 archived，entries 一条都不删 —— 一个班一学期几百条流水，
 * 用两年 D1 免费额度也未必撑满，但每次拉取、每次重建索引都在为"早就翻篇的周期"付费，
 * 而且这些老数据在教室那台机器上本来就有完整副本。
 *
 * 做法：只保留最近 4 个周期（含当前这个）的明细，更早的周期连流水、考试、成绩一起删。
 *
 * 三条约束，缺一条就会出事：
 * ① 删的是**云端副本**。不写 changelog、不发删除墓碑 —— 否则另一台设备拉到"服务端说没有这行"
 *    会把本地历史也删掉，那是真丢数据。本地各留各的。
 * ② 顺序必须孙子→儿子→父亲：D1 开着外键，先删 periods 会被 entries 挡住整批回滚。
 * ③ changelog 里的相关行一起删：它是拉取的源头，留着会让客户端以为"这条被硬删了"。
 *
 * 代价：新配对/重装的机器只能看到最近 4 个周期。设置页里写明了。
 */
import type { Env } from './types';

/** 保留多少个周期（含当前 active 那个）。一个学期大约 4~6 个周期，4 够期中小结与期末总评。 */
export const KEEP_PERIODS = 4;

export interface TrimResult {
  /** 被清掉的周期 id（按时间从旧到新） */
  dropped: string[];
}

export async function trimOldPeriods(
  env: Env,
  classId: string,
  keep: number = KEEP_PERIODS,
): Promise<TrimResult> {
  const rows = await env.DB.prepare(
    'SELECT id FROM periods WHERE class_id = ? ORDER BY start_at DESC, created_at DESC',
  )
    .bind(classId)
    .all<{ id: string }>();

  const all = rows.results ?? [];
  if (all.length <= keep) return { dropped: [] };

  const drop = all.slice(keep).map((p) => p.id);
  const stmts: D1PreparedStatement[] = [];

  for (const periodId of drop) {
    // ① 先抹掉 changelog 里对这些行的引用（此时数据还在，子查询才查得到）
    stmts.push(
      env.DB.prepare(
        `DELETE FROM changelog WHERE class_id = ? AND entity = 'entries'
           AND row_id IN (SELECT id FROM entries WHERE class_id = ? AND period_id = ?)`,
      ).bind(classId, classId, periodId),
    );
    stmts.push(
      env.DB.prepare(
        `DELETE FROM changelog WHERE class_id = ? AND entity = 'exams'
           AND row_id IN (SELECT id FROM exams WHERE class_id = ? AND period_id = ?)`,
      ).bind(classId, classId, periodId),
    );
    stmts.push(
      env.DB.prepare(
        `DELETE FROM changelog WHERE class_id = ? AND entity = 'exam_scores'
           AND row_id IN (
             SELECT e.id || ':' || s.student_id
               FROM exams e JOIN exam_scores s ON s.exam_id = e.id
              WHERE e.class_id = ? AND e.period_id = ?)`,
      ).bind(classId, classId, periodId),
    );

    // ② 再删数据，孙子 → 儿子 → 父亲
    stmts.push(
      env.DB.prepare(
        `DELETE FROM exam_scores WHERE class_id = ?
           AND exam_id IN (SELECT id FROM exams WHERE class_id = ? AND period_id = ?)`,
      ).bind(classId, classId, periodId),
    );
    stmts.push(
      env.DB.prepare(
        `DELETE FROM scheme_weights
           WHERE exam_id IN (SELECT id FROM exams WHERE class_id = ? AND period_id = ?)`,
      ).bind(classId, periodId),
    );
    stmts.push(
      env.DB.prepare('DELETE FROM entries WHERE class_id = ? AND period_id = ?').bind(classId, periodId),
    );
    stmts.push(
      env.DB.prepare('DELETE FROM exams WHERE class_id = ? AND period_id = ?').bind(classId, periodId),
    );
    stmts.push(
      env.DB.prepare("DELETE FROM changelog WHERE class_id = ? AND entity = 'periods' AND row_id = ?").bind(
        classId,
        periodId,
      ),
    );
    stmts.push(env.DB.prepare('DELETE FROM periods WHERE class_id = ? AND id = ?').bind(classId, periodId));
  }

  await env.DB.batch(stmts);
  return { dropped: drop };
}
