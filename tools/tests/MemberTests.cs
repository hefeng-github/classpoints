using ClassPoints.Core;
using ClassPoints.Data;

namespace ClassPoints.Tests;

/// <summary>
/// 「一个班只有一个班主任」。
///
/// 现场症状：多台设备加入同一个班之后，认人列表里出现好几个「班主任」。
/// 成因是旧版电脑端开机取操作人时，若本机成员表还没有班主任行（新配对的机器
/// 在第一轮拉取之前就是这样），会自建一行「班主任」推上云端；两台设备先后
/// 触发，云端就有两个 owner，再同步回所有设备。成员行没有彻底删除入口，
/// 多出来的那个不会自己消失。
///
/// 这里钉住三道闸：Save 不再新建第二个、Owner() 只认最早那行、v9 迁移把
/// 历史遗留的重复行软删掉。
/// </summary>
internal static class MemberTests
{
    public static void Run(Action<string, bool, string> report)
    {
        void Check(string name, bool ok, string detail = "") => report(name, ok, detail);

        var dir = Path.Combine(Path.GetTempPath(), "cp-members-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);

        try
        {
            var db = new Database(Path.Combine(dir, "t.db"));
            db.Initialize();

            var members = new MemberRepository(db);
            var first = members.Save(new Member { DisplayName = "王老师", Role = MemberRole.Owner }, pin: null);
            var second = members.Save(new Member { DisplayName = "班主任", Role = MemberRole.Owner }, pin: null);

            Check("第二个班主任不会新建一行",
                second.Id == first.Id && members.ListAll().Count(m => m.Role == MemberRole.Owner) == 1,
                $"first={first.Id} second={second.Id} owners={members.ListAll().Count(m => m.Role == MemberRole.Owner)}");

            // 历史遗留：绕过 Save 直接塞两行 owner（旧版本推上来的那种数据）
            using (var conn = db.Open())
            {
                Database.Exec(conn, """
                    INSERT INTO members (id, display_name, role, pin_hash, created_at, rev, updated_at)
                    VALUES ($id, '班主任', 'owner', NULL, $created, 1, $created)
                    """, ("$id", "m-late-owner"), ("$created", first.CreatedAt + 60_000));
            }

            var all = members.ListAll();
            Check("两个人都在时 Owner() 只认最早那行",
                members.Owner()?.Id == first.Id,
                $"owner={members.Owner()?.Id} first={first.Id} count={all.Count}");

            // 把版本退回 8，再 Initialize 一次 —— 模拟老库升级到 v9
            using (var conn = db.Open())
            {
                Database.Exec(conn, "PRAGMA user_version = 8");
            }
            db.Initialize();

            var owners = members.ListAll().Count(m => m.Role == MemberRole.Owner);
            var kept = members.Owner();
            var late = members.Get("m-late-owner");

            Check("v9 迁移后只剩一个班主任", owners == 1, $"owners={owners}");
            Check("v9 迁移保留的是最早那行", kept?.Id == first.Id, $"kept={kept?.Id} first={first.Id}");
            Check("v9 迁移只软删、不删行（卡还挂着外键）",
                late is not null && late.DeletedAt is not null, $"late={late?.Id} deletedAt={late?.DeletedAt}");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
