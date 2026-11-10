using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using ClassPoints.Data.Net;

namespace ClassPoints.Data.Update;

/// <summary>下载过程中的进度。Total 来自清单，不是猜的 Content-Length。</summary>
public readonly record struct UpdateProgress(long Received, long Total)
{
    public double Fraction => Total <= 0 ? 0 : Math.Clamp((double)Received / Total, 0, 1);
}

/// <summary>
/// 更新通道的 HTTP 客户端：直接读公开版本库（CNB）的 Release 附件，不带任何凭据。
///
/// 为什么敢直连：分发仓库是**公开**的，里面只放安装包、清单和校验文件，
/// 没有任何源码或密钥。客户端手里没有、也不需要有能读私有库的令牌 ——
/// 教室里那台机器不是安全边界，把一个长期令牌打进安装包，等于把它发给每个人。
/// 代价是"谁都能下载安装包"，而这本来就是分发的目的。
///
/// 地址形状（FeedUrl 是版本库根，形如 https://cnb.cool/&lt;用户&gt;/&lt;仓库&gt;）：
///   GET {Feed}/-/releases/download/stable/release-manifest.json   固定标签，每次发布覆盖
///   GET {Feed}/-/releases/download/&lt;tag&gt;/&lt;文件名&gt;              版本化产物，内容永不变
///
/// CNB 的下载端点会 302 到对象存储；HttpClient 默认跟随重定向，且我们不发任何
/// Authorization 头，所以重定向到别的域名也没有凭据外泄的问题。
/// </summary>
public sealed class UpdateClient : IDisposable
{
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(20);

    /// <summary>固定标签：清单永远挂在这一份下面，发布时覆盖同名附件。</summary>
    public const string StableTag = "stable";

    /// <summary>清单文件名。客户端只认这一个名字，不接受服务端"推荐"别的地址。</summary>
    public const string ManifestName = "release-manifest.json";

    private readonly HttpClient _http;

    public UpdateClient(string feedUrl)
    {
        FeedUrl = Endpoint.Normalize(feedUrl, "更新地址");

        // 和同步一样不走系统代理：学校网络里的代理会让 .NET 用绝对 URI 发请求行，
        // 部分边缘节点只认 origin-form，直接 500。
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
            ConnectTimeout = TimeSpan.FromSeconds(15),
            // 下载端点 302 到对象存储，显式写清楚是刻意跟随
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
        };
        var proxy = Environment.GetEnvironmentVariable("CP_SYNC_PROXY");
        if (!string.IsNullOrWhiteSpace(proxy))
        {
            handler.UseProxy = true;
            handler.Proxy = new WebProxy(proxy);
        }

        _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("ClassPoints-Updater/1");
    }

    /// <summary>版本库根地址。真实仓库路径由发布时编译进程序，不写死在源码里。</summary>
    public string FeedUrl { get; }

    private string DownloadBase => $"{FeedUrl}/-/releases/download";

    public string CheckUrl => $"{DownloadBase}/{StableTag}/{ManifestName}";

    /// <summary>产物地址由清单里的 tag 和文件名拼出，两者都已被严格校验过。</summary>
    public string AssetUrl(string tag, string name) =>
        $"{DownloadBase}/{Uri.EscapeDataString(tag)}/{Uri.EscapeDataString(name)}";

    /// <summary>
    /// 问一次有没有正式版。返回 null 表示"这个通道上还没有发布过正式版"
    /// （全新部署、或还没发过版），这不是错误，界面不该报红。
    /// </summary>
    public async Task<UpdateManifest?> CheckAsync(CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(CheckTimeout);

        using var res = await _http.GetAsync(CheckUrl, timeout.Token);
        if (res.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.NotFound) return null;
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException($"读取更新清单失败（HTTP {(int)res.StatusCode}）");

        var body = await res.Content.ReadAsStringAsync(timeout.Token);
        return UpdateManifest.Parse(body);
    }

    /// <summary>
    /// 下载安装包到 <paramref name="targetDir"/>，边下边算 SHA-256。
    ///
    /// 先写 .part 再改名：断电、断网、老师关电脑都会留下半截文件，
    /// 而半截文件如果直接叫 .exe，下一次"已下载"的判断就会把它当成完好的包。
    /// </summary>
    public async Task<string> DownloadSetupAsync(
        UpdateManifest manifest, string targetDir, IProgress<UpdateProgress>? progress = null,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(targetDir);
        var finalPath = Path.Combine(targetDir, manifest.Setup.Name);
        var partPath = finalPath + ".part";

        // 已经下过且哈希对得上，直接复用，省一次几十兆的下载。
        if (File.Exists(finalPath) && await HashMatches(finalPath, manifest.Setup, ct))
            return finalPath;

        TryDelete(partPath);

        using var res = await _http.GetAsync(
            AssetUrl(manifest.Tag, manifest.Setup.Name), HttpCompletionOption.ResponseHeadersRead, ct);
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException($"下载安装包失败（HTTP {(int)res.StatusCode}）");

        var received = 0L;
        var buffer = new byte[81920];
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        var file = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, buffer.Length, true);
        try
        {
            await using (file)
            {
                await using var stream = await res.Content.ReadAsStreamAsync(ct);
                int read;
                while ((read = await stream.ReadAsync(buffer, ct)) > 0)
                {
                    received += read;
                    // 服务器如果一直往这边灌数据，超过清单大小就掐掉，别把磁盘写满
                    if (received > manifest.Setup.Size)
                        throw new InvalidDataException("下载的数据比清单声明的大小还多，已中止");

                    hash.AppendData(buffer, 0, read);
                    await file.WriteAsync(buffer.AsMemory(0, read), ct);
                    progress?.Report(new UpdateProgress(received, manifest.Setup.Size));
                }
                await file.FlushAsync(ct);
            }

            if (received != manifest.Setup.Size)
                throw new InvalidDataException($"下载不完整：{received} / {manifest.Setup.Size} 字节");

            // 防"服务器返回了一个网页/JSON 错误页，长度恰好对上但内容不对"
            var actual = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (!string.Equals(actual, manifest.Setup.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("安装包校验失败（SHA-256 不匹配），已丢弃");

            File.Move(partPath, finalPath, true);
            return finalPath;
        }
        catch
        {
            // 失败就清干净：留下半截文件只会让下一次重试更难排查
            TryDelete(partPath);
            throw;
        }
    }

    private static async Task<bool> HashMatches(string path, UpdateAsset asset, CancellationToken ct)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Length != asset.Size) return false;

            await using var stream = File.OpenRead(path);
            var hash = await SHA256.HashDataAsync(stream, ct);
            return string.Equals(Convert.ToHexStringLower(hash), asset.Sha256, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"删不掉临时文件 {path}：{e.Message}");
        }
    }

    public void Dispose() => _http.Dispose();
}
