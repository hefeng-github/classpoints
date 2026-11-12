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
            await _startupDialog.ShowAsync();
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

        // 每天首次启动自动备份。设置页、侧栏和说明文件都写着这一条，之前却没有任何调用点 ——
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
                    (AppNav.Print, "board-8-print"),
                    (AppNav.Periods, "board-9-periods"),
                    (AppNav.Settings, "board-a-settings"),
                ];

                foreach (var (index, name) in pages)
                {
                    board.Navigate(index);
                    // 打印页的预览是 WebView2 现渲染的，多等一会儿
                    await Task.Delay(index == AppNav.Print ? 5000 : 1400);
                    if (await Snapshot.SaveAsync(Root, Path.Combine(dir, name + ".png")))
                        Services.Log("快照 " + name);
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
                string[] names = ["desk-1-score", "desk-2-counters", "desk-3-wall", "desk-4-lookup",
                                  "desk-5-entries", "desk-6-students", "desk-7-exams", "desk-8-print",
                                  "desk-9-periods", "desk-a-settings"];
                for (var i = 0; i < names.Length; i++)
                {
                    desk.Navigate(i);
                    // 打印页的预览是 WebView2 现渲染的，1.5 秒只够它起进程，抓到的会是"正在生成预览"。
                    await Task.Delay(i == AppNav.Print ? 5000 : 1500);
                    if (await Snapshot.SaveAsync(Root, Path.Combine(dir, names[i] + ".png")))
                        Services.Log("快照 " + names[i]);
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
        App.SetMode(mode);
        Services.Settings.OnboardingDone = true;
        ShowShell();
    }

    /// <summary>重新跑一遍向导：形态清空，向导会从"这台电脑做什么"重新问。</summary>
    public void RunWizard()
    {
        _modeBeforeWizard = App.ModeKnown ? App.Mode : null;
        App.ForgetMode();
        ShowOnboarding(allowCancel: true);
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

        try
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                var path = dbPath + suffix;
                if (File.Exists(path)) File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            Services.LogError("reset-local", ex);

            // 删不掉就改名让它消失：下次启动会新建一个空库，旧文件留在旁边由人取舍
            try
            {
                File.Move(dbPath, dbPath + ".old-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), overwrite: true);
            }
            catch (Exception moveError)
            {
                Services.LogError("reset-local-move", moveError);
                throw;
            }
        }

        Relaunch();
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
