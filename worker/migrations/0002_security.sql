-- 0002 · 防爆破计数
--
-- 为什么要有这张表：Workers 是无状态的，一个请求结束后内存里什么都不剩。
-- /pair 可以被无限次试密码，唯一的办法是把失败次数落到 D1，下次请求再读出来。
--
-- fp 存的是 IP + User-Agent 的加盐哈希（盐是 TOKEN_SECRET），不是 IP 本身。
-- 这张表就算被人看到，也说不出"哪个地址试过密码"，只能看出"某个指纹试了几次"。
-- 一张表的行数是"失败过的指纹数"，而不是"请求数"，所以不会无限膨胀；
-- 过期行在下次命中时顺手删掉（见 guard.ts 的 assertNotBlocked）。
--
-- 不需要 class_id：限流是按来源做的，跨班共用同一个计数器反而更好
-- —— 一个人换了班级码继续猜，也一样会被挡住。

CREATE TABLE IF NOT EXISTS auth_throttle (
  scope         TEXT    NOT NULL,           -- pair / create / refresh
  fp            TEXT    NOT NULL,           -- 来源指纹（加盐哈希）
  fails         INTEGER NOT NULL DEFAULT 0, -- 本窗口内已失败次数
  window_start  INTEGER NOT NULL,           -- 本窗口起点（毫秒）
  blocked_until INTEGER,                    -- 非空且在将来 = 还在封禁期
  PRIMARY KEY (scope, fp)
);

CREATE INDEX IF NOT EXISTS idx_throttle_blocked ON auth_throttle(blocked_until);
