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
    string? LastError)
{
    /// <summary>
    /// 服务端明确拒收、永远也推不上去的那些（已经决定从队列里移出）。
    /// 和 Failed 分开：Failed 是"等一等还会再来"，这些是"不用等了，但要告诉人"。
    /// </summary>
    public IReadOnlyList<string> Dropped { get; init; } = Array.Empty<string>();

    /// <summary>
    /// 云端说"这条记录属于另一个班"的周期条目（outbox id，形如 `periods:{id}`）。
    ///
    /// 它和其它 Drop 不一样：这条数据本身没坏，只是**编号**在云端被别的班占了 ——
    /// 换一个编号就能推上去。调用方据此在本地换 id 并重新排队，见 PeriodRelocator。
    /// </summary>
    public IReadOnlyList<string> ForeignPeriods { get; init; } = Array.Empty<string>();

    public bool HasDropped => Dropped.Count > 0;
}

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

    public async Task<(string ClassId, string Access, string Refresh, bool ReadOnly, string? Message)> PairAsync(
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
        // 待审设备拿到的是只读票：配对算成功（能看班里数据），但写接口一律 403。
        // 服务端把话术也一起给过来，客户端照着显示就行 —— 里面带着特征码，
        // 老师要拿它去找班主任点通过，本地拼不出一模一样的句子。
        var readOnly = json["readOnly"]?.GetValue<bool>() ?? false;
        var message = json["message"]?.GetValue<string>();
        return (
            json["classId"]!.GetValue<string>(),
            json["access"]!.GetValue<string>(),
            json["refresh"]!.GetValue<string>(),
            readOnly,
            message);
    }

    public async Task<(string Access, string Refresh, bool ReadOnly)> RefreshAsync(string refresh, CancellationToken ct = default)
    {
        using var res = await _http.PostAsJsonAsync($"{BaseUrl}/api/v1/refresh", new { refresh }, Json, ct);
        var json = await ReadAsync(res, ct);
        return (
            json["access"]!.GetValue<string>(),
            json["refresh"]!.GetValue<string>(),
            json["readOnly"]?.GetValue<bool>() ?? false);
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
    /// - 失败分两类（见 <see cref="PushVerdicts"/>）：会自己好的退避重试，永远过不去的从队列移出，
    ///   并把原因带出去。**不能一律重试** —— 一条毒消息会把整批卡死，界面上只剩「待同步 N 条」。
    /// - 整批被永久拒时一分为二继续找，别让一条坏数据带走同批的好数据。
    /// </summary>
    public async Task<PushOutcome> PushAsync(IReadOnlyList<OutboxItem> items, CancellationToken ct = default)
    {
        var tally = new PushTally();

        var entries = new List<OutboxItem>();
        var ticks = new List<OutboxItem>();
        foreach (var item in items)
        {
            if (item.Kind is "entry" or "reverse") { entries.Add(item); continue; }
            if (item.Kind == "tick") { ticks.Add(item); continue; }

            var (ok, verdict, code, error) = await PushEntityAsync(item, ct);
            if (verdict == PushVerdict.Auth)
                return new PushOutcome(tally.Done, items.Select(i => i.Id).ToList(), true, error)
                {
                    Dropped = tally.Dropped,
                    ForeignPeriods = tally.ForeignPeriods,
                };

            if (ok) tally.Done.Add(item.Id);
            else if (verdict == PushVerdict.Drop)
            {
                // 以前这里当成"推成功了"直接出队，老师永远不知道哪条没上去
                tally.Dropped.Add(item.Id);
                tally.LastError = error;

                // 周期编号在云端属于别的班：不是"这条坏了"，是"编号撞了"。
                // 单独记下来，让 SyncService 在本地换个编号再推一次。
                if (item.Kind == "period" && code == "foreign_row") tally.ForeignPeriods.Add(item.Id);
            }
            else
            {
                tally.Failed.Add(item.Id);
                tally.LastError = error;
            }
        }

        foreach (var chunk in Chunk(entries, 50))
        {
            await PushBatchAsync("/api/v1/entries", "entries", chunk, EntryNode, "流水", tally, allowSplit: true, ct);
            if (tally.AuthExpired) break;
        }

        // 事件打点：和流水一样是"只追加"的事实表，一批一个事务
        foreach (var chunk in Chunk(ticks, 50))
        {
            await PushBatchAsync("/api/v1/counter_ticks", "ticks", chunk, TickNode, "计数", tally, allowSplit: true, ct);
            if (tally.AuthExpired) break;
        }

        return new PushOutcome(tally.Done, tally.Failed, tally.AuthExpired, tally.LastError)
        {
            Dropped = tally.Dropped,
            ForeignPeriods = tally.ForeignPeriods,
        };
    }

    /// <summary>
    /// 一批「只追加」的记录（流水 / 事件打点）。失败时按"会不会自己好"分开处理，
    /// 永久失败就二分下去定位到那一条 —— 一条毒数据不该让整批永远上不去。
    /// </summary>
    private async Task PushBatchAsync(string path, string arrayKey, List<OutboxItem> chunk,
        Func<OutboxItem, JsonObject> node, string what, PushTally tally, bool allowSplit, CancellationToken ct)
    {
        if (tally.AuthExpired || chunk.Count == 0) return;

        var (ok, verdict, code, error) = await PostBatchAsync(path, arrayKey, chunk, node, what, ct);

        if (ok)
        {
            foreach (var it in chunk) tally.Done.Add(it.Id);
            return;
        }

        if (verdict == PushVerdict.Auth)
        {
            tally.AuthExpired = true;
            tally.LastError = error;
            foreach (var it in chunk) tally.Failed.Add(it.Id);
            return;
        }

        if (verdict == PushVerdict.Retry)
        {
            tally.LastError = error;
            foreach (var it in chunk) tally.Failed.Add(it.Id);
            return;
        }

        // 永久拒收：整批被拒不代表每条都有问题，二分下去把坏的那条单独拎出来
        if (allowSplit && chunk.Count > 1)
        {
            var half = chunk.Count / 2;
            await PushBatchAsync(path, arrayKey, chunk.GetRange(0, half), node, what, tally, true, ct);
            await PushBatchAsync(path, arrayKey, chunk.GetRange(half, chunk.Count - half), node, what, tally, true, ct);
            return;
        }

        tally.Dropped.Add(chunk[0].Id);
        tally.LastError = error is null
            ? $"云端拒收了一条{what}记录，已从待推送里移出"
            : $"云端拒收{what}：{error}";
    }

    private async Task<(bool Ok, PushVerdict Verdict, string? Code, string? Error)> PostBatchAsync(
        string path, string arrayKey, List<OutboxItem> chunk,
        Func<OutboxItem, JsonObject> node, string what, CancellationToken ct)
    {
        var payload = new JsonObject { [arrayKey] = new JsonArray(chunk.Select(node).ToArray()) };
        try
        {
            using var res = await _http.PostAsync($"{BaseUrl}{path}",
                new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"), ct);
            var text = await res.Content.ReadAsStringAsync(ct);

            if (res.IsSuccessStatusCode) return (true, PushVerdict.Retry, null, null);

            var code = ErrorCode(text);
            var message = Describe(text, $"{what} {(int)res.StatusCode} 提交失败");
            return (false, PushVerdicts.Classify((int)res.StatusCode, code), code, message);
        }
        catch (Exception ex)
        {
            return (false, PushVerdict.Retry, null, ex.Message);
        }
    }

    private async Task<(bool Ok, PushVerdict Verdict, string? Code, string? Error)> PushEntityAsync(OutboxItem item, CancellationToken ct)
    {
        var (path, body) = Route(item);
        if (path is null) return (true, PushVerdict.Drop, null, null); // 不认识的 kind：丢，别卡队列
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
            if (res.IsSuccessStatusCode) return (true, PushVerdict.Retry, null, null);

            // 403 不要当"这条记录有问题"：它的意思是"这台机器现在不是班主任身份"，
            // 直接丢掉的话老师在界面上做的改动会静默消失。分类器把它归到"等一等会好"。
            var code = ErrorCode(text);
            var message = Describe(text, $"{path} {status} 提交失败");
            return (false, PushVerdicts.Classify(status, code), code, message);
        }
        catch (Exception ex)
        {
            Trace("!! " + ex.GetType().Name + ": " + ex.Message);
            return (false, PushVerdict.Retry, null, ex.Message);
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

    /// <summary>服务端错误体的机器可读代码（`{"error":"delta_too_large"}`），用来判断该不该重试。</summary>
    private static string? ErrorCode(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            return (JsonNode.Parse(body) as JsonObject)?["error"]?.GetValue<string>();
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>一轮推送的流水账。用可变对象而不是一堆 out 参数：推送是递归二分的，状态得共享。</summary>
    private sealed class PushTally
    {
        public List<string> Done { get; } = [];
        public List<string> Failed { get; } = [];
        public List<string> Dropped { get; } = [];
        public List<string> ForeignPeriods { get; } = [];
        public string? LastError { get; set; }
        public bool AuthExpired { get; set; }
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
            "counter" => "/api/v1/counters",
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

    /// <summary>事件打点。载荷形状是 {action:'tick', tick:{…}}，云端只收里面那个对象。</summary>
    private static JsonObject TickNode(OutboxItem item)
    {
        var node = JsonNode.Parse(item.Payload) as JsonObject ?? new JsonObject();
        var tick = node["tick"] as JsonObject ?? node;
        return (JsonObject)tick.DeepClone();
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
