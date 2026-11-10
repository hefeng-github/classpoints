using System.Diagnostics;
using ClassPoints.Core;

namespace ClassPoints.Data.Update;

/// <summary>
/// 更新流程的组合根：定地址、问版本、下包、拉起安装程序。
///
/// 一条硬规矩贯穿全文：**只有正式版、只有装好的版本、只有校验通过的包**才走到
/// 最后一步。三条里少任何一条，宁可这次不更新 —— 教室里一台机器升坏，
/// 比全班晚两周用上新功能严重得多。
/// </summary>
public sealed class UpdateService
{
    public UpdateService(string feedUrl)
    {
        Feed = Net.Endpoint.Normalize(feedUrl, "更新地址");
    }

    public string Feed { get; }

    /// <summary>下载落地目录。放 %LOCALAPPDATA%，程序在 Program Files 里是只读的。</summary>
    public static string UpdatesDir => Path.Combine(Database.RootDir, "updates");

    /// <summary>
    /// 这台机器是不是由安装程序装出来的。
    ///
    /// 判据是卸载程序存在 —— Inno Setup 装完一定会在安装目录留一个 unins000.exe，
    /// 而整目录拷走的便携版没有。搞错方向的代价很难看：对着便携版静默跑安装包，
    /// 会在 Program Files 里凭空多出一份，老师桌面上的图标还是连到旧的那份。
    /// </summary>
    public static bool IsInstalled =>
        File.Exists(Path.Combine(AppContext.BaseDirectory, "unins000.exe"));

    /// <summary>便携版不能自更新，只能给一个下好的新包，让老师自己覆盖。</summary>
    public static bool CanSelfUpdate => IsInstalled;

    /// <summary>
    /// 更新地址的优先级：用户手填 &gt; 编译期内置。
    ///
    /// 刻意**不**拿同步地址兜底：同步地址指向的是同步中枢（Cloudflare Worker），
    /// 而更新走的是公开版本库（CNB），两者是完全不同的服务，拼出来的 URL 必然 404。
    /// 地址没配就不检查更新 —— 静默跳过比每次启动都去撞一个错地址体面得多。
    /// </summary>
    public static string? ResolveFeed(string? configured, string? compiled)
    {
        foreach (var candidate in new[] { configured, compiled })
        {
            if (!string.IsNullOrWhiteSpace(candidate)) return candidate!.Trim();
        }
        return null;
    }

    /// <summary>拉一份清单。返回 null = 通道上没有正式版。</summary>
    public async Task<UpdateManifest?> CheckAsync(CancellationToken ct = default)
    {
        using var client = new UpdateClient(Feed);
        var manifest = await client.CheckAsync(ct);
        Log($"检查更新：{(manifest is null ? "通道上没有正式版" : "最新 " + manifest.Version)}");
        return manifest;
    }

    public async Task<string> DownloadAsync(
        UpdateManifest manifest, IProgress<UpdateProgress>? progress = null, CancellationToken ct = default)
    {
        using var client = new UpdateClient(Feed);
        var path = await client.DownloadSetupAsync(manifest, UpdatesDir, progress, ct);
        Log($"安装包已就绪 {Path.GetFileName(path)}");
        return path;
    }

    /// <summary>清掉上次留下的安装包（升级完第一次启动时调用）。</summary>
    public static void CleanupDownloads(SemanticVersion keepVersion)
    {
        try
        {
            if (!Directory.Exists(UpdatesDir)) return;

            // 保留当前版本的包：老师可能刚更新完就想再装一次（重装到另一台机器前先看版本）
            var keep = UpdateManifest.SetupFileNameFor(keepVersion);
            foreach (var file in Directory.EnumerateFiles(UpdatesDir))
            {
                if (string.Equals(Path.GetFileName(file), keep, StringComparison.OrdinalIgnoreCase)) continue;
                try { File.Delete(file); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"清理更新目录失败：{e.Message}");
        }
    }

    /// <summary>
    /// 启动安装程序。ShellExecute 是为了让 Windows 弹 UAC —— 装到 Program Files
    /// 必须要管理员，这一步绕不过去，也不该绕。
    ///
    /// /CLOSEAPPLICATIONS 让安装程序自己关掉还在跑的旧进程（就是调用者本身），
    /// 所以调用方拉起它之后要尽快退出，否则文件一直被占着，安装会卡在"正在删除文件"。
    /// </summary>
    public static void LaunchInstaller(string setupPath)
    {
        if (!IsInstalled)
            throw new InvalidOperationException("当前是便携版，不自动安装");

        if (!File.Exists(setupPath))
            throw new FileNotFoundException("安装包不存在", setupPath);

        var info = new ProcessStartInfo(setupPath)
        {
            UseShellExecute = true,
            Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /NOCANCEL /SP-",
        };
        Process.Start(info);
    }

    private void Log(string message) => Debug.WriteLine("[update] " + message);
}
