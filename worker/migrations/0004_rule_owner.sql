-- 加分项可以只属于某个成员（记分台上的「我的快捷项」）。
--
-- 客户端 rules 表新增 owner_id，NULL 表示全班共用 —— 老数据不需要回填，
-- 补一列就够。服务端要跟着加，否则同步回传的行里没有这一列，
-- 换一台设备拉下来，别人的私有项会变成"全班的"（或者干脆丢掉归属）。

ALTER TABLE rules ADD COLUMN owner_id TEXT;

CREATE INDEX idx_rules_owner ON rules(class_id, owner_id);
