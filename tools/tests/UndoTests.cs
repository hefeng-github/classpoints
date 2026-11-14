using ClassPoints.Core;
using ClassPoints.Data;

namespace ClassPoints.Tests;

/// <summary>
/// 撤销的回归测试。
///
/// 记分台「撤销上一步」曾经 100% 失败：SQLite Error 5 database is locked。
/// 原因是 TryPhysicalUndo 在事务还没提交时又开了一条连接写审计 ——
/// 第二条连接等第一条释放写锁，第一条正卡在等审计返回，5 秒后一起失败。
///
/// 这个用例不测"撤销能不能用"，只钉住"一个事务里只许有一条写连接"这条规则：
/// 只要有人再把 audit.Add（不带连接的那个重载）塞进事务里，这里就会红。
/// </summary>
internal static class UndoTests
{
    public static void Run(Action<string, bool, string> report)
    {
        void Check(string name, bool ok, string detail = "") => report(name, ok, detail);

        var dir = Path.Combine(Path.GetTempPath(), "cp-undo-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);

        try
        {
            var db = new Database(Path.Combine(dir, "t.db"));
            db.Initialize();

            var audit = new AuditRepository(db);
            var entries = new EntryService(db, audit);
            var periods = new PeriodRepository(db);
            var students = new StudentRepository(db);
            var rules = new RuleRepository(db);

            var period = periods.Active()!;
            var kid = students.Insert(new Student { Name = "王小明", SeatNo = 1 });
            var rule = rules.ListAll().First(r => r.Delta > 0);
            var op = new Member { Id = "m_owner", DisplayName = "班主任", Role = MemberRole.Owner };

            var outcome = entries.ApplyBatch(
                [new AwardLine(kid.Id, rule, rule.Delta)], op, "pin", period.Id);
            Check("入账成功", outcome.Count == 1, outcome.Count.ToString());

            var threw = "";
            var undone = false;
            try
            {
                undone = entries.TryPhysicalUndo(outcome.BatchId, Clock.NowMs());
            }
            catch (Exception ex)
            {
                threw = ex.GetType().Name + ": " + ex.Message;
            }

            Check("撤销未推送批次不再撞库锁", threw.Length == 0, threw);
            Check("撤销真的生效", undone, undone.ToString());
            Check("记录被物理删除", entries.Entries(period.Id, 50).Count(e => e.BatchId == outcome.BatchId) == 0,
                entries.Entries(period.Id, 50).Count.ToString());
            Check("审计里留下一条 undo_pending",
                audit.Recent(20).Any(a => a.Action == "undo_pending" && a.Target == outcome.BatchId));

            // 超过 30 秒的批次走冲销行，不该被物理删除 —— 顺带确认这条分支没被改坏
            var late = entries.ApplyBatch([new AwardLine(kid.Id, rule, rule.Delta)], op, "pin", period.Id);
            Check("超时批次不做物理撤销",
                !entries.TryPhysicalUndo(late.BatchId, Clock.NowMs() + 60_000));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (Exception) { }
        }
    }
}
