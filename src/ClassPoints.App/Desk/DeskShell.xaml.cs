using ClassPoints.App.Services;
using ClassPoints.App.Theme;
using ClassPoints.Core;
using ClassPoints.Data.Update;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ClassPoints.App.Desk;

/// <summary>
/// 电脑端外壳。和大屏端是同一套导航、同一套页面、同一份权限，
/// 只有排版不同（这里是键鼠密度的文字侧栏）。
///
/// 电脑端默认以班主任身份打开 —— 坐在办公室里的人不该为了记一笔分
/// 先输一遍密码；要换人就点侧栏的「切换」，输谁的密码就是谁。
/// </summary>
public sealed partial class DeskShell : UserControl, IShellHost
{
    private static readonly SolidColorBrush ActiveBg = Pal.AccentBg;
    private static readonly SolidColorBrush ActiveFg = Pal.Accent;
    private static readonly SolidColorBrush IdleFg = Pal.Secondary;
    private static readonly SolidColorBrush Transparent = new(Windows.UI.Color.FromArgb(0x00, 0, 0, 0));

    private readonly Dictionary<int, Button> _navButtons = new();
    private TaskCompletionSource<Member?>? _identifyTcs;
    private int _index = -1;

    public DeskShell()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    public AppServices Services => AppServices.Instance;

    private BoardSession Session => Services.Session;

    public int CurrentIndex => _index;

    // ================= 生命周期 =================

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        BuildNavigation();
        EnsureDefaultIdentity();

        if (_index < 0) Navigate(AppNav.Score);
        Services.Pump.Start();
        Refresh();

        Session.Changed += OnSessionChanged;

        // 更新提示跟着外壳走，不跟着页面走：切页不该把它弄丢，也不该重复订阅。
        UpdateCenter.Current.Changed += OnUpdateStateChanged;
        UpdateCenter.Current.Found += OnUpdateFound;
        RefreshUpdateBadge();

        if (Services.Settings.UpdateAutoCheck)
            _ = UpdateCenter.Current.AutoCheckAsync();

        Unloaded += (_, _) =>
        {
            Session.Changed -= OnSessionChanged;
            UpdateCenter.Current.Changed -= OnUpdateStateChanged;
            UpdateCenter.Current.Found -= OnUpdateFound;
        };
    }

    /// <summary>
    /// 电脑端开机即"是班主任"。这是一条产品判断，不是权限漏洞：
    /// 办公室的机器本来就属于班主任，让他每开一次程序输一次密码，
    /// 结果是密码被写在显示器边框上。真要换人，侧栏「切换」随时能换。
    /// </summary>
    private void EnsureDefaultIdentity()
    {
        if (Session.CanWrite) return;
        var owner = Services.DeskOperator;
        Session.Identify(owner, asAdmin: false);
    }

    private void OnSessionChanged() => DispatcherQueue?.TryEnqueue(() =>
    {
        // 电脑端不保留"未认人"状态：换周期、退出管理都会清会话，
        // 而办公室这台机器本来就该是班主任在用 —— 自动恢复，否则会卡在空身份上。
        if (!Session.CanWrite) EnsureDefaultIdentity();

        IdentityChip.Refresh();
        Refresh();

        if (!Session.CanWrite && AppNav.NeedsIdentity(_index))
        {
            Navigate(AppNav.Wall);
            Toast("身份已退出，回到看板");
        }
    });

    // ================= 导航 =================

    private void BuildNavigation()
    {
        NavStack.Children.Clear();
        _navButtons.Clear();

        foreach (var (index, title, glyph) in AppNav.Items)
        {
            var button = new Button
            {
                Style = (Style)Application.Current.Resources["DeskNavButton"],
                Tag = index,
            };

            var row = new Grid { ColumnSpacing = 10 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var icon = new FontIcon
            {
                Glyph = glyph,
                FontSize = 16,
                Width = 24,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = IdleFg,
            };
            Grid.SetColumn(icon, 0);
            row.Children.Add(icon);

            var text = new TextBlock
            {
                Text = title,
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = IdleFg,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Grid.SetColumn(text, 1);
            row.Children.Add(text);

            button.Content = row;
            button.Click += OnNavClick;
            NavStack.Children.Add(button);
            _navButtons[index] = button;
        }
    }

    private void OnNavClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not int index) return;
        _ = NavigateAsync(index);
    }

    private async Task NavigateAsync(int index)
    {
        if (AppNav.NeedsIdentity(index) && !Session.CanWrite)
        {
            var ok = await EnsureIdentifiedAsync();
            if (!ok) return;
        }
        Navigate(index);
    }

    public void Navigate(int index)
    {
        _index = index;
        var page = DeskPages.Map(index);
        if (DeskFrame.CurrentSourcePageType != page) DeskFrame.Navigate(page);
        Refresh();
    }

    /// <summary>设置页改了形态之后重挂外壳：同一个 exe 换角色，不用重开程序。</summary>
    public void ReloadShell()
    {
        _index = -1;
        Navigate(AppNav.Score);
    }

    public void Toast(string message, double seconds = 3) => ToastLayer.Show(message, seconds);

    private void Refresh()
    {
        ClassNameText.Text = Services.ClassName;
        PeriodText.Text = Services.PeriodName;
        ModeText.Text = App.Mode.ToDisplay() + " · 本机存储";

        foreach (var (index, button) in _navButtons)
        {
            var active = index == _index;
            button.Background = active ? ActiveBg : Transparent;
            if (button.Content is not Grid grid) continue;
            foreach (var child in grid.Children)
            {
                if (child is FontIcon icon) icon.Foreground = active ? ActiveFg : IdleFg;
                else if (child is TextBlock text) text.Foreground = active ? ActiveFg : IdleFg;
            }
        }

        var last = Services.Settings.LastBackupAt;
        BackupText.Text = last <= 0
            ? "还没有备份"
            : $"上次备份 {Clock.LocalDate(last):MM-dd HH:mm}";

        VersionText.Text = "班级积分 v" + AppServices.AppVersion + " · 本机存储";
        RefreshUpdateBadge();
    }

    // ================= 更新提示 =================

    private void OnUpdateStateChanged() => DispatcherQueue?.TryEnqueue(RefreshUpdateBadge);

    private void OnUpdateFound(UpdateManifest manifest) =>
        DispatcherQueue?.TryEnqueue(() =>
        {
            RefreshUpdateBadge();

            // 同一版只主动说一次：老师正在记分，被一句话打断两次就会去关掉自动检查。
            if (string.Equals(Services.Settings.UpdateSeenVersion, manifest.Version.ToString(),
                    StringComparison.Ordinal)) return;
            Services.Settings.UpdateSeenVersion = manifest.Version.ToString();
            Toast($"有新版本 {manifest.Version}，点左下角提示安装", 8);
        });

    private void RefreshUpdateBadge()
    {
        var manifest = UpdateCenter.Current.Available;
        if (manifest is null)
        {
            UpdateBadge.Visibility = Visibility.Collapsed;
            return;
        }

        UpdateBadgeText.Text = $"有新版本 {manifest.Version}";
        UpdateBadge.Visibility = Visibility.Visible;
        VersionText.Text = $"班级积分 v{AppServices.AppVersion} · 可更新到 {manifest.Version}";
    }

    private void OnUpdateBadgeClick(object sender, RoutedEventArgs e) => Navigate(AppNav.Settings);

    // ================= 认人 =================

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

        if (requireAdmin && Session.CanWrite)
        {
            var ok = await AskPinAsync("管理模式密码", "改名单、改分值、撤销记录都要这一步", requireManager: true);
            if (ok) Session.EnterAdmin();
            return ok;
        }

        var member = await ShowIdentifyAsync();
        if (member is null) return false;

        Session.Identify(member, asAdmin: member.IsManager);
        return !requireAdmin || Session.IsAdmin;
    }

    private Task<Member?> ShowIdentifyAsync()
    {
        _identifyTcs = new TaskCompletionSource<Member?>();
        IdentifyTitle.Text = "请输入密码";
        IdentifySub.Text = "谁输对了密码，就按谁的身份记。";
        IdentifyLayer.Visibility = Visibility.Visible;
        return _identifyTcs.Task;
    }

    private void CompleteIdentify(Member? member)
    {
        var tcs = _identifyTcs;
        _identifyTcs = null;
        IdentifyLayer.Visibility = Visibility.Collapsed;
        tcs?.TrySetResult(member);
    }

    private void OnIdentifyCancelClick(object sender, RoutedEventArgs e) => CompleteIdentify(null);

    private async void OnIdentifyPinClick(object sender, RoutedEventArgs e)
    {
        var ok = await AskPinAsync("输入密码", "谁的密码就对，就按谁的身份记", requireManager: false);
        CompleteIdentify(ok ? Session.Current : null);
    }

    public async Task<bool> SwitchIdentityAsync()
    {
        if (Session.IsAdmin)
        {
            Session.ExitAdmin();
            Toast("已退出管理模式（身份保留）");
            return true;
        }

        var ok = await AskPinAsync("切换身份", "输入谁的密码，就按谁的身份记分", requireManager: false);
        if (!ok) return false;

        Toast($"当前身份：{Session.IdentityText}");
        return true;
    }

    public Task<bool> AskPinAsync(string title, string sub, bool requireManager)
    {
        var tcs = new TaskCompletionSource<bool>();

        Pin.ShowVerify(title, sub, pin =>
        {
            if (Session.IsPinLocked) return $"请等 {Session.PinLockSecondsLeft} 秒再试";

            var member = Session.VerifyPin(pin);
            if (member is null) return "密码不对";
            if (requireManager && !member.IsManager) return "这不是班主任或班长的密码";

            Session.Identify(member, asAdmin: member.IsManager);
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
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
}
