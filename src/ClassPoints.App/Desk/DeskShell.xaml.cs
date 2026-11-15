using ClassPoints.App.Services;
using ClassPoints.App.Theme;
using ClassPoints.Core;
using ClassPoints.Data;
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
    private readonly Dictionary<int, NavigationViewItem> _navItems = new();
    private TaskCompletionSource<Member?>? _identifyTcs;
    private int _index = -1;
    private bool _navSyncing;

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

        if (_index < 0) Navigate(Session.ReadOnly ? AppNav.Wall : AppNav.Score);
        Services.Pump.Start();

        // 读卡器启动：优先用记下的端口；没记过时，只在"像是 USB 读卡器"的口存在时才自动找。
        // 这条底线不能撤：办公室电脑的 COM1 常常是主板串口，无脑探一遍，
        // 设置页一开机就顶着红字"读卡器没有回应"——什么都没配就先报错，比不报错更糟。
        try
        {
            var auto = string.IsNullOrWhiteSpace(Services.Settings.ReaderPort)
                ? Pn532Serial.GuessUsbPort()
                : Services.Settings.ReaderPort;
            if (!string.IsNullOrWhiteSpace(auto))
                Services.Reader.Start(auto, Services.Settings.ReaderBaud, Services.Settings.ReaderProtocol);
        }
        catch (Exception ex)
        {
            Services.LogError("reader-start", ex);
        }

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
            Services.Reader.Stop();
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

        // 身份没了却停在需要身份的页面（记分台、名单…）没有意义：回看板。
        // 只读设备走的是同一条路，只是话术不同 —— 它不是"退出了"，是"还不许写"。
        if (!Session.CanWrite && AppNav.NeedsIdentity(_index))
        {
            Navigate(AppNav.Wall);
            Toast(Session.ReadOnly ? "这台电脑还在等班主任通过，先回看板" : "身份已退出，回到看板");
        }
    });

    // ================= 导航 =================

    private void BuildNavigation()
    {
        // 清空 / 重建期间不要响应选中变化：被移除的那一项会触发一次 SelectionChanged，
        // 那时 Tag 还是旧序号，会把人带到别的页面去。
        _navSyncing = true;
        Nav.MenuItems.Clear();
        _navItems.Clear();

        foreach (var (index, title, glyph) in AppNav.ForDisplay(Services.Settings.SeatEnabled))
        {
            var item = new NavigationViewItem
            {
                Content = title,
                Tag = index,
                Icon = new FontIcon
                {
                    Glyph = glyph,
                    FontFamily = new FontFamily(IconCatalog.FontFamily),
                },
            };

            Nav.MenuItems.Add(item);
            _navItems[index] = item;
        }

        _navSyncing = false;
    }

    /// <summary>
    /// 原生 NavigationView 的选中回调。权限不够时要把它弹回原来那一项 ——
    /// 否则侧栏会停在「设置」上，右边还是记分台。
    /// </summary>
    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_navSyncing) return;
        if (args.SelectedItemContainer?.Tag is not int index) return;
        if (index == _index) return;

        _ = NavigateAsync(index);
    }

    private void SelectWithoutNotify(int index)
    {
        _navSyncing = true;
        if (_navItems.TryGetValue(index, out var item)) Nav.SelectedItem = item;
        _navSyncing = false;
    }

    private async Task NavigateAsync(int index)
    {
        // 管理类页面要管理态（班委能记分，但不能改名单/口径/设置）
        if (AppNav.NeedsAdmin(index))
        {
            if (!await EnsureIdentifiedAsync(requireAdmin: true))
            {
                SelectWithoutNotify(_index);
                return;
            }
        }
        else if (AppNav.NeedsIdentity(index) && !Session.CanWrite)
        {
            if (!await EnsureIdentifiedAsync())
            {
                SelectWithoutNotify(_index);
                return;
            }
        }
        Navigate(index);
    }

    public void Navigate(int index)
    {
        _index = index;
        var page = DeskPages.Map(index);
        if (DeskFrame.CurrentSourcePageType != page)
        {
            var ok = DeskFrame.Navigate(page);
            // 导航失败时 Frame 会安静地停在原页面（侧栏已经跳过去了，右边还是旧的）。
            // 这种"看起来点了没反应"最难查，所以留一行日志。
            Services.Log($"导航 {index} → {page.Name} ok={ok} 现={DeskFrame.CurrentSourcePageType?.Name ?? "null"}");
        }
        SelectWithoutNotify(index);
        Refresh();
    }

    /// <summary>设置页改了形态之后重挂外壳：同一个 exe 换角色，不用重开程序。</summary>
    public void ReloadShell()
    {
        _index = -1;
        Navigate(AppNav.Score);
    }

    public void Toast(string message, double seconds = 3) => ToastLayer.Show(message, seconds);

    public void RefreshChrome() => Refresh();

    /// <summary>
    /// 设置里开关了可选功能之后重画导航。停在当前页；当前页已经不在导航里
    /// （座位刚被关掉）就回设置 —— 不然侧栏没有任何一项是选中的，看着像卡死。
    /// </summary>
    public void RebuildNavigation()
    {
        BuildNavigation();

        if (_index >= 0 && !_navItems.ContainsKey(_index))
        {
            Navigate(AppNav.Settings);
            return;
        }

        SelectWithoutNotify(_index);
    }

    private void Refresh()
    {
        ClassNameText.Text = Services.ClassName;
        PeriodText.Text = Services.PeriodName;
        // 这里以前写死「本机存储」：明明已经连上云端，侧栏还是一句话否定它。
        // 状态归会话层一处管（记忆卡、角标、大屏顶栏都读它），别再在这里猜。
        ModeText.Text = App.Mode.ToDisplay() + " · " + Session.SyncText;

        var last = Services.Settings.LastBackupAt;
        BackupText.Text = last <= 0
            ? "还没有备份"
            : $"上次备份 {Clock.LocalDate(last):MM-dd HH:mm}";

        // 侧栏底部只有一两行的位置：形态已经写着「本机存储」，这里再缀一次就被裁掉了
        VersionText.Text = "班级积分 v" + AppServices.AppVersion;
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

    private void OnUpdateBadgeClick(object sender, RoutedEventArgs e) => _ = NavigateAsync(AppNav.Settings);

    // ================= 认人 =================

    public async Task<bool> EnsureIdentifiedAsync(bool requireAdmin = false)
    {
        if (Session.CanWrite && (!requireAdmin || Session.IsAdmin)) return true;

        // 待审设备：不走下面"还没设过密码就直接放行"的兜底
        if (Session.ReadOnly)
        {
            Toast("这台电脑还在等班主任通过，现在只能查看", 6);
            return false;
        }

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
            if (!Services.HasManagerPin)
            {
                Session.EnterAdmin();
                Toast("还没设管理密码，先以管理态进来；建议到设置里补一个");
                return true;
            }

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

    public void ExitIdentity()
    {
        if (Session.Current is null) return;

        var name = Session.Current.DisplayName;
        Session.SignOut();
        RefreshChrome();
        Toast($"{name} 已退出，下次记分要重新认人", 5);
    }

    public Task<bool> AskPinAsync(string title, string sub, bool requireManager)
    {
        // 待审设备：认人没有任何意义，写操作会被服务端 403 挡回来。
        // 在这里一句话说清楚，比让老师输完密码再看见满屏灰按钮好。
        if (Session.ReadOnly)
        {
            Toast("这台电脑还在等班主任通过，现在只能查看", 6);
            return Task.FromResult(false);
        }

        var tcs = new TaskCompletionSource<bool>();

        // 密码框开着的时候也收卡：卡本身就是身份凭证，班委没必要为了记一次分背一串数字。
        // 订阅是临时的 —— 面板一关就摘掉，不和外壳别处的刷卡逻辑抢同一张卡。
        Action<CardRead>? onCard = null;

        void Detach()
        {
            Pin.Completed -= OnCompleted;
            Pin.Cancelled -= OnCancelled;
            if (onCard is not null)
            {
                Services.Reader.CardDetected -= onCard;
                onCard = null;
            }
        }

        void Finish(bool ok)
        {
            Detach();
            Pin.Hide();
            tcs.TrySetResult(ok);
        }

        void OnCompleted(string _) => Finish(true);
        void OnCancelled() => Finish(false);

        var readerOn = Services.Reader.Status.IsRunning;
        Pin.ShowVerify(title, readerOn ? sub + "，也可以刷卡。" : sub, pin =>
        {
            if (Session.IsPinLocked) return $"请等 {Session.PinLockSecondsLeft} 秒再试";

            var member = Session.VerifyPin(pin);
            if (member is null) return "密码不对";
            if (requireManager && !member.IsManager) return "这不是班主任或班长的密码";

            Session.Identify(member, asAdmin: member.IsManager);
            return null;
        });

        if (readerOn)
        {
            onCard = read => DispatcherQueue.TryEnqueue(() =>
            {
                if (tcs.Task.IsCompleted) return;

                var member = Services.Members.FindByCard(read.Uid);
                if (member is null)
                {
                    Pin.ShowError($"这张卡还没登记（{read.ShortUid}）");
                    return;
                }
                if (requireManager && !member.IsManager)
                {
                    Pin.ShowError("这不是班主任或班长的卡");
                    return;
                }

                Session.Identify(member, asAdmin: member.IsManager);
                IdentityChip.Refresh();
                Finish(true);
            });
            Services.Reader.CardDetected += onCard;
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
        return await Dialogs.ShowAsync(dialog) == ContentDialogResult.Primary;
    }
}
