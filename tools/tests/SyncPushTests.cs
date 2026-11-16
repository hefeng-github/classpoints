using ClassPoints.Core;
using ClassPoints.Data;
using ClassPoints.Data.Sync;

namespace ClassPoints.Tests;

/// <summary>
/// 推送失败的分类，以及「推上去之后本地要跟着改什么」。
///
/// 起因（2026-11-16 报障「本机的数据一直待同步」）：队列里 9 条流水重试了 40 轮还在。
/// 真凶有两处：
/// ① 云端把「单条超过 50 分必须标 overCap」当硬规矩，而内置规则「课上黑板演题」就是 +100，
///    于是这批永远 422；
/// ② 客户端对所有非 2xx 一律退避重试，一条永远过不去的记录能把整批卡死，
///    界面上只有「待同步 N 条」，老师看不出是"网络不好"还是"这条根本不收"。
///
/// 这里钉住客户端这一半：哪些该等、哪些该丢、丢了以后本地状态怎么变。
/// </summary>
internal static class SyncPushTests
{
    public static void Run(Action<string, bool, string> report)
    {
        void Check(string name, bool ok, string detail = "") => report(name, ok, detail);

        // ---- 该不该重试：分界线是「等一等会不会变好」 ----
        Check("401 走续期", PushVerdicts.Classify(401, "bad_token") == PushVerdict.Auth);
        Check("周期还没同步上来要等（unknown_period）",
            PushVerdicts.Classify(422, "unknown_period") == PushVerdict.Retry);
        Check("学生还没同步上来要等（unknown_student）",
            PushVerdicts.Classify(422, "unknown_student") == PushVerdict.Retry);
        Check("身份不够要等（403，换回班主任能补推）",
            PushVerdicts.Classify(403, "read_only_device") == PushVerdict.Retry);
        Check("正在重置周期要等（423）", PushVerdicts.Classify(423, "period_reset") == PushVerdict.Retry);
        Check("服务端 500 要等", PushVerdicts.Classify(500, null) == PushVerdict.Retry);
        Check("限流 429 要等", PushVerdicts.Classify(429, null) == PushVerdict.Retry);
        Check("分值不合法是永久失败（422 delta_too_large）",
            PushVerdicts.Classify(422, "delta_too_large") == PushVerdict.Drop);
        Check("缺字段是永久失败（400 invalid_delta）",
            PushVerdicts.Classify(400, "invalid_delta") == PushVerdict.Drop);

        // ---- 出队之后本地必须知道「这条已经上云了」 ----
        var dir = Path.Combine(Path.GetTempPath(), "cp-push-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            RunMarkSynced(dir, Check);
        }
        finally
        {
            try { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); } catch (Exception) { }
            try { Directory.Delete(dir, recursive: true); } catch (Exception) { }
        }
    }

    private static void RunMarkSynced(string dir, Action<string, bool, string> check)
    {
        void Check(string name, bool ok, string detail = "") => check(name, ok, detail);

        var db = new Database(Path.Combine(dir, "push.db"));
        db.Initialize();
        var periodId = new PeriodRepository(db).Active()!.Id;

        using (var conn = db.Open())
            Database.Exec(conn, "INSERT INTO students (id, name, updated_at, created_at) VALUES ('s1', '张三', 1, 1)");

        var entries = new EntryService(db, new AuditRepository(db));
        var member = new Member { Id = "m1", DisplayName = "张老师", Role = MemberRole.Owner };
        var board = new Rule { Id = "r_board", Label = "课上黑板演题", Delta = 100 };

        var first = entries.ApplyBatch(new[] { new AwardLine("s1", board, 100) }, member, "desk", periodId);
        Check("刚记的流水算「还没上云」", entries.IsBatchPending(first.BatchId));
        Check("没上云时允许 30 秒内物理撤销", entries.TryPhysicalUndo(first.BatchId, Clock.NowMs() + 1000));
        Check("物理撤销之后流水真的没了", entries.BatchRows(first.BatchId).Count == 0);

        // 再记一批，模拟「推成功 → 出队 + 标记已上云」
        var second = entries.ApplyBatch(new[] { new AwardLine("s1", board, 100) }, member, "desk", periodId);
        var outbox = new SyncOutbox(db);
        Check("记完分就在待推送队列里", outbox.Count() == 1, outbox.Count().ToString());

        var ids = second.Entries.Select(e => e.Id).ToList();
        outbox.MarkSynced(ids);
        outbox.RemoveMany(ids);

        Check("推成功之后队列清空", outbox.Count() == 0, outbox.Count().ToString());
        Check("已上云的批次不再算待推送", !entries.IsBatchPending(second.BatchId));

        // 已经上云就不能物理撤销了 —— 本地删掉、云端还留着分，两台设备就对不上
        Check("已上云的批次拒绝物理撤销", !entries.TryPhysicalUndo(second.BatchId, Clock.NowMs() + 1000));
        Check("已上云的行还在本地", entries.BatchRows(second.BatchId).Count > 0);

        // 撤销改走冲销行：推的是 -100 那条，云端才认得出来
        var reversed = entries.ReverseBatch(second.BatchId, member, "desk");
        Check("撤销改走冲销行", reversed == 1, reversed.ToString());

        var after = entries.BatchRows(second.BatchId);
        Check("原行被标成已撤销", after.Count == 1 && after[0].IsReversed);

        // BatchRows 只取 reverses_entry_id IS NULL 的行（列表里不重复显示冲销），所以直接查库
        using (var conn = db.Open())
        {
            var delta = Database.Scalar(conn, "SELECT delta FROM entries WHERE reverses_entry_id = $id", ("$id", ids[0]));
            Check("冲销行是原值取反", delta is long d && d == -100, delta?.ToString() ?? "没有冲销行");

            var queued = Database.Scalar(conn, "SELECT kind FROM outbox WHERE id <> '' LIMIT 1");
            Check("冲销行进了待推送队列", queued is string k && k == "reverse", queued?.ToString() ?? "队列是空的");
        }
    }
}
