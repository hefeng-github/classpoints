namespace ClassPoints.App.Services;

/// <summary>
/// 外壳对页面暴露的那一小撮能力。
///
/// 页面不再直接写 <c>App.Board?.Toast(...)</c> —— 那样同一页搬到大屏/电脑的另一端就静默失效，
/// 而这正是"两端功能应该一样"最容易出问题的地方。页面只认这个接口，外壳负责实现。
/// </summary>
public interface IShellHost
{
    int CurrentIndex { get; }

    void Navigate(int index);

    void Toast(string message, double seconds = 3);

    /// <summary>重画常驻状态（身份、备份时间、同步角标）。后台任务完成后调它，页面不必知道细节。</summary>
    void RefreshChrome();

    Task<bool> ConfirmAsync(string title, string message, string primary, string close);

    /// <summary>没认人就弹认人；<paramref name="requireAdmin"/> 时还要求是班主任/班长。</summary>
    Task<bool> EnsureIdentifiedAsync(bool requireAdmin = false);

    /// <summary>输密码验身份。谁输对了就是谁，权限跟着角色走。</summary>
    Task<bool> AskPinAsync(string title, string sub, bool requireManager);

    /// <summary>只收数字不校验（设新密码用）。</summary>
    Task<string?> AskDigitsAsync(string title, string sub, int digits = 6);

    /// <summary>换人：弹密码框，输谁的密码就换成谁。</summary>
    Task<bool> SwitchIdentityAsync();

    /// <summary>退出当前身份，回到「未认人」。下一步写操作会重新要密码/刷卡。</summary>
    void ExitIdentity();
}
