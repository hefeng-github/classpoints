/**
 * 事件计数进云同步（1.3.3）。
 *
 * 两张表各走一条路：
 *   counters       —— 事件定义，走通用实体接口（/api/v1/counters），带 rev 与 changelog
 *   counter_ticks  —— 打点，只追加，走批量接口（/api/v1/counter_ticks），按 id 幂等
 *
 * 为什么打点单独开一张表而不是塞进 entries：
 *   积分要参与总评、要能冲销、要进 Excel；事件只关心"几次"。
 *   合成一张表以后，凡是遍历 entries 的地方（总评、导出、冲销）都得多一个 if，
 *   而且"一条 0 分的记录"在 Excel 里毫无意义。
 *
 * 外键：counter_ticks 的三个引用都指向同班的行。D1 的外键默认不开，
 * 这里的 REFERENCES 主要是给读的人看的；真正的门在路由里（缺引用回 422 unknown_*）。
 */

-- id 做主键（和其它业务表一致）：通用实体接口的 upsert 走 `ON CONFLICT(id) DO UPDATE`，
-- 主键写成 (class_id, id) 的话那句冲突子句匹配不上任何约束，SQLite 直接报错。
CREATE TABLE IF NOT EXISTS counters (
  id          TEXT PRIMARY KEY,
  class_id    TEXT NOT NULL,
  label       TEXT NOT NULL,
  icon        TEXT,
  sort        INTEGER NOT NULL DEFAULT 0,
  preset_key  TEXT,
  archived_at INTEGER,
  rev         INTEGER NOT NULL DEFAULT 1,
  updated_at  INTEGER NOT NULL,
  deleted_at  INTEGER
);
CREATE INDEX IF NOT EXISTS idx_counters_class ON counters(class_id, deleted_at);
-- 预设事件按 preset_key 去重，班内唯一（两个班各有一份"迟到"是正常的）
CREATE UNIQUE INDEX IF NOT EXISTS uq_counters_preset ON counters(class_id, preset_key)
  WHERE preset_key IS NOT NULL;

CREATE TABLE IF NOT EXISTS counter_ticks (
  id            TEXT NOT NULL,
  class_id      TEXT NOT NULL,
  counter_id    TEXT NOT NULL,
  student_id    TEXT NOT NULL,
  period_id     TEXT NOT NULL,
  value         INTEGER NOT NULL,
  note          TEXT,
  operator_id   TEXT NOT NULL,
  operator_name TEXT NOT NULL,
  occurred_at   INTEGER NOT NULL,
  created_at    INTEGER NOT NULL,
  PRIMARY KEY (class_id, id)
);
-- 拉取按 id 取；统计按 (周期, 事件, 学生) 走
CREATE INDEX IF NOT EXISTS idx_ticks_period ON counter_ticks(class_id, period_id, occurred_at DESC);
CREATE INDEX IF NOT EXISTS idx_ticks_count ON counter_ticks(class_id, period_id, counter_id, student_id);
