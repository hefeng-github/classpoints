using ClassPoints.Core;
using ClassPoints.Data;

namespace ClassPoints.App.Services;

public enum IdentityState
{
    Anonymous,
    Identified,
    Admin,
}

/// <summary>会话态：只影响返回键行为与超时，不影响数据。</summary>
public enum InteractionState
{
    Idle,
    Scoring,
    Composing,
}

/// <summary>
/// 大屏端的三个正交状态位（方案 9.9）。
/// 不要用一个 currentUser 变量扛所有事：身份、会话、同步这三件事的寿命完全不同。
/// 同步态只影响状态条，永远不阻塞操作 —— 一旦允许它挡 UI，网络一抖老师就点不动了。
/// </summary>
public sealed class BoardSession(MemberRepository members)
{
    private int _pinFailures;
    private long _lockedUntil;

    public IdentityState Identity { get; private set; } = IdentityState.Anonymous;

    public Member? Current { get; private set; }

    public InteractionState Interaction { get; set; } = InteractionState.Idle;

    public int PendingCount { get; set; }

    public bool SyncEnabled { get; set; }

    public bool IsOnline { get; set; } = true;

    public DateTime LastActivity { get; private set; } = DateTime.Now;

    public DateTime AdminEnteredAt { get; private set; } = DateTime.MinValue;

    public event Action? Changed;

    private bool _readOnly;

    /// <summary>
    /// 云端说这台机器只能看（设备还在等班主任通过）。
    ///
    /// 放在会话层而不是各页面各判一遍：所有写操作本来就要先认人，
    /// 认人这一道统一挡住，就不会出现"这个页面忘了判"的漏网之鱼。
    /// 它和身份是两回事 —— 身份是"你是谁"，这是"这台机器能不能写"。
    /// </summary>
    public bool ReadOnly
    {
        get => _readOnly;
        set
        {
            if (_readOnly == value) return;
            _readOnly = value;
            // 变成只读时立刻退回未认人：不能让界面继续显示"管理 · 可写"
            if (value) SignOut();
            else Changed?.Invoke();
        }
    }

    public bool CanWrite => !ReadOnly && Identity != IdentityState.Anonymous;

    /// <summary>
    /// 写操作被挡住时该说哪句话。
    ///
    /// 「待审核」和「没认人」在界面上长得一样（都不能写），但下一步动作完全相反：
    /// 一个去刷卡，一个等班主任在网页上点通过。说错一句，老师就会一直在那儿刷卡。
    /// </summary>
    public string WriteBlockedText =>
        ReadOnly ? "这台电脑还在等班主任通过，现在只能查看" : "请先刷卡（或输入密码）";

    public bool IsAdmin => Identity == IdentityState.Admin;

    public bool IsPinLocked => _lockedUntil > Clock.NowMs();

    public int PinLockSecondsLeft =>
        IsPinLocked ? (int)System.Math.Ceiling((_lockedUntil - Clock.NowMs()) / 1000.0) : 0;

    public string IdentityText => Identity switch
    {
        IdentityState.Anonymous => "未认人",
        IdentityState.Admin => $"{Current?.DisplayName ?? "班主任"} · 管理",
        _ => Current?.DisplayName ?? "已认人",
    };

    public string SyncText
    {
        get
        {
            // 只读优先说出来：老师最先要知道的不是"同步了几条"，而是"为什么记不了分"
            if (ReadOnly) return PendingCount > 0 ? $"待审核 · 只读（{PendingCount} 条待同步）" : "待审核 · 只读";
            if (!SyncEnabled) return "本机存储";
            if (!IsOnline) return PendingCount > 0 ? $"离线 · 待同步 {PendingCount} 条" : "离线";
            return PendingCount > 0 ? $"待同步 {PendingCount} 条" : "已同步";
        }
    }

    public void Touch() => LastActivity = DateTime.Now;

    /// <summary>刷卡 / PIN 成功。同一个人再刷一次不重算；换人要上层先确认。</summary>
    public bool Identify(Member member, bool asAdmin = false)
    {
        // 待审设备不认人：认了也写不进去（服务端 403），界面上却会显示"已认人、可以记分"，
        // 老师点下去才发现全灰 —— 不如在认人这一步就说清楚。
        if (ReadOnly) return false;
        var same = Current?.Id == member.Id && Identity != IdentityState.Anonymous;
        Current = member;
        Identity = asAdmin || member.IsManager ? (asAdmin ? IdentityState.Admin : IdentityState.Identified) : IdentityState.Identified;
        if (asAdmin) AdminEnteredAt = DateTime.Now;
        Touch();
        _pinFailures = 0;
        Changed?.Invoke();
        return !same;
    }

    public void EnterAdmin()
    {
        if (Current is null) return;
        Identity = IdentityState.Admin;
        AdminEnteredAt = DateTime.Now;
        Touch();
        Changed?.Invoke();
    }

    /// <summary>管理超时只退管理模式，不退身份 —— 班主任改完名单被叫走，回来还能记分。</summary>
    public void ExitAdmin()
    {
        if (Identity != IdentityState.Admin) return;
        Identity = Current is null ? IdentityState.Anonymous : IdentityState.Identified;
        Changed?.Invoke();
    }

    public void SignOut()
    {
        Current = null;
        Identity = IdentityState.Anonymous;
        Interaction = InteractionState.Idle;
        Changed?.Invoke();
    }

    public Member? VerifyPin(string pin)
    {
        if (IsPinLocked) return null;
        var member = members.VerifyPin(pin);
        if (member is not null)
        {
            _pinFailures = 0;
            return member;
        }

        _pinFailures++;
        if (_pinFailures >= 5)
        {
            _lockedUntil = Clock.NowMs() + 60_000;
            _pinFailures = 0;
        }
        return null;
    }

    public void ResetPinFailures() => _pinFailures = 0;

    /// <summary>班委会话是否该退：仅 idle 态计时，scoring/composing 不算（老师低头看名单很正常）。</summary>
    public bool ShouldIdleExit(int idleSeconds) =>
        Identity != IdentityState.Anonymous &&
        Interaction == InteractionState.Idle &&
        (DateTime.Now - LastActivity).TotalSeconds >= idleSeconds;

    public bool ShouldAdminExit(int lockSeconds) =>
        Identity == IdentityState.Admin &&
        (DateTime.Now - AdminEnteredAt).TotalSeconds >= lockSeconds;
}
