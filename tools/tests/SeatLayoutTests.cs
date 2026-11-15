using ClassPoints.Core;

namespace ClassPoints.Tests;

/// <summary>
/// 座位图的几何与排座策略。
///
/// 这两块是"排完座位对不上人"最容易出问题的地方，所以不靠界面点：
/// 座位号 ↔ (排, 列) 的来回换算、换布局时按排/列保位、六种策略的先后顺序
/// 全部在离线测试里钉死。界面只负责把这些结果画出来。
/// </summary>
internal static class SeatLayoutTests
{
    public static void Run(Action<string, bool, string> report)
    {
        void Check(string name, bool ok, string detail = "") => report(name, ok, detail);

        // ---------------- 几何 ----------------

        var six = SeatLayout.Create(6, 4, 2);
        Check("默认教室 6 排 4 组每组 2 座 = 48 个座位",
            six.Rows == 6 && six.Groups == 4 && six.PerGroup == 2 && six.Columns == 8 && six.Capacity == 48,
            six.ToString());
        Check("三条组间走廊", six.Aisles == 3, six.Aisles.ToString());

        Check("座位号 9 = 第 2 排第 1 座（满 8 列后换排）",
            six.Position(9) == (2, 1), six.Position(9)?.ToString() ?? "null");
        Check("第 2 排第 3 座 = 11 号", six.SeatNo(2, 3) == 11, six.SeatNo(2, 3)?.ToString() ?? "null");
        Check("座位号与排/列来回换算一致",
            Enumerable.Range(1, six.Capacity).All(n =>
                six.Position(n) is { } p && six.SeatNo(p.Row, p.Col) == n));

        Check("越界的座位号没有位置", six.Position(0) is null && six.Position(49) is null);
        Check("越界的排/列没有座位号", six.SeatNo(7, 1) is null && six.SeatNo(1, 9) is null);
        Check("第 5 列属于第 3 组（每组 2 座）", six.GroupOf(5) == 2, six.GroupOf(5).ToString());
        Check("第 5 列是组内第 1 座", six.ColumnInGroup(5) == 1, six.ColumnInGroup(5).ToString());
        Check("座位文字", six.SeatText(11) == "2 排 3 座", six.SeatText(11));

        var clamped = SeatLayout.Create(99, 99, 99);
        Check("参数越界会夹到 10 排 / 6 组 / 每组 2 座（总列数不超过 12）",
            clamped.Rows == 10 && clamped.Groups == 6 && clamped.PerGroup == 2 && clamped.Columns == 12,
            clamped.ToString());

        // ---------------- 换布局保位 ----------------

        var wide = SeatLayout.Create(6, 1, 8);
        Check("8 列换 4 组后，第 2 排第 1 座还是 9 号", wide.Remap(9, six) == 9,
            wide.Remap(9, six)?.ToString() ?? "null");
        Check("原本第 1 排第 8 座的人留在第 8 列", wide.Remap(8, six) == 8);

        var narrow = SeatLayout.Create(6, 2, 2); // 4 列
        Check("列数变少时，第 2 排第 1 座从 9 号变成 5 号", wide.Remap(9, narrow) == 5,
            wide.Remap(9, narrow)?.ToString() ?? "null");
        Check("新布局放不下的位置算未排座", wide.Remap(5, narrow) is null);
        Check("原本就没排座的人换布局还是没排座", six.Remap(null, narrow) is null);

        // ---------------- 设置值 ----------------

        Check("布局写成设置值", six.ToSetting() == "6|4|2", six.ToSetting());
        var parsed = SeatLayout.Parse("8|3|3");
        Check("设置值读回来",
            parsed.Rows == 8 && parsed.Groups == 3 && parsed.PerGroup == 3 && parsed.Columns == 9,
            parsed.ToString());
        Check("设置值坏掉时回到默认布局",
            SeatLayout.Parse("垃圾") == SeatLayout.Default && SeatLayout.Parse(null) == SeatLayout.Default);

        // ---------------- 自动排座 ----------------

        var room = SeatLayout.Create(2, 1, 2); // 2 排 2 列，4 个座
        SeatCandidate C(string id, int score) => new(id, score, id);

        SeatCandidate[] classOfFour =
        [
            C("a", 50), C("b", 100), C("c", 70), C("d", 80),
        ];

        var top = SeatArranger.Arrange(room, classOfFour, SeatStrategy.TopFront, null, 1)
            .ToDictionary(x => x.Seat, x => x.Id);
        Check("优生靠前：100/80/70/50 依次坐 1–4 号",
            top[1] == "b" && top[2] == "d" && top[3] == "c" && top[4] == "a",
            string.Join(",", top.Select(kv => kv.Key + ":" + kv.Value)));

        var back = SeatArranger.Arrange(room, classOfFour, SeatStrategy.TopBack, null, 1)
            .ToDictionary(x => x.Seat, x => x.Id);
        Check("优生靠后：50/70/80/100 依次坐 1–4 号",
            back[1] == "a" && back[2] == "c" && back[3] == "d" && back[4] == "b");

        var balanced = SeatArranger.Arrange(room, classOfFour, SeatStrategy.Balanced, null, 1)
            .ToDictionary(x => x.Seat, x => x.Id);
        Check("成绩均衡：最高与最低挨着（100,50,80,70）",
            balanced[1] == "b" && balanced[2] == "a" && balanced[3] == "d" && balanced[4] == "c",
            string.Join(",", balanced.Select(kv => kv.Key + ":" + kv.Value)));

        var snake = SeatArranger.Arrange(room, classOfFour, SeatStrategy.Snake, null, 1)
            .ToDictionary(x => x.Seat, x => x.Id);
        Check("蛇形：第 2 排从右往左填（4 号在 3 号之前）",
            snake[1] == "b" && snake[2] == "d" && snake[4] == "c" && snake[3] == "a",
            string.Join(",", snake.Select(kv => kv.Key + ":" + kv.Value)));

        var roster = SeatArranger.Arrange(room, classOfFour, SeatStrategy.Roster, null, 1)
            .ToDictionary(x => x.Seat, x => x.Id);
        Check("按名单顺序：a/b/c/d 依次坐 1–4 号",
            roster[1] == "a" && roster[2] == "b" && roster[3] == "c" && roster[4] == "d");

        var random1 = SeatArranger.Arrange(room, classOfFour, SeatStrategy.Random, null, 7)
            .ToDictionary(x => x.Seat, x => x.Id);
        var random2 = SeatArranger.Arrange(room, classOfFour, SeatStrategy.Random, null, 7)
            .ToDictionary(x => x.Seat, x => x.Id);
        Check("随机排座：每个座位都填到、每个人都只出现一次",
            random1.Count == 4 && random1.Values.Distinct().Count() == 4);
        Check("随机排座：同一个种子结果一致（便于复现问题）",
            random1.SequenceEqual(random2));

        var blocked = SeatArranger.Arrange(room, classOfFour, SeatStrategy.TopFront, [2], 1)
            .ToDictionary(x => x.Seat, x => x.Id);
        Check("被固定的座位不参与分配",
            blocked.Count == 3 && !blocked.ContainsKey(2) && blocked[1] == "b");

        var crowded = SeatArranger.Arrange(room,
            [.. classOfFour, C("e", 10)], SeatStrategy.TopFront, null, 1);
        Check("人多于座位时多出来的人不排（界面显示未排座）",
            crowded.Count == 4 && crowded.All(x => x.Id != "e"),
            crowded.Count.ToString());
    }
}
