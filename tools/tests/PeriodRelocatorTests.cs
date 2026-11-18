using ClassPoints.Core;
using ClassPoints.Data;
using ClassPoints.Data.Sync;
using Microsoft.Data.Sqlite;

namespace ClassPoints.Tests;

/// <summary>
/// 周期换编号（<see cref="PeriodRelocator"/>）。
///
/// 这件事的现场形态：老师换过班之后，本机周期的 id 在云端已经属于上一个班，
/// 推它永远被拒（409 foreign_row），引用它的打点一直 unknown_period ——
/// 设置页上永远挂着「云端还没有这个周期，等它同步上去后这批计数会自动补推」，
/// 而那批计数永远等不到。
///
/// 换编号本身不难，难在"不能有孤儿"和"队列里的旧 id 也要跟着换"，
/// 所以这里逐样钉住：周期行、流水、打点、考试、队列载荷、旧的推送条目。
/// </summary>
internal static class PeriodRelocatorTests
{
    public static void Run(Action<string, bool, string> check)
    {
        RelocateCarriesEverything(db =>
        {
            const string oldId = "p_2026-AUT";
            var now = Clock.NowMs();

            Seed(db, oldId, "active", now);

            var newId = new PeriodRelocator(db).Relocate(oldId);
            check("换编号成功并返回新 id", !string.IsNullOrEmpty(newId) && newId != oldId, newId ?? "null");

            using var conn = db.Open();

            var row = Read(conn, "SELECT id, name, term_key, status FROM periods", r =>
                (r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3)));
            check("本地只剩一行周期，且换了新 id", row is { Length: 1 } && row[0].Item1 == newId, row?.Length.ToString() ?? "null");
            check("周期名与归档桶原样保留", row is { Length: 1 } && row[0].Item2 == "第 1 周期" && row[0].Item3 == "2026-AUT",
                row is { Length: 1 } ? row[0].Item2 + "/" + row[0].Item3 : "");
            check("当前周期仍然是当前周期", row is { Length: 1 } && row[0].Item4 == "active",
                row is { Length: 1 } ? row[0].Item4 : "");

            check("流水改指到新周期",
                Convert.ToInt64(Database.Scalar(conn, "SELECT COUNT(*) FROM entries WHERE period_id = $id", ("$id", newId)) ?? 0L) == 1,
                "entries");
            check("事件打点改指到新周期",
                Convert.ToInt64(Database.Scalar(conn, "SELECT COUNT(*) FROM counter_ticks WHERE period_id = $id", ("$id", newId)) ?? 0L) == 1,
                "ticks");
            check("考试改指到新周期",
                Convert.ToInt64(Database.Scalar(conn, "SELECT COUNT(*) FROM exams WHERE period_id = $id", ("$id", newId)) ?? 0L) == 1,
                "exams");
            check("没有一行还指着旧周期",
                Convert.ToInt64(Database.Scalar(conn,
                    "SELECT (SELECT COUNT(*) FROM entries WHERE period_id = $old) + (SELECT COUNT(*) FROM counter_ticks WHERE period_id = $old) + (SELECT COUNT(*) FROM exams WHERE period_id = $old)",
                    ("$old", oldId)) ?? 0L) == 0,
                "orphans");

            // 队列：旧周期的推送条目该消失（推上去只会再被拒），载荷里的 periodId 该跟着换
            check("队列里不再有旧周期的推送条目",
                Convert.ToInt64(Database.Scalar(conn, "SELECT COUNT(*) FROM outbox WHERE id = $id",
                    ("$id", "periods:" + oldId)) ?? 0L) == 0,
                "outbox period row");

            var payload = Database.Scalar(conn, "SELECT payload FROM outbox WHERE id = $id", ("$id", "tick-1")) as string ?? "";
            check("打点载荷里的周期编号也换了", payload.Contains(newId!, StringComparison.Ordinal) && !payload.Contains(oldId, StringComparison.Ordinal),
                payload);

            var future = Convert.ToInt64(Database.Scalar(conn, "SELECT COUNT(*) FROM outbox WHERE next_at > $now",
                ("$now", Clock.NowMs())) ?? 0L);
            check("换完编号后队列立刻可推（退避被清掉）", future == 0, "still backing off: " + future);

            check("旧编号再换一次返回 null（已经不存在）", new PeriodRelocator(db).Relocate(oldId) is null, "second relocate");
            check("新编号自己也换得动（没有留下坏行）", new PeriodRelocator(db).Relocate(newId!) is not null, "relocate new");
        });

        // 归档周期不能被顺手扶成当前周期：uq_period_active 只许一行，扶错了整台机器就没有历史了
        RelocateKeepsArchived(db =>
        {
            const string oldId = "p_2026-SPRING";
            Seed(db, oldId, "archived", Clock.NowMs());

            var newId = new PeriodRelocator(db).Relocate(oldId);
            check("归档周期换编号后仍是归档", newId is not null && StatusOf(db, newId!) == "archived", StatusOf(db, newId!));
            check("换完之后没有多余的当前周期", CountActive(db) == 0, "active=" + CountActive(db));
        });
    }

    private static void RelocateCarriesEverything(Action<Database> body)
    {
        var dir = Path.Combine(Path.GetTempPath(), "cp-reloc-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var db = new Database(Path.Combine(dir, "classpoints.db"));
            db.Initialize();
            body(db);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (Exception) { }
        }
    }

    private static void RelocateKeepsArchived(Action<Database> body) => RelocateCarriesEverything(body);

    private static void Seed(Database db, string periodId, string status, long now)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();

        // Initialize 会种一条出厂周期；这里要的是完全受控的一行
        Database.Exec(conn, tx, "DELETE FROM periods");

        Database.Exec(conn, tx, """
            INSERT INTO periods (id, name, term_key, start_at, end_at, status, created_at)
            VALUES ($id, '第 1 周期', '2026-AUT', $start, NULL, $status, $start)
            """, ("$id", periodId), ("$start", now), ("$status", status));

        Database.Exec(conn, tx,
            "INSERT INTO students (id, name, active, rev, updated_at, created_at) VALUES ('s1','学生甲',1,1,$now,$now)",
            ("$now", now));

        Database.Exec(conn, tx, """
            INSERT INTO entries (id, batch_id, period_id, student_id, rule_id, rule_label, delta, source,
                                 operator_id, operator_name, occurred_at, created_at)
            VALUES ('e1','b1',$period,'s1','r1','回答问题',5,'rule','m1','老师',$now,$now)
            """, ("$period", periodId), ("$now", now));

        Database.Exec(conn, tx,
            "INSERT INTO counters (id, label, sort, rev, updated_at) VALUES ('c_test_late','迟到',0,1,$now)",
            ("$now", now));
        Database.Exec(conn, tx, """
            INSERT INTO counter_ticks (id, counter_id, student_id, period_id, value, operator_id, operator_name, occurred_at, created_at)
            VALUES ('t1','c_test_late','s1',$period,1,'m1','老师',$now,$now)
            """, ("$period", periodId), ("$now", now));

        Database.Exec(conn, tx, """
            INSERT INTO exams (id, period_id, name, full_score, imported_at)
            VALUES ('x1',$period,'期中',100,$now)
            """, ("$period", periodId), ("$now", now));

        // 队列里两样东西：一条注定被拒的周期推送，一条引用旧编号的打点（已经退避过一次）
        Database.Exec(conn, tx, """
            INSERT INTO outbox (id, kind, payload, attempts, next_at, created_at)
            VALUES ($id, 'period', $payload, 2, $future, $now)
            """,
            ("$id", "periods:" + periodId),
            ("$payload", "{\"action\":\"upsert\",\"period\":{\"id\":\"" + periodId + "\",\"name\":\"第 1 周期\"}}"),
            ("$future", now + 300_000), ("$now", now));

        Database.Exec(conn, tx, """
            INSERT INTO outbox (id, kind, payload, attempts, next_at, created_at)
            VALUES ('tick-1', 'tick', $payload, 3, $future, $now)
            """,
            ("$payload", "{\"action\":\"tick\",\"tick\":{\"id\":\"t1\",\"periodId\":\"" + periodId + "\",\"value\":1}}"),
            ("$future", now + 300_000), ("$now", now));

        tx.Commit();
    }

    private static (string, string, string, string)[] Read(
        SqliteConnection conn, string sql, Func<SqliteDataReader, (string, string, string, string)> map)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        using var r = cmd.ExecuteReader();
        var list = new List<(string, string, string, string)>();
        while (r.Read()) list.Add(map(r));
        return list.ToArray();
    }

    private static string StatusOf(Database db, string id)
    {
        using var conn = db.Open();
        return Database.Scalar(conn, "SELECT status FROM periods WHERE id = $id", ("$id", id)) as string ?? "";
    }

    private static long CountActive(Database db)
    {
        using var conn = db.Open();
        return Convert.ToInt64(Database.Scalar(conn, "SELECT COUNT(*) FROM periods WHERE status = 'active'") ?? 0L);
    }
}
