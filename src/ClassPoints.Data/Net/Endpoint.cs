namespace ClassPoints.Data.Net;

/// <summary>
/// 出网地址的规范化与传输层校验。同步和更新各走各的端点，但规矩必须一样：
/// 只认 https，只有本机回环允许 http。
///
/// 为什么把这件事收成一个地方：客户端里但凡多一个"图省事直接拼 URL"的入口，
/// 就会有一个人在学校 AP 上被抓到明文包 —— 而现在这个包里是全班名单、
/// 每一笔加减分，以及一个能直接换出令牌的 refresh token。
/// </summary>
public static class Endpoint
{
    /// <summary>
    /// 用户填 "classpoints.xxx.workers.dev" 是最自然的写法，不该因为这个报错；
    /// 但补协议这一步只能补 https，不能补 http。
    /// </summary>
    public static string Normalize(string? raw, string label)
    {
        var text = (raw ?? string.Empty).Trim();
        if (text.Length == 0) throw new ArgumentException($"{label}不能为空");
        if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;
        text = text.TrimEnd('/');

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
            throw new ArgumentException($"{label}不是合法的网址");

        // 回环例外：开发时跑 wrangler dev，流量不出网卡。
        var loopback = uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase);
        if (uri.Scheme == Uri.UriSchemeHttps) return text;
        if (uri.Scheme == Uri.UriSchemeHttp && loopback) return text;

        throw new ArgumentException($"{label}必须用 https://（只有本机调试才允许 http）");
    }
}
