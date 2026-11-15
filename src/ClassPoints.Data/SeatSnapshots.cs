using ClassPoints.Core;
using Microsoft.Data.Sqlite;

namespace ClassPoints.Data;

/// <summary>
/// 座位快照：某个时刻的「谁坐哪儿」。只在本机，不参与同步 ——
/// 它是"排过的几版方案"，换台电脑不该把旧方案也搬过去。
///
/// 存的是座位号 → 学生 id，不存姓名：学生改名之后快照里显示的仍是同一个人，
/// 而不是"张三"和"张三（新）"两份对不上的记录。
/// </summary>
public sealed record SeatSnapshot(
    string Id,
    string Name,
    string Layout,
    string Seats,
    long CreatedAt)
{
    public SeatLayout LayoutValue => SeatLayout.Parse(Layout);

    public Dictionary<int, string> SeatMap()
    {
        var map = new Dictionary<int, string>();
        foreach (var item in Seats.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = item.Split('=', 2);
            if (parts.Length != 2) continue;
            if (!int.TryParse(parts[0], out var seat) || seat < 1) continue;
            map[seat] = parts[1];
        }
        return map;
    }

    public int Count => SeatMap().Count;
}

public sealed class SeatSnapshotRepository(Database db)
{
    /// <summary>留多少份。座位表一学期排不了几次，30 份够回看两三个学期。</summary>
    public const int Keep = 30;

    public List<SeatSnapshot> List(int limit = Keep)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, name, layout, seats, created_at
            FROM seat_snapshots ORDER BY created_at DESC, rowid DESC LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$limit", limit);

        using var r = cmd.ExecuteReader();
        var list = new List<SeatSnapshot>();
        while (r.Read())
            list.Add(new SeatSnapshot(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt64(4)));
        return list;
    }

    public SeatSnapshot? Latest() => List(1).FirstOrDefault();

    public SeatSnapshot Save(string name, SeatLayout layout, IReadOnlyDictionary<int, string> seats)
    {
        var now = Clock.NowMs();
        var snapshot = new SeatSnapshot(
            Ids.NewUuid7(),
            string.IsNullOrWhiteSpace(name) ? "未命名 · " + Clock.LocalDate(now).ToString("MM-dd HH:mm") : name.Trim(),
            layout.ToSetting(),
            string.Join(";", seats.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}")),
            now);

        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        Database.Exec(conn, tx, """
            INSERT INTO seat_snapshots (id, name, layout, seats, created_at)
            VALUES ($id, $name, $layout, $seats, $at)
            """,
            ("$id", snapshot.Id), ("$name", snapshot.Name), ("$layout", snapshot.Layout),
            ("$seats", snapshot.Seats), ("$at", now));

        // 超出上限的旧快照顺手删掉：这个表没人会来清，留着只会一直涨。
        Database.Exec(conn, tx, """
            DELETE FROM seat_snapshots WHERE id NOT IN (
              SELECT id FROM seat_snapshots ORDER BY created_at DESC, rowid DESC LIMIT $keep
            )
            """, ("$keep", Keep));

        tx.Commit();
        return snapshot;
    }

    public void Delete(string id)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM seat_snapshots WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }
}
