using ClassPoints.Core;
using ClassPoints.Data;

namespace ClassPoints.Tests;

/// <summary>
/// 彻底删除（<see cref="PurgeService"/>）。
///
/// 归档是"不用了但留着"，彻底删除是"不要再看到它"。两者只差一个字，后果差很远：
/// 删错了没法恢复，删漏了别的设备会把它拉回来。所以这里逐条钉住四件事：
///   ① 行真的没了，子表跟着走（打点依附事件，父行没了子行毫无意义）；
///   ② 历史流水不受影响（规则名是冗余存的，删规则不该抹掉几百条记分）；
///   ③ 队列里那条还没推的 upsert 要清掉 —— 否则删完立刻推上去，等于又造出来一份；
///   ④ 入队一条 purge，云端据此物理删 + 写墓碑。
/// </summary>
internal static class PurgeTests
{
    public static void Run(Action<string, bool, string> check)
    {
        WithDb((db, now) =>
        {
            var rule = SeedRule(db, "r_archived", "课堂发言", now);
            SeedEntry(db, "e_rule", "r_archived", "课堂发言", now);

            check("删之前规则在库里", Count(db, "rules", "id = 'r_archived'") == 1, "");
            var removed = new PurgeService(db).Purge("rule", rule);

            check("规则被真删掉（不是打标记）", Count(db, "rules", "id = 'r_archived'") == 0, "");
            check("返回删掉的行数", removed == 1, removed.ToString());

            // 规则名是冗余存在 entries.rule_label 里的：删规则不能把历史记分一起带走，
            // 否则家长问"这 5 分怎么来的"就查不到了。
            check("历史记分还在，且仍然显示原来的名字",
                Count(db, "entries", "id = 'e_rule' AND rule_label = '课堂发言'") == 1, "");

            var payload = Payload(db, "purges:rule:r_archived");
            check("入队了一条 purge",
                payload.Contains("\"kind\":\"rule\"", StringComparison.Ordinal) &&
                payload.Contains("\"id\":\"r_archived\"", StringComparison.Ordinal),
                payload);
        });

        // 事件：打点依附它，必须一起删；数量要能提前告诉老师
        WithDb((db, now) =>
        {
            var counter = SeedCounter(db, "c_archived", now);
            SeedTick(db, "t1", "c_archived", now);
            SeedTick(db, "t2", "c_archived", now);
            SeedTick(db, "t3", "c_other", now);

            var service = new PurgeService(db);
            check("先能算出会连带删掉几次", service.CascadeCount("counter", "c_archived") == 2,
                service.CascadeCount("counter", "c_archived").ToString());

            service.Purge("counter", "c_archived");

            check("事件本身没了", Count(db, "counters", "id = 'c_archived'") == 0, "");
            check("它的两次打点一起删了（不留孤儿）",
                Count(db, "counter_ticks", "counter_id = 'c_archived'") == 0, "");
            check("别的事件的打点一条没动",
                Count(db, "counter_ticks", "id = 't3'") == 1, "");
        });

        // 队列里还躺着"刚归档"的 upsert：删完必须清掉，否则下一轮推上去又活了
        WithDb((db, now) =>
        {
            SeedRule(db, "r_pending", "待推的规则", now);
            using (var conn = db.Open())
            using (var tx = conn.BeginTransaction())
            {
                SyncOutbox.Enqueue(conn, tx, "rules:r_pending", "rule",
                    new { action = "upsert", rule = new { id = "r_pending", label = "待推的规则", delta = 5 } });
                tx.Commit();
            }

            check("删之前队列里有它的 upsert", Count(db, "outbox", "id = 'rules:r_pending'") == 1, "");
            new PurgeService(db).Purge("rule", "r_pending");

            check("删完之后那条 upsert 也被撤掉",
                Count(db, "outbox", "id = 'rules:r_pending'") == 0, "");
            check("队列里只留 purge 一条",
                Count(db, "outbox", "kind = 'purge'") == 1, "");
        });

        // 不存在的东西：不写墓碑、不入队，返回 0（界面上说"已经不在了"，不是报错）
        WithDb((db, now) =>
        {
            var removed = new PurgeService(db).Purge("rule", "r_missing");
            check("删不存在的东西返回 0", removed == 0, removed.ToString());
            check("也不会往队列里塞东西", Count(db, "outbox", "kind = 'purge'") == 0, "");
        });

        // 不支持的类型要立刻炸，而不是安静地什么都不做
        WithDb((db, now) =>
        {
            var threw = false;
            try { new PurgeService(db).Purge("student", "s1"); }
            catch (ArgumentOutOfRangeException) { threw = true; }
            check("学生不在可彻底删除的名单里（会抛，不会静默成功）", threw, "");
        });
    }

    private static void WithDb(Action<Database, long> body)
    {
        var dir = Path.Combine(Path.GetTempPath(), "cp-purge-" + Guid.NewGuid().ToString("N")[..8]);
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

    private static string SeedRule(Database db, string id, string label, long now)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        Database.Exec(conn, tx, """
            INSERT INTO rules (id, label, delta, icon, sort, archived_at, rev, updated_at)
            VALUES ($id, $label, 5, '⭐', 10, $now, 2, $now)
            """, ("$id", id), ("$label", label), ("$now", now));
        tx.Commit();
        return id;
    }

    private static string SeedCounter(Database db, string id, long now)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        Database.Exec(conn, tx, """
            INSERT INTO counters (id, label, sort, archived_at, rev, updated_at)
            VALUES ($id, '迟到', 10, $now, 2, $now)
            """, ("$id", id), ("$now", now));
        tx.Commit();
        return id;
    }

    private static void SeedEntry(Database db, string id, string ruleId, string label, long now)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        Database.Exec(conn, tx, """
            INSERT INTO students (id, name, active, rev, updated_at, created_at)
            VALUES ('s_purge', '学生甲', 1, 1, $now, $now)
            ON CONFLICT(id) DO NOTHING
            """, ("$now", now));
        Database.Exec(conn, tx, """
            INSERT INTO entries (id, batch_id, period_id, student_id, rule_id, rule_label, delta, source,
                                 operator_id, operator_name, occurred_at, created_at)
            VALUES ($id, 'b1', (SELECT id FROM periods LIMIT 1), 's_purge', $rule, $label, 5, 'rule',
                    'm1', '老师', $now, $now)
            """, ("$id", id), ("$rule", ruleId), ("$label", label), ("$now", now));
        tx.Commit();
    }

    private static void SeedTick(Database db, string id, string counterId, long now)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        if (CountIn(conn, tx, "counters", "id = 'c_other'") == 0)
        {
            Database.Exec(conn, tx, """
                INSERT INTO counters (id, label, sort, rev, updated_at) VALUES ('c_other', '作业未交', 20, 1, $now)
                """, ("$now", now));
        }
        // 打点引用的学生必须存在（外键开着）：每组用例都用新库，不能指望上一组留下的
        Database.Exec(conn, tx, """
            INSERT INTO students (id, name, active, rev, updated_at, created_at)
            VALUES ('s_purge', '学生甲', 1, 1, $now, $now)
            ON CONFLICT(id) DO NOTHING
            """, ("$now", now));
        Database.Exec(conn, tx, """
            INSERT INTO counter_ticks (id, counter_id, student_id, period_id, value, operator_id, operator_name, occurred_at, created_at)
            VALUES ($id, $counter, 's_purge', (SELECT id FROM periods LIMIT 1), 1, 'm1', '老师', $now, $now)
            """, ("$id", id), ("$counter", counterId), ("$now", now));
        tx.Commit();
    }

    private static int Count(Database db, string table, string where)
    {
        using var conn = db.Open();
        return CountIn(conn, null, table, where);
    }

    private static int CountIn(Microsoft.Data.Sqlite.SqliteConnection conn,
        Microsoft.Data.Sqlite.SqliteTransaction? tx, string table, string where)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"SELECT COUNT(*) FROM {table} WHERE {where}";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static string Payload(Database db, string outboxId)
    {
        using var conn = db.Open();
        return Database.Scalar(conn, "SELECT payload FROM outbox WHERE id = $id", ("$id", outboxId)) as string ?? "";
    }
}
