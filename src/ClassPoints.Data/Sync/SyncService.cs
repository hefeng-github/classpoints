using System.Net.Http;
using System.Text.Json.Nodes;
using ClassPoints.Core;
using Microsoft.Data.Sqlite;

namespace ClassPoints.Data.Sync;

public sealed record SyncReport(
    bool Ok,
    string? Error,
    int Pushed,
    int Pending,
    int Pulled,
    long Next,
    DateTime At)
{    /// <summary>建班时把班级码回传给向导显示，其余场景为 null。</summary>
    public string? CreatedCode { get; init; }

    /// <summary>本轮之后是不是只读（待审设备）。true = 能看不能写。</summary>
    public bool ReadOnly { get; init; }

    /// <summary>配对时服务端给的「待审」话术，里面带着特征码，界面直接显示。</summary>
    public string? PendingMessage { get; init; }

    /// <summary>
    /// 云端不认这台机器的凭据了（refresh 401）。
    ///
    /// 和"网络断了"必须分开：断网会自己好，这个不会 —— 只有重新配对能救。
    /// 以前两种情况都只显示一句"同步失败"，老师看不出区别，也就不会去重配。
    /// </summary>
    public bool NeedsPairing { get; init; }

    /// <summary>凭据失效的成因。三种成因的提示语完全不同，见 <see cref="Sync.PairingReason"/>。</summary>
    public PairingReason Reason { get; init; }

    /// <summary>这一轮是"连不上"（断网、超时），还是"连上了但服务端不满意"。两者话术不同。</summary>
    public bool Offline { get; init; }

    /// <summary>拉取时有多少行因为本地唯一项撞车而按云端为准合并了（见 SyncService.Conflicts）。</summary>
    public int Conflicts { get; init; }

    public static SyncReport Skipped(string reason) => new(false, reason, 0, 0, 0, 0, DateTime.Now);
}

/// <summary>
/// refresh 被云端拒掉的原因。服务端把三种情况分开报（`bad_refresh` / `refresh_expired` /
/// `device_revoked`），客户端也要分开说 —— 老师该做的事完全不同：
/// 被别处轮换要查"是不是两台电脑共用了一份数据"，过期只是时间到了，被撤销得找班主任。
/// </summary>
public enum PairingReason
{
    None,

    /// <summary>token 认不出来：多半是同一份 sync.json 被复制到了另一台机器，那边续期把它换掉了。</summary>
    Rotated,

    /// <summary>90 天没续期，自然过期。</summary>
    Expired,

    /// <summary>设备被班主任从白名单里撤销，或整班重置过。</summary>
    Revoked,

    /// <summary>自动重配时云端说口令不对 —— 老师改过密码，本机存的那份过期了。</summary>
    SecretRejected,

    /// <summary>班级码在云端已经不存在（整班注销，或者换了另一个部署）。</summary>
    ClassGone,

    /// <summary>云端拒绝但没说清原因。</summary>
    Unknown,
}

public static class PairingReasonTexts
{
    public static string Title(this PairingReason reason) => reason switch
    {
        PairingReason.Rotated => "同步凭据被换掉了",
        PairingReason.Expired => "同步凭据过期了",
        PairingReason.Revoked => "这台设备已被撤销",
        PairingReason.SecretRejected => "管理密码改过了",
        PairingReason.ClassGone => "这个班在云端找不到了",
        _ => "同步凭据已失效",
    };

    /// <summary>一句话说清"为什么"和"怎么办"，界面直接显示，不再自己拼。</summary>
    public static string Advice(this PairingReason reason) => reason switch
    {
        PairingReason.Rotated =>
            "这台电脑的同步凭据在别处被换掉了（多半是另一台机器用了同一份数据目录，也可能重装过），" +
            "自动重配也没能接上。到「设置 → 同步」点「重新配对」，地址和班级码已经填好，" +
            "补一次班主任密码就行；积压的记录会自动补推。",
        PairingReason.Expired =>
            "这台电脑 90 天没连过云端，凭据自然过期了，自动重配也没能接上。" +
            "到「设置 → 同步」点「重新配对」，积压的记录不会丢。",
        PairingReason.Revoked =>
            "班主任在班级管理台把这台设备移出了白名单（或者整班重置过）。" +
            "请班主任先在网页上重新批准，再到「设置 → 同步」重新配对；本机的数据一条都没少。",
        PairingReason.SecretRejected =>
            "自动重配时云端说口令不对 —— 班主任密码或班长口令应该是改过了。" +
            "到「设置 → 同步」点「重新配对」，用现在的密码再连一次；本机的名单和流水都还在。",
        PairingReason.ClassGone =>
            "云端没有这个班级码了（整班被注销，或者同步地址换成了另一个部署）。" +
            "确认地址和班级码之后到「设置 → 同步」重新配对；已经记下的分不会被删。",
        _ =>
            "云端不再认这台电脑，自动重配也没成功。到「设置 → 同步」点「重新配对」，" +
            "本机的名单和流水都还在，积压的记录会自动补推。",
    };

    public static PairingReason FromCode(string? errorCode) => errorCode switch
    {
        "bad_refresh" => PairingReason.Rotated,
        "refresh_expired" => PairingReason.Expired,
        "device_revoked" => PairingReason.Revoked,

        // 下面几个是 /pair 的错误码：只有"自动重配也失败"之后才会走到这里
        "bad_secret" => PairingReason.SecretRejected,
        "class_not_found" => PairingReason.ClassGone,
        "device_blocked" or "review_queue_full" or "device_fingerprint_required" => PairingReason.Revoked,

        null or "" => PairingReason.None,
        _ => PairingReason.Unknown,
    };
}
/// <summary>
/// 同步编排：先推、再拉。
///
/// 两条铁律（方案 5.8）：
/// 1. 本地写入永远先成功 —— 这个类只读 outbox，绝不在用户点"加一分"的路径上。
/// 2. 拉取失败不报错 —— 界面只用角标显示"待同步 3 条"和"最后同步：2 分钟前"。
///
/// /sync 返回的是库里的原始列名（snake_case），和本地 SQLite 的列名一一对应，
/// 所以这里没有任何字段映射层，直接按列名写回。
/// </summary>
public sealed class SyncService(Database db, SyncOutbox outbox, SyncStore? store = null)
{
    private readonly SyncStore _store = store ?? new SyncStore();
    private SyncClient? _client;
    private bool _busy;

    public const string KeyLastSeq = "last_seq";
    public const string KeyLastPullAt = "last_pull_at";

    public bool IsConfigured => _store.Load() is not null;

    /// <summary>
    /// 已配对时 sync.json 里记的班级码 —— **云端认的那一个**。
    ///
    /// 它和「设置」里那份不是一回事：本机没连云端时先懒生成了一个随机码，
    /// 建班或配对之后要把设置改成云端这个，否则老师照着设置念出来的码
    /// 在另一台机器上配对只会得到 class_not_found。
    /// </summary>
    public string? PairedClassCode
    {
        get
        {
            var code = _store.Load()?.ClassCode;
            return string.IsNullOrWhiteSpace(code) ? null : code;
        }
    }

    private bool? _readOnly;

    /// <summary>云端判定这台机器只能看时触发。界面据此切只读、通过后自动恢复。</summary>
    public event Action? ReadOnlyChanged;

    /// <summary>
    /// 现在是不是「待审只读」。
    ///
    /// 值来自服务端：配对时 readOnly=true，或者续期时白名单仍是 pending。
    /// 首次读取才去解密 sync.json，之后缓存在内存里 —— 界面每轮状态条都会问它，
    /// 不该每次都走一遍 DPAPI。
    /// </summary>
    public bool IsReadOnly
    {
        get => _readOnly ??= _store.Load()?.ReadOnly ?? false;
        private set
        {
            if (_readOnly == value) return;
            _readOnly = value;
            ReadOnlyChanged?.Invoke();
        }
    }

    public long LastSeq => ReadState(KeyLastSeq) ?? 0;

    public DateTime LastPullAt
    {
        get
        {
            var ms = ReadState(KeyLastPullAt) ?? 0;
            return ms <= 0 ? DateTime.MinValue : Clock.ToLocal(ms).DateTime;
        }
    }

    public string LastSyncText
    {
        get
        {
            if (!IsConfigured) return "未配置同步";
            var at = LastPullAt;
            if (at == DateTime.MinValue) return "还没同步过";
            var gap = DateTime.Now - at;
            return gap.TotalSeconds < 60 ? "刚刚同步"
                : gap.TotalMinutes < 60 ? $"{gap.TotalMinutes:0} 分钟前"
                : gap.TotalHours < 24 ? $"{gap.TotalHours:0} 小时前"
                : at.ToString("MM-dd HH:mm");
        }
    }

    // ---------------- 配对 ----------------

    public async Task<SyncReport> CreateClassAsync(string baseUrl, string name, string ownerName,
        string ownerPin, string? recoveryCode, string kind, string? bootstrapSecret = null,
        CancellationToken ct = default)
    {
        // 规范化放在这里而不是只放 SyncClient 里：存盘的地址也要是规整过的，
        // 否则 sync.json 里会留一份"用户当时怎么打的"，下次读出来还得再处理一遍。
        var normalized = SyncClient.NormalizeBaseUrl(baseUrl);
        using var client = new SyncClient(normalized);
        var (classId, code, access, refresh) =
            await client.CreateClassAsync(name, ownerName, ownerPin, recoveryCode, kind, bootstrapSecret, ct);
        _store.Save(new SyncCredentials(normalized, classId, access, refresh, code)
        {
            Secret = ownerPin,
            Kind = kind,
        });
        return new SyncReport(true, null, 0, outbox.Count(), 0, 0, DateTime.Now) { CreatedCode = code };
    }

    public async Task<SyncReport> PairAsync(string baseUrl, string code, string secret, string kind,
        CancellationToken ct = default)
    {
        var normalized = SyncClient.NormalizeBaseUrl(baseUrl);
        using var client = new SyncClient(normalized);
        var (classId, access, refresh, readOnly, message) = await client.PairAsync(code, secret, kind, ct);
        _store.Save(new SyncCredentials(normalized, classId, access, refresh, code, readOnly)
        {
            // 口令留在本机（DPAPI 加密）：下次 refresh 被云端拒掉时不用再麻烦人
            Secret = secret,
            Kind = kind,
        });
        IsReadOnly = readOnly;
        // 手动配过之后，自动重配的冷却要清零 —— 刚才这一下已经证明口令是对的
        _repairBlockedUntil = DateTime.MinValue;
        // 待审不是失败：配对确实成功了，只是暂时只能看。Ok 仍然是 true，
        // 界面据 ReadOnly 决定说"连上了，等通过"还是"配对成功，开始同步"。
        return new SyncReport(true, null, 0, outbox.Count(), 0, 0, DateTime.Now)
        {
            ReadOnly = readOnly,
            PendingMessage = readOnly ? message : null,
        };
    }

    public void SignOut() => _store.Clear();

    /// <summary>
    /// 有待推送的流水时，先把它们引用的周期排进队列。
    ///
    /// 云端的 entries.period_id 有外键。周期没推上去的时候整批撞外键：
    /// 服务端 500、客户端一轮轮退避重试，界面上只显示「待推送 8 条」——
    /// 老师只会以为网络不好，而实际上是"这台机器的第 1 周期从没被推过"。
    ///
    /// 判断依据是队列本身，不需要额外记"哪些周期推过了"：
    /// 有流水要推 ⇒ 队列里必须有它的周期。已经在队列里的不重复加（幂等 key），
    /// 所以每批最多多推一条 upsert，云端按 id 覆盖，无副作用。
    /// </summary>
    private void QueuePeriodsForPendingEntries()
    {
        List<string> periodIds = [];
        try
        {
            using var conn = db.Open();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT payload FROM outbox WHERE kind IN ('entry','reverse','tick') LIMIT 200";
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    if (JsonNode.Parse(reader.GetString(0)) is not JsonObject node) continue;
                    // 流水是 {entry:{…}}，打点是 {tick:{…}}
                    var row = node["entry"] as JsonObject ?? node["tick"] as JsonObject ?? node;
                    if (row["periodId"]?.GetValue<string>() is { Length: > 0 } id) periodIds.Add(id);
                }
            }

            foreach (var id in periodIds.Distinct().ToList())
            {
                var period = ReadPeriod(conn, id);
                if (period is null) continue;
                SyncOutbox.EnqueueIfAbsent(conn, null, $"periods:{id}", "period",
                    new { action = "upsert", period });
            }
        }
        catch (Exception)
        {
            // 补周期失败不该挡住这一轮推送：entries 那边会拿到明确的 422 再显示给老师
        }
    }

    private static Period? ReadPeriod(SqliteConnection conn, string id)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM periods WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Mappers.Period(reader) : null;
    }

    // ---------------- 一轮同步 ----------------

    /// <summary>
    /// 推完再拉。返回的报告只用于界面角标，不用来判断业务成败 ——
    /// 断网了本地照样记分，这才是这个应用能不能用的分界线。
    /// </summary>
    public async Task<SyncReport> RunOnceAsync(int maxPages = 5, CancellationToken ct = default)
    {
        if (_busy) return SyncReport.Skipped("上一轮还没跑完");
        var credentials = _store.Load();
        if (credentials is null) return SyncReport.Skipped("还没配对");

        _busy = true;
        Conflicts = 0;
        try
        {
            _client = new SyncClient(credentials.BaseUrl, credentials.Access);
            var pushed = 0;
            string? pushError = null;

            // ---- 推 ----
            if (credentials.ReadOnly)
            {
                // 待审设备拿的是只读票：一条都不推。
                // 推上去也是 403，而且会把 outbox 里的记录一条条标成"失败"退避 ——
                // 队列越积越乱，等真通过时反而要先消化一堆退避时间。
                // 所以原地不动，等班主任点通过、下一轮换到全权令牌再一次性补推。
                pushError = null;
            }
            else
            {
                // 先把流水引用的周期排上队：云端 entries 有外键，周期不在那儿整批都推不上去
                QueuePeriodsForPendingEntries();

                var due = outbox.Due(200);
                if (due.Count > 0)
                {
                    var outcome = await _client.PushAsync(due, ct);

                    if (outcome.AuthExpired)
                    {
                        var refreshed = await TryRefreshAsync(credentials, ct);
                        if (refreshed is null) return RefreshFailed(0, 0, LastSeq);

                        _client.SetAccess(refreshed.Access);
                        credentials = refreshed;
                        outcome = await _client.PushAsync(due, ct);
                    }

                    outbox.RemoveMany(outcome.Done);
                    outbox.RemoveMany(outcome.Dropped);
                    foreach (var id in outcome.Failed)
                    {
                        var item = due.FirstOrDefault(i => i.Id == id);
                        if (item is not null) outbox.Backoff(item);
                    }

                    // 推上去的本地标记"已上云"：撤销要不要走冲销、墓碑敢不敢删，都看这个字段
                    outbox.MarkSynced(outcome.Done);
                    pushed = outcome.Done.Count;

                    // 退避的只报最后一条原因（下一轮还会再来）；被云端明确拒收的必须点名，
                    // 因为它已经从队列里出去了 —— 不说不等于没发生，老师会以为已经同步完。
                    pushError = outcome.Failed.Count > 0
                        ? outcome.LastError ?? "推送失败，稍后重试"
                        : outcome.Dropped.Count > 0
                            ? $"有 {outcome.Dropped.Count} 条记录被云端拒收，已从待推送里移出（{outcome.LastError}）"
                            : null;
                }
            }

            // ---- 拉 ----
            var pulled = 0;
            var next = LastSeq;
            for (var page = 0; page < maxPages; page++)
            {
                JsonObject batch;
                try
                {
                    batch = await _client.PullAsync(next, 200, ct);
                }
                catch (SyncException ex) when (ex.IsAuthFailure)
                {
                    // 只读票最长 30 分钟，而只读期间没有推送，也就没人触发续期。
                    // 这里不补一次的话，界面会永远停在"令牌过期，下一轮续期"，
                    // 而下一轮同样没有推送 —— 等到班主任点通过也恢复不了。
                    var refreshed = await TryRefreshAsync(credentials, ct);
                    if (refreshed is null) return RefreshFailed(pushed, pulled, next);

                    credentials = refreshed;
                    _client.SetAccess(refreshed.Access);
                    batch = await _client.PullAsync(next, 200, ct);
                }

                var hasMore = false;
                var applied = Apply(batch, out hasMore);
                pulled += applied;
                next = batch["next"]?.GetValue<long>() ?? next;
                WriteState(KeyLastSeq, next);
                WriteState(KeyLastPullAt, Clock.NowMs());
                AdoptReadOnly(batch, credentials);
                if (!hasMore) break;
            }

            // 拉取成功但推送有失败时，也要让界面看到原因，而不是假装一切正常
            return new SyncReport(pushError is null, pushError, pushed, outbox.Count(), pulled, next, DateTime.Now)
            {
                ReadOnly = IsReadOnly,
                Conflicts = Conflicts,
            };
        }
        catch (SyncException ex) when (ex.IsAuthFailure)
        {
            return new SyncReport(false, "令牌过期，下一轮会静默续期", 0, outbox.Count(), 0, LastSeq, DateTime.Now);
        }
        catch (Exception ex)
        {
            // 断网、超时、服务端 5xx：都不算错误，下一轮再来。
            // 但要分开记："连不上"和"连上了但被拒"在界面上是两种话术 ——
            // 一个是等一会儿，一个是必须去改点什么。
            return new SyncReport(false, ex.Message, 0, outbox.Count(), 0, LastSeq, DateTime.Now)
            {
                Offline = ex is HttpRequestException or TaskCanceledException or OperationCanceledException,
            };
        }
        finally
        {
            _client?.Dispose();
            _client = null;
            _busy = false;
        }
    }

    /// <summary>续期时云端说的"为什么不认这台机器"。只有 401 才有意义（其余是网络/服务端的事）。</summary>
    private string? _refreshErrorCode;

    /// <summary>自动重配失败的原因（`bad_secret` / `class_not_found` / `device_blocked`…）。</summary>
    private string? _repairErrorCode;

    /// <summary>自动重配失败后冷却到什么时候。口令错了再去试只会给云端送失败计数，最后被封 15 分钟。</summary>
    private DateTime _repairBlockedUntil = DateTime.MinValue;

    /// <summary>这一次是「自己接上了」。界面据此给一句轻提示，别让老师以为系统抽风。</summary>
    public event Action? AutoRepaired;

    private async Task<SyncCredentials?> TryRefreshAsync(SyncCredentials credentials, CancellationToken ct)
    {
        _refreshErrorCode = null;
        try
        {
            var (access, refresh, readOnly) = await _client!.RefreshAsync(credentials.Refresh, ct);
            var next = credentials with { Access = access, Refresh = refresh, ReadOnly = readOnly };
            _store.Save(next);
            // 续期是"现在"的判定：班主任刚点通过，这一下就该把只读摘掉
            IsReadOnly = readOnly;
            return next;
        }
        catch (SyncException ex) when (ex.Status == 401)
        {
            // 云端明确说"不认这个 refresh"：先自己接一次，接不上才需要人来
            _refreshErrorCode = ex.Code;
        }
        catch (Exception)
        {
            // 断网、超时、429、5xx：凭据还是好的，下一轮再来。
            // 以前这里也返回 null，于是"学校网断了"会被说成"凭据失效，请重新配对"——
            // 老师按提示跑去重配，越弄越乱。
            return null;
        }

        return await TryRepairAsync(credentials, ct);
    }

    /// <summary>
    /// 凭据被云端拒掉之后，用本机存着的口令自己重新配对一次。
    ///
    /// 为什么值得自动做：refresh token 一次一换，同一份数据目录被两台机器用（或者
    /// 换过系统、隔了 90 天）时，token 必然失效一次。而失效的时候本机其实什么都不缺 ——
    /// 班级码在，配对时的口令也在，完全够再换一对新的回来。把这一步推给老师，
    /// 等于让人停下手里的活去输一遍十分钟前才输过的密码。
    ///
    /// 三次机会用完（口令改了、设备被撤销、班级注销）就不再自己试：
    /// 继续试只会在云端攒失败计数，最后连手动重配都被限流挡住。
    /// </summary>
    private async Task<SyncCredentials?> TryRepairAsync(SyncCredentials stale, CancellationToken ct)
    {
        _repairErrorCode = null;
        if (!stale.CanSelfRepair) return null;
        if (DateTime.Now < _repairBlockedUntil) return null;

        try
        {
            using var client = new SyncClient(stale.BaseUrl);
            var (classId, access, refresh, readOnly, _) =
                await client.PairAsync(stale.ClassCode, stale.Secret, stale.Kind, ct);

            var next = stale with
            {
                ClassId = classId,
                Access = access,
                Refresh = refresh,
                ReadOnly = readOnly,
            };
            _store.Save(next);
            IsReadOnly = readOnly;
            _repairBlockedUntil = DateTime.MinValue;
            AutoRepaired?.Invoke();
            return next;
        }
        catch (SyncException ex)
        {
            // 口令不对 / 班级码不对 / 设备被撤销：再试一万次也一样。
            // 而且每试一次云端就记一次失败，攒够 6 次这个来源会被封 15 分钟 ——
            // 到时候连老师手动重配都进不来。所以冷却半小时。
            _repairErrorCode = ex.Code;
            _repairBlockedUntil = DateTime.Now.AddMinutes(30);
            return null;
        }
        catch (Exception)
        {
            // 网络断了：凭据本身没坏，过一会儿再自己试一次
            _repairBlockedUntil = DateTime.Now.AddMinutes(2);
            return null;
        }
    }

    /// <summary>续期失败之后的报告：真是凭据问题才叫 NeedsPairing，网络问题只说"等一会儿"。</summary>
    private SyncReport RefreshFailed(int pushed, int pulled, long next)
    {
        // 自动重配失败的原因更贴近"现在该做什么"：token 被换掉是过去时，
        // 口令不对才是拦住我们的那件事。
        var repaired = PairingReasonTexts.FromCode(_repairErrorCode);
        var reason = repaired != PairingReason.None ? repaired : PairingReasonTexts.FromCode(_refreshErrorCode);
        if (reason == PairingReason.None)
        {
            return new SyncReport(false, "连不上云端，下一轮再试", pushed, outbox.Count(), pulled, next, DateTime.Now)
            {
                Offline = true,
            };
        }

        return new SyncReport(false, reason.Title() + "：" + reason.Advice(), pushed, outbox.Count(), pulled, next, DateTime.Now)
        {
            NeedsPairing = true,
            Reason = reason,
        };
    }

    /// <summary>
    /// 按 /sync 的返回对齐本机的只读标记。
    ///
    /// 为什么信这一条而不是等续期：续期只在推送 401 时发生，而只读期间根本不推送，
    /// 所以只读设备永远等不到续期 —— 班主任点了通过，客户端还是只读。
    /// 拉取每 45 秒一次，是唯一稳定的心跳。
    /// </summary>
    private void AdoptReadOnly(JsonObject batch, SyncCredentials credentials)
    {
        var readOnly = batch["readOnly"]?.GetValue<bool>() ?? credentials.ReadOnly;
        if (readOnly == credentials.ReadOnly) return;

        _store.Save(credentials with { ReadOnly = readOnly });
        IsReadOnly = readOnly;
    }

    // ---------------- 落库 ----------------

    /// <summary>
    /// 这一轮里有多少行因为「本地唯一索引」撞车而按云端为准合并了。
    ///
    /// 为什么要计数而不是静默跳过：跳过本身是对的（不然整页事务回滚、游标永远不动），
    /// 但"某人的学号被让出去了"这种事老师有权知道 —— 下次导入名单时会按学号找人对不上。
    /// </summary>
    public int Conflicts { get; private set; }

    /// <summary>
    /// 把一页 /sync 写进本地库。顺序有讲究：先父后子，否则外键会挡。
    /// 整页一个事务：拉到一半断电，游标也不会先跑。
    /// </summary>
    private int Apply(JsonObject batch, out bool hasMore)
    {
        hasMore = batch["hasMore"]?.GetValue<bool>() ?? false;
        var applied = 0;


        using var conn = db.Open();
        using var tx = conn.BeginTransaction();

        applied += ApplyPeriods(conn, tx, Ar(batch, "periods"));
        applied += ApplyStudents(conn, tx, Ar(batch, "students"));
        applied += ApplyMembers(conn, tx, Ar(batch, "members"));
        applied += ApplyRules(conn, tx, Ar(batch, "rules"));
        applied += ApplyCounters(conn, tx, Ar(batch, "counters"));
        applied += ApplyNotes(conn, tx, Ar(batch, "note_templates"));
        applied += ApplySchemes(conn, tx, Ar(batch, "schemes"));
        applied += ApplyExams(conn, tx, Ar(batch, "exams"));
        applied += ApplyScores(conn, tx, Ar(batch, "exam_scores"));
        applied += ApplyWeights(conn, tx, Ar(batch, "scheme_weights"));
        applied += ApplyEntries(conn, tx, Ar(batch, "entries"));
        applied += ApplyTicks(conn, tx, Ar(batch, "counter_ticks"));
        applied += ApplyTombstones(conn, tx, batch["deleted"] as JsonObject);

        tx.Commit();
        return applied;
    }

    private static JsonArray Ar(JsonObject batch, string key) => batch[key] as JsonArray ?? new JsonArray();

    private static int ApplyPeriods(SqliteConnection conn, SqliteTransaction tx, JsonArray rows)
    {
        var n = 0;
        foreach (var row in rows)
        {
            if (row is not JsonObject o) continue;

            // 本地已有 active 周期时，远端那个 active 进不来（uq_period_active 是全局唯一）。
            // 先把本地的归档掉 —— 远端说谁是当前周期，就以远端为准。
            if (S(o, "status") == "active")
                Database.Exec(conn, tx,
                    "UPDATE periods SET status = 'archived', end_at = COALESCE(end_at, $at) WHERE status = 'active' AND id <> $id",
                    ("$at", L(o, "start_at")), ("$id", S(o, "id")));

            Database.Exec(conn, tx, """
                INSERT INTO periods (id, name, term_key, start_at, end_at, status, created_at)
                VALUES ($id, $name, $term, $start, $end, $status, $created)
                ON CONFLICT(id) DO UPDATE SET
                  name = excluded.name, term_key = excluded.term_key, start_at = excluded.start_at,
                  end_at = excluded.end_at, status = excluded.status
                """,
                ("$id", S(o, "id")), ("$name", S(o, "name")), ("$term", S(o, "term_key")),
                ("$start", L(o, "start_at")), ("$end", L(o, "end_at")), ("$status", S(o, "status")),
                ("$created", L(o, "created_at")));
            n++;
        }
        return n;
    }

    private int ApplyStudents(SqliteConnection conn, SqliteTransaction tx, JsonArray rows)
    {
        var n = 0;
        foreach (var row in rows)
        {
            if (row is not JsonObject o) continue;
            try
            {
                Database.Exec(conn, tx, """
                    INSERT INTO students (id, name, student_no, seat_no, active, rev, updated_at, deleted_at, created_at)
                    VALUES ($id, $name, $no, $seat, $active, $rev, $updated, $deleted, $created)
                    ON CONFLICT(id) DO UPDATE SET
                      name = excluded.name, student_no = excluded.student_no, seat_no = excluded.seat_no,
                      active = excluded.active, rev = excluded.rev,
                      updated_at = excluded.updated_at, deleted_at = excluded.deleted_at
                    """,
                    ("$id", S(o, "id")), ("$name", S(o, "name")), ("$no", S(o, "student_no")),
                    ("$seat", L(o, "seat_no")), ("$active", B(o, "active")),
                    ("$rev", L(o, "rev")), ("$updated", L(o, "updated_at")), ("$deleted", L(o, "deleted_at")),
                    ("$created", L(o, "created_at")));
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
            {
                // 学号撞了：两台机器各自导入名单时，同一个学号会落到两个不同的 id 上。
                // 本地 uq_students_no 是唯一的，硬插会让**整页**事务回滚 —— 于是每一轮都
                // 从这一页开始重新失败，游标永远不动，界面上只有"同步失败"四个字。
                // 处理原则：云端是权威 —— 学号归它，本地占着这个学号的那一行让出来。
                // 只让学号，不让人：两个学生都还在，只是其中一个没有学号了。
                Database.Exec(conn, tx,
                    "UPDATE students SET student_no = NULL WHERE student_no = $no AND id <> $id",
                    ("$no", S(o, "student_no")), ("$id", S(o, "id")));
                Database.Exec(conn, tx, """
                    INSERT INTO students (id, name, student_no, seat_no, active, rev, updated_at, deleted_at, created_at)
                    VALUES ($id, $name, $no, $seat, $active, $rev, $updated, $deleted, $created)
                    ON CONFLICT(id) DO UPDATE SET
                      name = excluded.name, student_no = excluded.student_no, seat_no = excluded.seat_no,
                      active = excluded.active, rev = excluded.rev,
                      updated_at = excluded.updated_at, deleted_at = excluded.deleted_at
                    """,
                    ("$id", S(o, "id")), ("$name", S(o, "name")), ("$no", S(o, "student_no")),
                    ("$seat", L(o, "seat_no")), ("$active", B(o, "active")),
                    ("$rev", L(o, "rev")), ("$updated", L(o, "updated_at")), ("$deleted", L(o, "deleted_at")),
                    ("$created", L(o, "created_at")));
                Conflicts++;
            }
            n++;
        }
        return n;
    }

    private static int ApplyMembers(SqliteConnection conn, SqliteTransaction tx, JsonArray rows)
    {
        var n = 0;
        foreach (var row in rows)
        {
            if (row is not JsonObject o) continue;
            Database.Exec(conn, tx, """
                INSERT INTO members (id, display_name, role, pin_hash, created_at, revoked_at, rev, updated_at, deleted_at)
                VALUES ($id, $name, $role, $pin, $created, $revoked, $rev, $updated, $deleted)
                ON CONFLICT(id) DO UPDATE SET
                  display_name = excluded.display_name, role = excluded.role, pin_hash = excluded.pin_hash,
                  revoked_at = excluded.revoked_at, rev = excluded.rev, updated_at = excluded.updated_at,
                  deleted_at = excluded.deleted_at
                """,
                ("$id", S(o, "id")), ("$name", S(o, "display_name")),
                ("$role", MemberRoleNames.Parse(S(o, "role")).ToDb()), ("$pin", S(o, "pin_hash")),
                ("$created", L(o, "created_at")), ("$revoked", L(o, "revoked_at")), ("$rev", L(o, "rev")),
                ("$updated", L(o, "updated_at")), ("$deleted", L(o, "deleted_at")));
            n++;
        }
        return n;
    }

    private static int ApplyRules(SqliteConnection conn, SqliteTransaction tx, JsonArray rows)
    {
        var n = 0;
        foreach (var row in rows)
        {
            if (row is not JsonObject o) continue;
            Database.Exec(conn, tx, """
                INSERT INTO rules (id, label, delta, icon, sort, preset_key, owner_id, cap_value, cap_scope, archived_at, rev, updated_at, deleted_at)
                VALUES ($id, $label, $delta, $icon, $sort, $preset, $owner, $cap, $scope, $archived, $rev, $updated, $deleted)
                ON CONFLICT(id) DO UPDATE SET
                  label = excluded.label, delta = excluded.delta, icon = excluded.icon, sort = excluded.sort,
                  preset_key = excluded.preset_key, owner_id = excluded.owner_id,
                  cap_value = excluded.cap_value, cap_scope = excluded.cap_scope,
                  archived_at = excluded.archived_at, rev = excluded.rev, updated_at = excluded.updated_at,
                  deleted_at = excluded.deleted_at
                """,
                ("$id", S(o, "id")), ("$label", S(o, "label")), ("$delta", L(o, "delta") ?? 0),
                ("$icon", S(o, "icon")), ("$sort", L(o, "sort") ?? 0), ("$preset", S(o, "preset_key")),
                ("$owner", S(o, "owner_id")), ("$cap", L(o, "cap_value")), ("$scope", S(o, "cap_scope")),
                ("$archived", L(o, "archived_at")),
                ("$rev", L(o, "rev") ?? 1), ("$updated", L(o, "updated_at")), ("$deleted", L(o, "deleted_at")));
            n++;
        }
        return n;
    }

    private int ApplyCounters(SqliteConnection conn, SqliteTransaction tx, JsonArray rows)
    {
        var n = 0;
        foreach (var row in rows)
        {
            if (row is not JsonObject o) continue;
            try
            {
                Database.Exec(conn, tx, """
                    INSERT INTO counters (id, label, icon, sort, preset_key, archived_at, rev, updated_at, deleted_at)
                    VALUES ($id, $label, $icon, $sort, $preset, $archived, $rev, $updated, $deleted)
                    ON CONFLICT(id) DO UPDATE SET
                      label = excluded.label, icon = excluded.icon, sort = excluded.sort,
                      preset_key = excluded.preset_key, archived_at = excluded.archived_at,
                      rev = excluded.rev, updated_at = excluded.updated_at, deleted_at = excluded.deleted_at
                    """,
                    ("$id", S(o, "id")), ("$label", S(o, "label")), ("$icon", S(o, "icon")),
                    ("$sort", L(o, "sort") ?? 0), ("$preset", S(o, "preset_key")),
                    ("$archived", L(o, "archived_at")),
                    ("$rev", L(o, "rev") ?? 1), ("$updated", L(o, "updated_at")), ("$deleted", L(o, "deleted_at")));
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
            {
                // 两台机器各自建了同一类事件（预设的 preset_key 一样，id 不一样）。
                // 和备注模板同理：键一样就是同一件事，留着已有的那条，别让整页拉取翻车。
                Conflicts++;
                continue;
            }
            n++;
        }
        return n;
    }

    /// <summary>
    /// 事件打点：只追加，按 id 幂等（重拉不会重复计数）。
    /// 不写 seq —— 那是本地自增列，把云端的值抄过来必然撞主键。
    /// </summary>
    private static int ApplyTicks(SqliteConnection conn, SqliteTransaction tx, JsonArray rows)
    {
        var n = 0;
        foreach (var row in rows)
        {
            if (row is not JsonObject o) continue;
            var id = S(o, "id");
            if (string.IsNullOrEmpty(id)) continue;

            Database.Exec(conn, tx, """
                INSERT OR IGNORE INTO counter_ticks
                  (id, counter_id, student_id, period_id, value, note, operator_id, operator_name, occurred_at, created_at)
                VALUES ($id, $c, $s, $p, $v, $note, $op, $opName, $at, $created)
                """,
                ("$id", id), ("$c", S(o, "counter_id")), ("$s", S(o, "student_id")), ("$p", S(o, "period_id")),
                ("$v", L(o, "value") ?? 0), ("$note", S(o, "note")),
                ("$op", S(o, "operator_id") ?? "unknown"), ("$opName", S(o, "operator_name") ?? "未知"),
                ("$at", L(o, "occurred_at")), ("$created", L(o, "created_at")));
            n++;
        }
        return n;
    }

    private int ApplyNotes(SqliteConnection conn, SqliteTransaction tx, JsonArray rows)
    {
        var n = 0;
        foreach (var row in rows)
        {
            if (row is not JsonObject o) continue;
            try
            {
                Database.Exec(conn, tx, """
                    INSERT INTO note_templates (id, text, use_count, last_used_at, archived_at, rev, updated_at, deleted_at)
                    VALUES ($id, $text, $count, $last, $archived, $rev, $updated, $deleted)
                    ON CONFLICT(id) DO UPDATE SET
                      text = excluded.text, use_count = excluded.use_count, last_used_at = excluded.last_used_at,
                      archived_at = excluded.archived_at, rev = excluded.rev, updated_at = excluded.updated_at,
                      deleted_at = excluded.deleted_at
                    """,
                    ("$id", S(o, "id")), ("$text", S(o, "text")), ("$count", L(o, "use_count") ?? 1),
                    ("$last", L(o, "last_used_at")), ("$archived", L(o, "archived_at")), ("$rev", L(o, "rev") ?? 1),
                    ("$updated", L(o, "updated_at")), ("$deleted", L(o, "deleted_at")));
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
            {
                // 两台机器各自攒了一句同样的话（「作业未交」这种），云端给了两个 id，
                // 本地 uq_note_text 只认文本。文本一样就是同一个意思 —— 留着已有的那条即可，
                // 不能让这一行把整页拉取拖住。
                Conflicts++;
                continue;
            }
            n++;
        }
        return n;
    }

    private static int ApplySchemes(SqliteConnection conn, SqliteTransaction tx, JsonArray rows)
    {
        var n = 0;
        foreach (var row in rows)
        {
            if (row is not JsonObject o) continue;
            if (B(o, "is_active") == 1)
                Database.Exec(conn, tx, "UPDATE schemes SET is_active = 0");

            Database.Exec(conn, tx, """
                INSERT INTO schemes (id, name, exam_weight, daily_weight, normalize, is_active, created_at)
                VALUES ($id, $name, $exam, $daily, $norm, $active, $created)
                ON CONFLICT(id) DO UPDATE SET
                  name = excluded.name, exam_weight = excluded.exam_weight, daily_weight = excluded.daily_weight,
                  normalize = excluded.normalize, is_active = excluded.is_active
                """,
                ("$id", S(o, "id")), ("$name", S(o, "name")), ("$exam", D(o, "exam_weight")),
                ("$daily", D(o, "daily_weight")), ("$norm", B(o, "normalize")), ("$active", B(o, "is_active")),
                ("$created", L(o, "created_at")));
            n++;
        }
        return n;
    }

    private static int ApplyExams(SqliteConnection conn, SqliteTransaction tx, JsonArray rows)
    {
        var n = 0;
        foreach (var row in rows)
        {
            if (row is not JsonObject o) continue;
            Database.Exec(conn, tx, """
                INSERT INTO exams (id, period_id, name, full_score, exam_date, imported_at, source_file)
                VALUES ($id, $period, $name, $full, $date, $imported, $src)
                ON CONFLICT(id) DO UPDATE SET
                  name = excluded.name, full_score = excluded.full_score, exam_date = excluded.exam_date,
                  imported_at = excluded.imported_at, source_file = excluded.source_file
                """,
                ("$id", S(o, "id")), ("$period", S(o, "period_id")), ("$name", S(o, "name")),
                ("$full", D(o, "full_score") ?? 750), ("$date", S(o, "exam_date")),
                ("$imported", L(o, "imported_at")), ("$src", S(o, "source_file")));
            n++;
        }
        return n;
    }

    private static int ApplyScores(SqliteConnection conn, SqliteTransaction tx, JsonArray rows)
    {
        var n = 0;
        foreach (var row in rows)
        {
            if (row is not JsonObject o) continue;
            Database.Exec(conn, tx, """
                INSERT OR REPLACE INTO exam_scores (exam_id, student_id, score, absent)
                VALUES ($exam, $student, $score, $absent)
                """,
                ("$exam", S(o, "exam_id")), ("$student", S(o, "student_id")),
                ("$score", D(o, "score")), ("$absent", B(o, "absent")));
            n++;
        }
        return n;
    }

    private static int ApplyWeights(SqliteConnection conn, SqliteTransaction tx, JsonArray rows)
    {
        var n = 0;
        foreach (var row in rows)
        {
            if (row is not JsonObject o) continue;
            Database.Exec(conn, tx, """
                INSERT OR REPLACE INTO scheme_weights (scheme_id, exam_id, weight)
                VALUES ($scheme, $exam, $weight)
                """,
                ("$scheme", S(o, "scheme_id")), ("$exam", S(o, "exam_id")), ("$weight", D(o, "weight") ?? 0));
            n++;
        }
        return n;
    }

    /// <summary>
    /// 流水只追加：先 INSERT OR IGNORE（重拉不会重复计分），再单独更新撤销标记。
    /// 注意不写 seq —— 本地 seq 是本地自增的，服务端那个全局 seq 直接抄过来一定撞主键。
    /// </summary>
    private static int ApplyEntries(SqliteConnection conn, SqliteTransaction tx, JsonArray rows)
    {
        var n = 0;
        foreach (var row in rows)
        {
            if (row is not JsonObject o) continue;
            var id = S(o, "id");
            if (string.IsNullOrEmpty(id)) continue;

            Database.Exec(conn, tx, """
                INSERT OR IGNORE INTO entries
                  (id, batch_id, period_id, student_id, rule_id, rule_label, rule_delta_default, delta, source,
                   note, note_template_id, capped, over_cap, operator_id, operator_name, identify_method,
                   occurred_at, created_at, reverses_entry_id, reversed_at, reversed_by, synced)
                VALUES ($id, $batch, $period, $student, $rule, $label, $def, $delta, $source,
                   $note, $noteId, $capped, $over, $opId, $opName, $method,
                   $occurred, $created, $reverses, $reversed, $reversedBy, 1)
                """,
                ("$id", id), ("$batch", S(o, "batch_id")), ("$period", S(o, "period_id")),
                ("$student", S(o, "student_id")), ("$rule", S(o, "rule_id")), ("$label", S(o, "rule_label")),
                ("$def", L(o, "rule_delta_default")), ("$delta", L(o, "delta") ?? 0),
                ("$source", S(o, "source") ?? "rule"), ("$note", S(o, "note")),
                ("$noteId", S(o, "note_template_id")), ("$capped", B(o, "capped")), ("$over", B(o, "over_cap")),
                ("$opId", S(o, "operator_id") ?? "unknown"), ("$opName", S(o, "operator_name") ?? "未知"),
                ("$method", S(o, "identify_method")), ("$occurred", L(o, "occurred_at")),
                ("$created", L(o, "created_at")), ("$reverses", S(o, "reverses_entry_id")),
                ("$reversed", L(o, "reversed_at")), ("$reversedBy", S(o, "reversed_by")));

            // 撤销发生在远端时，本地这条要跟着变成"已撤销"
            var reversedAt = L(o, "reversed_at");
            if (reversedAt is not null)
                Database.Exec(conn, tx,
                    "UPDATE entries SET reversed_at = $at, reversed_by = $by, synced = 1 WHERE id = $id",
                    ("$id", id), ("$at", reversedAt), ("$by", S(o, "reversed_by")));
            n++;
        }
        return n;
    }

    /// <summary>服务端说没有的行，本地也得删 —— 否则会一直留着一份幽灵数据。</summary>
    private static int ApplyTombstones(SqliteConnection conn, SqliteTransaction tx, JsonObject? deleted)
    {
        if (deleted is null) return 0;
        var n = 0;

        (string Table, string Key)[] map =
        [
            ("students", "students"),
            ("rules", "rules"),
            ("members", "members"),
            ("note_templates", "note_templates"),
            ("counters", "counters"),
            ("exams", "exams"),
            ("schemes", "schemes"),
            ("periods", "periods"),
        ];

        foreach (var (jsonKey, table) in map)
        {
            if (deleted[jsonKey] is not JsonArray ids) continue;
            foreach (var id in ids)
            {
                var key = id?.GetValue<string>();
                if (string.IsNullOrEmpty(key)) continue;
                Database.Exec(conn, tx, $"DELETE FROM {table} WHERE id = $id", ("$id", key));
                n++;
            }
        }

        // 成绩的 row_id 是 exam:student
        if (deleted["exam_scores"] is JsonArray scores)
        {
            foreach (var id in scores)
            {
                var raw = id?.GetValue<string>();
                if (string.IsNullOrEmpty(raw)) continue;
                var parts = raw.Split(':', 2);
                if (parts.Length != 2) continue;
                Database.Exec(conn, tx, "DELETE FROM exam_scores WHERE exam_id = $e AND student_id = $s",
                    ("$e", parts[0]), ("$s", parts[1]));
                n++;
            }
        }

        // 流水：只删"已经同步过"的行。本地刚记、还没推上去的那条不能因为服务端没有就抹掉。
        if (deleted["entries"] is JsonArray entries)
        {
            foreach (var id in entries)
            {
                var key = id?.GetValue<string>();
                if (string.IsNullOrEmpty(key)) continue;
                Database.Exec(conn, tx, "DELETE FROM entries WHERE id = $id AND synced = 1", ("$id", key));
                n++;
            }
        }

        return n;
    }

    // ---------------- 状态读写 ----------------

    private long? ReadState(string key)
    {
        using var conn = db.Open();
        var raw = Database.Scalar(conn, Sql.SyncStateGet, ("$key", key));
        return raw is string s && long.TryParse(s, out var v) ? v : null;
    }

    private void WriteState(string key, long value)
    {
        using var conn = db.Open();
        Database.Exec(conn, Sql.SyncStateSet, ("$key", key), ("$value", value.ToString()));
    }

    // ---------------- JSON 取值 ----------------

    private static string? S(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static long? L(JsonObject o, string key)
    {
        if (o[key] is not JsonValue v) return null;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<double>(out var d)) return (long)d;
        if (v.TryGetValue<string>(out var s) && long.TryParse(s, out var p)) return p;
        return null;
    }

    private static double? D(JsonObject o, string key)
    {
        if (o[key] is not JsonValue v) return null;
        if (v.TryGetValue<double>(out var d)) return d;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<string>(out var s) && double.TryParse(s, out var p)) return p;
        return null;
    }

    private static int B(JsonObject o, string key) => (int)(L(o, key) ?? 0);
}
