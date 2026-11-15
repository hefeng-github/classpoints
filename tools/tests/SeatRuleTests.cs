using ClassPoints.Core;

namespace ClassPoints.Tests;

/// <summary>
/// 带约束的排座：前/中/后区域、必须同桌、不能同桌、尽量不重复上次区域。
///
/// 这些规则一旦"看起来生效了其实没生效"，老师是最后一个知道的 —— 他要等人坐下了
/// 才发现那个必须坐前排的学生又在最后一排。所以每条规则都在这里钉死：
/// 排上了、排不下时给的原因对、约束之间不互相踩。
/// </summary>
internal static class SeatRuleTests
{
    public static void Run(Action<string, bool, string> report)
    {
        void Check(string name, bool ok, string detail = "") => report(name, ok, detail);

        var room = SeatLayout.Create(2, 1, 2); // 2 排 × 2 列
        SeatCandidate C(string id, int score = 0) => new(id, score, id);

        // ---------------- 区域与同桌位 ----------------

        var six = SeatLayout.Create(6, 4, 2);
        Check("6 排时前三段：1–2 排前、3–4 排中、5–6 排后",
            SeatArranger.ZoneOf(six, 1) == SeatZone.Front &&
            SeatArranger.ZoneOf(six, 17) == SeatZone.Mid &&
            SeatArranger.ZoneOf(six, 33) == SeatZone.Back,
            $"{SeatArranger.ZoneOf(six, 1)}/{SeatArranger.ZoneOf(six, 17)}/{SeatArranger.ZoneOf(six, 33)}");

        Check("同桌位：1 号与 2 号、3 号与 4 号互为同桌",
            SeatArranger.DeskPartner(six, 1) == 2 &&
            SeatArranger.DeskPartner(six, 2) == 1 &&
            SeatArranger.DeskPartner(six, 3) == 4);

        Check("每组只有 1 座时没有同桌位",
            SeatArranger.DeskPartner(SeatLayout.Create(2, 2, 1), 1) is null);

        var threeWide = SeatLayout.Create(2, 1, 3);
        Check("每组 3 座时第 3 座落单（1↔2 同桌，3 独自）",
            SeatArranger.DeskPartner(threeWide, 1) == 2 &&
            SeatArranger.DeskPartner(threeWide, 5) == 4 &&
            SeatArranger.DeskPartner(threeWide, 6) is null,
            SeatArranger.DeskPartner(threeWide, 6)?.ToString() ?? "null");

        // ---------------- 区域约束 ----------------

        var rules = new SeatRules();
        rules.SetZone("a", SeatZone.Front);
        rules.SetZone("b", SeatZone.Front);

        var plan = SeatArranger.Plan(room, [C("a"), C("b"), C("c")], SeatStrategy.TopFront,
            new SeatPlanOptions(Rules: rules));
        var seats = plan.Placements.ToDictionary(p => p.Id, p => p.Seat);
        Check("要求前排的两个人拿到第 1 排",
            seats["a"] is 1 or 2 && seats["b"] is 1 or 2 && seats["a"] != seats["b"],
            $"{seats.GetValueOrDefault("a")}/{seats.GetValueOrDefault("b")}");

        var onlyOne = SeatLayout.Create(2, 1, 1); // 两个座位，都在不同排
        var frontOnly = new SeatRules();
        frontOnly.SetZone("a", SeatZone.Front);
        frontOnly.SetZone("b", SeatZone.Front);
        var tight = SeatArranger.Plan(onlyOne, [C("a"), C("b")], SeatStrategy.TopFront,
            new SeatPlanOptions(Rules: frontOnly));
        Check("前排只有一个位、两个人都要前排：排不下的人进未排座并给出原因",
            tight.Unplaced.Count == 1 &&
            tight.Issues.Any(i => i.Kind == SeatIssueKind.ZoneUnavailable && i.Zone == SeatZone.Front),
            string.Join(",", tight.Unplaced));

        var back = new SeatRules();
        back.SetZone("a", SeatZone.Back);
        var backPlan = SeatArranger.Plan(room, [C("a"), C("b")], SeatStrategy.TopFront,
            new SeatPlanOptions(Rules: back));
        Check("要求后排的人落到第 2 排",
            backPlan.Placements.First(p => p.Id == "a").Seat is 3 or 4);

        // ---------------- 必须同桌 / 不能同桌 ----------------

        var together = new SeatRules();
        together.SetLink("a", "b", SeatLinkKind.Together);
        var pairPlan = SeatArranger.Plan(room, [C("a", 100), C("b", 10), C("c", 50)],
            SeatStrategy.TopFront, new SeatPlanOptions(Rules: together));
        var pairSeats = pairPlan.Placements.ToDictionary(p => p.Id, p => p.Seat);
        Check("必须同桌的两个人坐在同一张桌子的左右",
            SeatArranger.DeskPartner(room, pairSeats["a"]) == pairSeats["b"] &&
            SeatArranger.DeskPartner(room, pairSeats["b"]) == pairSeats["a"],
            $"{pairSeats.GetValueOrDefault("a")}/{pairSeats.GetValueOrDefault("b")}");

        var apart = new SeatRules();
        apart.SetLink("a", "b", SeatLinkKind.Apart);
        var apartPlan = SeatArranger.Plan(room, [C("a"), C("b")], SeatStrategy.TopFront,
            new SeatPlanOptions(Rules: apart));
        var apartSeats = apartPlan.Placements.ToDictionary(p => p.Id, p => p.Seat);
        Check("不能同桌的两个人没有挨着坐",
            apartSeats["a"] != SeatArranger.DeskPartner(room, apartSeats["b"]),
            $"{apartSeats["a"]}/{apartSeats["b"]}");

        var crowded = SeatArranger.Plan(room, [C("a"), C("b"), C("c"), C("d"), C("e")],
            SeatStrategy.TopFront);
        Check("人多于座位时多出来的人进未排座，原因是没座位",
            crowded.Unplaced.Count == 1 && crowded.Issues[0].Kind == SeatIssueKind.NoSeat,
            crowded.Unplaced.Count.ToString());

        var conflict = new SeatRules();
        conflict.SetLink("a", "b", SeatLinkKind.Together);
        conflict.SetZone("a", SeatZone.Front);
        conflict.SetZone("b", SeatZone.Back);
        var conflictPlan = SeatArranger.Plan(room, [C("a"), C("b")], SeatStrategy.TopFront,
            new SeatPlanOptions(Rules: conflict));
        var conflictSeats = conflictPlan.Placements.ToDictionary(p => p.Id, p => p.Seat);
        Check("区域要求和同桌要求打架时：两个人都先坐下，只是没能同桌",
            conflictPlan.Unplaced.Count == 0 &&
            conflictSeats["a"] is 1 or 2 && conflictSeats["b"] is 3 or 4 &&
            conflictPlan.Issues.Any(i => i.Kind == SeatIssueKind.PairUnavailable),
            $"{conflictSeats.GetValueOrDefault("a")}/{conflictSeats.GetValueOrDefault("b")} unplaced={conflictPlan.Unplaced.Count}");

        // ---------------- 偏好：尽量不坐上次的区域 ----------------

        var tall = SeatLayout.Create(3, 1, 2); // 3 排，每排都是不同区域
        var last = new Dictionary<string, SeatZone>
        {
            ["a"] = SeatZone.Front,
            ["b"] = SeatZone.Front,
            ["c"] = SeatZone.Front,
        };
        var preferred = SeatArranger.Plan(tall, [C("a"), C("b"), C("c")], SeatStrategy.TopFront,
            new SeatPlanOptions(LastZones: last, PreferDifferentZone: true));
        var preferredSeats = preferred.Placements.ToDictionary(p => p.Id, p => p.Seat);
        Check("上次都坐前排的人这轮不再排到第 1 排",
            preferredSeats.Values.All(seat => SeatArranger.ZoneOf(tall, seat) != SeatZone.Front),
            string.Join(",", preferredSeats.Select(kv => $"{kv.Key}:{kv.Value}")));

        var noHistory = SeatArranger.Plan(tall, [C("a")], SeatStrategy.TopFront,
            new SeatPlanOptions(PreferDifferentZone: true));
        Check("没有历史区域时偏好不改变结果（还是第 1 排第 1 座）",
            noHistory.Placements[0].Seat == 1);

        // ---------------- 规则的存取 ----------------

        var store = new SeatRules();
        store.SetZone("s1", SeatZone.Mid);
        store.SetZone("s2", SeatZone.Front);
        store.SetZone("s3", SeatZone.Front);
        store.SetZone("s3", SeatZone.Any); // 取消
        store.SetLink("s1", "s2", SeatLinkKind.Together);
        store.SetLink("s2", "s1", SeatLinkKind.Apart); // 换规则：s2 的关系覆盖旧的那条

        var reloaded = SeatRules.Parse(store.ZonesValue, store.LinksValue);
        Check("区域能存进设置再读回来",
            reloaded.ZoneOf("s1") == SeatZone.Mid && reloaded.ZoneOf("s2") == SeatZone.Front &&
            reloaded.ZoneOf("s3") == SeatZone.Any);
        Check("每人只保留最后一条同桌关系",
            store.Links.Count == 1 && store.Links[0].Kind == SeatLinkKind.Apart &&
            reloaded.Links.Count == 1 && reloaded.Links[0].A == "s2" && reloaded.Links[0].B == "s1");

        var junk = SeatRules.Parse("坏数据;x:Z;y", "a>>b;c>Q>d;;");
        Check("规则串坏掉时不抛异常、坏项直接忽略",
            junk.Zones.Count == 0 && junk.Links.Count == 0,
            $"zones={junk.Zones.Count},links={junk.Links.Count}");
    }
}
