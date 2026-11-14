using ClassPoints.Core;
using ClassPoints.Data;

namespace ClassPoints.Tests;

/// <summary>
/// 名单软删除的语义。
///
/// 「停用」= 软删除（deleted_at 置位），不是真的删行 —— 同步下硬删会让另一台设备
/// 把旧数据推回来，转学生就复活了。所以停用之后必须还能被找到：
///   · ListAll()            不含已删除（名单页列表、榜单、打印都靠它）
///   · ListAll(true)        含已删除（回收站靠它）
///
/// 之前名单页两处都用了无参的 ListAll()，于是「停用」之后学生既不在名单里、
/// 也不在回收站里，界面上就是"人没了，也恢复不了"。这个用例钉住两个重载的差别。
/// </summary>
internal static class StudentListTests
{
    public static void Run(Action<string, bool, string> report)
    {
        void Check(string name, bool ok, string detail = "") => report(name, ok, detail);

        var dir = Path.Combine(Path.GetTempPath(), "cp-students-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);

        try
        {
            var db = new Database(Path.Combine(dir, "t.db"));
            db.Initialize();

            var students = new StudentRepository(db);
            var a = students.Insert(new Student { Name = "王小明", SeatNo = 1 });
            var b = students.Insert(new Student { Name = "李思远", SeatNo = 2 });

            students.SoftDelete(b.Id);

            var visible = students.ListAll();
            var all = students.ListAll(includeDeleted: true);

            Check("停用的人不在名单列表里", visible.Count == 1 && visible[0].Id == a.Id,
                visible.Count.ToString());
            Check("停用的人仍在全量列表里（回收站靠它）",
                all.Count == 2 && all.Any(s => s.Id == b.Id));
            Check("停用的人 IsDeleted 为真",
                all.First(s => s.Id == b.Id).IsDeleted);
            Check("停用的人 Active 为假（不占座位、不进榜单）",
                !all.First(s => s.Id == b.Id).Active);

            // 另一条容易写错的规则：软删除的同步载荷必须是 delete，而不是 upsert，
            // 否则云端会把这条又当成"新增/修改"推回别的设备。
            // 注意要在 Restore 之前查：outbox 是按 id 覆盖的，恢复会把这条改成 restore。
            var payload = new SyncOutbox(db).Due(100)
                .FirstOrDefault(i => i.Id == $"students:{b.Id}")?.Payload ?? "";
            Check("停用的同步载荷是 delete",
                payload.Contains("\"action\":\"delete\"", StringComparison.Ordinal), payload);

            students.Restore(b.Id);
            Check("恢复后回到名单", students.ListAll().Count == 2);
            Check("恢复后不再算已删除",
                !students.ListAll(includeDeleted: true).First(s => s.Id == b.Id).IsDeleted);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (Exception) { }
        }
    }
}
