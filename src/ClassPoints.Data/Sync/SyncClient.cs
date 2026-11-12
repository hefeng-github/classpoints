using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClassPoints.Core;

namespace ClassPoints.Data.Sync;

public sealed record PushOutcome(
    IReadOnlyList<string> Done,
    IReadOnlyList<string> Failed,
    bool AuthExpired,
    string? LastError);

public sealed record PullOutcome(
    long Next,
    bool HasMore,
    int Entries,
    int Entities,
    int Deleted);

/// <summary>
/// HTTP 客户端。只认路径和 JSON 结构，不认 Worker、不认 D1 ——
/// 换云端时这个文件一个字节都不用改（见方案 5.7 的"接口契约"那一层）。
/// </summary>
public sealed class SyncClient : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;

    public SyncClient(string baseUrl, string? accessToken = null)
    {
        BaseUrl = NormalizeBaseUrl(baseUrl);
        // 不走系统代理：系统代理会让 .NET 用绝对 URI 作请求行（POST http://host/path），
        // 而 Cloudflare 边缘与本地 wrangler dev 都只认 origin-form，直接回 500。
        // 真需要代理的学校网络，用 CP_SYNC_PROXY 显式指定，别静默继承。
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
        };
        var proxy = Environment.GetEnvironmentVariable("CP_SYNC_PROXY");
        if (!string.IsNullOrWhiteSpace(proxy))
        {
            handler.UseProxy = true;
            handler.Proxy = new System.Net.WebProxy(proxy);
        }

        _http = new HttpClient(handler)
        {
            // 一体机休眠醒来后 TCP 是假活的，不设超时会一直挂着
            Timeout = TimeSpan.FromSeconds(15),
        };
        if (!string.IsNullOrWhiteSpace(accessToken))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        
    }

    public string BaseUrl { get; }

    /// <summary>
    /// 地址规范化 + 传输层校验。
    ///
    /// 只认 https：同步的是全班名单和每一笔加减分，走明文等于把这一个班的东西
    /// 摊在教室的 AP 上。唯一的例外是本机回环（127.0.0.1 / localhost），
    /// 那是开发时跑 wrangler dev 用的，不出网卡。
    ///
    /// 不带协议头的输入自动补 https —— 用户填 "classpoints.xxx.workers.dev"
    /// 是最自然的方式，不该因为这个就报错。
    /// </summary>
    public static string NormalizeBaseUrl(string? raw) => Net.Endpoint.Normalize(raw, "同步地址");

    public void SetAccess(string token) =>
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

    // ---------------- 配对 ----------------

    public async Task<(string ClassId, string Code, string Access, string Refresh)> CreateClassAsync(
        string name, string ownerName, string ownerPin, string? recoveryCode, string kind,
        string? bootstrapSecret = null, CancellationToken ct = default)
    {
        var body = new
        {
            name,
            ownerName,
            ownerPin,
            recoveryCode,
            kind,
            deviceLabel = Environment.MachineName,
            // 班级开了设备审核时，服务端要靠这个值判断"这台机器批过没有"
            deviceFingerprint = DeviceFingerprint.Raw,
        };
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/api/v1/classes")
        {
            Content = JsonContent.Create(body, options: Json),
        };
        // 部署口令只放请求头，不进 body、不进日志、不落盘。
        // 没配 BOOTSTRAP_SECRET 的服务端会直接忽略这个头。
        if (!string.IsNullOrWhiteSpace(bootstrapSecret))
            req.Headers.Add("x-bootstrap-secret", bootstrapSecret);

        using var res = await _http.SendAsync(req, ct);
        var json = await ReadAsync(res, ct);
        return (
            json["classId"]!.GetValue<string>(),
            json["code"]!.GetValue<string>(),
            json["access"]!.GetValue<string>(),
            json["refresh"]!.GetValue<string>());
    }

    public async Task<(string ClassId, string Access, string Refresh)> PairAsync(
        string code, string secret, string kind, CancellationToken ct = default)
    {
        var body = new
        {
            code,
            secret,
            kind,
            deviceLabel = Environment.MachineName,
            deviceFingerprint = DeviceFingerprint.Raw,
        };
        using var res = await _http.PostAsJsonAsync($"{BaseUrl}/api/v1/pair", body, Json, ct);
        var json = await ReadAsync(res, ct);
        return (
            json["classId"]!.GetValue<string>(),
            json["access"]!.GetValue<string>(),
            json["refresh"]!.GetValue<string>());
    }

    public async Task<(string Access, string Refresh)> RefreshAsync(string refresh, CancellationToken ct = default)
    {
        using var res = await _http.PostAsJsonAsync($"{BaseUrl}/api/v1/refresh", new { refresh }, Json, ct);
        var json = await ReadAsync(res, ct);
        return (json["access"]!.GetValue<string>(), json["refresh"]!.GetValue<string>());
    }

    public async Task<bool> PingAsync(CancellationToken ct = default)
    {
        try
        {
            using var res = await _http.GetAsync($"{BaseUrl}/api/v1/ping", ct);
            return res.IsSuccessStatusCode;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // ---------------- 推送 ----------------

    /// <summary>
    /// 一次推一批 outbox。
    /// - 流水按 50 条一批合并成一次请求（云端整批一个事务，也只算一次配额）。
    /// - 4xx 一律当"这条永远推不上去"，直接从队列里丢掉，否则队列会被一条毒消息卡死；
    ///   但要排除 423（正在重置周期），那是暂时性的。
    /// - 5xx / 网络错误留在队列里等退避重试。
    /// </summary>
    public async Task<PushOutcome> PushAsync(IReadOnlyList<OutboxItem> items, CancellationToken ct = default)
    {
        var done = new List<string>();
        var failed = new List<string>();
        string? lastError = null;

        var entries = new List<OutboxItem>();
        foreach (var item in items)
        {
            if (item.Kind is "entry" or "reverse") entries.Add(item);
            else
            {
                var (ok, authExpired, error) = await PushEntityAsync(item, ct);
                if (authExpired) return new PushOutcome(done, items.Select(i => i.Id).ToList(), true, error);
                if (ok) done.Add(item.Id);
                else { failed.Add(item.Id); lastError = error; }
            }
        }

        foreach (var chunk in Chunk(entries, 50))
        {
            var payload = new JsonObject { ["entries"] = new JsonArray(chunk.Select(EntryNode).ToArray()) };
            try
            {
                using var res = await _http.PostAsync($"{BaseUrl}/api/v1/entries",
                    new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"), ct);
                var text = await res.Content.ReadAsStringAsync(ct);

                if (res.StatusCode == HttpStatusCode.Unauthorized)
                    return new PushOutcome(done, items.Select(i => i.Id).ToList(), true, "令牌过期");

                if (res.IsSuccessStatusCode)
                    foreach (var it in chunk) done.Add(it.Id);
                else
                {
                    lastError = Describe(text, $"entries {(int)res.StatusCode} 提交失败");
                    foreach (var it in chunk) failed.Add(it.Id);
                }
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
                foreach (var it in chunk) failed.Add(it.Id);
            }
        }

        return new PushOutcome(done, failed, false, lastError);
    }

    private async Task<(bool Ok, bool AuthExpired, string? Error)> PushEntityAsync(OutboxItem item, CancellationToken ct)
    {
        var (path, body) = Route(item);
        if (path is null) return (true, false, null); // 不认识的 kind：丢，别卡队列
        Trace("POST " + path);

        try
        {
            using var res = await _http.PostAsync($"{BaseUrl}{path}",
                new StringContent(body, Encoding.UTF8, "application/json"), ct);

            // 响应体必须读掉：不读就 Dispose 会让这条连接处于半关闭状态，
            // 下一个请求复用它会直接被服务端打断（表现是莫名其妙的 500）。
            var text = await res.Content.ReadAsStringAsync(ct);
            var status = (int)res.StatusCode;

            Trace($"<- {status}");
            if (res.StatusCode == HttpStatusCode.Unauthorized) return (false, true, "令牌过期");
            if (res.IsSuccessStatusCode) return (true, false, null);

            // 423 是"正在重置周期"，过几十秒再来；其余 4xx 是这条记录本身有问题，重试一万次也一样。
            if (status == 423 || status >= 500)
                return (false, false, $"{path} {status} {(string.IsNullOrWhiteSpace(text) ? "" : text.Trim())}".Trim());

            // 403 单独处理：它不代表"这条记录有问题"，而是"这台机器现在是班委身份"。
            // 直接丢掉的话，老师在界面上做的改动会静默消失 —— 本地还有，云端永远没有，
            // 另一台设备上也看不到。所以留在队列里退避，等换回班主任/班长身份再补推。
            if (status == 403)
                return (false, false, Describe(text, "当前身份没有这个权限，这条改动还没同步到云端"));

            return (true, false, null);
        }
        catch (Exception ex)
        {
            Trace("!! " + ex.GetType().Name + ": " + ex.Message);
            return (false, false, ex.Message);
        }
    }

    /// <summary>
    /// 把服务端的错误体整理成一句人话。云端 4xx 回的是 {error, message}，
    /// 把整段 JSON 丢到界面角标上没人看得懂，但完全不说是哪条也不行。
    /// </summary>
    private static string Describe(string body, string fallback)
    {
        if (string.IsNullOrWhiteSpace(body)) return fallback;
        try
        {
            if (JsonNode.Parse(body) is JsonObject obj)
            {
                var message = obj["message"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(message)) return message;
            }
        }
        catch (Exception)
        {
            // 不是 JSON 就原样带出去
        }
        return $"{fallback}（{body.Trim()}）";
    }

    /// <summary>
    /// 排障开关：设 CP_SYNC_TRACE=1 就把每次推送的路径与状态码打到 stderr。
    /// 平时一句话不输出 —— 教室电脑上的控制台不该出现这些东西。
    /// </summary>
    private static void Trace(string line)
    {
        if (Environment.GetEnvironmentVariable("CP_SYNC_TRACE") == "1")
            Console.Error.WriteLine("      " + line);
    }

    /// <summary>outbox 的 kind → 云端路径。payload 原样转发，云端按 action 分支。</summary>
    private static (string? Path, string Body) Route(OutboxItem item)
    {
        var node = JsonNode.Parse(item.Payload) as JsonObject ?? new JsonObject();
        var action = node["action"]?.GetValue<string>();

        // 权重是一次换一整张表，路径多一段
        if (item.Kind == "scheme" && action == "weights")
        {
            var schemeId = node["schemeId"]?.GetValue<string>();
            var weights = node["weights"] ?? new JsonObject();
            return ($"/api/v1/schemes/{Uri.EscapeDataString(schemeId ?? "")}/weights",
                new JsonObject { ["weights"] = weights.DeepClone() }.ToJsonString());
        }

        var path = item.Kind switch
        {
            "student" => "/api/v1/students",
            "rule" => "/api/v1/rules",
            "member" => "/api/v1/members",
            "note" => "/api/v1/note_templates",
            "period" => "/api/v1/periods",
            "exam" => "/api/v1/exams",
            "scheme" => "/api/v1/schemes",
            _ => null,
        };
        return (path, node.ToJsonString());
    }

    private static JsonObject EntryNode(OutboxItem item)
    {
        var node = JsonNode.Parse(item.Payload) as JsonObject ?? new JsonObject();
        var entry = node["entry"] as JsonObject ?? node;
        return (JsonObject)entry.DeepClone();
    }

    // ---------------- 拉取 ----------------

    public async Task<JsonObject> PullAsync(long since, int limit = 200, CancellationToken ct = default)
    {
        using var res = await _http.GetAsync($"{BaseUrl}/api/v1/sync?since={since}&limit={limit}", ct);
        return await ReadAsync(res, ct);
    }

    private static async Task<JsonObject> ReadAsync(HttpResponseMessage res, CancellationToken ct)
    {
        var text = await res.Content.ReadAsStringAsync(ct);
        JsonObject? node = null;
        try
        {
            node = JsonNode.Parse(text) as JsonObject;
        }
        catch (Exception)
        {
            // 下面统一报错
        }

        if (!res.IsSuccessStatusCode)
        {
            var message = node?["message"]?.GetValue<string>() ?? text;
            throw new SyncException((int)res.StatusCode, node?["error"]?.GetValue<string>() ?? "http_error", message);
        }

        return node ?? throw new SyncException((int)res.StatusCode, "bad_json", "服务端返回的不是 JSON 对象");
    }

    private static IEnumerable<List<T>> Chunk<T>(List<T> source, int size)
    {
        for (var i = 0; i < source.Count; i += size)
            yield return source.GetRange(i, System.Math.Min(size, source.Count - i));
    }

    public void Dispose() => _http.Dispose();
}

public sealed class SyncException(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;

    public bool IsAuthFailure => Status == 401;
}
