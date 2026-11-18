using ClassPoints.Core;
using Microsoft.Data.Sqlite;

namespace ClassPoints.Data;

/// <summary>
/// 彻底删除：把一条已经归档 / 停用的东西真的从库里删掉，并让所有设备跟着删。
///
/// 和 <c>Archive</c> / <c>SoftDelete</c> 的区别只有一个字：**行还在不在**。
/// 归档是"不用了但留着"（能恢复、能查历史），彻底删除是"不要再看到它"。
///
/// 三件事必须一起做：
///   ① 删本地行，连子表一起删 —— `counter_ticks.counter_id` 有外键，
///      只删事件会直接 `SQLITE_CONSTRAINT`；就算外键关着，也会留下指向不存在事件的打点。
///   ② 清掉队列里这条的待推送 —— 一条还没推上去的 upsert 在删完之后推，等于把它又造出来。
///      打点要单独按载荷反查：它在队列里的 id 是打点自己的 id，不是 `counters:xxx`。
///   ③ 入队一条 purge —— 云端物理删 + 写墓碑，别的设备拉到时也会物理删。
///      不推的话只删了这台机器，另一台下次同步又把它拉回来。
///
/// 历史流水不受影响：`entries` 里的 `rule_label` / `note` 都是冗余存的，规则或评语删了，
/// 那几百条"回答问题 +5"照样查得到、导得出。所以评语删掉时**只摘引用、不删流水**
/// （见 <see cref="DetachNoteRefs"/>）。
/// </summary>
public sealed class PurgeService(Database db)
{
    /// <summary>能彻底删除的三类。学生与周期没开 —— 它们连着一整段历史，界面上也没给入口。</summary>
    public static readonly IReadOnlyList<string> Kinds = ["rule", "counter", "note"];

    /// <summary>
    /// 删掉一条。返回删掉的行数（0 = 本来就不在）。
    /// </summary>
    public int Purge(string kind, string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return 0;

        var (table, cascades) = kind switch
        {
            "rule" => ("rules", Array.Empty<(string Table, string Column)>()),
            "counter" => ("counters", new[] { ("counter_ticks", "counter_id") }),
            // 评语没有子表：`entries.note_template_id` 只是"当初用了哪几条理由"的线索，
            // 流水本身是独立的事实（note 里也存了文本）。以前这里把 entries 当子表删，
            // 既误删历史，又因为那个字段是逗号拼接的多值而只删得掉一部分。
            "note" => ("note_templates", Array.Empty<(string Table, string Column)>()),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "这一类不支持彻底删除"),
        };

        using var conn = db.Open();
        using var tx = conn.BeginTransaction();

        // `Database.Exec` 不返回行数，先看一眼在不在：不在就别写墓碑，
        // 否则云端会收到一条"删掉一条本来就不存在于这个班的数据"的指令。
        var exists = Convert.ToInt32(Database.Scalar(conn,
            $"SELECT COUNT(*) FROM {table} WHERE id = $id", ("$id", id)));
        if (exists == 0)
        {
            tx.Commit();
            return 0;
        }

        // 子表先删：外键开着时顺序反了会直接被拦
        foreach (var (cascadeTable, column) in cascades)
        {
            Database.Exec(conn, tx, $"DELETE FROM {cascadeTable} WHERE {column} = $id", ("$id", id));
        }

        // 打点走了，队列里那些还没推上去的也一起清掉：云端拿不到 counter 会一直回
        // unknown_counter，而它会跟着墓碑永久推不上去 —— 界面上就是"待同步 N 条"永不归零。
        if (kind == "counter") SyncOutbox.DeletePendingTicks(conn, tx, id);

        // 评语删了，流水里的引用要摘掉（不是把整列置空：一行可以挂好几条理由）
        if (kind == "note") DetachNoteRefs(conn, tx, id);

        Database.Exec(conn, tx, $"DELETE FROM {table} WHERE id = $id", ("$id", id));

        // 这一条可能刚到队列里（刚归档过、还没推）。留着它推上去，云端就又有了一份。
        Database.Exec(conn, tx,
            "DELETE FROM outbox WHERE id IN ($a, $b, $c, $d)",
            ("$a", $"rules:{id}"), ("$b", $"counters:{id}"), ("$c", $"note_templates:{id}"), ("$d", $"notes:{id}"));

        SyncOutbox.Enqueue(conn, tx, $"purges:{kind}:{id}", "purge",
            new { action = "purge", kind, id });

        tx.Commit();
        return exists;
    }

    /// <summary>
    /// 这个东西删掉会连带影响多少条记录。界面上要写在确认框里 ——
    /// "彻底删除"不该让人靠猜后果。
    /// </summary>
    public int CascadeCount(string kind, string id)
    {
        using var conn = db.Open();
        return kind switch
        {
            "counter" => Convert.ToInt32(Database.Scalar(conn,
                "SELECT COUNT(*) FROM counter_ticks WHERE counter_id = $id", ("$id", id))),
            // 评语：一条流水都不会丢，只把引用摘掉
            "note" => 0,
            // 规则：流水里的名字是冗余存的，删了不影响历史，一条都不丢
            _ => 0,
        };
    }

    /// <summary>
    /// 把某条评语从流水的 `note_template_id` 里摘掉。
    ///
    /// 这个字段是逗号拼接的多值（一次入账可以同时勾几条理由，见 EntryService），
    /// 所以 `DELETE FROM entries WHERE note_template_id = $id` 只能命中"刚好只挂这一条"的行，
    /// 删得毫无规律 —— 更糟的是它删的是历史流水本身。这里改成逐行摘引用，行一条不动。
    /// </summary>
    private static void DetachNoteRefs(SqliteConnection conn, SqliteTransaction tx, string noteId)
    {
        var rows = new List<(string Id, string Refs)>();

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                SELECT id, note_template_id FROM entries
                 WHERE note_template_id IS NOT NULL AND note_template_id LIKE $like
                """;
            cmd.Parameters.AddWithValue("$like", $"%{noteId}%");
            using var r = cmd.ExecuteReader();
            while (r.Read()) rows.Add((r.GetString(0), r.GetString(1)));
        }

        foreach (var (id, refs) in rows)
        {
            var all = refs.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var rest = all.Where(x => !string.Equals(x, noteId, StringComparison.Ordinal)).ToList();
            if (rest.Count == all.Length) continue;

            if (rest.Count == 0)
                Database.Exec(conn, tx, "UPDATE entries SET note_template_id = NULL WHERE id = $id", ("$id", id));
            else
                Database.Exec(conn, tx, "UPDATE entries SET note_template_id = $refs WHERE id = $id",
                    ("$refs", string.Join(",", rest)), ("$id", id));
        }
    }
}
