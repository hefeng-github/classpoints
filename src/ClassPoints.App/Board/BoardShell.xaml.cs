using ClassPoints.App.Services;
using ClassPoints.App.Theme;
using ClassPoints.Core;
using ClassPoints.Data;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ClassPoints.App.Board;

/// <summary>
/// 大屏端外壳。它管的不是"页面长什么样"，而是"页面之间怎么走"：
/// 一组共用导航、四条超时、一个常驻的读卡监听。
///
/// 和大屏以前那版的区别：导航项、可用页面和电脑端完全一致。
/// 大屏上缺"名单 / 考试 / 打印 / 周期"的唯一后果是老师得跑去办公室，
/// 而这套东西最怕的就是"记一笔分要换台机器"。
/// </summary>
public sealed partial class BoardShell : UserControl, IShellHost
{
    private static readonly SolidColorBrush ActiveBg = Pal.AccentBg;
    private static readonly SolidColorBrush ActiveFg = Pal.Accent;
    private static readonly SolidColorBrush IdleFg = Pal.Secondary;
    private static readonly SolidColorBrush Transparent = new(Windows.UI.Color.FromArgb(0x00, 0, 0, 0));

    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Dictionary<int, Button> _railButtons = new();
    private readonly Dictionary<int, Button> _paneButtons = new();

    private TaskCompletionSource<Member?>? _identifyTcs;
    private int _currentIndex = AppNav.Wall;
    private bool _paneOpen;
    private string _clockMinute = "";

    public BoardShell()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public AppServices Services => AppServices.Instance;

    public BoardSession Session => Services.Session;

    public int CurrentIndex => _currentIndex;

    public string DebugFrameContent =>
        $"{ContentFrame.Content?.GetType().Name ?? "null"} " +
        $"{ContentFrame.ActualWidth}x{ContentFrame.ActualHeight} " +
        $"vis={ContentFrame.Visibility} idx={_currentIndex}";

    // ================= 生命周期 =================

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            LoadInternal();
        }
        catch (Exception ex)
        {
            Services.LogError("board-shell-loaded", ex);
            Toast("界面初始化失败：" + ex.Message, 8);
        }
    }

    private void LoadInternal()
    {
        Sounds.Enabled = Services.Settings.SoundOn;
        BuildNavigation();

        HeadModeText.Text = "教室大屏";
        RefreshHeader();
        RefreshClock();

        Session.Changed += OnSessionChanged;
        _tick.Tick += OnTick;
        _tick.Start();

        // 同步只在后台跑，永不挡在"加一分"的路径上
        Services.Pump.Start();

        // 常驻读卡监听：在看板前碰一下卡就直接进记分台，这是班委最常用的路径
        Services.Reader.CardDetected += OnCardDetected;
        try
        {
            Services.Reader.Start(string.IsNullOrWhiteSpace(Services.Settings.ReaderPort)
                ? null
                : Services.Settings.ReaderPort);
        }
        catch (Exception ex)
        {
            Services.LogError("reader-start", ex);
        }

        // 有身份才有记分台；匿名时停在看板（看板是停留页，不是"主页"）
        Navigate(Session.CanWrite ? AppNav.Score : AppNav.Wall);
        Services.Log("大屏外壳初始化完成");
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _tick.Stop();
        Session.Changed -= OnSessionChanged;
        Services.Reader.CardDetected -= OnCardDetected;
        Services.Reader.Stop();
    }

    // ================= 导航 =================

    /// <summary>
    /// 十项导航是数据驱动的：加一页只改 <see cref="AppNav.Items"/>，
    /// 两个外壳各自拿到同样的条目。展开面板里图标占固定 56 宽，
    /// 文字从同一列开始 —— 展开后不会出现"图标缩进变了、文字跟着跳"。
    /// </summary>
    private void BuildNavigation()
    {
        RailStack.Children.Clear();
        PaneStack.Children.Clear();
        _railButtons.Clear();
        _paneButtons.Clear();

        foreach (var (index, title, glyph) in AppNav.Items)
        {
            var rail = new Button
            {
                Style = (Style)Application.Current.Resources["RailButtonStyle"],
                Tag = index,
                Content = new FontIcon { Glyph = glyph, FontSize = 20 },
            };
            ToolTipService.SetToolTip(rail, title);
            rail.Click += OnRailClick;
            RailStack.Children.Add(rail);
            _railButtons[index] = rail;

            var pane = new Button
            {
                Style = (Style)Application.Current.Resources["PaneItemStyle"],
                Tag = index,
            };
            var row = new Grid { ColumnSpacing = 4 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var icon = new FontIcon
            {
                Glyph = glyph,
                FontSize = 18,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = IdleFg,
            };
            Grid.SetColumn(icon, 0);
            row.Children.Add(icon);

            var text = new TextBlock
            {
                Text = title,
                FontSize = 16,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Pal.Primary,
            };
            Grid.SetColumn(text, 1);
            row.Children.Add(text);

            pane.Content = row;
            pane.Click += OnPaneItemClick;
            PaneStack.Children.Add(pane);
            _paneButtons[index] = pane;
        }
    }

    private void OnRailClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not int index) return;
        _ = NavigateAsync(index);
    }

    private void OnPaneItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not int index) return;
        ClosePane();
        _ = NavigateAsync(index);
    }

    private async Task NavigateAsync(int index)
    {
        // 会改数据的页面先认人；看板/查分/明细随便看
        if (AppNav.NeedsIdentity(index) && !Session.CanWrite)
        {
            var ok = await EnsureIdentifiedAsync();
            if (!ok) return;
        }
        Navigate(index);
    }

    public void Navigate(int index)
    {
        _currentIndex = index;
        var page = BoardPages.Map(index);
        if (ContentFrame.CurrentSourcePageType != page) ContentFrame.Navigate(page);
        RefreshRail();
    }

    private void RefreshRail()
    {
        foreach (var (index, button) in _railButtons)
        {
            var active = index == _currentIndex;
            button.Background = active ? ActiveBg : Transparent;
            if (button.Content is FontIcon icon) icon.Foreground = active ? ActiveFg : IdleFg;
        }

        foreach (var (index, button) in _paneButtons)
        {
            var active = index == _currentIndex;
            button.Background = active ? ActiveBg : Transparent;
            if (button.Content is not Grid grid) continue;
            foreach (var child in grid.Children)
            {
                if (child is FontIcon icon) icon.Foreground = active ? ActiveFg : IdleFg;
                else if (child is TextBlock text) text.Foreground = active ? ActiveFg : Pal.Primary;
            }
        }
    }

    private void OnPaneToggleClick(object sender, RoutedEventArgs e) => TogglePane();

    private void TogglePane()
    {
        _paneOpen = !_paneOpen;
        PaneLayer.Visibility = _paneOpen ? Visibility.Visible : Visibility.Collapsed;
        PaneClassText.Text = Services.ClassTitle;
    }

    private void ClosePane()
    {
        _paneOpen = false;
        PaneLayer.Visibility = Visibility.Collapsed;
    }

    private void OnPaneScrimTapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e) => ClosePane();

    // ================= 会话与超时 =================

    private void RefreshHeader()
    {
        HeadClassText.Text = Services.ClassName;
        HeadPeriodText.Text = Services.PeriodName;
        PaneClassText.Text = Services.ClassTitle;
        HeadSyncText.Text = Session.SyncText;
    }

    private void OnSessionChanged()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            RefreshHeader();
            IdentityChip.Refresh();

            // 身份被清掉时，停在需要身份的页面上没有意义
            if (!Session.CanWrite && AppNav.NeedsIdentity(_currentIndex))
                Navigate(AppNav.Wall);
        });
    }

    private void OnTick(object? sender, object e)
    {
        var settings = Services.Settings;

        if (Session.ShouldAdminExit(settings.LockTimeoutSec))
        {
            Session.ExitAdmin();
            Toast("已退出管理模式，仍可记分");
        }

        // 有选中项不计时：老师框选 12 人后转身喊一句话，30 秒静默很正常
        if (Session.ShouldIdleExit(settings.IdleTimeoutSec) &&
            _currentIndex is AppNav.Score or AppNav.Counters)
        {
            Session.SignOut();
            Navigate(AppNav.Wall);
            Toast("已回到看板");
        }

        RefreshClock();
    }

    /// <summary>状态条上的时钟。每秒一次，只在分数变化时才写 Text（避免无谓的布局）。</summary>
    private void RefreshClock()
    {
        var now = DateTime.Now;
        var minute = now.ToString("HH:mm");
        if (minute == _clockMinute) return;

        _clockMinute = minute;
        HeadClockText.Text = minute;
        HeadDateText.Text = now.ToString("M 月 d 日") + " " + Weekday(now.DayOfWeek);
    }

    private static string Weekday(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => "周一",
        DayOfWeek.Tuesday => "周二",
        DayOfWeek.Wednesday => "周三",
        DayOfWeek.Thursday => "周四",
        DayOfWeek.Friday => "周五",
        DayOfWeek.Saturday => "周六",
        _ => "周日",
    };

    // ================= 认人 =================

    private void OnCardDetected(CardRead read)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            var member = Services.Members.FindByCard(read.Uid);
            HandleCard(member, read);
        });
    }

    private void HandleCard(Member? member, CardRead read)
    {
        if (member is null)
        {
            Toast($"这张卡还没登记（{read.ShortUid}）");
            if (_identifyTcs is not null)
                IdentifyHint.Text = $"这张卡（{read.ShortUid}）没登记。到「设置 → 成员与权限」加一下。";
            Sounds.Play(Cue.Error);
            return;
        }

        if (_identifyTcs is not null)
        {
            CompleteIdentify(member);
            return;
        }

        // 已认人时再刷一张卡 = 换人，不是叠加。不提示的话，后一个人会以为前一个人的分是自己的。
        if (Session.Current is not null && Session.Current.Id != member.Id)
        {
            _ = ConfirmSwitchAsync(member);
            return;
        }

        ApplyIdentity(member);
        Toast($"{member.DisplayName} · {member.Role.ToDisplay()}");
        Navigate(AppNav.Score);
    }

    /// <summary>
    /// 认人之后按角色给权限：班主任和班长直接进管理模式，班委只到记分。
    /// 「输谁的密码就是谁」这句话的落点就在这一个方法里。
    /// </summary>
    private void ApplyIdentity(Member member)
    {
        Session.Identify(member, asAdmin: member.IsManager);
        Sounds.Play(Cue.Identify);
        IdentityChip.Refresh();
        RefreshHeader();
    }

    private async Task ConfirmSwitchAsync(Member next)
    {
        var previous = Session.Current?.DisplayName ?? "上一个人";
        var confirmed = await ConfirmAsync("换人", $"当前是 {previous}，要换成 {next.DisplayName} 吗？",
            $"换成 {next.DisplayName}", "保持当前");
        if (!confirmed) return;

        ApplyIdentity(next);
        Toast($"已切换为 {next.DisplayName}");
        Navigate(AppNav.Score);
    }

    /// <summary>页面在写操作前调用。已认人直接返回 true。</summary>
    public async Task<bool> EnsureIdentifiedAsync(bool requireAdmin = false)
    {
        if (Session.CanWrite && (!requireAdmin || Session.IsAdmin)) return true;

        if (!Services.HasAnyCredential)
        {
            var owner = Services.Members.Owner() ?? Services.Members.Save(
                new Member { DisplayName = "班主任", Role = MemberRole.Owner });
            Session.Identify(owner, asAdmin: true);
            Toast("还没设过密码，先以班主任身份进来；到「设置 → 密码与恢复码」补一个", 8);
            return true;
        }

        // 已经是班委/其他人，但要进管理 → 要管理密码，而不是重新刷一张卡
        if (requireAdmin && Session.CanWrite)
        {
            var ok = await AskPinAsync("管理模式密码", "改名单、改分值、撤销记录都要这一步", requireManager: true);
            if (ok) Session.EnterAdmin();
            return ok;
        }

        var member = await ShowIdentifyOverlayAsync();
        if (member is null) return false;

        ApplyIdentity(member);
        return !requireAdmin || Session.IsAdmin;
    }

    private Task<Member?> ShowIdentifyOverlayAsync()
    {
        _identifyTcs = new TaskCompletionSource<Member?>();
        IdentifyTitle.Text = "请刷卡或输密码";
        IdentifySub.Text = "谁刷的卡、谁输对了密码，就按谁的身份记。";
        IdentifyHint.Text = "没卡也没密码？到「设置 → 成员与权限」给每个人设一个。";
        IdentifyLayer.Visibility = Visibility.Visible;

        // 30 秒没人理就收起，避免一个模态框永远压在屏幕上
        var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        timeout.Tick += (_, _) =>
        {
            timeout.Stop();
            if (_identifyTcs is not null) CompleteIdentify(null);
        };
        timeout.Start();

        return _identifyTcs.Task;
    }

    private void CompleteIdentify(Member? member)
    {
        var tcs = _identifyTcs;
        _identifyTcs = null;
        IdentifyLayer.Visibility = Visibility.Collapsed;

        if (member is not null) ApplyIdentity(member);
        tcs?.TrySetResult(member);
    }

    private void OnIdentifyCancelClick(object sender, RoutedEventArgs e) => CompleteIdentify(null);

    private async void OnIdentifyPinClick(object sender, RoutedEventArgs e)
    {
        var ok = await AskPinAsync("输入密码", "谁的密码就对，就按谁的身份记", requireManager: false);
        if (!ok) return;

        var member = Session.Current;
        CompleteIdentify(member);
        Navigate(AppNav.Score);
    }

    /// <summary>换人：输谁的密码就换成谁。同一个人在管理态就是退出管理。</summary>
    public async Task<bool> SwitchIdentityAsync()
    {
        if (Session.IsAdmin)
        {
            Session.ExitAdmin();
            Toast("已退出管理模式（身份保留）");
            IdentityChip.Refresh();
            return true;
        }

        var ok = await AskPinAsync("切换身份", "输入谁的密码，就按谁的身份记分", requireManager: false);
        if (!ok) return false;

        Toast($"当前身份：{Session.IdentityText}");
        return true;
    }

    // ================= 复用的小工具 =================

    /// <summary>PIN 校验。返回 true 表示通过并已认人（管理态由调用方决定）。</summary>
    public Task<bool> AskPinAsync(string title, string sub, bool requireManager)
    {
        var tcs = new TaskCompletionSource<bool>();

        Pin.ShowVerify(title, sub, pin =>
        {
            if (Session.IsPinLocked) return $"请等 {Session.PinLockSecondsLeft} 秒再试";

            var member = Session.VerifyPin(pin);
            if (member is null) return "密码不对";

            if (requireManager && !member.IsManager) return "这不是班主任或班长的密码";

            // 换人就换权限：原来的管理态立即失效
            if (Session.Current?.Id != member.Id)
                Session.Identify(member, asAdmin: member.IsManager);
            else if (!requireManager && Session.IsAdmin)
                Session.EnterAdmin();

            IdentityChip.Refresh();
            return null;
        });

        void OnCompleted(string _)
        {
            Pin.Completed -= OnCompleted;
            Pin.Cancelled -= OnCancelled;
            tcs.TrySetResult(true);
        }

        void OnCancelled()
        {
            Pin.Completed -= OnCompleted;
            Pin.Cancelled -= OnCancelled;
            tcs.TrySetResult(false);
        }

        Pin.Completed += OnCompleted;
        Pin.Cancelled += OnCancelled;
        return tcs.Task;
    }

    /// <summary>只收数字不校验（设置新密码时用两次）。</summary>
    public Task<string?> AskDigitsAsync(string title, string sub, int digits = 6)
    {
        var tcs = new TaskCompletionSource<string?>();
        Pin.ShowInput(title, sub, digits);

        void OnCompleted(string pin)
        {
            Pin.Completed -= OnCompleted;
            Pin.Cancelled -= OnCancelled;
            tcs.TrySetResult(pin);
        }

        void OnCancelled()
        {
            Pin.Completed -= OnCompleted;
            Pin.Cancelled -= OnCancelled;
            tcs.TrySetResult(null);
        }

        Pin.Completed += OnCompleted;
        Pin.Cancelled += OnCancelled;
        return tcs.Task;
    }

    public async Task<bool> ConfirmAsync(string title, string message, string primary, string close)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            PrimaryButtonText = primary,
            CloseButtonText = close,
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        var result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary;
    }

    public void Toast(string message, double seconds = 3) => ToastLayer.Show(message, seconds);
}
