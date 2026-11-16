using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClassPoints.Core;

namespace ClassPoints.Data.Sync;

/// <summary>
/// 同步凭据：地址和班级 id 明文，refresh token 用 DPAPI 加密后存文件。
/// access token 只放内存 —— 它 12 小时就过期，落盘没有意义，反而多一个泄漏面。
///
/// <c>ReadOnly</c> 是服务端发的「待审设备」标记：这台机器口令对，
/// 但班主任还没在网页上点通过，只能拉、不能写。它要落盘 —— 重启之后
/// 界面不能又从「只读」跳回「正常」，否则老师会以为改动同步上去了。
/// </summary>
public sealed record SyncCredentials(string BaseUrl, string ClassId, string Access, string Refresh, string ClassCode = "", bool ReadOnly = false)
{
    public bool HasAccess => !string.IsNullOrWhiteSpace(Access);

    /// <summary>
    /// 配对时用过的口令（班主任密码 / 班长口令 / 恢复码）。
    ///
    /// 存它是为了让「凭据被换掉」能自己接上：refresh token 一次一换，同一份数据目录
    /// 被两台机器用的时候，先续期的那台会把后一台的 token 顶掉。以前这种情况只能
    /// 弹一句「请重新配对」，老师得停下手里的活去输密码 —— 而配对时那个口令本来就在手上。
    ///
    /// 落盘方式和 refresh token 一样（DPAPI，按 Windows 账号加密），
    /// 换一个 Windows 用户就读不出来。
    /// </summary>
    public string Secret { get; init; } = "";

    /// <summary>配对时上报的设备类型（desk / board）。自动重配要原样再报一次，否则设备记录会对不上。</summary>
    public string Kind { get; init; } = "desk";

    /// <summary>能不能不问人自己重配。老版本存的凭据里没有 secret，只能走手动那条路。</summary>
    public bool CanSelfRepair => Secret.Length > 0 && ClassCode.Length > 0 && BaseUrl.Length > 0;
}

public sealed class SyncStore(string? path = null)
{
    private sealed class Dto
    {
        [JsonPropertyName("baseUrl")] public string? BaseUrl { get; set; }
        [JsonPropertyName("classId")] public string? ClassId { get; set; }
        [JsonPropertyName("refresh")] public string? Refresh { get; set; }
        [JsonPropertyName("classCode")] public string? ClassCode { get; set; }
        [JsonPropertyName("readOnly")] public bool ReadOnly { get; set; }
        [JsonPropertyName("savedAt")] public long SavedAt { get; set; }

        /// <summary>配对口令的密文。老文件里没有这一项 —— 那就退化成"手动手动重配"。</summary>
        [JsonPropertyName("pairSecret")] public string? PairSecret { get; set; }

        [JsonPropertyName("kind")] public string? Kind { get; set; }
    }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly string _path = path ?? Path.Combine(Database.RootDir, "sync.json");

    /// <summary>内存里的 access token，进程重启就没了，靠 refresh 换新的。</summary>
    private string _access = "";

    public SyncCredentials? Load()
    {
        try
        {
            if (!File.Exists(_path)) return null;
            var dto = JsonSerializer.Deserialize<Dto>(File.ReadAllText(_path), Json);
            if (dto?.BaseUrl is null || dto.ClassId is null || dto.Refresh is null) return null;

            var refresh = Unprotect(dto.Refresh);
            if (refresh is null) return null;

            return new SyncCredentials(dto.BaseUrl, dto.ClassId, _access, refresh, dto.ClassCode ?? "", dto.ReadOnly)
            {
                Secret = Unprotect(dto.PairSecret) ?? "",
                Kind = string.IsNullOrWhiteSpace(dto.Kind) ? "desk" : dto.Kind,
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void Save(SyncCredentials credentials)
    {
        _access = credentials.Access;
        var dto = new Dto
        {
            BaseUrl = credentials.BaseUrl,
            ClassId = credentials.ClassId,
            Refresh = Protect(credentials.Refresh),
            ClassCode = credentials.ClassCode,
            ReadOnly = credentials.ReadOnly,
            SavedAt = Clock.NowMs(),
            PairSecret = credentials.Secret.Length > 0 ? Protect(credentials.Secret) : null,
            Kind = credentials.Kind,
        };
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
        File.WriteAllText(_path, JsonSerializer.Serialize(dto, Json), Encoding.UTF8);
    }

    public void UpdateAccess(string access) => _access = access;

    public void Clear()
    {
        _access = "";
        try
        {
            if (File.Exists(_path)) File.Delete(_path);
        }
        catch (Exception)
        {
            // 删不掉就算了，下次 Save 会覆盖
        }
    }

    private static string? Protect(string plain)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var bytes = Dpapi.Protect(Encoding.UTF8.GetBytes(plain));
            return Convert.ToBase64String(bytes);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? Unprotect(string? cipher)
    {
        if (string.IsNullOrEmpty(cipher) || !OperatingSystem.IsWindows()) return null;
        try
        {
            return Encoding.UTF8.GetString(Dpapi.Unprotect(Convert.FromBase64String(cipher)));
        }
        catch (Exception)
        {
            // 换了 Windows 用户就解不开 —— 这正是我们要的效果，静默当作没配对
            return null;
        }
    }
}
