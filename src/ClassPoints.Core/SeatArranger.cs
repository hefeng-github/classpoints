namespace ClassPoints.Core;

/// <summary>自动排座的几种口味。前四种是"按成绩定前后"，后两种是"按名单 / 打散"。</summary>
public enum SeatStrategy
{
    /// <summary>随机。</summary>
    Random,

    /// <summary>成绩高的坐前排。</summary>
    TopFront,

    /// <summary>成绩高的坐后排（把前排留给需要盯的人）。</summary>
    TopBack,

    /// <summary>成绩高的坐前排，但左右按排蛇形换向 —— 不会每学期都是同一侧靠前。</summary>
    Snake,

    /// <summary>一个高分挨一个低分：同桌互补，也顺带把小组均分拉开。</summary>
    Balanced,

    /// <summary>按名单（学号 / 姓名）的顺序，一个接一个往下排。</summary>
    Roster,
}

/// <summary>教室的前 / 中 / 后三段。按排数三等分，除不尽时前后各多一排。</summary>
public enum SeatZone
{
    Any,
    Front,
    Mid,
    Back,
}

/// <summary>两个人之间的关系：必须同桌（同一张桌子的左右）或不能同桌。</summary>
public enum SeatLinkKind
{
    Together,
    Apart,
}

public sealed record SeatLink(string A, string B, SeatLinkKind Kind);

/// <summary>
/// 排座的约束：某个人要坐前排，某两个人必须 / 不能挨着。
///
/// 每人最多一条「同桌关系」—— 现实里老师提的要求也基本是这一种；
/// 允许多条时，两条互相矛盾（A 必须和 B、又不能和 C，而 B/C 又必须挨着）
/// 就变成一道解不出来的题，最后只能给一个"没排成"的对话框。
///
/// 只存 id，学生转走 / 停用后读回来自然失效，不需要去清。
/// </summary>
public sealed class SeatRules
{
    public Dictionary<string, SeatZone> Zones { get; } = new(StringComparer.Ordinal);

    public List<SeatLink> Links { get; } = [];

    public SeatZone ZoneOf(string id) => Zones.TryGetValue(id, out var zone) ? zone : SeatZone.Any;

    /// <summary>设置区域；<see cref="SeatZone.Any"/> 等于删掉这条要求。</summary>
    public void SetZone(string id, SeatZone zone)
    {
        if (zone == SeatZone.Any) Zones.Remove(id);
        else Zones[id] = zone;
    }

    public SeatLink? LinkOf(string id) => Links.FirstOrDefault(l => l.A == id || l.B == id);

    /// <summary>设置同桌关系。<paramref name="other"/> 为空或等于自己时只清掉旧关系。</summary>
    public void SetLink(string id, string? other, SeatLinkKind kind)
    {
        Links.RemoveAll(l => l.A == id || l.B == id);
        if (!string.IsNullOrWhiteSpace(other) && other != id)
            Links.Add(new SeatLink(id, other!, kind));
    }

    public void RemoveStudent(string id)
    {
        Zones.Remove(id);
        Links.RemoveAll(l => l.A == id || l.B == id);
    }

    public SeatRules Clone()
    {
        var copy = new SeatRules();
        foreach (var (id, zone) in Zones) copy.Zones[id] = zone;
        copy.Links.AddRange(Links);
        return copy;
    }

    /// <summary>settings 里的写法：<c>id:F;id2:B</c>。学生 id 是 UUID，不会有分隔符。</summary>
    public string ZonesValue => string.Join(";", Zones.Select(kv => $"{kv.Key}:{ZoneCode(kv.Value)}"));

    /// <summary>settings 里的写法：<c>a&gt;T&gt;b;a&gt;X&gt;c</c>（T = 必须同桌，X = 不能同桌）。</summary>
    public string LinksValue => string.Join(";", Links.Select(l => $"{l.A}>{LinkCode(l.Kind)}>{l.B}"));

    public static SeatRules Parse(string? zones, string? links)
    {
        var rules = new SeatRules();

        foreach (var item in Split(zones))
        {
            var parts = item.Split(':', 2);
            if (parts.Length != 2) continue;
            var zone = ParseZone(parts[1]);
            if (zone != SeatZone.Any) rules.Zones[parts[0]] = zone;
        }

        foreach (var item in Split(links))
        {
            var parts = item.Split('>', 3);
            if (parts.Length != 3 || parts[0].Length == 0 || parts[2].Length == 0) continue;
            if (!parts[1].Equals("T", StringComparison.OrdinalIgnoreCase) &&
                !parts[1].Equals("X", StringComparison.OrdinalIgnoreCase)) continue;
            var kind = parts[1].Equals("T", StringComparison.OrdinalIgnoreCase)
                ? SeatLinkKind.Together
                : SeatLinkKind.Apart;
            rules.Links.Add(new SeatLink(parts[0], parts[2], kind));
        }

        return rules;
    }

    private static IEnumerable<string> Split(string? raw) =>
        string.IsNullOrWhiteSpace(raw)
            ? []
            : raw.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static string ZoneCode(SeatZone zone) => zone switch
    {
        SeatZone.Front => "F",
        SeatZone.Mid => "M",
        SeatZone.Back => "B",
        _ => "A",
    };

    private static SeatZone ParseZone(string code) => code.ToUpperInvariant() switch
    {
        "F" => SeatZone.Front,
        "M" => SeatZone.Mid,
        "B" => SeatZone.Back,
        _ => SeatZone.Any,
    };

    private static string LinkCode(SeatLinkKind kind) => kind == SeatLinkKind.Together ? "T" : "X";

    public static string ZoneLabel(SeatZone zone) => zone switch
    {
        SeatZone.Front => "前排",
        SeatZone.Mid => "中排",
        SeatZone.Back => "后排",
        _ => "不限",
    };
}

/// <summary>没排上的原因。界面拿它拼成人话（Core 不知道学生叫什么）。</summary>
public enum SeatIssueKind
{
    /// <summary>座位不够 / 没空位。</summary>
    NoSeat,

    /// <summary>要求的区域没有空位。</summary>
    ZoneUnavailable,

    /// <summary>找不到能和同桌一起坐的位置。</summary>
    PairUnavailable,
}

public sealed record SeatIssue(string StudentId, SeatIssueKind Kind, SeatZone Zone = SeatZone.Any, string? PartnerId = null);

/// <summary>一次自动排座的结果。未排座与没满足的约束都摆在明面上，不静默吞掉。</summary>
public sealed record SeatPlan(
    IReadOnlyList<(string Id, int Seat)> Placements,
    IReadOnlyList<string> Unplaced,
    IReadOnlyList<SeatIssue> Issues);

/// <summary>自动排座的输入旋钮。</summary>
public sealed record SeatPlanOptions(
    IReadOnlyCollection<int>? BlockedSeats = null,
    SeatRules? Rules = null,
    IReadOnlyDictionary<string, SeatZone>? LastZones = null,
    bool PreferDifferentZone = false,
    int? Seed = null);

/// <summary>
/// 排座算法。只认"人 + 分数 + 排序键"、座位图和几条约束，不碰数据库也不碰界面，
/// 所以能在离线测试里把结果钉死。
///
/// 固定座位不在这里处理：调用方先把固定的人和他们的座位从名单里摘掉，
/// 剩下的座位再交给这里 —— 算法越简单，出问题的时候越容易说清是谁的责任。
///
/// 做法是贪心：按策略排好人的顺序，依次给每个人找座位。
/// 区域（前/中/后）是硬约束；两人必须同桌时，找座位的同时把同桌也放下去，
/// 所以不会出现"排完一轮再来回挪"的来回倒手。
///
/// 策略与「组间走廊 + 讲台 + 固定座位优先」这套思路，参考了开源项目
/// open_fuckseats（Python + Django）。那套代码跑不到 WinUI 里，这里按
/// ClassPoints 的数据形状重写成纯函数，没有直接搬运代码。
/// </summary>
public static class SeatArranger
{
    public static string Label(SeatStrategy s) => s switch
    {
        SeatStrategy.TopFront => "优生靠前",
        SeatStrategy.TopBack => "优生靠后",
        SeatStrategy.Snake => "优生靠前（蛇形）",
        SeatStrategy.Balanced => "成绩均衡（高低同桌）",
        SeatStrategy.Roster => "按名单顺序",
        _ => "随机",
    };

    public static string Hint(SeatStrategy s) => s switch
    {
        SeatStrategy.TopFront => "按本周期积分从高到低，从第 1 排第 1 座依次排。",
        SeatStrategy.TopBack => "按本周期积分从低到高，从第 1 排第 1 座依次排。",
        SeatStrategy.Snake => "高分在前排，但单双排左右换向，避免总坐在同一侧。",
        SeatStrategy.Balanced => "一个高分配一个低分，相邻座位分数差最大。",
        SeatStrategy.Roster => "按学号（没有学号就按姓名）的顺序，从第 1 排第 1 座依次排。",
        _ => "打乱顺序后依次填入。",
    };

    public static IReadOnlyList<SeatStrategy> All { get; } =
    [
        SeatStrategy.Random,
        SeatStrategy.TopFront,
        SeatStrategy.TopBack,
        SeatStrategy.Snake,
        SeatStrategy.Balanced,
        SeatStrategy.Roster,
    ];

    /// <summary>不带约束的排座（老调用方与测试用）。</summary>
    public static List<(string Id, int Seat)> Arrange(
        SeatLayout layout,
        IReadOnlyList<SeatCandidate> students,
        SeatStrategy strategy,
        IReadOnlyCollection<int>? blockedSeats = null,
        int? seed = null) =>
        Plan(layout, students, strategy, new SeatPlanOptions(blockedSeats, Seed: seed)).Placements.ToList();

    /// <summary>带约束的排座。</summary>
    public static SeatPlan Plan(
        SeatLayout layout,
        IReadOnlyList<SeatCandidate> students,
        SeatStrategy strategy,
        SeatPlanOptions? options = null)
    {
        options ??= new SeatPlanOptions();
        var rules = options.Rules ?? new SeatRules();
        var blocked = options.BlockedSeats is { Count: > 0 }
            ? new HashSet<int>(options.BlockedSeats.Where(s => s >= 1 && s <= layout.Capacity))
            : [];

        var free = SeatOrder(layout, strategy).Where(s => !blocked.Contains(s)).ToList();
        var occupied = new HashSet<int>(blocked);
        var placed = new Dictionary<string, int>(StringComparer.Ordinal);
        var owner = new Dictionary<int, string>();

        var known = students.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        var partners = TogetherPartners(rules, known);

        var ordered = OrderStudents(students, strategy, options.Seed);
        var unplaced = new List<string>();
        var issues = new List<SeatIssue>();
        var brokenPairs = new HashSet<string>(StringComparer.Ordinal);

        foreach (var student in ordered)
        {
            if (placed.ContainsKey(student.Id)) continue;

            var required = rules.ZoneOf(student.Id);
            var partnerId = partners.GetValueOrDefault(student.Id);
            if (partnerId is not null && placed.ContainsKey(partnerId)) partnerId = null;
            if (partnerId is not null && brokenPairs.Contains(student.Id)) partnerId = null;

            var candidates = free
                .Where(seat => !occupied.Contains(seat) && Matches(required, ZoneOf(layout, seat)))
                .ToList();

            if (candidates.Count == 0)
            {
                unplaced.Add(student.Id);
                issues.Add(new SeatIssue(student.Id,
                    required == SeatZone.Any ? SeatIssueKind.NoSeat : SeatIssueKind.ZoneUnavailable,
                    required));
                continue;
            }

            // 偏好：尽量不坐上次那个区域（只在没有硬性区域要求时起作用）。
            if (options.PreferDifferentZone && required == SeatZone.Any &&
                options.LastZones is { } last && last.GetValueOrDefault(student.Id) is var lastZone &&
                lastZone != SeatZone.Any)
            {
                candidates = candidates
                    .OrderBy(seat => ZoneOf(layout, seat) == lastZone ? 1 : 0)
                    .ToList();
            }

            int? chosen = null;
            int? chosenPartnerSeat = null;

            foreach (var seat in candidates)
            {
                if (HasApartConflict(layout, rules, owner, student.Id, seat)) continue;

                if (partnerId is null)
                {
                    chosen = seat;
                    break;
                }

                // 必须同桌：这个人和同桌要一起落在同一张桌子的左右两个座。
                var partnerZone = rules.ZoneOf(partnerId);
                var partnerSeat = DeskPartner(layout, seat);
                if (partnerSeat is not int ps) continue;
                if (occupied.Contains(ps)) continue;
                if (!Matches(partnerZone, ZoneOf(layout, ps))) continue;
                if (HasApartConflict(layout, rules, owner, partnerId, ps)) continue;
                if (AreApart(rules, student.Id, partnerId)) continue;

                chosen = seat;
                chosenPartnerSeat = ps;
                break;
            }

            if (chosen is not int picked)
            {
                // 同桌坐不到一起时，先让这个人自己坐下（区域要求仍然有效），
                // 把"没做成同桌"当成提示，而不是把两个人都留成未排座 ——
                // 老师要的是"尽量按我说的排"，不是"有一条做不到就全班重来"。
                if (partnerId is string failedPartner)
                {
                    var alone = candidates.FirstOrDefault(seat =>
                        !HasApartConflict(layout, rules, owner, student.Id, seat));

                    if (alone >= 1)
                    {
                        placed[student.Id] = alone;
                        owner[alone] = student.Id;
                        occupied.Add(alone);
                        brokenPairs.Add(student.Id);
                        brokenPairs.Add(failedPartner);
                        issues.Add(new SeatIssue(student.Id, SeatIssueKind.PairUnavailable, required, failedPartner));
                        continue;
                    }
                }

                unplaced.Add(student.Id);
                issues.Add(new SeatIssue(student.Id,
                    partnerId is null ? SeatIssueKind.NoSeat : SeatIssueKind.PairUnavailable,
                    required, partnerId));
                continue;
            }

            placed[student.Id] = picked;
            owner[picked] = student.Id;
            occupied.Add(picked);

            if (partnerId is string pid && chosenPartnerSeat is int ps2)
            {
                placed[pid] = ps2;
                owner[ps2] = pid;
                occupied.Add(ps2);
            }
        }

        // 「必须同桌」但另一个人这次没排上（座位不够）：把原因说清楚，别让人以为是没生效。
        foreach (var link in rules.Links.Where(l => l.Kind == SeatLinkKind.Together))
        {
            if (!known.Contains(link.A) || !known.Contains(link.B)) continue;
            if (placed.ContainsKey(link.A) && placed.ContainsKey(link.B)) continue;
            if (issues.Any(i => i.StudentId == link.A || i.StudentId == link.B)) continue;
            issues.Add(new SeatIssue(link.A, SeatIssueKind.PairUnavailable, rules.ZoneOf(link.A), link.B));
        }

        var placements = placed.Select(kv => (Id: kv.Key, Seat: kv.Value)).ToList();
        return new SeatPlan(placements, unplaced, issues);
    }

    /// <summary>座位号的填写顺序。蛇形策略单数排从左往右、双数排从右往左。</summary>
    public static IEnumerable<int> SeatOrder(SeatLayout layout, SeatStrategy strategy)
    {
        for (var row = 1; row <= layout.Rows; row++)
        {
            var reverse = strategy == SeatStrategy.Snake && row % 2 == 0;
            for (var i = 0; i < layout.Columns; i++)
            {
                var col = reverse ? layout.Columns - i : i + 1;
                if (layout.SeatNo(row, col) is int seat) yield return seat;
            }
        }
    }

    /// <summary>座位在第几段（前 / 中 / 后）。</summary>
    public static SeatZone ZoneOf(SeatLayout layout, int seat)
    {
        if (layout.Position(seat) is not { } pos) return SeatZone.Any;

        var band = System.Math.Max(1, (int)System.Math.Ceiling(layout.Rows / 3.0));
        if (pos.Row <= band) return SeatZone.Front;
        if (pos.Row > layout.Rows - band) return SeatZone.Back;
        return SeatZone.Mid;
    }

    /// <summary>
    /// 同一张桌子的另一个座位（组内左右成对）。每组 1 座、或组内最后一列落单时返回 null。
    /// 不讲"前后相邻"：教室里前后桌不是同桌，把前后也算进来会让"不能同桌"变成"不能相邻"。
    /// </summary>
    public static int? DeskPartner(SeatLayout layout, int seat)
    {
        if (layout.Position(seat) is not { } pos) return null;

        var inGroup = layout.ColumnInGroup(pos.Col);
        if (inGroup % 2 == 1 && inGroup + 1 <= layout.PerGroup)
            return layout.SeatNo(pos.Row, pos.Col + 1);
        if (inGroup % 2 == 0)
            return layout.SeatNo(pos.Row, pos.Col - 1);
        return null;
    }

    /// <summary>学生的先后。蛇形与随机只影响座位顺序的，这里仍按成绩从高到低。</summary>
    public static List<SeatCandidate> OrderStudents(
        IReadOnlyList<SeatCandidate> students, SeatStrategy strategy, int? seed = null)
    {
        var list = students.ToList();

        switch (strategy)
        {
            case SeatStrategy.Random:
                Shuffle(list, seed);
                break;
            case SeatStrategy.TopBack:
                list.Sort((a, b) => Compare(a.Score, b.Score, a.SortKey, b.SortKey));
                break;
            case SeatStrategy.Roster:
                list.Sort((a, b) => string.Compare(a.SortKey, b.SortKey, StringComparison.CurrentCulture));
                break;
            case SeatStrategy.Balanced:
                list.Sort((a, b) => Compare(b.Score, a.Score, a.SortKey, b.SortKey));
                list = Interleave(list);
                break;
            default:
                list.Sort((a, b) => Compare(b.Score, a.Score, a.SortKey, b.SortKey));
                break;
        }

        return list;
    }

    private static bool Matches(SeatZone required, SeatZone actual) =>
        required == SeatZone.Any || required == actual;

    private static bool AreApart(SeatRules rules, string a, string b) =>
        rules.Links.Any(l => l.Kind == SeatLinkKind.Apart &&
                             ((l.A == a && l.B == b) || (l.A == b && l.B == a)));

    /// <summary>这个座位旁边（同桌位）是否坐着一个"不能挨着"的人。</summary>
    private static bool HasApartConflict(
        SeatLayout layout, SeatRules rules, IReadOnlyDictionary<int, string> owner, string studentId, int seat)
    {
        if (DeskPartner(layout, seat) is not int partnerSeat) return false;
        if (!owner.TryGetValue(partnerSeat, out var neighbor)) return false;
        return AreApart(rules, studentId, neighbor);
    }

    /// <summary>每个人最多一条同桌关系，取第一条。</summary>
    private static Dictionary<string, string> TogetherPartners(SeatRules rules, IReadOnlySet<string> known)
    {
        var partners = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var link in rules.Links.Where(l => l.Kind == SeatLinkKind.Together))
        {
            if (!known.Contains(link.A) || !known.Contains(link.B)) continue;
            partners.TryAdd(link.A, link.B);
            partners.TryAdd(link.B, link.A);
        }
        return partners;
    }

    /// <summary>分数高的在前；同分按排序键，保证同一份名单每次排出来一样。</summary>
    private static int Compare(int scoreA, int scoreB, string keyA, string keyB)
    {
        var byScore = scoreA.CompareTo(scoreB);
        if (byScore != 0) return byScore;
        return string.Compare(keyA, keyB, StringComparison.CurrentCulture);
    }

    /// <summary>头一个、尾一个、第二个、倒数第二个……</summary>
    private static List<SeatCandidate> Interleave(List<SeatCandidate> sorted)
    {
        var result = new List<SeatCandidate>(sorted.Count);
        var left = 0;
        var right = sorted.Count - 1;
        while (left <= right)
        {
            result.Add(sorted[left++]);
            if (left <= right) result.Add(sorted[right--]);
        }
        return result;
    }

    private static void Shuffle(List<SeatCandidate> list, int? seed)
    {
        var rng = seed is int s ? new Random(s) : Random.Shared;
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }
}

/// <summary>排座的输入：一个人、他本周期多少分、同分时按什么排（学号，退到姓名）。</summary>
public sealed record SeatCandidate(string Id, int Score, string SortKey);
