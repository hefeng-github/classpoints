using ClassPoints.Core;
using ClassPoints.Data;
using ClassPoints.Data.Sync;
using Microsoft.Data.Sqlite;

namespace ClassPoints.Tests;

/// <summary>
/// 预设事件换编号 / 并入云端那一条（<see cref="CounterRelocator"/>）。
///
/// 现场形态：出厂三个事件（迟到、作业未交、课堂违纪）的 id 是写死的，而云端 `counters.id`
/// 是全局主键 —— 换过班的机器上，这三个 id 早被旧班占着，推给新班永远被拒，
/// 引用它们的打点在设置页上永远挂着「云端还没有这个事件，等它同步上去后这批计数会自动补推」。
///
/// 两条路都要钉住：
///   ① 换号：行还在、preset_key 还在（否则出厂种子会再种一份）、打点与队列载荷跟着改；
///   ② 并入：本机已经有云端那条 → 打点并过去、不留重复。
/// 另外还要钉住"外键开着也能跑"——`counter_ticks.counter_id` 有 REFERENCES，
/// 直接 UPDATE 主键会抛 SQLite Error 19，这条正是踩过的坑。
/// </summary>
internal static class CounterRelocatorTests
{
    public static void Run(Action<string, bool, string> check)
    {
        Relocate(check, (db, now) =>
        {
            const string oldId = "c_late";
            Seed(db, oldId, now);

            var newId = new CounterRelocator(db).Relocate(oldId);
            check("事件换号成功并返回新 id", !string.IsNullOrEmpty(newId) && newId != oldId, newId ?? "null");

            using var conn = db.Open();
            check("旧编号那一行不在了",
                Convert.ToInt64(Database.Scalar(conn, "SELECT COUNT(*) FROM counters WHERE id = $id",
                    ("$id", oldId)) ?? 0L) == 0, "old row");
            var kept = Database.Scalar(conn, "SELECT preset_key FROM counters WHERE id = $id",
                ("$id", newId)) as string ?? "";
            check("preset_key 保住了（出厂种子不会再种一份）", kept == "std.late", kept);
            check("标签也原样带过来",
                (Database.Scalar(conn, "SELECT label FROM counters WHERE id = $id", ("$id", newId)) as string ?? "") == "迟到",
                "label");
            check("这个班仍然只有一条「迟到」",
                Convert.ToInt64(Database.Scalar(conn,
                    "SELECT COUNT(*) FROM counters WHERE preset_key = 'std.late'") ?? 0L) == 1, "dup");

            check("打点改指到新编号",
                Convert.ToInt64(Database.Scalar(conn,
                    "SELECT COUNT(*) FROM counter_ticks WHERE counter_id = $id", ("$id", newId)) ?? 0L) == 1,
                "ticks");
            check("没有打点还指着旧编号",
                Convert.ToInt64(Database.Scalar(conn,
                    "SELECT COUNT(*) FROM counter_ticks WHERE counter_id = $id", ("$id", oldId)) ?? 0L) == 0,
                "orphan ticks");

            var payload = Database.Scalar(conn, "SELECT payload FROM outbox WHERE id = 'tick-1'") as string ?? "";
            // 新编号本身以旧编号开头（c_late.xxxx），所以不能只判"含不含旧串"，
            // 要看整个字段值 —— 否则这条断言永远假绿。
            check("队列载荷里的 counterId 也换了",
                payload.Contains($"\"counterId\":\"{newId}\"", StringComparison.Ordinal) &&
                !payload.Contains($"\"counterId\":\"{oldId}\"", StringComparison.Ordinal),
                payload);
            check("旧事件那条推送条目被撤掉（推上去只会再被拒）",
                Convert.ToInt64(Database.Scalar(conn, "SELECT COUNT(*) FROM outbox WHERE id = 'counters:c_late'") ?? 0L) == 0,
                "outbox counter row");
            check("换完之后队列立刻可推（退避清零）",
                Convert.ToInt64(Database.Scalar(conn, "SELECT COUNT(*) FROM outbox WHERE next_at > $now",
                    ("$now", Clock.NowMs())) ?? 0L) == 0, "backoff");
        });

        // 云端说"这个班已经有同类事件了，用那个编号"：本机还没有那条 → 直接搬过去
        Relocate(check, (db, now) =>
        {
            Seed(db, "c_late", now);
            var adopted = new CounterRelocator(db).Adopt("c_late", "c_late.remote001");

            using var conn = db.Open();
            check("并入云端那条时返回它", adopted == "c_late.remote001", adopted ?? "null");
            check("并入后只剩一条「迟到」",
                Convert.ToInt64(Database.Scalar(conn,
                    "SELECT COUNT(*) FROM counters WHERE preset_key = 'std.late'") ?? 0L) == 1, "dup");
            check("并入后打点指向云端那条",
                Convert.ToInt64(Database.Scalar(conn,
                    "SELECT COUNT(*) FROM counter_ticks WHERE counter_id = 'c_late.remote001'") ?? 0L) == 1,
                "ticks");
        });

        // 本机已经有云端那条（另一端推下来过）：打点并过去，删掉重复的
        Relocate(check, (db, now) =>
        {
            Seed(db, "c_late", now);
            using (var conn0 = db.Open())
            using (var tx0 = conn0.BeginTransaction())
            {
                Database.Exec(conn0, tx0, """
                    INSERT INTO counters (id, label, sort, preset_key, rev, updated_at)
                    VALUES ('c_late.remote002', '迟到', 0, NULL, 1, $now)
                    """, ("$now", now));
                tx0.Commit();
            }

            var adopted = new CounterRelocator(db).Adopt("c_late", "c_late.remote002");
            using var conn = db.Open();
            check("并进已存在的那条时返回它", adopted == "c_late.remote002", adopted ?? "null");
            check("重复的那条被删掉（班内只剩一份）",
                Convert.ToInt64(Database.Scalar(conn,
                    "SELECT COUNT(*) FROM counters WHERE id IN ('c_late','c_late.remote002')") ?? 0L) == 1,
                "rows");
            check("打点还在，且指向留下的那条",
                Convert.ToInt64(Database.Scalar(conn,
                    "SELECT COUNT(*) FROM counter_ticks WHERE counter_id = 'c_late.remote002'") ?? 0L) == 1,
                "ticks");
            check("preset_key 仍然只有一份",
                Convert.ToInt64(Database.Scalar(conn,
                    "SELECT COUNT(*) FROM counters WHERE preset_key = 'std.late'") ?? 0L) <= 1, "preset");
        });

        // 本地没有这个事件：不该凭空造一条出来
        Relocate(check, (db, now) =>
        {
            Seed(db, "c_late", now);
            check("换一个不存在的事件返回 null", new CounterRelocator(db).Relocate("c_nope") is null, "relocate");
            check("并入一个不存在的事件返回 null",
                new CounterRelocator(db).Adopt("c_nope", "c_x") is null, "adopt");
        });
    }

    private static void Relocate(Action<string, bool, string> check, Action<Database, long> body)
    {
        var dir = Path.Combine(Path.GetTempPath(), "cp-counter-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var db = new Database(Path.Combine(dir, "classpoints.db"));
            db.Initialize();
            body(db, Clock.NowMs());
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (Exception) { }
        }
    }

    /// <summary>一条周期 + 一个学生 + 一条打点 + 两条队列记录（一条事件推送、一条打点）。</summary>
    private static void Seed(Database db, string counterId, long now)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();

        Database.Exec(conn, tx, "DELETE FROM counter_ticks; DELETE FROM outbox;");

        Database.Exec(conn, tx, """
            INSERT INTO students (id, name, active, rev, updated_at, created_at)
            VALUES ('s1','学生甲',1,1,$now,$now)
            ON CONFLICT(id) DO NOTHING
            """, ("$now", now));

        Database.Exec(conn, tx, """
            INSERT INTO counter_ticks (id, counter_id, student_id, period_id, value, operator_id, operator_name, occurred_at, created_at)
            VALUES ('t1',$counter,'s1',(SELECT id FROM periods LIMIT 1),1,'m1','老师',$now,$now)
            """, ("$counter", counterId), ("$now", now));

        Database.Exec(conn, tx, """
            INSERT INTO outbox (id, kind, payload, attempts, next_at, created_at)
            VALUES ($id, 'counter', $payload, 2, $future, $now)
            """,
            ("$id", "counters:" + counterId),
            ("$payload", "{\"action\":\"upsert\",\"counter\":{\"id\":\"" + counterId + "\",\"label\":\"迟到\",\"presetKey\":\"std.late\"}}"),
            ("$future", now + 300_000), ("$now", now));

        Database.Exec(conn, tx, """
            INSERT INTO outbox (id, kind, payload, attempts, next_at, created_at)
            VALUES ('tick-1', 'tick', $payload, 3, $future, $now)
            """,
            ("$payload", "{\"action\":\"tick\",\"tick\":{\"id\":\"t1\",\"counterId\":\"" + counterId + "\",\"value\":1}}"),
            ("$future", now + 300_000), ("$now", now));

        tx.Commit();
    }
}
