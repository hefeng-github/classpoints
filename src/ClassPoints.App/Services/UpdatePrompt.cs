using ClassPoints.Data.Update;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClassPoints.App.Services;

/// <summary>
/// 「有新版本」的弹窗。
///
/// 以前只有一句 Toast + 侧栏一个小角标：大屏端连自动检查都没有，教室那台机器
/// 等于永远收不到新版本；电脑端则是一句话飘过去，老师低头记两笔分就错过了。
/// 现在改成一次性的弹窗：说清楚新版本号、这一版改了什么、包有多大，
/// 三个按钮对应三种态度 —— 现在更新 / 稍后 / 跳过这一版。
///
/// 三条约束：
///   ① 每一版只弹一次（<see cref="AppSettings.UpdateSeenVersion"/>）。老师正在记分，
///      同一句话反复弹两次，换来的就是他把自动检查关掉；
///   ② 下载要有进度、可取消。教室的网断一次要重下 70 MB，没有进度条会让人以为卡死；
///   ③ 点「跳过这一版」之后不再问 —— 跳过是人的决定，不是"下次再烦你一遍"。
/// </summary>
public static class UpdatePrompt
{
    /// <summary>
    /// 发现新版本时调一次。可以从后台线程调（<see cref="UpdateCenter.Found"/> 就在后台触发）。
    /// 同一版只弹一次；弹过之后靠侧栏角标和设置页提醒。
    /// </summary>
    public static void NotifyIfNew(UpdateManifest manifest)
    {
        var services = App.Services;
        if (services is null) return;

        if (string.Equals(services.Settings.UpdateSeenVersion, manifest.Version.ToString(), StringComparison.Ordinal))
            return;
        services.Settings.UpdateSeenVersion = manifest.Version.ToString();

        // ContentDialog 只能在 UI 线程上开；Found 事件是在检查那条异步链里抛出来的
        var queue = App.Window?.DispatcherQueue;
        if (queue is null) return;
        queue.TryEnqueue(() => _ = ShowAsync(manifest));
    }

    private static async Task ShowAsync(UpdateManifest manifest)
    {
        var body = new StackPanel { Spacing = 12, MaxWidth = 460 };

        if (!string.IsNullOrWhiteSpace(manifest.Notes))
        {
            body.Children.Add(new TextBlock
            {
                Text = manifest.Notes,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 14,
            });
        }

        // 说清楚"会发生什么"：升级会关程序再打开。老师最担心的是正在记的分会不会没。
        body.Children.Add(new TextBlock
        {
            Text = $"当前 v{AppServices.AppVersion} · 安装包 {manifest.Setup.Size / 1048576.0:F1} MB"
                   + (UpdateService.IsInstalled
                       ? " · 升级时程序会自动关闭再重新打开，名单和积分不受影响。"
                       : " · 便携版：下载后解压覆盖旧目录即可。"),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            Opacity = 0.75,
        });

        var dialog = new ContentDialog
        {
            Title = $"有新版本 {manifest.Version}",
            Content = body,
            PrimaryButtonText = UpdateService.IsInstalled ? "现在更新" : "下载",
            SecondaryButtonText = "跳过这一版",
            CloseButtonText = "稍后",
            DefaultButton = ContentDialogButton.Primary,
        };

        var choice = await Dialogs.ShowAsync(dialog);
        switch (choice)
        {
            case ContentDialogResult.Primary:
                await InstallAsync(manifest);
                break;
            case ContentDialogResult.Secondary:
                UpdateCenter.Current.Skip(manifest.Version);
                App.Host?.Toast($"已跳过 {manifest.Version}，可以在「设置 → 更新」里手动装", 6);
                break;
        }
    }

    /// <summary>下载（带进度、可取消）→ 校验 → 拉起安装程序。</summary>
    private static async Task InstallAsync(UpdateManifest manifest)
    {
        var bar = new ProgressBar { Minimum = 0, Maximum = 1, Value = 0, Height = 4 };
        var status = new TextBlock { Text = "准备下载…", FontSize = 14 };
        var panel = new StackPanel { Spacing = 10, MaxWidth = 460 };
        panel.Children.Add(status);
        panel.Children.Add(bar);

        using var cancel = new CancellationTokenSource();
        var progress = new Progress<UpdateProgress>(p =>
        {
            bar.Value = p.Fraction;
            status.Text = $"正在下载 {p.Received / 1048576.0:F1} / {p.Total / 1048576.0:F1} MB";
        });

        var dialog = new ContentDialog
        {
            Title = $"更新到 {manifest.Version}",
            Content = panel,
            CloseButtonText = "取消",
        };
        dialog.CloseButtonClick += (_, _) => cancel.Cancel();

        var showing = Dialogs.ShowAsync(dialog);
        string? path = null;
        try
        {
            path = await UpdateCenter.Current.DownloadAsync(progress, cancel.Token);
        }
        catch (OperationCanceledException)
        {
            App.Services?.Log("update-download-cancelled");
        }
        catch (Exception ex)
        {
            App.Services?.LogError("update-download", ex);
            App.Host?.Toast("下载失败：" + ex.Message, 8);
        }
        finally
        {
            dialog.Hide();
            try { await showing; } catch (Exception) { }
        }

        if (path is null) return;

        if (!UpdateService.IsInstalled)
        {
            // 便携版不替人覆盖：把文件选中给他，省得在资源管理器里翻
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{path}\"",
                UseShellExecute = true,
            });
            App.Host?.Toast($"已下载 {manifest.Version}，解压后覆盖旧目录即可", 8);
            return;
        }

        try
        {
            App.Services?.Log($"拉起安装程序 {path}");
            UpdateService.LaunchInstaller(path);
        }
        catch (Exception ex)
        {
            App.Services?.LogError("update-launch", ex);
            App.Host?.Toast("启动安装程序失败：" + ex.Message, 10);
            return;
        }

        // 安装程序要用 Restart Manager 关掉本进程才能覆盖 ClassPoints.exe，
        // "关掉"依赖我们自己放锁 —— 这里要的是确定性，所以直接退出。
        Environment.Exit(0);
    }
}
