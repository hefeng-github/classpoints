-- 设备角色。
--
-- 0001 里的 devices 只记了"这是哪台机器"，没记"这台机器代表谁"。
-- 于是令牌里的 role 一直是写死的 'owner'：不管谁输的密码，签出来的都是班主任。
-- 这不是 bug 而是当时的取舍（班内分工靠客户端本地身份锁），但现在要在服务端
-- 校验权限，就得先让令牌说真话 —— 否则校验一上，所有班委直接全废。
--
-- 两列都给了默认值，所以**升级顺序无所谓**：已经配对过的设备刷新一次之后
-- 仍然是 owner（它们本来拿的就是班主任的密码），不会因为这次迁移被踢下线。

-- owner / co_owner / officer，和客户端 MemberRoleNames.ToDb() 是同一套字面量
ALTER TABLE devices ADD COLUMN role TEXT NOT NULL DEFAULT 'owner';

-- 这行 devices 是哪个成员刷出来的。空 = 用班主任密码/恢复码配的，没有对应成员。
-- 记它不是为了展示，是为了成员被撤销时能顺着找到该断哪几台设备。
ALTER TABLE devices ADD COLUMN member_id TEXT;

CREATE INDEX idx_devices_member ON devices(class_id, member_id);
