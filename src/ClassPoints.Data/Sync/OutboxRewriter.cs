using System.Text.Json.Nodes;
using ClassPoints.Core;
using Microsoft.Data.Sqlite;

namespace ClassPoints.Data.Sync;

/// <summary>
/// 改编号之后收拾队列：把载荷里指向旧编号的字段换掉、删掉作废的条目、把退避清零。
///
/// 为什么必须改载荷：outbox 里存的是**推上去那一刻的 JSON 快照**，不是引用。
/// 只改本地表不改队列，下一轮推上去的还是旧编号，云端依旧拒。
///
/// 周期与预设事件（counter）共用这段：区别只是字段名不同（periodId / counterId）。
/// </summary>
internal static class OutboxRewriter
{
    /// <summary>删掉某一条队列记录（例如旧编号那条周期推送，推上去只会再被拒一次）。</summary>
    public static void Drop(SqliteConnection conn, SqliteTransaction tx, string outboxId)
    {
        Database.Exec(conn, tx, "DELETE FROM outbox WHERE id = $id", ("$id", outboxId));
    }

    /// <summary>
    /// 把所有载荷里 <paramref name="fields"/> 之一等于旧编号的字段换成新编号。
    /// 返回改到的条数。
    /// </summary>
    public static int Retarget(SqliteConnection conn, SqliteTransaction tx,
        IReadOnlyList<string> fields, string oldId, string newId)
    {
        List<(string Id, string Payload)> rows = [];
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT id, payload FROM outbox";
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) rows.Add((reader.GetString(0), reader.GetString(1)));
        }

        var changed = 0;
        foreach (var (id, payload) in rows)
        {
            if (Replace(payload, fields, oldId, newId) is not { } updated) continue;
            Database.Exec(conn, tx, "UPDATE outbox SET payload = $payload WHERE id = $id",
                ("$payload", updated), ("$id", id));
            changed++;
        }

        return changed;
    }

    /// <summary>
    /// 这些记录大多已经退避到几分钟之后了。编号刚换好正是最该马上重试的时刻，
    /// 让老师点一下「立即同步」就真的通，而不是等下一个退避窗口。
    /// </summary>
    public static void ResetBackoff(SqliteConnection conn, SqliteTransaction tx) =>
        Database.Exec(conn, tx, "UPDATE outbox SET next_at = $now, attempts = 0",
            ("$now", Clock.NowMs()));

    /// <summary>把 JSON 里指定字段等于旧值的换掉；没改返回 null。</summary>
    private static string? Replace(string payload, IReadOnlyList<string> fields, string oldId, string newId)
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

        if (node is null || !ReplaceIn(node, fields, oldId, newId)) return null;
        return node.ToJsonString();
    }

    private static bool ReplaceIn(JsonNode node, IReadOnlyList<string> fields, string oldId, string newId)
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
                        fields.Contains(key, StringComparer.Ordinal) && text == oldId)
                    {
                        obj[key] = newId;
                        changed = true;
                    }
                    else if (child is not null && ReplaceIn(child, fields, oldId, newId))
                    {
                        changed = true;
                    }
                }
                break;

            case JsonArray array:
                foreach (var child in array)
                {
                    if (child is not null && ReplaceIn(child, fields, oldId, newId)) changed = true;
                }
                break;
        }

        return changed;
    }
}
