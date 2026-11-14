# 同步中枢 · Cloudflare Worker + D1

这一层只做四件事：**鉴权、校验、幂等写入、增量查询**。不排名、不折算、不做任何业务计算 —— 计算全留在客户端，
所以换算法不用动服务端，服务端挂了也不影响教室里加分。

## 一、它长什么样

```
客户端 A ──┐                        ┌── D1: classes / members / devices
           ├─ HTTPS + Bearer ── Worker ── students / entries / periods
客户端 B ──┘                        └── exams / exam_scores / rules / schemes
                                        + changelog（增量游标）
```

- **鉴权**：`POST /classes` 建班返回 access（12 小时）+ refresh（90 天）；`POST /pair` 用班级码 + 口令配对设备。
  令牌是 HMAC-SHA256 签的紧凑 JWT，密码用 PBKDF2。
- **PBKDF2 迭代数封顶 10 万**：这是 Workers 运行时的硬上限（超过抛 `NotSupportedError`，
  任何套餐都不放宽），不是性能调参。**本地 workerd 不检查这个上限**，所以写大了不会在本地报错，
  只会在部署后第一下建班时 500。客户端 `PasswordHasher.Iterations` 必须同步顶在 10 万，
  否则它生成的成员口令哈希服务端算不出来。
- **角色**：`/pair` 试三种口令 —— 班主任密码、恢复码、成员自己的口令；最后一种签出该成员的真实角色
  （`owner` / `co_owner` / `officer`）。写接口按角色分两档：记分、撤销、读取谁都能做，
  名单 / 规则 / 成员 / 周期 / 考试 / 折算方案只有班主任和班长（`requireManager`）。
  角色跟着成员行走：成员被删、被撤销或改了角色，他那台设备下一次续期就跟着变。
- **只读票**：设备在审核模式下还没被批准时，签出的令牌带 `ro: true`，只放行读接口
  （`/sync` `/ping`），其余写接口 403 `read_only_device`。它不记失败次数 —— 那不是猜密码，
  是"还没点通过"。
- **幂等**：所有写操作以客户端生成的 UUIDv7 为主键，重复提交只更新不重复插入。
- **并发**：每张表带 `rev`，客户端带着读到的 `rev` 写，服务端 `rev = rev + 1` 后比对，落后就 409。
- **增量**：任何写入都往 `changelog` 追加一条，`GET /sync?since=<seq>` 按游标返回，客户端记 `next`。
- **周期重置**：`POST /periods/reset` 两阶段（prepare → commit），重置期间普通写入返回 423，
  免得一台设备的写入落到已经归档的周期里。

## 二、本地跑

```bash
cd worker
npm install
cp .dev.vars.example .dev.vars     # 第一次跑才需要，密钥不进仓库
npx wrangler d1 migrations apply classpoints --local
npx wrangler dev --port 8787 --local
```

`.dev.vars` 只对 `--local` 生效，不要拿去部署；它已经在 `.gitignore` 里，
仓库里只有一份 `.dev.vars.example`。想换一个密钥就改这一行，或者：

```bash
node -e "console.log('TOKEN_SECRET = ' + require('crypto').randomBytes(32).toString('hex'))" > .dev.vars
```

跑契约测试（66 项，覆盖建班、配对、幂等重传、并发冲突、周期重置、撤销、越权、**多班隔离**、安全头与限流）：

```bash
npm run typecheck
npm run test:smoke
```

本地 D1 是持久化的，反复跑会累积数据。想从干净库开始：

```bash
npm run db:reset      # 把 .wrangler/state 挪走并重新应用迁移
```

## 三、部署到 Cloudflare

```bash
# 1. 登录
npx wrangler login

# 2. 建 D1 库，把输出的 database_id 填进自己的部署配置
npx wrangler d1 create classpoints

# 3. 建表（远端）
npx wrangler d1 migrations apply classpoints --remote --config wrangler.deploy.toml

# 4. 设令牌签名密钥（32 字节以上随机串，自己生成一个存好）
npx wrangler secret put TOKEN_SECRET

# 4.1 设建班口令（公网部署必做；不设就只剩数量上限和限流兜着）
npx wrangler secret put BOOTSTRAP_SECRET

# 5. 发布
npx wrangler deploy --config wrangler.deploy.toml
```

`wrangler.toml` 是脱敏版（`database_id` 是占位符、路由注释掉了），给读代码的人看结构用。
真正部署要一份本机的 `wrangler.deploy.toml`：复制 `wrangler.toml`、填真 id、解注 `[[routes]]`，
并且**把已经绑定的每条自定义域都写上** —— deploy 会用配置里的 routes 覆盖线上绑定。
这份文件已在 `.gitignore` 里。

发布后会得到一个 `https://classpoints-api.<你的账号>.workers.dev` 地址，填到电脑端
`设置 → 同步 → 服务器地址` 即可。第一台设备点「在这台电脑上建班」，拿到 8 位班级码给其它设备配对。
建班时如果服务端设了 `BOOTSTRAP_SECRET`，同一个界面上的「部署口令」要填它，否则返回 403。

### 一定要绑自有域名

`*.workers.dev` 在国内网络下经常直接不通（代理 `CONNECT` 返回 502），表现成「同步一直失败」，
而开发机上因为有代理完全看不出来。在 `wrangler.toml` 里挂一条路由就行：

```toml
[[routes]]
pattern = "classpoints.你的域名.com"
custom_domain = true
```

前提是这个域名的 DNS 已经托管在同一家 Cloudflare 账号下 —— 不然 wrangler 找不到 zone，
`deploy` 会失败。`custom_domain = true` 会自动建 DNS 记录、自动签证书，不用手动去 DNS 面板加。

本项目部署时用的就是自有域名。`classpoints.你的域名.com` 这种写法要自己替换 ——
**换域名时保留旧路由一段时间**，比让已经装好的机器手动改设置稳妥：
旧域名删掉的那一刻，所有填了它的机器会同时同步不了，而用户不会知道该去哪里改。

### 免费额度够不够

一个 45 人的班，一学期大约 3000 条流水，边写边同步：

| 资源 | 免费额度 | 实际用量 |
| --- | --- | --- |
| Workers 请求 | 10 万次/天 | 每天几百次（写操作合并成批，读按 30 秒节流） |
| D1 存储 | 5 GB | 一学期不到 20 MB |
| D1 读行 | 500 万行/天 | 增量拉取，每次只读变更 |

一个账号可以放几十个班，不必按班建库。**每张业务表都带 `class_id`**，令牌里带 `cid`，
所有读写都按 `claims.cid` 收口：

- `/sync` 只读 `WHERE class_id = ?` 的 changelog 与业务行；
- 通用实体的写路由把 `class_id` 写死成 `claims.cid`，`ON CONFLICT` 的 `DO UPDATE` 还带
  `AND 表.class_id = excluded.class_id`，跨班撞同一个主键 id 时宁可不落地；
- 唯一约束全是"班内唯一"（学号、常用评语、同期 active 周期、启用中的折算方案），
  两个班各自有 01 号学生、各自有一个当前周期，互不相干。

越权的结果是"看不见"而不是"报错"：拿到 A 班令牌去删 B 班的记录会返回 200 但改动 0 行，
去撤 B 班的批次会 404。冒烟测试第 9 节专门钉这一组行为。

> 历史说明：`0001_init.sql` 在**真实 Cloudflare 部署之前**重写过一次（从"中心化单班"
> 改成"全表带 `class_id`"）。那时远端 D1 还不存在，没有需要兼容的线上数据。
> 一旦上过线，改结构就必须新增 `0002_xxx.sql` 走 SQLite 的 12 步表重建，不能再动这个文件。

## 四、接口一览

| 方法 | 路径 | 说明 |
| --- | --- | --- |
| GET | `/api/v1/ping` | 探活；带令牌时验令牌链路（一体机休眠后 TCP 假活，先打这个） |
| POST | `/api/v1/classes` | 建班，返回班级码 + 双令牌（全程只调用一次） |
| POST | `/api/v1/pair` | 班级码 + 口令（班主任密码 / 恢复码 / 成员口令）配对设备，角色跟着口令走；待审设备拿到只读令牌 |
| POST | `/api/v1/refresh` | 换 access，refresh 一次一换；顺带复核成员还在不在、角色变没变 |
| POST | `/api/v1/signout` | 撤销本设备 refresh |
| GET | `/api/v1/sync?since=&limit=` | 增量拉取，返回各实体当前状态 + `deleted` + `next` + `readOnly` |
| POST | `/api/v1/entries` | 流水批量入账 1–50 条，可同批带冲销行 |
| POST | `/api/v1/entries/batch/:id/reverse` | 按批次整批撤销 |
| POST | `/api/v1/exams` | 一场考试 + 全部成绩（整批一个事务）🔒 |
| POST | `/api/v1/periods` | 周期 upsert 🔒 |
| POST | `/api/v1/periods/reset` | 两阶段重置（`request` / `renew` / `abort` / `commit`）🔒 |
| POST/PATCH | `/api/v1/:entity` | `students` / `rules` / `members` / `note_templates` / `schemes` 🔒 |
| POST | `/api/v1/schemes/:id/weights` | 换整张考试权重表，不是逐条改 🔒 |
| GET | `/admin` | 网页管理台（设备白名单）；HTML 页面，不走 `/api/v1` |
| POST | `/api/v1/admin/session` | 管理台登录：**只认班主任密码和恢复码**，成员口令一律 401 |
| GET | `/api/v1/admin/devices` | 待审 / 已通过 / 已拒绝三张清单 + 当前模式 🔒 |
| POST | `/api/v1/admin/devices` | 手动把一串特征码加进白名单（老师提前放行办公室那台）🔒 |
| POST | `/api/v1/admin/devices/:id/approve\|reject\|revoke` | 通过 / 拒绝 / 撤销 🔒 |
| DELETE | `/api/v1/admin/devices/:id` | 移出白名单（下次会重新进待审）🔒 |
| PATCH | `/api/v1/admin/settings` | 切 `joinMode`：`open` / `approval` 🔒 |

🔒 = 只给班主任和班长，班委令牌调用返回 `403 forbidden_role`。

### 设备白名单

班级有两种入班模式，存在 `classes.join_mode`：

- `open`（默认）—— 班级码 + 口令对了就进，老班不受影响。
- `approval` —— 口令对了还要过机器：客户端上报的**设备特征码**必须在白名单里是 `approved`。
  新机器第一次来只落一条 `pending` 记录，拿到的是**只读令牌**（`claims.ro = true`）：
  能 `/sync` 拉、能看，所有写接口一律 `403 read_only_device`，响应里 `readOnly: true`，
  话术里直接给出可抄的特征码。

特征码是客户端算的（`MachineGuid` + 机器名 → SHA-256 前 10 字节），20 位十六进制，
界面上显示成 4 位一段。它不是强身份凭证，伪造成本只比抄班级码高一点 ——
它的价值是把"谁进得来"变成一件需要班主任点头、并且留下记录的事。

三条容易写错的规则，都已经有用例钉住：

1. **开审核模式时，现役设备自动入白名单。** 那些机器本来就是班主任亲手配进来的，
   开个开关不该让教室里的设备陆续掉线。
2. **撤销 = 当场断续期。** 网页上点撤销会把该指纹下所有 `devices` 行置 `revoked_at`，
   那台机器下一次 `/refresh` 直接 `401 device_revoked`。已签发的 access 仍能用到过期（待审的只读票最长 30 分钟）。
3. **待审不算失败次数。** `/pair` 的口令已经验过了，等待批准不是猜密码 ——
   把待审记进 `auth_throttle`，学生点两次就会被封 15 分钟，而屏幕上只会显示"尝试次数太多"。

只读票的升级通道：`pending` 状态不撤销设备，`/refresh` 会继续发只读票；班主任点通过之后，
同一张 refresh 换出来的就是全权令牌，**客户端不用重新配对**。客户端也靠 `/sync` 响应里的 `readOnly`
对齐界面状态 —— 只读期间没有推送，续期不会被触发，拉取是唯一稳定的心跳。
撤销（`/entries/batch/:id/reverse`）故意不设门槛：本地规则允许班委 5 分钟内撤掉自己刚记的那批，
云端要是比本地严，就会出现"界面上撤了、账对不上"。

所有写接口的请求体形状：`{ action: "upsert" | "delete" | "restore", <实体名>: { ... } }`，
和客户端 `outbox.payload` 一字不差 —— 客户端把 payload 原样转发，不重新组装。

## 五、表结构

`migrations/0001_init.sql` 里 15 张表，关键约束：

- **每张业务表都带 `class_id TEXT NOT NULL`**，所有唯一约束都是班内唯一。
- `students` 上 `UNIQUE(class_id, student_no) WHERE student_no IS NOT NULL` ——
  学号在班里不能重（两个班各自的「01 号」互不影响）。同名同姓靠学号区分，系统不做姓名归一。
- `entries` 主键是服务端 `seq`，客户端 UUID 走 `UNIQUE(class_id, id)` 当幂等键；
  `batch_id` 保证一批加分原子可见；冲销行用 `reverses_entry_id` 指向原行，
  `UNIQUE(class_id, reverses_entry_id) WHERE reverses_entry_id IS NOT NULL` 保证同一批不会被撤两次。
- `periods` 上 `UNIQUE(class_id) WHERE status='active'` —— 一个班永远只有一个当前周期。
- `schemes` 上 `UNIQUE(class_id) WHERE is_active = 1` —— 一个班只有一个启用中的折算方案。
- `changelog(class_id, seq AUTOINCREMENT)` 是增量游标，`(class_id, entity, row_id)` 记录每次变更。
  `seq` 全局自增，但每个班只读 `class_id = ? AND seq > ?`，对它自己来说游标仍然连续单调。

`migrations/0002_security.sql` 只有一张表：`auth_throttle(scope, fp)` ——
防爆破的失败计数。`fp` 是 IP+UA 的加盐哈希（盐就是 `TOKEN_SECRET`），不是 IP 本身。
这张表不按班隔离：一个人换了班级码继续猜，也应该被同一个计数器挡住。

`migrations/0003_roles.sql` 给 `devices` 补了 `role` / `member_id`（令牌得说真话）；
`0004_rule_owner.sql` 给 `rules` 补 `owner_id`（记分台上的私有快捷项）；
`0005_device_grants.sql` 是白名单：`classes.join_mode`、`devices.fingerprint`，
外加 `device_grants(class_id, fingerprint)` 唯一索引 —— 同班同指纹只允许一行，
pending 被批准是原地改状态，不是新增。

## 六、注意

- 密码只在服务端做 PBKDF2，**明文不落库、不入日志**。迭代数顶在 10 万，别往上调（见上）。
- `TOKEN_SECRET` 换掉之后所有令牌立即失效，客户端会提示重新配对。
- 这一层故意不存「总分」。总分是客户端 `SUM(delta)` 现算的，服务端存分数就等于埋一个必然对不上的冗余字段。
- 5xx 一律只回 `{ error, requestId }`，原始报错（含表名、SQL 片段）只进 `wrangler tail`。
  拿着 `requestId` 去日志里搜同一条。
- 限流是"按来源"的，不是"按班级"的：同一个办公室 NAT 后面多台设备共用一个出口 IP，
  正常使用时远到不了阈值（配对 5 分钟 6 次、建班 1 小时 10 次、续期 10 分钟 30 次）。
