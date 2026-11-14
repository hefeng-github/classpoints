namespace ClassPoints.App.Services;

/// <summary>
/// 两端共用的页面序号。
///
/// 大屏端和电脑端**功能完全一样**，只是排版不同：同一套序号，两端各自映射到
/// 适合自己的那个页面实现（只有「事件计数」「设置」两侧各有一版，其余共用）。
/// 以前两端各有一套序号、各缺一半功能，结果是大屏建不了名单、电脑端记不了分 ——
/// 老师被迫在两台机器之间来回跑。
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
    public const int Count = 9;

    /// <summary>导航条上的文案。两端共用一份，避免"大屏叫明细、电脑叫记录"。</summary>
    public static readonly (int Index, string Title, string Glyph)[] Items =
    [
        (Score, "记分台", "\uE710"),
        (Counters, "事件计数", "\uE7C1"),
        (Wall, "看板", "\uE80F"),
        (Lookup, "查分", "\uE721"),
        (Entries, "明细", "\uE8A5"),
        (Students, "名单", "\uE716"),
        (Exams, "考试与总评", "\uE9D9"),
        (Periods, "周期", "\uE81C"),
        (Settings, "设置", "\uE713"),
    ];

    /// <summary>会改数据的页面：没认人时进去会先弹认人。看板、查分、明细永远可看。</summary>
    public static bool NeedsIdentity(int index) =>
        index is Score or Counters or Students or Exams or Periods or Settings;

    /// <summary>
    /// 管理类页面：改名单、改分值、定口径、开周期、改设置。
    /// 班委的卡能记分，但进不来这些页 —— 和云端「班委令牌一律 403」是同一条规则，
    /// 不能只靠"按钮藏起来"（大屏是公开设备，谁都能点）。
    /// </summary>
    public static bool NeedsAdmin(int index) =>
        index is Students or Exams or Periods or Settings;
}
