using Windows.UI;
using ClassPoints.App.Services;
using ClassPoints.Core;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using ClassPoints.App.Theme;
using ClassPoints.Data;

namespace ClassPoints.App;

/// <summary>
/// 窗口外壳。同一个 exe 三种形态：大屏端、电脑端、只读查询端，形态由首次启动向导定。
/// 窗口的位置、尺寸写进 settings，下次启动还原；超出当前屏幕时夹回可见区域。
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly ContentDialog? _startupDialog;
    private bool _started;

    public MainWindow()
    {
        InitializeComponent();
        Root.Loaded += OnRootLoaded;
        Closed += OnClosed;
    }

    public MainWindow(ContentDialog dialog) : this()
    {
        _startupDialog = dialog;
    }

    private AppServices Services => App.Services;

    private async void OnRootLoaded(object sender, RoutedEventArgs e)
    {
        if (_started) return;
        _started = true;

        if (_startupDialog is not null)
        {
            _startupDialog.XamlRoot = Root.XamlRoot;
            await Dialogs.ShowAsync(_startupDialog);
            Application.Current.Exit();
            return;
        }

        if (Environment.GetEnvironmentVariable("CP_NORESIZE") != "1") RestoreWindow();

        // 先定外观再挂界面：否则第一帧会用错的色，切换时闪一下
        App.ApplyTheme(Services.Settings.Theme);

        // 形态与配置包都归向导管。以前这两件事是命令行参数，老师换台机器就得改快捷方式，
        // 忘了改就静默进错形态（大屏端没有导入入口，电脑端没有记分台），所以收进向导里问一次。
        var snapshot = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CP_SNAP"));
        if (snapshot)
        {
            // 验收脚本：形态由 CP_MODE 指定（CP_MODE 不算"定过"，所以单看 ModeKnown 不够），
            // 两者都没有就走向导 —— 截向导自己的页面。
            if (App.ModeKnown || App.Options.ModeOverride is not null) ShowShell();
            else ShowOnboarding();
            return;
        }

        if (!App.ModeKnown || !Services.Settings.OnboardingDone)
            ShowOnboarding();
        else
            ShowShell();
    }

    // ---------------- 路由 ----------------

    private void ShowShell()
    {
        switch (App.Mode)
        {
            case AppMode.Board:
            {
                Services.Log("创建大屏外壳");
                var shell = new Board.BoardShell();
                App.Board = shell;
                App.Desk = null;
                Title = "班级积分 · 大屏";
                Host.Content = shell;
                Services.Log("大屏外壳已挂载");
                break;
            }

            default:
            {
                Services.Log("创建电脑端外壳");
                var shell = new Desk.DeskShell();
                App.Desk = shell;
                App.Board = null;
                Title = "班级积分 · " + Services.ClassName;
                Host.Content = shell;
                break;
            }
        }

        AppWindow.Title = Title;

        // 每天一份自动备份（启动时，或当天记满 50 人次时补一份；见 AppServices.TryAutoBackup）。
        // 设置页、侧栏和说明文件都写着这一条，之前却没有任何调用点 ——
        // 只有「向导完成」和「每 50 条入账」会触发，「上次备份」于是可以永远停在几个月前。
        // 后台跑：VACUUM INTO 再快也不该挡住第一帧。
        if (Services.Settings.OnboardingDone &&
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CP_SNAP")))
        {
            _ = Task.Run(() => Services.TryAutoBackup("startup")).ContinueWith(task =>
            {
                if (task.Result is null) return;

                // 备份完成后把侧栏的「上次备份」刷一下：不然今天第一次启动，
                // 明明备份已经写好了，左下角还挂着「还没有备份」。
                DispatcherQueue.TryEnqueue(() => App.Host?.RefreshChrome());
            });
        }

        // 数据目录退到用户目录这件事必须说一句：老师会以为"我的名单没了"。
        if (Services.DataDirFallbackNotice is { Length: > 0 } notice)
            DispatcherQueue.TryEnqueue(() => App.Host?.Toast(notice, 12));

        MaybeRunSnapshots();
    }

    /// <summary>
    /// 验收通道：CP_SNAP=&lt;目录&gt; 时逐页自截图后退出。
    /// 远程会话/虚拟显示器上桌面截图拿不到 WinUI 的合成结果，只能让程序自己渲染。
    /// </summary>
    private void MaybeRunSnapshots()
    {
        var snapDir = Environment.GetEnvironmentVariable("CP_SNAP");
        if (!string.IsNullOrWhiteSpace(snapDir)) RunSnapshots(snapDir);
    }

    private async void RunSnapshots(string dir)
    {
        try
        {
            await Task.Delay(1200);

            if (Host.Content is Onboarding.OnboardingView wizard)
            {
                for (var i = 0; i < wizard.StepCount; i++)
                {
                    wizard.DebugGoToStep(i);
                    await Task.Delay(1200);
                    if (await Snapshot.SaveAsync(Root, Path.Combine(dir, $"wizard-{i + 1}.png")))
                        Services.Log($"快照 wizard-{i + 1}");
                }
            }
            else if (App.Board is { } board)
            {
                // 记分台要写权限，先造一个班主任身份（只影响内存会话，不落库）
                var who = new ClassPoints.Core.Member
                {
                    Id = "snapshot-owner",
                    DisplayName = "班主任",
                    Role = ClassPoints.Core.MemberRole.Owner,
                };
                Services.Session.Identify(who, asAdmin: true);

                (int Index, string Name)[] pages =
                [
                    (AppNav.Wall, "board-1-wall"),
                    (AppNav.Score, "board-2-score"),
                    (AppNav.Counters, "board-3-counters"),
                    (AppNav.Lookup, "board-4-lookup"),
                    (AppNav.Entries, "board-5-entries"),
                    (AppNav.Students, "board-6-students"),
                    (AppNav.Exams, "board-7-exams"),
                    (AppNav.Periods, "board-8-periods"),
                    (AppNav.Settings, "board-9-settings"),
                ];

                foreach (var (index, name) in pages)
                {
                    board.Navigate(index);
                    await Task.Delay(1400);
                    if (await Snapshot.SaveAsync(Root, Path.Combine(dir, name + ".png")))
                        Services.Log("快照 " + name);
                }

                // 座位是可选功能：开着才截，关着的时候连页面都不该出现。
                if (Services.Settings.SeatEnabled)
                {
                    board.Navigate(AppNav.Seats);
                    await Task.Delay(1400);
                    if (await Snapshot.SaveAsync(Root, Path.Combine(dir, "board-a3-seats.png")))
                        Services.Log("快照 board-a3-seats");
                }

                await ScrollToBottomAsync();
                if (await Snapshot.SaveAsync(Root, Path.Combine(dir, "board-a2-settings-bottom.png")))
                    Services.Log("快照 board-a2-settings-bottom");

                // 再看一眼"未认人"的明细：操作人列必须整列消失（教室是公开屏幕）
                Services.Session.SignOut();
                board.Navigate(AppNav.Entries);
                await Task.Delay(1400);
                if (await Snapshot.SaveAsync(Root, Path.Combine(dir, "board-5b-entries-anon.png")))
                    Services.Log("快照 board-5b-entries-anon");
            }
            else if (App.Desk is { } desk)
            {
                // 显式写序号（和大屏那边一致）：按数组位置猜序号，座位功能一开关就会整排错位，
                // 于是"desk-9-settings.png"里拍的是周期页，看图的还以为设置页崩了。
                (int Index, string Name)[] pages =
                [
                    (AppNav.Score, "desk-1-score"),
                    (AppNav.Counters, "desk-2-counters"),
                    (AppNav.Wall, "desk-3-wall"),
                    (AppNav.Lookup, "desk-4-lookup"),
                    (AppNav.Entries, "desk-5-entries"),
                    (AppNav.Students, "desk-6-students"),
                    (AppNav.Exams, "desk-7-exams"),
                    (AppNav.Periods, "desk-8-periods"),
                    (AppNav.Settings, "desk-9-settings"),
                ];
                foreach (var (index, name) in pages)
                {
                    desk.Navigate(index);
                    await Task.Delay(1500);
                    if (await Snapshot.SaveAsync(Root, Path.Combine(dir, name + ".png")))
                        Services.Log("快照 " + name);
                }

                // 座位是可选功能：开着才截，关着的时候连页面都不该出现。
                if (Services.Settings.SeatEnabled)
                {
                    desk.Navigate(AppNav.Seats);
                    await Task.Delay(1500);
                    if (await Snapshot.SaveAsync(Root, Path.Combine(dir, "desk-a3-seats.png")))
                        Services.Log("快照 desk-a3-seats");
                }

                // 设置页比一屏长，底部几节（同步、其它）首屏看不到 —— 滚到底再抓一张
                await ScrollToBottomAsync();
                if (await Snapshot.SaveAsync(Root, Path.Combine(dir, "desk-a2-settings-bottom.png")))
                    Services.Log("快照 desk-a2-settings-bottom");

                // 长页面的中间几节（备份、预配置）两头都拍不到，按比例补一张
                if (double.TryParse(Environment.GetEnvironmentVariable("CP_SNAP_SCROLL"),
                        out var fraction) && fraction is > 0 and < 1)
                {
                    await ScrollToFractionAsync(fraction);
                    if (await Snapshot.SaveAsync(Root, Path.Combine(dir, "desk-a3-settings-mid.png")))
                        Services.Log("快照 desk-a3-settings-mid");
                }

                // 当场切一次主题：不重建页面，验证共享画刷与 ThemeResource 都跟着变
                var before = Services.Settings.Theme;
                App.ApplyTheme(before == Theme.AppTheme.Dark ? Theme.AppTheme.Light : Theme.AppTheme.Dark);
                await Task.Delay(900);
                if (await Snapshot.SaveAsync(Root, Path.Combine(dir, "desk-b-theme-switched.png")))
                    Services.Log("快照 desk-b-theme-switched");
                App.ApplyTheme(before);
            }

            // 退出前先离开打印页：WebView2 还活着时直接 Exit 会在终结器里崩。
            App.Desk?.Navigate(AppNav.Score);
            await Task.Delay(400);

            Services.Log("快照完成");
            Application.Current.Exit();
        }
        catch (Exception ex)
        {
            Services.LogError("snapshots", ex);
            Application.Current.Exit();
        }
    }

    /// <summary>
    /// 把可见范围里最长的那个 ScrollViewer 滚到底。自截图只能拍视口，
    /// 长页面必须滚一下才看得到底部。
    /// </summary>
    private async Task ScrollToBottomAsync()
    {
        var best = LongestScrollViewer();
        if (best is null) return;
        best.ChangeView(null, best.ScrollableHeight, null, disableAnimation: true);
        await Task.Delay(500);
    }

    /// <summary>按比例滚到长页面的某一处（0=顶部，1=底部）。</summary>
    private async Task ScrollToFractionAsync(double fraction)
    {
        var best = LongestScrollViewer();
        if (best is null) return;
        best.ChangeView(null, best.ScrollableHeight * System.Math.Clamp(fraction, 0, 1), null,
            disableAnimation: true);
        await Task.Delay(500);
    }

    private ScrollViewer? LongestScrollViewer()
    {
        ScrollViewer? best = null;
        var queue = new Queue<DependencyObject>();
        queue.Enqueue(Root);
        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            var count = VisualTreeHelper.GetChildrenCount(node);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(node, i);
                if (child is ScrollViewer sv && sv.ScrollableHeight > (best?.ScrollableHeight ?? 0))
                    best = sv;
                queue.Enqueue(child);
            }
        }
        return best;
    }

    /// <summary>设置页换形态：整个外壳换掉，进程不重启。数据、会话、日志都还在。</summary>
    public void SwitchToMode(AppMode mode)
    {
        CloseOpenDialogs();
        App.SetMode(mode);
        Services.Settings.OnboardingDone = true;
        ShowShell();
    }

    /// <summary>重新跑一遍向导：形态清空，向导会从"这台电脑做什么"重新问。</summary>
    public void RunWizard()
    {
        CloseOpenDialogs();
        _modeBeforeWizard = App.ModeKnown ? App.Mode : null;
        App.ForgetMode();
        ShowOnboarding(allowCancel: true);
    }

    /// <summary>
    /// 换外壳前先把还开着的 ContentDialog 关掉。
    ///
    /// 对话框挂在窗口的弹出层上，不在页面树里 —— 换掉页面时它不会跟着消失，
    /// 会留在新形态的界面上，而它的 XamlRoot 还指着已经拆掉的那棵树，
    /// 点它的按钮不是没反应就是崩。
    /// </summary>
    private void CloseOpenDialogs()
    {
        if (Root is null) return;
        foreach (var dialog in FindAll<ContentDialog>(Root))
        {
            try { dialog.Hide(); } catch (Exception) { }
        }
    }

    private static IEnumerable<T> FindAll<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T hit) yield return hit;
            foreach (var found in FindAll<T>(child)) yield return found;
        }
    }

    /// <summary>向导中途退出（重新配置时才有这条路）：形态恢复原样，外壳照旧。</summary>
    public void CancelWizard() => ShowShell();

    /// <summary>
    /// 本机重置：删掉本地库再重启。设置、密码、名单都只存在这个文件里，所以删了就是出厂状态。
    ///
    /// 删之前调用方已经做过一次备份 —— 这里不能再做：真正的删除点必须只有一个，
    /// 否则"备份完删库"和"删库"之间会多出一次可能失败的动作。
    /// </summary>
    /// <summary>
    /// 换库之后重新打开程序：恢复备份、导入整包都会替换掉数据库文件，
    /// 而内存里的会话、周期、形态还是旧的 —— 不重启就会在新库上写旧身份。
    /// </summary>
    public void RestartApp() => Relaunch();

    public void ResetLocalAndRestart()
    {
        var dbPath = Services.Db.DbPath;
        var currentRoot = Path.GetDirectoryName(Path.GetFullPath(dbPath))!;

        // 先把同步泵停掉：重置过程中正在跑的那一轮会把 sync.json 重新写回来，
        // 重启后程序就又能自动接上云端，把名单和流水整份拉回来 —— 老师看到的就是
        // "重置了，数据还在"。停泵要放在删文件之前。
        try { Services.Pump.Stop(); }
        catch (Exception ex) { Services.LogError("reset-local-pump", ex); }

        try { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); }
        catch (Exception ex) { Services.LogError("reset-local-pool", ex); }

        // 要清的目录可能不止一个。
        //
        // 数据目录有三条来源（指针文件、程序目录\data、%LOCALAPPDATA%），而"搬家"一直是
        // **复制、不删原件**：老用户的旧库还留在 AppData 里。只删当前这一份的话，
        // 重启时 DataPaths.Resolve 发现程序目录没库、AppData 里有一份，会把它当成
        // "老用户的旧数据"再搬回来 —— 这正是「本机重置好像没用」的根因：
        // 删掉的是新库，打开的是几个月前那份旧库，名单一条不少。
        var roots = new List<string> { currentRoot };

        // 给了 --db 就只认那一个库：参数是明确的，别去动这台机器上别的副本。
        if (string.IsNullOrWhiteSpace(App.Options.DbPath))
        {
            roots.Add(DataPaths.PortableRoot(AppContext.BaseDirectory));
            roots.Add(DataPaths.LocalRoot(DataPaths.LocalAppData));

            foreach (var pointer in new[]
                     {
                         DataPaths.PointerFile(AppContext.BaseDirectory),
                         DataPaths.LocalPointerFile(DataPaths.LocalAppData),
                     })
            {
                try
                {
                    if (File.Exists(pointer))
                    {
                        var target = File.ReadAllText(pointer).Trim();
                        if (target.Length > 0) roots.Add(target);
                    }
                }
                catch (Exception) { /* 读不出就算了，另外两条路已经覆盖了绝大多数情况 */ }
            }
        }

        var removed = false;
        foreach (var root in roots
                     .Where(r => !string.IsNullOrWhiteSpace(r))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var ok = EraseDataAt(root);
            if (string.Equals(root, currentRoot, StringComparison.OrdinalIgnoreCase)) removed = ok;
        }

        // 指针和"我搬过"的记录也一起清：留着它们，下次启动还会把旧目录认成自己的数据。
        foreach (var marker in new[]
                 {
                     DataPaths.AdoptFile(DataPaths.LocalAppData),
                     DataPaths.PointerFile(AppContext.BaseDirectory),
                     DataPaths.LocalPointerFile(DataPaths.LocalAppData),
                 })
        {
            try { if (File.Exists(marker)) File.Delete(marker); }
            catch (Exception ex) { Services.LogError("reset-local-marker", ex); }
        }

        if (!removed)
        {
            // 当前库既删不掉也挪不动（同一份数据开了两个窗口、杀软正在扫）：就地清空。
            // 表全删、结构版本归零，下次启动重新建表 + 播种 —— 对老师来说结果一样。
            try
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                Services.Db.WipeInPlace();
            }
            catch (Exception wipe)
            {
                Services.LogError("reset-local-wipe", wipe);
                throw;   // 两条路都断了才真的没辙，交给调用方报给用户
            }
        }

        // 库清干净了，旧配对也得断：否则新班级会顶着旧班级码去同步。
        try { Services.Sync.SignOut(); }
        catch (Exception ex) { Services.LogError("reset-local-sync", ex); }

        Services.Log(removed
            ? $"本机重置：已清空 {roots.Count} 个候选目录"
            : "本机重置：库文件占用中，已就地清空");
        Relaunch();
    }

    /// <summary>
    /// 把一个数据目录里"会让程序认为这里还有数据"的文件清掉。
    ///
    /// 删不掉就改名（加 .reset-时间戳）：判断依据是 `classpoints.db` 存不存在，
    /// 改名之后 DataPaths 就不会再把这份旧库搬回来。改名也失败才返回 false。
    /// </summary>
    private static bool EraseDataAt(string root)
    {
        var names = new[]
        {
            DataPaths.DbName, DataPaths.DbName + "-wal", DataPaths.DbName + "-shm",
            "sync.json", "session.bin", "recovery.bin",
        };

        var ok = true;
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");

        foreach (var name in names)
        {
            var path = Path.Combine(root, name);
            try
            {
                if (!File.Exists(path)) continue;
                File.Delete(path);
            }
            catch (Exception)
            {
                try
                {
                    if (File.Exists(path)) File.Move(path, $"{path}.reset-{stamp}", overwrite: true);
                }
                catch (Exception)
                {
                    ok = false;
                }
            }
        }

        return ok;
    }

    private static void Relaunch()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
            {
                // 带上原来的参数再启动：用 --db 指过库的机器，重开后不能偷偷换成默认库。
                var args = Environment.GetCommandLineArgs().Skip(1)
                    .Select(a => a.Contains(' ') ? $"\"{a}\"" : a);

                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = string.Join(' ', args),
                    UseShellExecute = true,
                });
            }
        }
        catch (Exception ex)
        {
            App.Services?.LogError("relaunch", ex);
        }

        Environment.Exit(0);
    }

    private void ShowOnboarding(bool allowCancel = false)
    {
        var onboarding = new Onboarding.OnboardingView { AllowCancel = allowCancel };
        onboarding.Completed += (_, _) =>
        {
            Services.Settings.OnboardingDone = true;
            ShowShell();
        };
        onboarding.Cancelled += (_, _) =>
        {
            // 取消就把形态还原：向导一打开就清了它，否则下次启动会再问一遍
            if (_modeBeforeWizard is AppMode mode) App.SetMode(mode);
            Services.Settings.OnboardingDone = true;
            _modeBeforeWizard = null;
            ShowShell();
        };
        Host.Content = onboarding;
        MaybeRunSnapshots();
    }

    private AppMode? _modeBeforeWizard;

    // ---------------- 窗口尺寸与标题栏 ----------------

    private void RestoreWindow()
    {
        var settings = Services.Settings;
        var (w, h, x, y) = settings.GetWindow();

        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var board = App.Mode == AppMode.Board;

        if (w <= 0 || h <= 0)
        {
            w = board ? System.Math.Min(1920, area.Width) : System.Math.Min(1360, area.Width);
            h = board ? System.Math.Min(1048, area.Height) : System.Math.Min(880, area.Height);
        }

        w = System.Math.Clamp(w, board ? 1024 : 900, area.Width);
        h = System.Math.Clamp(h, board ? 700 : 600, area.Height);

        var left = x is int px ? System.Math.Clamp(px, area.X, area.X + area.Width - w) : area.X + (area.Width - w) / 2;
        var top = y is int py ? System.Math.Clamp(py, area.Y, area.Y + area.Height - h) : area.Y + (area.Height - h) / 2;

        AppWindow.MoveAndResize(new RectInt32(left, top, w, h));
    }

    /// <summary>外观切换后重画一次标题栏按钮的深浅，否则浅色主题上会留三个白图标。</summary>
    public void RefreshTitleBar() => ConfigureTitleBar();

    private void ConfigureTitleBar()
    {
        if (!AppWindowTitleBar.IsCustomizationSupported()) return;

        var bar = AppWindow.TitleBar;
        bar.BackgroundColor = Colors.Transparent;
        bar.InactiveBackgroundColor = Colors.Transparent;
        bar.ButtonBackgroundColor = Colors.Transparent;
        bar.ButtonInactiveBackgroundColor = Colors.Transparent;

        if (Pal.IsDark)
        {
            bar.ButtonForegroundColor = Colors.White;
            bar.ButtonInactiveForegroundColor = Pal.Secondary.Color;
            bar.ButtonHoverBackgroundColor = Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF);
            bar.ButtonPressedBackgroundColor = Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF);
        }
        else
        {
            bar.ButtonForegroundColor = Pal.Primary.Color;
            bar.ButtonInactiveForegroundColor = Pal.Secondary.Color;
            bar.ButtonHoverBackgroundColor = Color.FromArgb(0x14, 0x00, 0x00, 0x00);
            bar.ButtonPressedBackgroundColor = Color.FromArgb(0x22, 0x00, 0x00, 0x00);
        }
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        try
        {
            var pos = AppWindow.Position;
            var size = AppWindow.Size;
            Services.Settings.SetWindow(size.Width, size.Height, pos.X, pos.Y);
            Services.Reader.Dispose();
            Services.Log("窗口关闭");
        }
        catch (Exception ex)
        {
            Services?.LogError("window-close", ex);
        }
    }
}
