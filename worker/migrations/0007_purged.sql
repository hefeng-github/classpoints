/**
 * 彻底删除的墓碑（1.3.11）。
 *
 * 回收站/归档是软删（deleted_at / archived_at），行还在，随时能恢复 —— 这是对的，
 * 因为"点错了"远比"真想删"常见。但有些东西确实要真删：录错的加分项、试出来的事件、
 * 建重复的模板。软删留着它们，界面上就永远躺着一个只能看不能清的条目。
 *
 * 于是要回答一个新问题：**删了之后，另一台离线设备把旧数据推回来怎么办？**
 * 软删时代不需要回答 —— 行还在，rev 守卫挡得住覆盖。硬删之后行没了，
 * 旧设备的 upsert 会是一条干净的 INSERT，人就这么"复活"了。
 *
 * 这张表就是答案：删过的 id 记在这儿，之后任何写入先查一遍，一律 409 purged。
 * **它只增不减** —— 定期任务和 trim.ts 都不许删它。理由是"能不能复活"这件事没有时效：
 * 一台放假三个月没开的教室电脑，回来照样会把三年前那条记录推上来。
 * 现在 trim 只清明细（流水/考试/打点/changelog），墓碑一条不动。
 */
CREATE TABLE IF NOT EXISTS purged (
  class_id TEXT NOT NULL,
  entity   TEXT NOT NULL,
  row_id   TEXT NOT NULL,
  at       INTEGER NOT NULL,
  PRIMARY KEY (class_id, entity, row_id)
);
