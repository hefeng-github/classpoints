namespace ClassPoints.App.Services;

/// <summary>
/// 序号 → 页面。两端各一份，因为只有「事件计数」和「设置」两侧各有一版；
/// 其余九项共用同一个页面 —— 功能完全一致，差异由外壳（图标轨 / 文字侧栏）承担。
/// </summary>
public static class BoardPages
{
    public static Type Map(int index) => index switch
    {
        AppNav.Score => typeof(Board.BoardScorePage),
        AppNav.Counters => typeof(Board.BoardCountersPage),
        AppNav.Wall => typeof(Board.BoardWallPage),
        AppNav.Lookup => typeof(Board.BoardLookupPage),
        AppNav.Entries => typeof(Board.BoardEntriesPage),
        AppNav.Students => typeof(Desk.DeskStudentsPage),
        AppNav.Exams => typeof(Desk.DeskExamsPage),
        AppNav.Print => typeof(Desk.DeskPrintPage),
        AppNav.Periods => typeof(Desk.DeskPeriodsPage),
        _ => typeof(Desk.DeskSettingsPage),
    };
}

public static class DeskPages
{
    public static Type Map(int index) => index switch
    {
        AppNav.Score => typeof(Board.BoardScorePage),
        AppNav.Counters => typeof(Desk.DeskCountersPage),
        AppNav.Wall => typeof(Board.BoardWallPage),
        AppNav.Lookup => typeof(Board.BoardLookupPage),
        AppNav.Entries => typeof(Board.BoardEntriesPage),
        AppNav.Students => typeof(Desk.DeskStudentsPage),
        AppNav.Exams => typeof(Desk.DeskExamsPage),
        AppNav.Print => typeof(Desk.DeskPrintPage),
        AppNav.Periods => typeof(Desk.DeskPeriodsPage),
        _ => typeof(Desk.DeskSettingsPage),
    };
}
