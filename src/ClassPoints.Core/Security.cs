using System.Security.Cryptography;
using System.Text;

namespace ClassPoints.Core;

/// <summary>PBKDF2-SHA256。密码与恢复码用同一套参数，库被翻开也拿不到明文。</summary>
public static class PasswordHasher
{
    private const int SaltSize = 16;
    private const int KeySize = 32;
    private const int Iterations = 120_000;

    public static string Hash(string secret)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var key = Rfc2898DeriveBytes.Pbkdf2(secret, salt, Iterations, HashAlgorithmName.SHA256, KeySize);
        return $"pbkdf2${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(key)}";
    }

    public static bool Verify(string secret, string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return false;
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2") return false;
        if (!int.TryParse(parts[1], out var iter) || iter <= 0) return false;
        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(secret, salt, iter, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

public static class Crypto
{
    /// <summary>固定盐，让「同一个 UID」在两台设备上算出同一个哈希。</summary>
    private const string CredentialPepper = "ClassPoints.Credential.v1";

    /// <summary>卡号只存 SHA-256(UID + 盐)，不存明文。</summary>
    public static string HashCredential(string raw) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(CredentialPepper + "|" + NormalizeUid(raw))));

    public static string NormalizeUid(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw.Trim().ToUpperInvariant())
            if (char.IsAsciiLetterOrDigit(ch)) sb.Append(ch);
        return sb.ToString();
    }

    public static string Sha256Hex(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    public static string Sha256Hex(Stream stream)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }

    public static string RandomHex(int bytes) => Convert.ToHexString(RandomNumberGenerator.GetBytes(bytes)).ToLowerInvariant();
}

/// <summary>Crockford Base32（去掉 I L O U），16 位分 4 组：K7QM-3XP9-8RTD-W2HN。</summary>
public static class RecoveryCode
{
    public const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public static string Generate()
    {
        var chars = new char[16];
        for (var i = 0; i < chars.Length; i++)
            chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        var raw = new string(chars);
        return $"{raw[..4]}-{raw[4..8]}-{raw[8..12]}-{raw[12..]}";
    }

    /// <summary>抄错一位就打不开，所以先把 O/I/L 拉回 0/1、去掉分隔符再校验。</summary>
    public static string Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "";
        var sb = new StringBuilder(16);
        foreach (var ch in input.Trim().ToUpperInvariant())
        {
            var c = ch switch
            {
                'O' => '0',
                'I' => '1',
                'L' => '1',
                _ => ch,
            };
            if (Alphabet.IndexOf(c) >= 0 && sb.Length < 16) sb.Append(c);
        }
        return sb.ToString();
    }

    public static string Format(string normalized)
    {
        if (normalized.Length != 16) return normalized;
        return $"{normalized[..4]}-{normalized[4..8]}-{normalized[8..12]}-{normalized[12..]}";
    }

    public static bool LooksValid(string? input) => Normalize(input).Length == 16;
}

/// <summary>
/// 班级码。以前是「C3-2026」这种带年份的编号：年份一露就等于告诉别人这个班哪年建的，
/// 而且同年建的班很容易撞号。现在纯随机，去掉 I/O/0/1 这些念出来会听错的字符。
/// </summary>
public static class ClassCodes
{
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public static string New()
    {
        Span<char> chars = stackalloc char[8];
        for (var i = 0; i < chars.Length; i++)
            chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        return $"{new string(chars[..4])}-{new string(chars[4..])}";
    }

    /// <summary>
    /// 老库里可能存着带年份的旧码。只要形如「X?-20??」就换成新的 ——
    /// 换之前没人知道它，换之后也不会让谁连不上（没连云端时它只是个编号）。
    /// </summary>
    public static bool LooksLikeOldScheme(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return false;
        var text = code.Trim();
        return text.Length >= 4 && text.Contains("20", StringComparison.Ordinal) &&
               (text.Contains('-') || text.Length <= 8);
    }
}

/// <summary>UUIDv7：带时间前缀，索引友好；同时充当同步的幂等键。</summary>
public static class Ids
{
    public static string NewUuid7()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);

        long ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        bytes[0] = (byte)(ms >> 40);
        bytes[1] = (byte)(ms >> 32);
        bytes[2] = (byte)(ms >> 24);
        bytes[3] = (byte)(ms >> 16);
        bytes[4] = (byte)(ms >> 8);
        bytes[5] = (byte)ms;
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x70); // version 7
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80); // variant

        var hex = Convert.ToHexString(bytes).ToLowerInvariant();
        return $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..]}";
    }

    /// <summary>批次号：不带连字符，便于按前缀排序。</summary>
    public static string NewBatchId() => Guid.NewGuid().ToString("N");
}
