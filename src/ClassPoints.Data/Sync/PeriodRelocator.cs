using System.Text.Json.Nodes;
using ClassPoints.Core;
using Microsoft.Data.Sqlite;

namespace ClassPoints.Data.Sync;

/// <summary>
/// 给一个本地周期换新编号。
///
/// 为什么需要它：`periods.id` 在云端是**全局主键**，一条周期只能属于一个班。
/// 老师换过班（或者重复建过班）之后，本机周期编号可能已经在云端属于另一个班 ——
/// 这时无论推多少次都会被 `class_id` 守卫挡回来，而引用它的流水与事件打点
/// 永远回 `unknown_period`。界面上就是那句「云端还没有这个周期，等它同步上去后这批计数会自动补推」，
/// 但它永远等不到，因为那个周期根本推不上去。
///
/// 换编号是唯一不需要人工介入的出路：本地这份数据是完整的，缺的只是一个不撞车的 id。
/// 换的时候必须把**所有引用它的行一起改指**（流水、事件打点、考试），
/// 否则删旧行会被外键拦住，或者留下一批指向不存在周期的孤儿。
/// 队列里那些载荷也要改：`{entry:{periodId:…}}`、`{tick:{periodId:…}}` 都得跟着换，
/// 不然下一轮推上去的还是旧编号。
/// </summary>
public sealed class PeriodRelocator(Database db)
{
    /// <summary>换成功返回新编号；本地没有这个周期就返回 null（没什么可救的）。</summary>
    public string? Relocate(string oldId)
    {
        if (string.IsNullOrWhiteSpace(oldId)) return null;

        using var conn = db.Open();
        using var tx = conn.BeginTransaction();

        var period = ReadPeriod(conn, tx, oldId);
        if (period is null) return null;

        var newId = Guid.NewGuid().ToString("D");

        // 顺序不能变：
        //   ① 新行先落成 archived —— uq_period_active 是"status='active' 只许一行"的部分索引，
        //      直接插 active 会和旧行撞车；
        //   ② 引用改指到新行（entries / counter_ticks 的 period_id 有外键，改指时新行必须已存在）；
        //   ③ 删旧行；
        //   ④ 旧行是当前周期的话，把新行扶正。
        Database.Exec(conn, tx, """
            INSERT INTO periods (id, name, term_key, start_at, end_at, status, created_at)
            VALUES ($id, $name, $term, $start, $end, 'archived', $created)
            """,
            ("$id", newId), ("$name", period.Name), ("$term", period.TermKey),
            ("$start", period.StartAt),
            ("$end", period.EndAt ?? (object)DBNull.Value),
            ("$created", period.CreatedAt));

        Database.Exec(conn, tx, "UPDATE entries SET period_id = $new WHERE period_id = $old",
            ("$new", newId), ("$old", oldId));
        Database.Exec(conn, tx, "UPDATE counter_ticks SET period_id = $new WHERE period_id = $old",
            ("$new", newId), ("$old", oldId));
        Database.Exec(conn, tx, "UPDATE exams SET period_id = $new WHERE period_id = $old",
            ("$new", newId), ("$old", oldId));

        Database.Exec(conn, tx, "DELETE FROM periods WHERE id = $old", ("$old", oldId));

        if (period.IsActive)
        {
            Database.Exec(conn, tx, "UPDATE periods SET status = 'active' WHERE id = $id", ("$id", newId));
        }

        RewriteQueue(conn, tx, oldId, newId);

        tx.Commit();
        return newId;
    }

    private static Period? ReadPeriod(SqliteConnection conn, SqliteTransaction tx, string id)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT * FROM periods WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Mappers.Period(reader) : null;
    }

    /// <summary>队列里所有指向旧编号的载荷改成新编号，并让它们立刻重试。</summary>
    private static void RewriteQueue(SqliteConnection conn, SqliteTransaction tx, string oldId, string newId)
    {
        // 旧周期的推送条目本身没有意义了（推上去只会再被拒一次）；
        // 新周期的条目下一轮由 QueuePeriodsForPendingEntries 自动补上。
        Database.Exec(conn, tx, "DELETE FROM outbox WHERE id = $id", ("$id", $"periods:{oldId}"));

        List<(string Id, string Payload)> rows = [];
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT id, payload FROM outbox";
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) rows.Add((reader.GetString(0), reader.GetString(1)));
        }

        foreach (var (id, payload) in rows)
        {
            if (Replace(payload, oldId, newId) is not { } updated) continue;
            Database.Exec(conn, tx, "UPDATE outbox SET payload = $payload WHERE id = $id",
                ("$payload", updated), ("$id", id));
        }

        // 这些记录大多已经退避到几分钟之后了。编号刚换好正是最该马上重试的时刻，
        // 让老师点一下「立即同步」就真的通，而不是等下一个退避窗口。
        Database.Exec(conn, tx, "UPDATE outbox SET next_at = $now, attempts = 0", ("$now", Clock.NowMs()));
    }

    /// <summary>把 JSON 里所有 periodId / period_id 等于旧值的字段换掉；没改返回 null。</summary>
    private static string? Replace(string payload, string oldId, string newId)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(payload);
        }
        catch (Exception)
        {
            return null;
        }

        if (node is null || !ReplaceIn(node, oldId, newId)) return null;
        return node.ToJsonString();
    }

    private static bool ReplaceIn(JsonNode node, string oldId, string newId)
    {
        var changed = false;
        switch (node)
        {
            case JsonObject obj:
                // 先取键快照：循环里会改 obj
                foreach (var key in obj.Select(pair => pair.Key).ToList())
                {
                    var child = obj[key];
                    if (child is JsonValue value && value.TryGetValue<string>(out var text) &&
                        (key == "periodId" || key == "period_id") && text == oldId)
                    {
                        obj[key] = newId;
                        changed = true;
                    }
                    else if (child is not null && ReplaceIn(child, oldId, newId))
                    {
                        changed = true;
                    }
                }
                break;

            case JsonArray array:
                foreach (var child in array)
                {
                    if (child is not null && ReplaceIn(child, oldId, newId)) changed = true;
                }
                break;
        }

        return changed;
    }
}
