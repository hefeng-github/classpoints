using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClassPoints.App.Services;

/// <summary>
/// 所有 ContentDialog 的唯一入口。
///
/// 为什么必须绕一层：同一个窗口同时只能有一个 ContentDialog 是"打开"的，而"关闭中"
/// 也算打开 —— 关闭动画那 100 多毫秒里再 ShowAsync 一个新框，WinUI 直接抛
/// COMException「Only a single ContentDialog can be open at any time」。
///
/// 这个异常以前没人接（调用点几乎都是 async void），进程就这么静默退了：
/// 没有提示、没有"出错了"、WER 也来不及记。老师的感受是"点一下新增学生，程序没了"。
/// 所以现在两件事一起做：
///   ① 排队：一次只让一个对话框走，走完还要多等一下关闭动画；
///   ② 兜底：真撞上了就等一会儿重试，绝不让异常冒到 async void 里。
///
/// 嵌套（对话框的按钮事件里再开一个）不算排队：那一路本来就在父框自己的流程里，
/// 排到队尾反而会和"父框等子框关闭"互相等死。
/// </summary>
public static class Dialogs
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>当前这条异步流是否已经在对话框里（嵌套判定用）。</summary>
    private static readonly AsyncLocal<bool> Inside = new();

    /// <summary>关闭动画的宽限：太短还是会撞，太长则连点两下要等半秒多。</summary>
    private const int CloseGraceMs = 180;

    private const int RetryDelayMs = 60;
    private const int RetryTimes = 40;

    public static async Task<ContentDialogResult> ShowAsync(ContentDialog dialog)
    {
        ArgumentNullException.ThrowIfNull(dialog);

        // 忘了设 XamlRoot 会在 ShowAsync 里抛一个更难懂的错误，这里顺手补上
        if (dialog.XamlRoot is null && App.Window?.Content is FrameworkElement root)
            dialog.XamlRoot = root.XamlRoot;

        if (Inside.Value) return await ShowGuarded(dialog);

        await Gate.WaitAsync();
        Inside.Value = true;
        try
        {
            return await ShowGuarded(dialog);
        }
        finally
        {
            Inside.Value = false;
            await Task.Delay(CloseGraceMs);
            Gate.Release();
        }
    }

    private static async Task<ContentDialogResult> ShowGuarded(ContentDialog dialog)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await dialog.ShowAsync();
            }
            catch (COMException ex) when (attempt < RetryTimes)
            {
                // 上一个框的关闭动画还没走完，等一帧再来 —— 这是唯一预期会撞上的情况
                App.Services?.Log($"[dialog] 第 {attempt + 1} 次重试：{ex.Message}");
                await Task.Delay(RetryDelayMs);
            }
            catch (Exception ex)
            {
                // 到这里说明不是"排队没排上"：再抛出去就会顺着 async void 把进程带走。
                // 宁可这次没弹出来（用户再点一次即可），也不能让整个程序消失。
                App.Services?.LogError("dialog-show", ex);
                try { App.Host?.Toast("界面正忙，刚才那个窗口没打开，请再点一次。", 5); }
                catch (Exception)
                {
                    // toast 都不行就只剩日志了
                }
                return ContentDialogResult.None;
            }
        }
    }
}
