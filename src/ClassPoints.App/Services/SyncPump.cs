using ClassPoints.Data.Sync;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace ClassPoints.App.Services;

/// <summary>
/// 同步的心跳。前台 45 秒一轮，窗口重新激活时立刻补一次。
///
/// 不做 WebSocket，也不用长连接：两台设备的场景不值得为"实时"付这个复杂度。
/// 重要的是这一条 —— 它永远不在用户点"加一分"的路径上，本地写成功就算成功。
/// </summary>
public sealed class SyncPump(AppServices services)
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(45) };
    private bool _running;
    private bool _busy;

    /// <summary>Start() 那一刻的 UI 队列。后台链路上要回 UI 只能靠它。</summary>
    private DispatcherQueue? _ui;

    public DateTime LastAttempt { get; private set; } = DateTime.MinValue;
    public string LastError { get; private set; } = "";
    public int LastPushed { get; private set; }
    public int LastPulled { get; private set; }

    /// <summary>拉取时按云端为准合并掉的行数（学号/标签重名）。只在设置页露出来，不打扰记分。</summary>
    public int Conflicts { get; private set; }

    /// <summary>云端不认这台机器的凭据了。界面据此给"重新配对"的入口 —— 这条路只能人来走。</summary>
    public bool NeedsPairing { get; private set; }

    /// <summary>凭据失效的成因（被别处轮换 / 过期 / 被撤销），提示语按它分。</summary>
    public PairingReason PairingReason { get; private set; }

    /// <summary>
    /// 刚发现凭据失效时触发一次（恢复之后再失效会再触发）。
    ///
    /// 只触发"变化"而不是每轮都喊：45 秒一次、每次弹一句同样的话，
    /// 老师第一件事就是把提示关掉，然后真的需要重配时也看不见了。
    /// </summary>
    public event Action<PairingReason>? PairingNeeded;

    /// <summary>
    /// 凭据失效后自己接上了（用本机存着的口令重新配对成功）。
    ///
    /// 也要说一句：老师上一眼可能刚看到"待同步 9 条"，下一秒这些记录自己走了，
    /// 不说的话很像界面在乱跳。但我们把它说成"已经自动接上"，而不是"出错了"。
    /// </summary>
    public event Action? AutoRepaired;

    /// <summary>每轮结束后触发，界面用来刷新角标。</summary>
    public event Action? Changed;

    public bool IsBusy => _busy;

    public string StatusText
    {
        get
        {
            if (!services.Sync.IsConfigured) return "未启用";
            // 只读优先：老师最先要知道的不是"同步了几条"，而是"为什么记不了分"
            if (services.Sync.IsReadOnly) return "待审核 · 只读";
            if (_busy) return "同步中…";

            // 凭据失效排在错误前面：它不是"等一会儿自己会好"的那类
            if (NeedsPairing) return "凭据失效，需要重新配对";
            if (!string.IsNullOrEmpty(LastError)) return "上次失败：" + LastError;
            return services.Sync.LastSyncText;
        }
    }

    public void Start()
    {
        if (_running) return;
        _running = true;
        _ui = DispatcherQueue.GetForCurrentThread();

        // 先把"有没有接云端"这件事同步出去：第一轮网络还没跑完，
        // 侧栏就已经能显示正确的状态，不用先错一次再改。
        services.Session.SyncEnabled = services.Sync.IsConfigured;

        _timer.Tick += OnTick;
        // 凭据是自己接上的还是需要人管，只有 SyncService 知道 —— 这里只负责搬到 UI 线程
        services.Sync.AutoRepaired -= OnAutoRepaired;
        services.Sync.AutoRepaired += OnAutoRepaired;
        _timer.Start();
        _ = RunAsync();
    }

    public void Stop()
    {
        _running = false;
        _timer.Stop();
        _timer.Tick -= OnTick;
        services.Sync.AutoRepaired -= OnAutoRepaired;
    }

    private void OnAutoRepaired()
    {
        // 这个回调可能落在同步那条 async 链的任意一段上（网络续完也可能是后台线程），
        // 所以用 Start 时记下来的 UI 队列，而不是"当前线程的"。
        var queue = _ui ?? DispatcherQueue.GetForCurrentThread();
        if (queue is null) AutoRepaired?.Invoke();
        else queue.TryEnqueue(() => AutoRepaired?.Invoke());
    }

    private void OnTick(object? sender, object e) => _ = RunAsync();

    /// <summary>窗口重新激活时调一次：一体机休眠醒来后 TCP 是假活的，光靠定时器会撞上挂死的连接。</summary>
    public void Wake() => _ = RunAsync();

    public async Task<SyncReport?> RunAsync()
    {
        if (!services.Sync.IsConfigured || _busy) return null;
        _busy = true;
        Notify();
        try
        {
            var report = await services.Sync.RunOnceAsync();
            LastAttempt = DateTime.Now;
            LastError = report.Ok ? "" : report.Error ?? "";

            var wasPairing = NeedsPairing;
            NeedsPairing = report.NeedsPairing;
            PairingReason = report.Reason;
            if (NeedsPairing && !wasPairing)
            {
                // 扔到 UI 线程再喊：这里可能就在 DispatcherTimer 的回调里
                var queue = DispatcherQueue.GetForCurrentThread();
                if (queue is null) PairingNeeded?.Invoke(report.Reason);
                else queue.TryEnqueue(() => PairingNeeded?.Invoke(report.Reason));
            }

            LastPushed += report.Pushed;
            LastPulled += report.Pulled;
            Conflicts += report.Conflicts;

            // 状态条上的"本机存储 / 已同步 / 离线"不能各页面各猜一套：
            // 以前三处各存一份判断，结果设置页说"已连接"、侧栏说"本机存储"。
            services.Session.SyncEnabled = services.Sync.IsConfigured;
            services.Session.IsOnline = !report.Offline;

            return report;
        }
        catch (Exception ex)
        {
            services.LogError("sync", ex);
            LastError = ex.Message;
            return null;
        }
        finally
        {
            _busy = false;
            Notify();
        }
    }

    private void Notify()
    {
        // 待同步条数也在这里顺手对一次：以前只有记分台会更新它，
        // 只读设备停在别的页面时角标永远是 0，看起来像"什么都没攒"。
        services.Session.PendingCount = services.Outbox.Count();
        // 有没有接云端也是同一件事：SyncEnabled 以前只在记分台被硬写成 false，
        // 于是"已连接云端"和"本机存储"可以同时出现在同一台机器上。
        services.Session.SyncEnabled = services.Sync.IsConfigured;
        services.Session.NeedsPairing = NeedsPairing;
        var queue = DispatcherQueue.GetForCurrentThread();
        if (queue is null)
        {
            services.Session.Refresh();
            Changed?.Invoke();
        }
        else
        {
            queue.TryEnqueue(() =>
            {
                services.Session.Refresh();
                Changed?.Invoke();
            });
        }
    }
}
