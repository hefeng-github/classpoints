-- 设备白名单：新设备要先被班主任在网页上点一下「通过」，才拿得到令牌。
--
-- 原来只有一道门：班级码 + 口令。口令一旦在班里传开（班委要自己刷进来记分，
-- 必然传开），任何一台装了客户端的机器都能进来，还能读到全班名单和流水。
-- 白名单把「知道口令」和「允许这台机器」拆成两件事：
--
--   join_mode = 'open'      照旧，口令对了就进（默认值，老班不受影响）
--   join_mode = 'approval'  口令对了还不够，这台机器的特征码得在白名单里是 approved
--
-- 特征码是客户端本地算的（MachineGuid + 机器名的 SHA-256 前 20 位十六进制），
-- 用户改不了、重装系统才变。它不是强身份凭证 —— 伪造一个特征码的成本比抄一个
-- 班级码高，但也仅此而已；所以它只是「第二道门」，第一道仍然是口令。
--
-- 为什么单独一张表而不是复用 devices：devices 一行对应一份 refresh token，
-- 一个指纹可以有多台设备（重装客户端、换 Windows 用户），而且待审的设备
-- 根本还没有 token —— 那时候没有 devices 行可写。

ALTER TABLE classes ADD COLUMN join_mode TEXT NOT NULL DEFAULT 'open';

-- 记住这台设备当初是凭哪个指纹进来的，网页上「撤销这台机器」要能顺着指纹
-- 把它的所有 refresh 一起作废。
ALTER TABLE devices ADD COLUMN fingerprint TEXT;

CREATE TABLE device_grants (
  id           TEXT PRIMARY KEY,
  class_id     TEXT NOT NULL REFERENCES classes(id),
  fingerprint  TEXT NOT NULL,                     -- 20 位小写十六进制
  label        TEXT,                              -- 客户端自报的设备名，纯展示
  status       TEXT NOT NULL CHECK (status IN ('pending','approved','blocked')),
  role         TEXT,                              -- 最近一次尝试/批准时的角色，纯展示
  member_id    TEXT,
  note         TEXT,                              -- 班主任备注，比如「讲台那台」
  attempts     INTEGER NOT NULL DEFAULT 0,        -- 试过几次，用来识别反复重连的机器
  created_at   INTEGER NOT NULL,
  last_seen_at INTEGER,
  decided_at   INTEGER,
  decided_by   TEXT
);

-- 同班同指纹只有一行：pending 被批准就原地改状态，不新增
CREATE UNIQUE INDEX uq_device_grants ON device_grants(class_id, fingerprint);
CREATE INDEX idx_device_grants_status ON device_grants(class_id, status, created_at);

-- 按指纹作废旧设备时要扫这个索引
CREATE INDEX idx_devices_fingerprint ON devices(class_id, fingerprint);
