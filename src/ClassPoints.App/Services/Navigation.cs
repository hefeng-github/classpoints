namespace ClassPoints.App.Services;

/// <summary>
/// 两端共用的页面序号。
///
/// 大屏端和电脑端**功能完全一样**，只是排版不同：同一套序号，两端各自映射到
/// 适合自己的那个页面实现（只有「事件计数」「设置」两侧各有一版，其余共用）。
/// 以前两端各有一套序号、各缺一半功能，结果是大屏建不了名单、电脑端记不了分 ——
/// 老师被迫在两台机器之间来回跑。
///
/// 「座位」是可选功能（设置 → 可选功能），默认关闭；序号和页面一直在，
/// 只是 <see cref="ForDisplay"/> 会把这一项从导航里摘掉，关掉时界面上找不到它。
/// </summary>
public static class AppNav
{
    public const int Score = 0;
    public const int Counters = 1;
    public const int Wall = 2;
    public const int Lookup = 3;
    public const int Entries = 4;
    public const int Students = 5;
    public const int Exams = 6;
    public const int Periods = 7;
    public const int Settings = 8;
    public const int Seats = 9;
    public const int Count = 10;

    /// <summary>导航条上的文案。两端共用一份，避免"大屏叫明细、电脑叫记录"。</summary>
    public static readonly (int Index, string Title, string Glyph)[] Items =
    [
        (Score, "记分台", "\uE710"),
        (Counters, "事件计数", "\uE7C1"),
        (Wall, "看板", "\uE80F"),
        (Lookup, "查分", "\uE721"),
        (Entries, "明细", "\uE8A5"),
        (Students, "名单", "\uE716"),
        (Seats, "座位", "\uE80A"),
        (Exams, "考试与总评", "\uE9D9"),
        (Periods, "周期", "\uE81C"),
        (Settings, "设置", "\uE713"),
    ];

    /// <summary>
    /// 这台机器该看到的导航项。
    ///
    /// 座位关闭时整项不出现 —— 不是"灰掉"，是根本不存在：没有这一项、没有提示、
    /// 没有"未启用"的字样。一个用不上的功能占着一格，只会让人多问一句这是什么。
    /// </summary>
    public static IReadOnlyList<(int Index, string Title, string Glyph)> ForDisplay(bool seatsEnabled) =>
        Items.Where(i => i.Index != Seats || seatsEnabled).ToList();

    /// <summary>会改数据的页面：没认人时进去会先弹认人。看板、查分、明细永远可看。</summary>
    public static bool NeedsIdentity(int index) =>
        index is Score or Counters or Students or Seats or Exams or Periods or Settings;

    /// <summary>
    /// 管理类页面：改名单、改分值、定口径、开周期、改设置。
    /// 班委的卡能记分，但进不来这些页 —— 和云端「班委令牌一律 403」是同一条规则，
    /// 不能只靠"按钮藏起来"（大屏是公开设备，谁都能点）。
    /// 座位会写 seat_no、改教室布局，所以和名单同一档。
    /// </summary>
    public static bool NeedsAdmin(int index) =>
        index is Students or Seats or Exams or Periods or Settings;
}
