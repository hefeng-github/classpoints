/**
 * 彻底删除（1.3.11）。
 *
 * 与 entities 的 `action: 'delete'` 不同：那个是**软删**（deleted_at），
 * 行还在、能恢复、别的设备上会显示成"回收站里有一条"。这里是真的 DELETE。
 *
 * 三件事必须一起做，少一件就出问题：
 *   ① 删行 —— 连它的子表一起删。云端 D1 外键没开，光删 counters 会留下一批
 *      指向不存在事件的打点，拉取端看到的就是"未知事件，点了 36 次"。
 *   ② 写 changelog（op='delete'）—— /sync 的 `deleted` 数组是"要了却取不出来"算出来的，
 *      别的设备靠它把本地那份也删掉。不写的话对面永远留着一份幽灵数据。
 *   ③ 记 purged 墓碑 —— 防离线设备把旧数据推回来（见 migrations/0007）。
 *
 * 权限与编辑同一条：能改这条规则的人才能删它（路由层用 requireManager 把门）。
 */
import type { Env } from '../types';
import { HttpError, changeStmt, json, now, readJson, requireString } from '../util';
import { resolveEntity } from './entities';

/** 硬删时要连带清掉的子表。周期/学生不在这条链上（界面上没给它们开这个动作）。 */
const CASCADE: Record<string, { table: string; column: string }[]> = {
  counters: [{ table: 'counter_ticks', column: 'counter_id' }],
  schemes: [{ table: 'scheme_weights', column: 'scheme_id' }],
};

export async function purgeEntity(env: Env, request: Request, classId: string): Promise<Response> {
  const body = await readJson<Record<string, unknown>>(request);
  const spec = resolveEntity(String(body.kind ?? ''));
  if (!spec) throw new HttpError(404, 'unknown_entity', `未知实体 ${String(body.kind ?? '')}`);
  const id = requireString(body.id, 'id', 40);
  const at = now();

  // 只认自己班的行：这条记录不存在于本班时，不能顺手把 changelog 和墓碑写下去 ——
  // 那会让另一台设备删掉一条其实属于别的班的数据。
  //
  // 为什么先查一次存在性、而不是像以前那样"删完看 changes 再决定写不写"：
  // 那需要两个 batch（D1 的 batch 各是一个事务），而两个 batch 之间一旦失败
  // （进程重启、D1 抖动、purged 表还没 apply 迁移），行已经删了、changelog 和墓碑却没写 ——
  // 客户端重试时 removed 已经是 0，直接回 not_found_in_class，这个缺口就永久固化：
  // 别的设备拉不到 deleted、旧设备推回来也没有墓碑拦，幽灵数据会一直在。
  // 现在：查是一次读，删子表 + 删主表 + changelog + 墓碑在同一个 batch 里，要么全成要么全不成。
  const existing = await env.DB.prepare(`SELECT 1 AS ok FROM ${spec.table} WHERE id = ? AND class_id = ?`)
    .bind(id, classId)
    .first<{ ok: number }>();

  if (!existing) {
    // 已经不在本班了（别处先删掉了）：不报错，让客户端正常出队。
    // 报 4xx 会被当成"这条坏了"，老师看到一句没头没尾的失败。
    return json({ ok: true, id, removed: 0, note: 'not_found_in_class' });
  }

  await env.DB.batch([
    // ① 子表（云端 D1 外键没开，不显式删就会留下一批指向不存在事件的打点）
    ...(CASCADE[spec.entity] ?? []).map((c) =>
      env.DB.prepare(`DELETE FROM ${c.table} WHERE class_id = ? AND ${c.column} = ?`).bind(classId, id),
    ),
    // ② 主表
    env.DB.prepare(`DELETE FROM ${spec.table} WHERE id = ? AND class_id = ?`).bind(id, classId),
    // ③ 让别的设备知道这一行没了
    changeStmt(env.DB, classId, spec.entity, id, 'delete', at),
    // ④ 墓碑：拦住离线设备把这个 id 推回来
    env.DB.prepare(
      'INSERT INTO purged (class_id, entity, row_id, at) VALUES (?, ?, ?, ?) ON CONFLICT DO NOTHING',
    ).bind(classId, spec.entity, id, at),
  ]);

  return json({ ok: true, id, removed: 1 });
}
