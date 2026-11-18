using ClassPoints.Core;
using Microsoft.Data.Sqlite;

namespace ClassPoints.Data.Sync;

/// <summary>
/// 给一个本地事件定义换编号，或者并入云端已有的那一条。
///
/// 为什么需要它：出厂的三个预设事件（迟到 / 作业未交 / 课堂违纪）**编号是写死的**
/// （`c_late`、`c_homework`、`c_discipline`），而云端 `counters.id` 是全局主键 ——
/// 一台机器换过班之后，这三个编号早被旧班占着，推给新班永远被拒；
/// 引用它们的打点则一直回「云端还没有这个事件」，界面上就是那条永远推不掉的待推送。
///
/// 两条出路，都不丢数据：
///   ① <see cref="Relocate"/>：本班在云端还没有同类事件 → 换一个本地唯一编号，保留 preset_key，
///      打点与队列载荷一起改指。preset_key 保留着，出厂种子（INSERT OR IGNORE）就不会再种一份。
///   ② <see cref="Adopt"/>：云端说"这个班已经有一条同类事件了，用那个编号" → 把本机并过去。
///      两台机器各自换号之后最终会在这条路上收敛，一个班只剩一条"迟到"。
///
/// **不能直接 UPDATE 主键**：`counter_ticks.counter_id` 上有外键并且是开着的，
/// 改父表时子表还指着旧号，SQLite 会直接抛 Error 19。所以一律"复制新行 → 改引用 → 删旧行"
/// （和 <see cref="PeriodRelocator"/> 同一套顺序）。
/// </summary>
public sealed class CounterRelocator(Database db)
{
    /// <summary>换一个新编号。本地没有这个事件返回 null（没什么可救的）。</summary>
    public string? Relocate(string oldId)
    {
        if (string.IsNullOrWhiteSpace(oldId)) return null;

        using var conn = db.Open();
        using var tx = conn.BeginTransaction();

        var counter = Read(conn, tx, oldId);
        if (counter is null) return null;

        // 在旧编号后面加一段随机后缀：看得出是哪来的，也和云端其它班不会撞。
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var newId = $"{oldId}.{suffix}";

        MoveTo(conn, tx, oldId, newId, counter.PresetKey);
        OutboxRewriter.Drop(conn, tx, $"counters:{oldId}");
        OutboxRewriter.Retarget(conn, tx, ["counterId", "counter_id"], oldId, newId);
        OutboxRewriter.ResetBackoff(conn, tx);

        tx.Commit();
        return newId;
    }

    /// <summary>
    /// 并入云端认的那一条（本班已经有同 preset_key 的事件）。
    /// 返回云端那个编号；本地没有这个事件返回 null。
    /// </summary>
    public string? Adopt(string oldId, string canonicalId)
    {
        if (string.IsNullOrWhiteSpace(oldId) || string.IsNullOrWhiteSpace(canonicalId)) return null;
        if (oldId == canonicalId) return canonicalId;

        using var conn = db.Open();
        using var tx = conn.BeginTransaction();

        var counter = Read(conn, tx, oldId);
        if (counter is null) return null;

        if (Read(conn, tx, canonicalId) is not null)
        {
            // 本机已经有那条（另一台推下来过）：把打点并过去，删掉这条重复的。
            Database.Exec(conn, tx, "UPDATE counter_ticks SET counter_id = $new WHERE counter_id = $old",
                ("$new", canonicalId), ("$old", oldId));
            Database.Exec(conn, tx, "DELETE FROM counters WHERE id = $old", ("$old", oldId));
        }
        else
        {
            MoveTo(conn, tx, oldId, canonicalId, counter.PresetKey);
        }

        OutboxRewriter.Drop(conn, tx, $"counters:{oldId}");
        OutboxRewriter.Retarget(conn, tx, ["counterId", "counter_id"], oldId, canonicalId);
        OutboxRewriter.ResetBackoff(conn, tx);

        tx.Commit();
        return canonicalId;
    }

    /// <summary>
    /// 把一行事件搬到新编号：复制 → 改引用 → 删旧行 → 把 preset_key 还回去。
    ///
    /// 中间那一步 preset_key 必须先置 NULL：它上面有 UNIQUE，新旧两行同时带着
    /// `std.late` 就撞了，而"新增一条"和"删掉旧的"没法在一条语句里完成。
    /// </summary>
    private static void MoveTo(SqliteConnection conn, SqliteTransaction tx, string oldId, string newId, string? preset)
    {
        Database.Exec(conn, tx, """
            INSERT INTO counters (id, label, icon, sort, preset_key, archived_at, rev, updated_at, deleted_at)
            SELECT $new, label, icon, sort, NULL, archived_at, rev, updated_at, deleted_at
              FROM counters WHERE id = $old
            """, ("$new", newId), ("$old", oldId));

        Database.Exec(conn, tx, "UPDATE counter_ticks SET counter_id = $new WHERE counter_id = $old",
            ("$new", newId), ("$old", oldId));

        Database.Exec(conn, tx, "DELETE FROM counters WHERE id = $old", ("$old", oldId));

        if (!string.IsNullOrEmpty(preset))
            Database.Exec(conn, tx, "UPDATE counters SET preset_key = $preset WHERE id = $new",
                ("$preset", preset), ("$new", newId));
    }

    private static Counter? Read(SqliteConnection conn, SqliteTransaction tx, string id)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT * FROM counters WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Mappers.Counter(reader) : null;
    }
}
