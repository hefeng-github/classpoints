using System.Globalization;

namespace ClassPoints.Core;

/// <summary>
/// 三段式正式版本号：主.次.修。
///
/// 为什么不用完整 SemVer（带 -beta.1 / +build 那套）：这个软件的升级路径只有一条
/// ——老师点「更新」，装完重启。预发布版会引入"同主版本下谁比谁新"的判断，
/// 而版本号一旦在教室里判错，后果是整机停在旧版或反复更新。所以这里只认
/// 纯数字三段，带任何后缀的一律当作非正式版，自动更新根本不看它。
///
/// 比较规则就是元组比较：先主、再次、最后修。刻意不支持 1.2 这种两段式
/// 输入 —— 发布会由脚本写成三段，人工能少写一个数就少一次事故。
/// </summary>
public readonly record struct SemanticVersion(int Major, int Minor, int Patch)
    : IComparable<SemanticVersion>, IComparable
{
    /// <summary>解析失败时的兜底版本。永远小于任何真实版本，不会顶掉真更新。</summary>
    public static readonly SemanticVersion Zero = new(0, 0, 0);

    public static SemanticVersion Parse(string text)
    {
        if (!TryParse(text, out var version))
            throw new FormatException($"不是合法版本号：{text}");
        return version;
    }

    /// <summary>
    /// 只接受 "1.2.3" 这种形态。前导零、正负号、空白、四位段全部拒绝：
    /// 这些都能让两个字符串在字典序上排对、在数值序上排错。
    /// </summary>
    public static bool TryParse(string? text, out SemanticVersion version)
    {
        version = Zero;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var parts = text.Split('.');
        if (parts.Length != 3) return false;

        var numbers = new int[3];
        for (var i = 0; i < 3; i++)
        {
            var part = parts[i];
            if (part.Length == 0 || part.Length > 6) return false;
            foreach (var ch in part)
                if (ch is < '0' or > '9') return false;
            if (part.Length > 1 && part[0] == '0') return false;
            numbers[i] = int.Parse(part, CultureInfo.InvariantCulture);
        }

        version = new SemanticVersion(numbers[0], numbers[1], numbers[2]);
        return true;
    }

    /// <summary>标签形态：v1.2.3。CNB 的 Release 标签就是这种写法。</summary>
    public static bool TryParseTag(string? tag, out SemanticVersion version)
    {
        version = Zero;
        if (string.IsNullOrWhiteSpace(tag)) return false;
        var text = tag.Trim();
        if (text.Length < 2 || (text[0] != 'v' && text[0] != 'V')) return false;
        return TryParse(text[1..], out version);
    }

    public string ToTag() => "v" + ToString();

    public int CompareTo(SemanticVersion other)
    {
        var major = Major.CompareTo(other.Major);
        if (major != 0) return major;
        var minor = Minor.CompareTo(other.Minor);
        return minor != 0 ? minor : Patch.CompareTo(other.Patch);
    }

    int IComparable.CompareTo(object? obj) =>
        obj is SemanticVersion other ? CompareTo(other) : throw new ArgumentException("只能和版本号比较", nameof(obj));

    public static bool operator >(SemanticVersion a, SemanticVersion b) => a.CompareTo(b) > 0;
    public static bool operator <(SemanticVersion a, SemanticVersion b) => a.CompareTo(b) < 0;
    public static bool operator >=(SemanticVersion a, SemanticVersion b) => a.CompareTo(b) >= 0;
    public static bool operator <=(SemanticVersion a, SemanticVersion b) => a.CompareTo(b) <= 0;

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Major}.{Minor}.{Patch}");
}
