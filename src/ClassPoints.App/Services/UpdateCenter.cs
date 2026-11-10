using System.Reflection;
using ClassPoints.Core;
using ClassPoints.Data.Update;

namespace ClassPoints.App.Services;

/// <summary>
/// 更新的界面侧状态机。仓储层（<see cref="UpdateService"/>）只管下不下得下来、
/// 校验对不对；这里管"什么时候问、问完怎么显示、要不要提醒老师"。
///
/// 三条原则：
///   ① 检查永远在后台线程，失败不打扰人 —— 没网、学校屏蔽了外网都正常，
///      上课时弹一个"更新检查失败"只会让人关掉整个功能；
///   ② 同一个版本只主动提醒一次，之后靠侧栏那个常驻标记；
///   ③ 便携版不自动安装：它没有安装目录的概念，静默跑安装包会在
///      Program Files 里凭空多一份，而桌面上还是旧的那份。
/// </summary>
public sealed class UpdateCenter
{
    /// <summary>启动后等一会儿再查。开机那一刻网络通常还没就绪，也不该跟首屏抢带宽。</summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(10);

    private static readonly Lazy<UpdateCenter> Lazy = new(() => new UpdateCenter());

    public static UpdateCenter Current => Lazy.Value;

    private UpdateCenter()
    {
    }

    private static AppServices Services => App.Services;

    /// <summary>发现可用新版本（只触发一次/版本）。可能在后台线程触发。</summary>
    public event Action<UpdateManifest>? Found;

    /// <summary>状态变化（检查开始/结束/进度）。可能在后台线程触发。</summary>
    public event Action? Changed;

    /// <summary>真正可以装的那个新版本。null = 没有可动的。</summary>
    public UpdateManifest? Available { get; private set; }

    /// <summary>人话状态，直接显示在设置页。</summary>
    public string Status { get; private set; } = "还没检查过";

    public bool IsBusy { get; private set; }

    /// <summary>下载进度 0～1。没在下载时是 0。</summary>
    public double Progress { get; private set; }

    public static bool IsInstalled => UpdateService.IsInstalled;

    /// <summary>
    /// 编译期内置的版本库地址（公开分发仓库根）。CI 用 -p:UpdateFeed=... 注入，
    /// 仓库里不出现真实仓库路径 —— 换部署时只要改 CI 变量，不用改代码。
    /// </summary>
    public static string CompiledFeed =>
        typeof(UpdateCenter).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "UpdateFeed")?.Value ?? "";

    /// <summary>更新地址：手填 &gt; 编译期内置。都不填就不联网检查。</summary>
    public static string? ResolveFeed() =>
        UpdateService.ResolveFeed(Services.Settings.UpdateFeed, CompiledFeed);

    public void Notify() => Changed?.Invoke();

    /// <summary>
    /// 检查一次。<paramref name="manual"/> 为 true 时把失败原因写到 Status 上；
    /// 自动检查静默失败，只记日志。
    /// </summary>
    public async Task CheckAsync(bool manual, CancellationToken ct = default)
    {
        if (IsBusy) return;

        var feed = ResolveFeed();
        if (string.IsNullOrWhiteSpace(feed))
        {
            Status = "没有更新地址：在这一页填一个公开版本库地址就能接收新版本";
            Notify();
            return;
        }

        IsBusy = true;
        Status = "正在检查…";
        Notify();

        try
        {
            var service = new UpdateService(feed);
            var manifest = await service.CheckAsync(ct).ConfigureAwait(false);
            Services.Settings.UpdateLastCheckAt = DateTimeOffset.Now.ToUnixTimeMilliseconds();

            if (manifest is null)
            {
                Available = null;
                Status = "更新通道还没有正式版本";
            }
            else if (manifest.Version <= AppServices.CurrentVersion)
            {
                Available = null;
                Status = $"已是最新（{AppServices.CurrentVersion}）";
            }
            else if (string.Equals(Services.Settings.UpdateSkippedVersion, manifest.Version.ToString(),
                         StringComparison.Ordinal))
            {
                Available = null;
                Status = $"已跳过 {manifest.Version}";
            }
            else
            {
                Available = manifest;
                Status = $"有新版本 {manifest.Version}";
                Services.Log($"发现新版本 {manifest.Version}（当前 {AppServices.CurrentVersion}）");
                Found?.Invoke(manifest);
            }
        }
        catch (Exception ex)
        {
            Services.LogError("update-check", ex);
            if (manual) Status = "检查失败：" + ex.Message;
            else Status = "上次自动检查没成功（不影响使用）";
        }
        finally
        {
            IsBusy = false;
            Notify();
        }
    }

    /// <summary>启动后台检查。重复调用无副作用。</summary>
    public async Task AutoCheckAsync()
    {
        try
        {
            await Task.Delay(StartupDelay).ConfigureAwait(false);
            if (!Services.Settings.UpdateAutoCheck) return;
            await CheckAsync(manual: false).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Services.LogError("update-auto", ex);
        }
    }

    /// <summary>下载并校验。返回安装包本地路径。</summary>
    public async Task<string> DownloadAsync(IProgress<UpdateProgress>? progress = null,
        CancellationToken ct = default)
    {
        if (Available is not { } manifest) throw new InvalidOperationException("没有可下载的新版本");

        var feed = ResolveFeed();
        if (string.IsNullOrWhiteSpace(feed)) throw new InvalidOperationException("没有配置更新地址");

        IsBusy = true;
        Progress = 0;
        Notify();

        try
        {
            var service = new UpdateService(feed);
            var reporter = new Progress<UpdateProgress>(p =>
            {
                Progress = p.Fraction;
                progress?.Report(p);
            });
            return await service.DownloadAsync(manifest, reporter, ct).ConfigureAwait(false);
        }
        finally
        {
            IsBusy = false;
            Notify();
        }
    }

    /// <summary>这一版不再提示。已经下好的包保留着，老师想装还是能装。</summary>
    public void Skip(SemanticVersion version)
    {
        Services.Settings.UpdateSkippedVersion = version.ToString();
        Available = null;
        Status = $"已跳过 {version}";
        Notify();
    }

    /// <summary>清掉旧安装包，只留当前版本那个。</summary>
    public static void CleanupDownloads() => UpdateService.CleanupDownloads(AppServices.CurrentVersion);
}
