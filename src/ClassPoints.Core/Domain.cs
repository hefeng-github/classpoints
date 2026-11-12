namespace ClassPoints.Core;

/// <summary>数据层所有时间戳统一为 UTC 毫秒。</summary>
public static class Clock
{
    public static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public static DateTimeOffset ToLocal(long ms) =>
        DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime();

    public static DateTime LocalDate(long ms) =>
        DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime().DateTime;

    public static long FromLocal(DateTime local) =>
        new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Local)).ToUnixTimeMilliseconds();

    /// <summary>某天 00:00（本地）的毫秒时间戳。</summary>
    public static long StartOfDay(DateTime localDate) =>
        new DateTimeOffset(localDate.Date).ToUnixTimeMilliseconds();
}

/// <summary>
/// 这台机器长什么样。两种形态**功能完全一样**，区别只在排版：
/// 大屏是触屏、大字、厚按钮；电脑是键鼠、紧凑、信息密度高。
///
/// 以前还有第三种「只读查询端」，已经去掉了：它和大屏端权限不同，
/// 而权限不同的两端会立刻分叉成两套产品 —— 现在只读是「不认人」这个状态，
/// 谁都可以用，只是不能写。
/// </summary>
public enum AppMode
{
    /// <summary>教室大屏：触屏、大字、记分和计数。</summary>
    Board,

    /// <summary>班主任电脑：键鼠、密度高、导入导出。</summary>
    Desk,
}

public static class AppModeNames
{
    public static string ToDb(this AppMode m) => m == AppMode.Board ? "board" : "desk";

    public static AppMode? Parse(string? s) => (s ?? "").Trim().ToLowerInvariant() switch
    {
        "board" => AppMode.Board,

        // 老库里可能还留着 "viewer"：那一端已经不存在了，按电脑端打开，
        // 至少让人能进去把数据导出来，而不是卡在向导里。
        "desk" or "viewer" => AppMode.Desk,
        _ => null,
    };

    public static string ToDisplay(this AppMode m) => m == AppMode.Board ? "教室大屏" : "班主任电脑";

    /// <summary>一句"这台机器长什么样"，给向导和设置页用。</summary>
    public static string ToHint(this AppMode m) => m == AppMode.Board
        ? "触屏用，字大、按钮厚，站在两米外也看得清"
        : "键鼠用，紧凑、信息密度高，改名单和算总评方便";
}

public enum MemberRole
{
    Owner,
    CoOwner,
    Officer,
}

public static class MemberRoleNames
{
    public static string ToDb(this MemberRole r) => r switch
    {
        MemberRole.Owner => "owner",
        MemberRole.CoOwner => "co_owner",
        _ => "officer",
    };

    public static MemberRole Parse(string? s) => s switch
    {
        "owner" => MemberRole.Owner,
        "co_owner" => MemberRole.CoOwner,
        _ => MemberRole.Officer,
    };

    public static string ToDisplay(this MemberRole r) => r switch
    {
        MemberRole.Owner => "班主任",
        MemberRole.CoOwner => "班长",
        _ => "班委",
    };

    /// <summary>这个角色能不能进管理模式。班主任和班长都可以，班委只能记分。</summary>
    public static bool CanManage(this MemberRole r) => r is MemberRole.Owner or MemberRole.CoOwner;
}

public sealed class Student
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? StudentNo { get; set; }
    public int? SeatNo { get; set; }
    public bool Active { get; set; } = true;
    public int Rev { get; set; } = 1;
    public long UpdatedAt { get; set; }
    public long? DeletedAt { get; set; }
    public long CreatedAt { get; set; }

    /// <summary>非持久化：当前周期/学期积分，由查询填充。</summary>
    public int Points { get; set; }

    public bool IsDeleted => DeletedAt is not null;

    public string SeatText => SeatNo is int n ? $"{System.Math.Max(1, (n - 1) / 8 + 1)} 排 {(n - 1) % 8 + 1} 座" : "未排座";
}

public sealed class Member
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public MemberRole Role { get; set; } = MemberRole.Officer;
    public string? PinHash { get; set; }
    public long CreatedAt { get; set; }
    public long? RevokedAt { get; set; }
    public int Rev { get; set; } = 1;
    public long UpdatedAt { get; set; }
    public long? DeletedAt { get; set; }

    public bool HasPin => !string.IsNullOrEmpty(PinHash);

    /// <summary>能进管理模式：班主任 + 班长。班委只记分。</summary>
    public bool IsManager => Role.CanManage();

    /// <summary>界面上的一行身份：「李老师 · 班主任」。</summary>
    public string IdentityText => $"{DisplayName} · {Role.ToDisplay()}";
}

public sealed class Identity
{
    public string Id { get; set; } = "";
    public string MemberId { get; set; } = "";
    public string Method { get; set; } = "nfc";
    public string Credential { get; set; } = "";
    public string? Label { get; set; }
    public long CreatedAt { get; set; }
    public long? LastUsedAt { get; set; }
}

public enum CapScope
{
    None,
    Single,
    Period,
}

public sealed class Rule
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public int Delta { get; set; }

    /// <summary>Segoe MDL2 字形，见 <see cref="IconCatalog"/>。库里可能留着旧版本的 emoji，读的时候过一遍 Normalize。</summary>
    public string Icon { get; set; } = IconCatalog.Default;
    public int Sort { get; set; }
    public string? PresetKey { get; set; }

    /// <summary>
    /// 归属成员。NULL = 全班共用（记分台上每个人都能看到）；
    /// 非空 = 只有这个人的快捷项。班级的分值项应当保持 NULL，
    /// 否则换个人刷卡后按钮就"少了一半"。
    /// </summary>
    public string? OwnerId { get; set; }

    public bool IsShared => string.IsNullOrEmpty(OwnerId);
    public int? CapValue { get; set; }
    public CapScope CapScope { get; set; } = CapScope.None;
    public long? ArchivedAt { get; set; }
    public int Rev { get; set; } = 1;
    public long UpdatedAt { get; set; }
    public long? DeletedAt { get; set; }

    public bool IsPositive => Delta >= 0;
    public bool HasCap => CapValue is > 0 && CapScope != CapScope.None;
    public bool IsArchived => ArchivedAt is not null;

    public string DeltaText => Delta >= 0 ? $"+{Delta}" : Delta.ToString();

    public string CapText => HasCap
        ? (CapScope == CapScope.Single ? $"≤{CapValue}" : $"每周期≤{CapValue}")
        : "";

    /// <summary>界面直接用这个：旧数据里的 emoji 在这里被收敛成字形。</summary>
    public string Glyph => IconCatalog.Normalize(Icon);
}

/// <summary>
/// 事件类型（如「迟到」）。它只计数，不产生积分 —— 所以单独一张表，
/// 不混进 entries：混进去以后每一次计数都得在总分、总评、导出里被排除一次，
/// 迟早漏一处，而积分算错是这套东西最不能犯的错。
/// </summary>
public sealed class Counter
{
    public string Id { get; set; } = "";
    public string Label { get; set; } = "";
    public string Icon { get; set; } = IconCatalog.Flag;
    public int Sort { get; set; }
    public string? PresetKey { get; set; }
    public long? ArchivedAt { get; set; }
    public int Rev { get; set; } = 1;
    public long UpdatedAt { get; set; }
    public long? DeletedAt { get; set; }

    public bool IsArchived => ArchivedAt is not null;

    public string Glyph => IconCatalog.Normalize(Icon);
}

/// <summary>
/// 一次计数动作。只记 +1 / -1，次数永远现算 SUM(value)：
/// 这样「撤一下」就是补一条 -1，不需要在流水上做算术，也不会出现负数被夹没。
/// </summary>
public sealed class CounterTick
{
    public long Seq { get; set; }
    public string Id { get; set; } = "";
    public string CounterId { get; set; } = "";
    public string StudentId { get; set; } = "";
    public string PeriodId { get; set; } = "";
    public int Value { get; set; } = 1;
    public string? Note { get; set; }
    public string OperatorId { get; set; } = "";
    public string OperatorName { get; set; } = "";
    public long OccurredAt { get; set; }
    public long CreatedAt { get; set; }

    /// <summary>非持久化：查明细时连接出的姓名/事件名。</summary>
    public string StudentName { get; set; } = "";
    public string CounterLabel { get; set; } = "";

    public string TimeText => Clock.ToLocal(OccurredAt).ToString("MM-dd HH:mm");
}

public sealed class NoteTemplate
{
    public string Id { get; set; } = "";
    public string Text { get; set; } = "";
    public int UseCount { get; set; } = 1;
    public long LastUsedAt { get; set; }
    public long? ArchivedAt { get; set; }
    public int Rev { get; set; } = 1;
    public long UpdatedAt { get; set; }
    public long? DeletedAt { get; set; }
}

public enum PeriodStatus
{
    Active,
    Archived,
}

public sealed class Period
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";

    /// <summary>
    /// 内部归档桶。以前叫"学期编号"并显示在界面上，现在只用来回答一个问题：
    /// 「总评的累计积分，从哪一桶开始算」。界面上不再出现学期。
    /// </summary>
    public string TermKey { get; set; } = "";

    public long StartAt { get; set; }
    public long? EndAt { get; set; }
    public PeriodStatus Status { get; set; } = PeriodStatus.Active;
    public long CreatedAt { get; set; }

    public bool IsActive => Status == PeriodStatus.Active;

    /// <summary>第几个周期。从名字里取，取不到就按 0 处理（界面退回显示原名）。</summary>
    public int Index
    {
        get
        {
            var span = Name.AsSpan();
            var start = span.IndexOf('第');
            if (start < 0) return 0;

            // 「第 12 周期」中间有空格，跳过它再读数字
            var i = start + 1;
            while (i < span.Length && (span[i] == ' ' || span[i] == '\u00A0')) i++;

            var digits = 0;
            var count = 0;
            while (i < span.Length && char.IsDigit(span[i]))
            {
                digits = digits * 10 + (span[i] - '0');
                i++;
                if (++count > 4) break;
            }
            return digits;
        }
    }

    /// <summary>界面上一律用它：「第 3 周期」。</summary>
    public string TitleText => Index > 0 ? PeriodNames.Format(Index) : Name;

    public string RangeText
    {
        get
        {
            var s = Clock.LocalDate(StartAt).ToString("yyyy-MM-dd");
            var e = EndAt is long t ? Clock.LocalDate(t).ToString("yyyy-MM-dd") : "至今";
            return $"{s} ~ {e}";
        }
    }
}

public static class PeriodNames
{
    public static string Format(int index) => $"第 {System.Math.Max(1, index)} 周期";

    /// <summary>老数据里自动生成的「2026 秋季学期」这类名字要改写；人工改过的不动。</summary>
    public static bool LooksAutoGenerated(string? name) =>
        !string.IsNullOrWhiteSpace(name) &&
        (name.Contains("学期", StringComparison.Ordinal) ||
         name.StartsWith("当前周期", StringComparison.Ordinal));
}

public sealed class Entry
{
    public long Seq { get; set; }
    public string Id { get; set; } = "";
    public string BatchId { get; set; } = "";
    public string PeriodId { get; set; } = "";
    public string StudentId { get; set; } = "";
    public string? RuleId { get; set; }
    public string? RuleLabel { get; set; }
    public int? RuleDeltaDefault { get; set; }
    public int Delta { get; set; }
    public string Source { get; set; } = "rule";
    public string? Note { get; set; }
    public string? NoteTemplateId { get; set; }
    public bool Capped { get; set; }
    public bool OverCap { get; set; }
    public string OperatorId { get; set; } = "";
    public string OperatorName { get; set; } = "";
    public string? IdentifyMethod { get; set; }
    public long OccurredAt { get; set; }
    public long CreatedAt { get; set; }
    public string? ReversesEntryId { get; set; }
    public long? ReversedAt { get; set; }
    public string? ReversedBy { get; set; }
    public bool Synced { get; set; } = true;

    /// <summary>非持久化：查明细时连接出的学生姓名。</summary>
    public string StudentName { get; set; } = "";

    public bool IsReversal => ReversesEntryId is not null;
    public bool IsReversed => ReversedAt is not null;
    public bool IsCustom => Source == "custom" || (RuleDeltaDefault is int d && d != Delta);

    public string DeltaText => Delta >= 0 ? $"+{Delta}" : Delta.ToString();

    public string TimeText => Clock.ToLocal(OccurredAt).ToString("MM-dd HH:mm");

    /// <summary>明细里的副标题：「+80（默认 +100）」「已撤销 · 张老师 14:35」。</summary>
    public string DetailSuffix
    {
        get
        {
            var parts = new List<string>();
            if (RuleDeltaDefault is int d && d != Delta && !IsReversal)
                parts.Add($"默认 {(d >= 0 ? "+" : "")}{d}");
            if (Capped) parts.Add("已压到上限");
            if (OverCap) parts.Add("超出上限");
            if (IsReversed)
                parts.Add($"已撤销 · {ReversedBy} {Clock.ToLocal(ReversedAt ?? 0):HH:mm}");
            return string.Join(" · ", parts);
        }
    }
}

public sealed class Exam
{
    public string Id { get; set; } = "";
    public string PeriodId { get; set; } = "";
    public string Name { get; set; } = "";
    public double FullScore { get; set; } = 750;
    public string? ExamDate { get; set; }
    public long ImportedAt { get; set; }
    public string? SourceFile { get; set; }
}

public sealed class ExamScore
{
    public string ExamId { get; set; } = "";
    public string StudentId { get; set; } = "";
    public double? Score { get; set; }
    public bool Absent { get; set; }
}

public sealed class Scheme
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "默认方案";
    public double ExamWeight { get; set; } = 0.7;
    public double DailyWeight { get; set; } = 0.3;
    public bool Normalize { get; set; }
    public bool IsActive { get; set; } = true;
    public long CreatedAt { get; set; }

    public int ExamPercent => (int)System.Math.Round(ExamWeight * 100);

    public int DailyPercent => 100 - ExamPercent;

    /// <summary>给界面/导出用的一句公式原文，永远现算，不写死字符串。</summary>
    public string FormulaText => Normalize
        ? $"总评 = 考试百分制 × {ExamPercent}% + 平时百分制 × {DailyPercent}%（已归一）"
        : $"总评 = 考试总分加权和 × {ExamPercent}% + 累计积分 × {DailyPercent}%（原值加权，未归一）";
}

public sealed class SchemeWeight
{
    public string SchemeId { get; set; } = "";
    public string ExamId { get; set; } = "";
    public double Weight { get; set; }
}

public enum AbsencePolicy
{
    /// <summary>缺考那场不计，权重按比例摊给其余场次（默认）。</summary>
    Redistribute,

    /// <summary>直接按 0 分计入。</summary>
    Zero,

    /// <summary>用该场全班平均分替代。</summary>
    ClassAverage,
}

public sealed class AuditLogItem
{
    public long Id { get; set; }
    public long At { get; set; }
    public string? ActorId { get; set; }
    public string Action { get; set; } = "";
    public string? Target { get; set; }
    public string? Detail { get; set; }

    public string AtText => Clock.ToLocal(At).ToString("MM-dd HH:mm:ss");
}

public sealed class BackupRecord
{
    public long Id { get; set; }
    public long At { get; set; }
    public string Path { get; set; } = "";
    public int EntryCount { get; set; }
    public bool Ok { get; set; } = true;
}
