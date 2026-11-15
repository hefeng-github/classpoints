namespace ClassPoints.Core;

/// <summary>
/// 教室座位图：排 × 组 × 每组座位。
///
/// 排从讲台往后数（第 1 排最靠讲台），列从左往右，组与组之间是走廊。
/// 座位号仍然是"从左到右、从上到下"的连续整数（1 起），走廊只是画的时候空出来，
/// 不占号 —— 库里只有 students.seat_no 一个字段，名单排序、记分台列数、
/// 同步、预配置包都还按这一套走，不需要为新布局加表。
///
/// 参数都做了夹取：布局来自设置、预配置包和手输，任何一条路径都不该让
/// 容量算成 0 或者一屏塞 60 列。
/// </summary>
public sealed record SeatLayout(int Rows, int Groups, int PerGroup)
{
    public const int MinRows = 2;
    public const int MaxRows = 10;
    public const int MinGroups = 1;
    public const int MaxGroups = 6;
    public const int MinPerGroup = 1;
    public const int MaxPerGroup = 12;
    public const int MaxColumns = 12;

    /// <summary>新装默认：6 排 × 4 组 × 每组 2 座 —— 和以前一屏 8 列对得上，还多了三条走廊。</summary>
    public static SeatLayout Default => new(6, 4, 2);

    public int Columns => Groups * PerGroup;

    public int Capacity => Rows * Columns;

    /// <summary>组间走廊的条数：组数减一。</summary>
    public int Aisles => Groups - 1;

    public static SeatLayout Create(int rows, int groups, int perGroup)
    {
        var g = System.Math.Clamp(groups, MinGroups, MaxGroups);
        var p = System.Math.Clamp(perGroup, MinPerGroup, System.Math.Max(MinPerGroup, MaxColumns / g));
        var r = System.Math.Clamp(rows, MinRows, MaxRows);
        return new SeatLayout(r, g, p);
    }

    /// <summary>座位号 → (排, 列)，都从 1 起。越界返回 null。</summary>
    public (int Row, int Col)? Position(int? seatNo)
    {
        if (seatNo is not int n || n < 1 || n > Capacity) return null;
        var index = n - 1;
        return (index / Columns + 1, index % Columns + 1);
    }

    /// <summary>(排, 列) → 座位号。越界返回 null。</summary>
    public int? SeatNo(int row, int col)
    {
        if (row < 1 || row > Rows || col < 1 || col > Columns) return null;
        return (row - 1) * Columns + col;
    }

    /// <summary>第几组（0 起）。</summary>
    public int GroupOf(int col) => (System.Math.Clamp(col, 1, Columns) - 1) / PerGroup;

    /// <summary>组内第几座（1 起）。</summary>
    public int ColumnInGroup(int col) => (System.Math.Clamp(col, 1, Columns) - 1) % PerGroup + 1;

    /// <summary>"3 排 5 座"。列号跨走廊连续，和座位号同一套。</summary>
    public string SeatText(int? seatNo)
    {
        var pos = Position(seatNo);
        return pos is { } p ? $"{p.Row} 排 {p.Col} 座" : "未排座";
    }

    /// <summary>
    /// 换布局时保住"第几排第几列"，而不是保住座位号。
    ///
    /// 老师把 6 排 × 8 列改成分 4 组之后，座位号会整体重排；如果直接沿用旧号，
    /// 原来坐第 2 排第 1 座的人会被挪到第 3 排 —— 明明是同一个位置，换了个编号就换人了。
    /// 新布局放不下的（原来的第 9 列、第 11 排）返回 null，由调用方算作未排座。
    /// </summary>
    public int? Remap(int? seatNo, SeatLayout target)
    {
        if (Position(seatNo) is not { } p) return null;
        if (p.Row > target.Rows || p.Col > target.Columns) return null;
        return target.SeatNo(p.Row, p.Col);
    }

    /// <summary>排号（1 起）、组（0 起）、组内座（1 起）→ 座位号。</summary>
    public int? SeatAt(int row, int group, int columnInGroup)
    {
        if (group < 0 || group >= Groups || columnInGroup < 1 || columnInGroup > PerGroup) return null;
        return SeatNo(row, group * PerGroup + columnInGroup);
    }

    /// <summary>落库用的紧凑写法："6|4|2"。刻意不用 JSON：这个值只有三个整数，
    /// 用 JSON 反而多一层解析失败的可能（库里出现过被同步截断的半截值）。</summary>
    public string ToSetting() => $"{Rows}|{Groups}|{PerGroup}";

    /// <summary>解析设置值。认不出来就回到默认布局 —— 布局坏了不该让整页打不开。</summary>
    public static SeatLayout Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Default;
        var parts = value.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3) return Default;
        if (!int.TryParse(parts[0], out var rows) ||
            !int.TryParse(parts[1], out var groups) ||
            !int.TryParse(parts[2], out var per)) return Default;
        return Create(rows, groups, per);
    }

    public override string ToString() => $"{Rows} 排 × {Groups} 组 × 每组 {PerGroup} 座";
}
