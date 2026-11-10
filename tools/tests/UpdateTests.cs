using System.Net;
using System.Net.Sockets;
using System.Text;
using ClassPoints.Core;
using ClassPoints.Data.Net;
using ClassPoints.Data.Update;

namespace ClassPoints.Tests;

/// <summary>
/// 版本号、清单校验、下载校验。
///
/// 为什么这一组要真起一个本地 HTTP 服务：更新这块最容易出错的地方不是解析，
/// 而是"下到一半断了""服务器给了别的文件""哈希对不上"这些只在真实 IO 里出现的情况。
/// 用假的服务器把它们一个个造出来，比在教室里遇到一次再回头查划算得多。
/// </summary>
internal static class UpdateTests
{
    // 委托自带默认参数，测例里就能写 check("名字", 条件)，
    // 需要补充失败细节时再传第三个参数。Program.Check 的方法组可直接转换。
    public delegate void CheckFn(string name, bool ok, string detail = "");

    internal static async Task RunAsync(CheckFn check)
    {
        Versions(check);
        Addresses(check);
        ManifestValidation(check);
        await DownloadAsync(check);
    }

    /// <summary>
    /// 真连一个更新源（本地模拟 CNB 或已部署的真实仓库）。
    /// 用 `--update-feed http://127.0.0.1:PORT/&lt;用户&gt;/&lt;仓库&gt;` 触发，
    /// 发布前确认"清单挂对了、安装包真能匿名下下来、哈希对得上"。
    /// </summary>
    internal static async Task RunLiveAsync(string feed, CheckFn check)
    {
        var dir = Path.Combine(Path.GetTempPath(), "cp-live-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            using var client = new UpdateClient(feed);
            Console.WriteLine($"    更新源 {client.CheckUrl}");
            var manifest = await client.CheckAsync();
            check("真连：能读到正式版清单", manifest is not null);

            if (manifest is not null)
            {
                Console.WriteLine($"    版本 {manifest.Version} · {manifest.Setup.Size / 1048576.0:F1} MB");
                var path = await client.DownloadSetupAsync(manifest, dir);
                check("真连：下载大小与清单一致", new FileInfo(path).Length == manifest.Setup.Size);
                await using var stream = File.OpenRead(path);
                var hash = Convert.ToHexStringLower(
                    await System.Security.Cryptography.SHA256.HashDataAsync(stream));
                check("真连：SHA-256 与清单一致",
                    string.Equals(hash, manifest.Setup.Sha256, StringComparison.OrdinalIgnoreCase), hash[..16]);
            }
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    private static void Versions(CheckFn check)
    {
        check("三段式能解析", SemanticVersion.TryParse("1.2.3", out var v) && v == new SemanticVersion(1, 2, 3));
        check("两段式拒绝", !SemanticVersion.TryParse("1.2", out _));
        check("带后缀的预览版拒绝", !SemanticVersion.TryParse("1.2.3-beta", out _));
        check("前导零拒绝（排序会错）", !SemanticVersion.TryParse("01.2.3", out _));
        check("四段拒绝", !SemanticVersion.TryParse("1.2.3.4", out _));
        check("空串拒绝", !SemanticVersion.TryParse("", out _));
        check("文字拒绝", !SemanticVersion.TryParse("v1.2.3", out _), "v 前缀只走 TryParseTag");

        check("标签 v1.2.3 能解析",
            SemanticVersion.TryParseTag("v1.2.3", out var tag) && tag == new SemanticVersion(1, 2, 3));
        check("标签不带 v 拒绝", !SemanticVersion.TryParseTag("1.2.3", out _));

        SemanticVersion.TryParse("1.10.0", out var a);
        SemanticVersion.TryParse("1.9.0", out var b);
        check("1.10 比 1.9 新（按数值，不按字典序）", a > b, $"{a} vs {b}");

        SemanticVersion.TryParse("1.2.3", out var c);
        SemanticVersion.TryParse("1.2.4", out var d);
        SemanticVersion.TryParse("1.2.3", out var c2);
        check("修订号参与比较", d > c);
        check("同版本相等且不满足大于", c.CompareTo(c2) == 0 && !(c > c2));

        check("安装包名由版本号推出",
            UpdateManifest.SetupFileNameFor(new SemanticVersion(1, 2, 0)) == "ClassPoints-Setup-1.2.0.exe");
    }

    private static void Addresses(CheckFn check)
    {
        check("https 原样通过", Endpoint.Normalize("https://a.example.com/x/", "更新地址") == "https://a.example.com/x");
        check("不带协议补 https", Endpoint.Normalize("a.example.com", "更新地址") == "https://a.example.com");
        check("回环允许 http", Endpoint.Normalize("http://127.0.0.1:8787", "更新地址") == "http://127.0.0.1:8787");

        var rejected = false;
        try
        {
            Endpoint.Normalize("http://a.example.com", "更新地址");
        }
        catch (ArgumentException)
        {
            rejected = true;
        }
        check("公网地址必须 https", rejected);

        check("更新地址优先级：手填 > 内置",
            UpdateService.ResolveFeed("https://mine", "https://builtin") == "https://mine");
        check("没手填就用内置",
            UpdateService.ResolveFeed("  ", "https://builtin") == "https://builtin");
        check("全空返回 null（不拿同步地址兜底）",
            UpdateService.ResolveFeed("", "  ") is null);

        using (var client = new UpdateClient("https://cnb.cool/someone/dist"))
        {
            check("清单走 stable 固定标签",
                client.CheckUrl == "https://cnb.cool/someone/dist/-/releases/download/stable/release-manifest.json");
            check("安装包地址由 tag 和文件名拼出",
                client.AssetUrl("v1.2.0", "ClassPoints-Setup-1.2.0.exe") ==
                "https://cnb.cool/someone/dist/-/releases/download/v1.2.0/ClassPoints-Setup-1.2.0.exe");
            check("文件名里的空格被转义",
                client.AssetUrl("v1.2.0", "a b.exe").EndsWith("/a%20b.exe"));
        }
    }

    private static void ManifestValidation(CheckFn check)
    {
        var good = Good("1.2.0");
        check("合法清单能解析", UpdateManifest.TryParse(good, out var parsed, out var error), error);
        check("解析出的版本与标签",
            parsed is { Version: { Major: 1, Minor: 2, Patch: 0 }, Tag: "v1.2.0" });

        void Reject(string name, string json)
        {
            var ok = UpdateManifest.TryParse(json, out _, out var why);
            check(name, !ok, ok ? "居然通过了" : "");
        }

        Reject("渠道不是 stable 就作废", good.Replace("\"stable\"", "\"beta\""));
        Reject("标签与版本号对不上", good.Replace("\"tag\": \"v1.2.0\"", "\"tag\": \"v1.3.0\""));
        Reject("两段式版本作废", good.Replace("\"version\": \"1.2.0\"", "\"version\": \"1.2\""));
        Reject("安装包名必须与版本一致",
            good.Replace("ClassPoints-Setup-1.2.0.exe", "ClassPoints-Setup-9.9.9.exe"));
        Reject("哈希长度不对作废", good.Replace(Sha, Sha[..63]));
        Reject("大小为零作废", good.Replace("\"size\": 1024", "\"size\": 0"));
        Reject("名字里带路径作废", good.Replace("ClassPoints-Setup-1.2.0.exe", "../evil.exe"));
        Reject("不是 JSON 作废", "{ oops");
        Reject("空串作废", "");
    }

    private static async Task DownloadAsync(CheckFn check)
    {
        var dir = Path.Combine(Path.GetTempPath(), "cp-update-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);

        using var feed = new FakeFeed();
        var bytes = Encoding.UTF8.GetBytes("not really an installer, but exactly this many bytes");
        var manifest = UpdateManifest.Parse(Good("1.2.0", bytes));

        try
        {
            using var client = new UpdateClient(feed.BaseUrl);

            feed.ManifestJson = Good("1.2.0", bytes);
            var found = await client.CheckAsync();
            check("能问到清单", found?.Version == new SemanticVersion(1, 2, 0));
            check("请求不带任何凭据（公开仓库）", feed.SawAuthorization == false);

            // 通道上还没发过正式版：404 是正常状态，不该抛异常
            feed.MissingManifest = true;
            check("清单 404 视为没有正式版", await client.CheckAsync() is null);
            feed.MissingManifest = false;

            feed.SetupBytes = bytes;
            var path = await client.DownloadSetupAsync(manifest, dir);
            check("下载后文件大小一致", new FileInfo(path).Length == bytes.Length);
            check("下载后没有 .part 残留", !File.Exists(path + ".part"));

            // 再下一次：命中已有文件，不该重新拉（服务端计数不变）
            var before = feed.DownloadCount;
            await client.DownloadSetupAsync(manifest, dir);
            check("已下好的包直接复用，不重复下载", feed.DownloadCount == before);

            // 内容被换掉：哈希对不上，必须丢弃并且不留残骸
            feed.CorruptPayload = true;
            var tampered = false;
            try
            {
                var badDir = Path.Combine(dir, "bad");
                await client.DownloadSetupAsync(manifest, badDir);
            }
            catch (InvalidDataException)
            {
                tampered = true;
            }
            check("哈希不匹配时拒绝执行并丢弃", tampered
                && !File.Exists(Path.Combine(dir, "bad", manifest.Setup.Name)));

            // 断流：只给一半
            feed.CorruptPayload = false;
            feed.TruncateAfter = bytes.Length / 2;
            var truncated = false;
            try
            {
                await client.DownloadSetupAsync(manifest, Path.Combine(dir, "cut"));
            }
            catch (InvalidDataException)
            {
                truncated = true;
            }
            check("下载不完整时拒绝", truncated);
            check("断流后不留 .part",
                !File.Exists(Path.Combine(dir, "cut", manifest.Setup.Name + ".part")));

            // CNB 的下载端点永远先给 302 再去对象存储取，客户端必须跟得上
            feed.TruncateAfter = 0;
            feed.RedirectDownloads = true;
            var redirected = await client.DownloadSetupAsync(manifest, Path.Combine(dir, "redir"));
            check("跟随 302 后仍能下载并校验",
                redirected.Length > 0 && new FileInfo(redirected).Length == bytes.Length);
            check("302 目标确实被请求过", feed.RedirectFollowed);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    private const string Sha = "9f2c1d3e4b5a69788796a5b4c3d2e1f00f1e2d3c4b5a69788796a5b4c3d2e1f0";

    private static string Good(string version, byte[]? payload = null)
    {
        var sha = payload is null ? Sha : Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(payload));
        var size = payload?.Length ?? 1024;
        return $$"""
            {
              "channel": "stable",
              "version": "{{version}}",
              "tag": "v{{version}}",
              "notes": "测试用",
              "publishedAt": "2026-11-10T12:00:00Z",
              "setup": { "name": "ClassPoints-Setup-{{version}}.exe", "size": {{size}}, "sha256": "{{sha}}" }
            }
            """;
    }

    /// <summary>一个够用就好的假更新服务：只认那两条路径。</summary>
    private sealed class FakeFeed : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Task _loop;

        public FakeFeed()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            // 带一段仓库路径，模拟版本库根地址（https://host/<用户>/<仓库>），
            // 这样拼出来的 URL 和真实 CNB 直链形状完全一致
            BaseUrl = $"http://127.0.0.1:{port}/someone/dist";
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            _loop = Task.Run(LoopAsync);
        }

        public string BaseUrl { get; }

        public string ManifestJson { get; set; } = "";

        public byte[] SetupBytes { get; set; } = [];

        public bool CorruptPayload { get; set; }

        public bool MissingManifest { get; set; }

        public bool RedirectDownloads { get; set; }

        public bool RedirectFollowed { get; private set; }

        public int TruncateAfter { get; set; }

        public int DownloadCount { get; private set; }

        /// <summary>只要见到一次 Authorization 头就记下来：客户端不该带任何令牌。</summary>
        public bool SawAuthorization { get; private set; }

        private async Task LoopAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await _listener.GetContextAsync();
                }
                catch (Exception)
                {
                    return; // Stop() 之后 GetContext 会抛，正常退出
                }

                try
                {
                    Handle(ctx);
                }
                catch (Exception)
                {
                    // 单个请求出错不该把整个假服务弄死
                }
            }
        }

        private void Handle(HttpListenerContext ctx)
        {
            var path = ctx.Request.Url?.AbsolutePath ?? "";
            if (ctx.Request.Headers["Authorization"] is not null) SawAuthorization = true;

            const string dl = "/someone/dist/-/releases/download/";
            byte[] body;
            var status = 200;

            if (path == dl + "stable/" + UpdateClient.ManifestName)
            {
                if (MissingManifest)
                {
                    status = 404;
                    body = [];
                }
                else
                {
                    body = Encoding.UTF8.GetBytes(ManifestJson);
                }
            }
            else if (path.StartsWith("/blob/", StringComparison.Ordinal))
            {
                // 302 的目标：对象存储。这里才真正吐字节
                RedirectFollowed = true;
                DownloadCount++;
                body = CorruptPayload
                    ? SetupBytes.Select(b => (byte)(b ^ 0xFF)).ToArray()
                    : SetupBytes;
                if (TruncateAfter > 0 && TruncateAfter < body.Length) body = body[..TruncateAfter];
            }
            else if (path.StartsWith(dl + "v1.2.0/", StringComparison.Ordinal))
            {
                DownloadCount++;
                body = CorruptPayload
                    ? SetupBytes.Select(b => (byte)(b ^ 0xFF)).ToArray()
                    : SetupBytes;
                if (TruncateAfter > 0 && TruncateAfter < body.Length) body = body[..TruncateAfter];

                // 模拟 CNB：给一个 302，把客户端引到另一个主机的对象存储路径。
                // 用完全不同的路径而不是加 query —— AbsolutePath 不含 query，
                // 拿它判重会变成无限重定向。
                if (RedirectDownloads)
                {
                    ctx.Response.StatusCode = 302;
                    ctx.Response.Headers["Location"] = "/blob/" + Path.GetFileName(path);
                    ctx.Response.ContentLength64 = 0;
                    ctx.Response.Close();
                    return;
                }
            }
            else
            {
                status = 404;
                body = [];
            }

            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentLength64 = body.Length;
            if (body.Length > 0) ctx.Response.OutputStream.Write(body);
            ctx.Response.Close();
        }

        public void Dispose()
        {
            try { _listener.Stop(); } catch (Exception) { }
            try { _listener.Close(); } catch (Exception) { }
            try { _loop.Wait(1000); } catch (Exception) { }
        }
    }
}
