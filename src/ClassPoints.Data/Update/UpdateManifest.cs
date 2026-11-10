using System.Globalization;
using System.Text.Json;
using ClassPoints.Core;

namespace ClassPoints.Data.Update;

/// <summary>
/// 一个可下载的产物。名字是相对文件名（不含路径），大小和哈希都要有 ——
/// 没有哈希的产物一律不执行，不管它自称是什么版本。
/// </summary>
public sealed record UpdateAsset(string Name, long Size, string Sha256);

/// <summary>
/// 更新清单。发布时挂在公开版本库的固定 stable 标签下，客户端直连取回来，只信这一份。
///
/// 清单是唯一的"该不该更新"依据，所以这里的校验必须是**白名单式**的：
/// 渠道必须是 stable、标签必须和版本号对得上、安装包名必须由版本号推出来、
/// 哈希必须是 64 位十六进制。任何一条不满足就整份清单作废 ——
/// 宁可这次不更新，也不要把"理论上应该没事"的可执行文件请进 Program Files。
/// </summary>
public sealed record UpdateManifest(
    string Channel,
    SemanticVersion Version,
    string Tag,
    string Notes,
    DateTimeOffset? PublishedAt,
    SemanticVersion? MinVersion,
    UpdateAsset Setup,
    UpdateAsset? Portable)
{
    public const string StableChannel = "stable";

    /// <summary>单个安装包的大小上限。真实值 40～120 MB，1 GB 是防"服务器返回了一个 ISO"。</summary>
    public const long MaxAssetBytes = 1024L * 1024 * 1024;

    /// <summary>正式版安装包的固定命名。名字由版本号唯一决定，不让服务端自由发挥。</summary>
    public static string SetupFileNameFor(SemanticVersion version) => $"ClassPoints-Setup-{version}.exe";

    public static UpdateManifest Parse(string json)
    {
        if (!TryParse(json, out var manifest, out var error))
            throw new InvalidDataException(error);
        return manifest!;
    }

    public static bool TryParse(string? json, out UpdateManifest? manifest, out string error)
    {
        manifest = null;
        error = "";

        if (string.IsNullOrWhiteSpace(json))
        {
            error = "更新清单是空的";
            return false;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            error = "更新清单不是合法 JSON";
            return false;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "更新清单不是一个对象";
                return false;
            }

            var channel = Text(root, "channel");
            if (!string.Equals(channel, StableChannel, StringComparison.Ordinal))
            {
                error = $"渠道不是 {StableChannel}，不做自动更新";
                return false;
            }

            if (!SemanticVersion.TryParse(Text(root, "version"), out var version) || version == SemanticVersion.Zero)
            {
                error = "版本号不合法（只认 主.次.修 三段数字）";
                return false;
            }

            // 标签必须和版本号自洽。这条防的是"清单写 1.3.0、附件却在 v1.2.0 的标签下"，
            // 装上去以后版本号永远对不上，每次启动都提示更新。
            var tag = Text(root, "tag");
            if (!SemanticVersion.TryParseTag(tag, out var tagVersion) || tagVersion != version)
            {
                error = "标签和版本号对不上";
                return false;
            }

            if (!TryAsset(root, "setup", out var setup, out error)) return false;
            var expected = SetupFileNameFor(version);
            if (!string.Equals(setup!.Name, expected, StringComparison.Ordinal))
            {
                error = $"安装包名应为 {expected}";
                return false;
            }

            // 便携版是可选字段：没有就只提供安装版更新，不算错误。
            UpdateAsset? portable = null;
            if (root.TryGetProperty("portable", out var p) && p.ValueKind == JsonValueKind.Object)
            {
                if (!TryAsset(root, "portable", out portable, out error)) return false;
            }

            SemanticVersion? min = null;
            var minText = Text(root, "minVersion");
            if (!string.IsNullOrWhiteSpace(minText))
            {
                if (!SemanticVersion.TryParse(minText, out var parsed))
                {
                    error = "minVersion 不合法";
                    return false;
                }
                min = parsed;
            }

            DateTimeOffset? published = null;
            var at = Text(root, "publishedAt");
            if (!string.IsNullOrWhiteSpace(at) &&
                DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var when))
            {
                published = when;
            }

            manifest = new UpdateManifest(StableChannel, version, tag!, Text(root, "notes") ?? "", published, min, setup, portable);
            return true;
        }
    }

    private static bool TryAsset(JsonElement root, string property, out UpdateAsset? asset, out string error)
    {
        asset = null;
        error = "";

        if (!root.TryGetProperty(property, out var node) || node.ValueKind != JsonValueKind.Object)
        {
            error = $"缺少 {property}";
            return false;
        }

        var name = Text(node, "name");
        if (string.IsNullOrWhiteSpace(name) ||
            name.Contains('/') || name.Contains('\\') || name.Contains("..", StringComparison.Ordinal))
        {
            error = $"{property}.name 不是合法的文件名";
            return false;
        }

        // 大小必须真实存在：没有它就没法显示进度，也没法识别"下到一半断了"。
        if (!node.TryGetProperty("size", out var sizeNode) || !sizeNode.TryGetInt64(out var size) ||
            size <= 0 || size > MaxAssetBytes)
        {
            error = $"{property}.size 不合法";
            return false;
        }

        var sha = Text(node, "sha256");
        if (!IsSha256(sha))
        {
            error = $"{property}.sha256 不是 64 位十六进制";
            return false;
        }

        asset = new UpdateAsset(name!, size, sha!.ToLowerInvariant());
        return true;
    }

    private static bool IsSha256(string? text)
    {
        if (text is null || text.Length != 64) return false;
        foreach (var ch in text)
        {
            var hex = ch is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
            if (!hex) return false;
        }
        return true;
    }

    private static string? Text(JsonElement node, string property) =>
        node.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
