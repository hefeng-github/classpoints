using ClassPoints.App.Services;
using ClassPoints.App.Theme;
using ClassPoints.Core;
using ClassPoints.Data;
using ClassPoints.Data.Sync;
using ClassPoints.Data.Update;
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

    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Dictionary<int, NavigationViewItem> _navItems = new();

    private TaskCompletionSource<Member?>? _identifyTcs;

    /// <summary>认人浮层的 30 秒自动关闭。必须留一个字段：早先它是方法里的局部变量，
    /// 认人提前完成后计时器还在跑，30 秒后会去关掉「下一次」打开的浮层。</summary>
    private DispatcherTimer? _identifyTimer;
    private int _currentIndex = AppNav.Wall;
    private bool _navSyncing;
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
        Services.Pump.PairingNeeded += OnPairingNeeded;
        Services.Pump.AutoRepaired += OnAutoRepaired;
        Services.Pump.PeriodRelocated += OnPeriodRelocated;
        _tick.Tick += OnTick;
        _tick.Start();

        // 同步只在后台跑，永不挡在"加一分"的路径上
        Services.Pump.Start();

        // 大屏以前压根不检查更新：教室那台机器永远收不到新版本，
        // 只能等老师自己想起来去装。这里补上，和电脑端同一套（弹窗 + 版本去重）。
        UpdateCenter.Current.Found += OnUpdateFound;
        if (Services.Settings.UpdateAutoCheck)
            _ = UpdateCenter.Current.AutoCheckAsync();

        // 常驻读卡监听：在看板前碰一下卡就直接进记分台，这是班委最常用的路径
        Services.Reader.CardDetected += OnCardDetected;
        try
        {
            Services.Reader.Start(string.IsNullOrWhiteSpace(Services.Settings.ReaderPort)
                    ? null
                    : Services.Settings.ReaderPort,
                Services.Settings.ReaderBaud, Services.Settings.ReaderProtocol);
        }
        catch (Exception ex)
        {
            Services.LogError("reader-start", ex);
        }

        // 有身份才有记分台；匿名时停在看板（看板是停留页，不是"主页"）
        Navigate(Session.CanWrite ? AppNav.Score : AppNav.Wall);
        Services.Log("大屏外壳初始化完成");
    }

    /// <summary>凭据失效时说一句：大屏通常没人守着，这句提示是班主任唯一的线索。</summary>
    private void OnPairingNeeded(PairingReason reason) =>
        Toast($"{reason.Title()}：到「设置 → 同步」点「重新配对」", 10);

    /// <summary>凭据自己接上了也留一句：教室里没人守着，出过什么事都要能从这块屏上看出来。</summary>
    private void OnAutoRepaired() => Toast("同步凭据已自动续上", 5);

    /// <summary>周期编号与云端另一个班撞了，已自动换新编号重推。大屏只留一句短的。</summary>
    private void OnPeriodRelocated(int count) => Toast($"周期编号冲突，已自动换新编号重新同步（{count} 个）", 6);

    /// <summary>有新版本就弹一次（同一版只弹一次）。大屏前面通常没人守着，弹窗比角标靠谱。</summary>
    private void OnUpdateFound(UpdateManifest manifest) => UpdatePrompt.NotifyIfNew(manifest);

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _tick.Stop();
        Session.Changed -= OnSessionChanged;
        Services.Pump.PairingNeeded -= OnPairingNeeded;
        Services.Pump.AutoRepaired -= OnAutoRepaired;
        Services.Pump.PeriodRelocated -= OnPeriodRelocated;
        UpdateCenter.Current.Found -= OnUpdateFound;
        Services.Reader.CardDetected -= OnCardDetected;
        Services.Reader.Stop();
    }

    // ================= 导航 =================

    /// <summary>
    /// 导航是数据驱动的：加一页只改 <see cref="AppNav.Items"/>，
    /// 两个外壳各自拿到同样的条目。座位这类可选功能由
    /// <see cref="AppNav.ForDisplay"/> 在关闭时整项摘掉。
    /// 展开面板里图标占固定 56 宽，文字从同一列开始 —— 展开后不会出现"图标缩进变了、文字跟着跳"。
    /// </summary>
    private void BuildNavigation()
    {
        // 重建期间不响应选中变化：被移除的那一项会触发 SelectionChanged。
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

            // 收起时只剩图标，靠工具提示说明这一格是什么
            ToolTipService.SetToolTip(item, title);
            Nav.MenuItems.Add(item);
            _navItems[index] = item;
        }

        _navSyncing = false;
    }

    /// <summary>原生 NavigationView 的选中回调；权限不够就弹回原来那一项。</summary>
    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_navSyncing) return;
        if (args.SelectedItemContainer?.Tag is not int index) return;
        if (index == _currentIndex) return;

        // 选完就收起面板：大屏是给全班看的，展开的侧栏挡住半个屏幕
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
        // 管理类页面要管理态；记分台/事件计数只要有身份；看板/查分/明细随便看
        if (AppNav.NeedsAdmin(index))
        {
            if (!await EnsureIdentifiedAsync(requireAdmin: true))
            {
                SelectWithoutNotify(_currentIndex);
                return;
            }
        }
        else if (AppNav.NeedsIdentity(index) && !Session.CanWrite)
        {
            if (!await EnsureIdentifiedAsync())
            {
                SelectWithoutNotify(_currentIndex);
                return;
            }
        }
        Navigate(index);
    }

    public void Navigate(int index)
    {
        _currentIndex = index;
        var page = BoardPages.Map(index);
        if (ContentFrame.CurrentSourcePageType != page) ContentFrame.Navigate(page);
        SelectWithoutNotify(index);
    }

    // ================= 会话与超时 =================

    private void RefreshHeader()
    {
        HeadClassText.Text = Services.ClassName;
        HeadPeriodText.Text = Services.PeriodName;
        HeadSyncText.Text = Session.SyncText;
    }

    private void OnSessionChanged()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            RefreshHeader();
            IdentityChip.Refresh();

            // 身份被清掉时，停在需要身份的页面上没有意义。
            // 只读设备根本进不来这些页（导航闸门已经挡了），所以这里不用再分情况。
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
            // 超时退出也要跟着导航：NeedsAdmin 页（设置/名单/考试/周期/座位）上
            // 身份条已经显示退出了、按钮全灰，人还站在那页上不知所措 ——
            // 和「退出身份」回记分台是同一条规则。
            if (AppNav.NeedsAdmin(_currentIndex)) Navigate(AppNav.Score);
            Toast("已退出管理模式，仍可记分");
        }

        // 有选中项不计时：老师框选 12 人后转身喊一句话，30 秒静默很正常
        if (Session.ShouldIdleExit(settings.IdleTimeoutSec) &&
            _currentIndex is AppNav.Score or AppNav.Counters)
        {
            Session.SignOut();
            Navigate(AppNav.Wall);
            // 说清"为什么回去了"，并且多留一会儿：演示时这句一闪而过，
            // 台下只会看到"画面自己跳了"。
            Toast($"已回到看板（{settings.IdleTimeoutSec} 秒没有操作）", 6);
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
        // 密码框正开着：这张卡归 AskPinAsync 的临时订阅管（它还要判"是不是管理员的卡"），
        // 这里必须让路，否则一次刷卡会连着弹「换人确认」，把密码框压在下面。
        if (Pin.IsOpen) return;

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

        // 待审设备：认人没有意义，写了也会被服务端 403 挡回来。
        // 单独一条，是为了不走下面"还没设过密码就直接放行"的兜底。
        if (Session.ReadOnly)
        {
            Toast("这台设备还在等班主任通过，现在只能查看", 6);
            return false;
        }

        if (!Services.HasAnyCredential)
        {
            // 没有班主任行时只给内存占位，不落库：落库就会推到云端，
            // 多台设备先后进来一次就是多个班主任（见 Member.PlaceholderOwnerId）。
            var owner = Services.Members.Owner()
                ?? new Member { Id = Member.PlaceholderOwnerId, DisplayName = "班主任", Role = MemberRole.Owner };
            Session.Identify(owner, asAdmin: true);
            Toast("还没设过密码，先以班主任身份进来；到「设置 → 密码与恢复码」补一个", 8);
            return true;
        }

        // 已经是班委/其他人，但要进管理 → 要管理密码，而不是重新刷一张卡
        if (requireAdmin && Session.CanWrite)
        {
            // 全班只有卡、没有任何管理密码时，校验永远过不去。放行进管理态，
            // 否则这台机器的名单和设置会彻底打不开（提示去补一个密码）。
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

        var member = await ShowIdentifyOverlayAsync();
        if (member is null) return false;

        ApplyIdentity(member);
        return !requireAdmin || Session.IsAdmin;
    }

    private Task<Member?> ShowIdentifyOverlayAsync()
    {
        // 上一轮的请求还没人应答就先收掉：直接覆盖 _identifyTcs 会让第一个 await
        // 永远没人应答（卡片 150ms 一刷、页面快速切换都会撞上），调用方挂死在那条链上。
        if (_identifyTcs is not null) CompleteIdentify(null);

        _identifyTcs = new TaskCompletionSource<Member?>();
        IdentifyTitle.Text = "请刷卡或输密码";
        IdentifySub.Text = "谁刷的卡、谁输对了密码，就按谁的身份记。";
        IdentifyHint.Text = "没卡也没密码？到「设置 → 成员与权限」给每个人设一个。";
        IdentifyLayer.Visibility = Visibility.Visible;

        // 30 秒没人理就收起，避免一个模态框永远压在屏幕上
        _identifyTimer?.Stop();
        _identifyTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _identifyTimer.Tick += (_, _) =>
        {
            _identifyTimer?.Stop();
            if (_identifyTcs is not null) CompleteIdentify(null);
        };
        _identifyTimer.Start();

        return _identifyTcs.Task;
    }

    private void CompleteIdentify(Member? member)
    {
        _identifyTimer?.Stop();

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
            // 退出管理态回到记分台：只有管理态能进的页面留着会全是灰按钮
            Navigate(AppNav.Score);
            Toast("已退出管理模式（身份保留）");
            IdentityChip.Refresh();
            return true;
        }

        var ok = await AskPinAsync("切换身份", "输入谁的密码，就按谁的身份记分", requireManager: false);
        if (!ok) return false;

        // 换了人回记分台：换了身份之后，上一页的内容和权限都不是这个人的了
        Navigate(AppNav.Score);
        Toast($"当前身份：{Session.IdentityText}");
        return true;
    }

    public void ExitIdentity()
    {
        if (Session.Current is null) return;

        var name = Session.Current.DisplayName;
        Session.SignOut();
        IdentityChip.Refresh();
        RefreshHeader();
        // 大屏退出身份就回看板：那里是「谁都能看、谁都不能改」的状态，
        // 停在记分台会让人以为还能接着记，点下去才发现全灰。
        Navigate(AppNav.Wall);
        Toast($"{name} 已退出，记分请重新刷卡", 5);
    }

    // ================= 复用的小工具 =================

    /// <summary>PIN 校验。返回 true 表示通过并已认人（管理态由调用方决定）。</summary>
    public Task<bool> AskPinAsync(string title, string sub, bool requireManager)
    {
        // 待审设备只能看：认人会显示"已认人、能记分"，点下去才发现写不进去。
        if (Session.ReadOnly)
        {
            Toast("这台设备还在等班主任通过，现在只能查看", 6);
            return Task.FromResult(false);
        }

        var tcs = new TaskCompletionSource<bool>();

        // 密码框开着时也收卡。★ 这里必须和外壳常驻的那个订阅互斥：
        // HandleCard 看到 Pin.IsOpen 会直接让路，把卡交给这里的临时订阅处理，
        // 否则一次刷卡会同时触发「换人确认」和「验卡通过」，两个面板叠在一起。
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
        Pin.ShowVerify(title, readerOn ? sub + "。也可以刷卡。" : sub, pin =>
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

                ApplyIdentity(member);
                Finish(true);
            });
            Services.Reader.CardDetected += onCard;
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
        var result = await Dialogs.ShowAsync(dialog);
        return result == ContentDialogResult.Primary;
    }

    public void Toast(string message, double seconds = 3) => ToastLayer.Show(message, seconds);

    public void RefreshChrome() => RefreshHeader();

    /// <summary>
    /// 设置里开关了可选功能之后重画导航。停在当前页；当前页已经不在导航里
    /// （座位刚被关掉）就回设置。
    /// </summary>
    public void RebuildNavigation()
    {
        BuildNavigation();

        if (!_navItems.ContainsKey(_currentIndex))
        {
            Navigate(AppNav.Settings);
            return;
        }

        SelectWithoutNotify(_currentIndex);
    }
}
