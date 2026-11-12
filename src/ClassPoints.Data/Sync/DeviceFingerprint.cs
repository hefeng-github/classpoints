using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace ClassPoints.Data.Sync;

/// <summary>
/// 设备特征码：这台机器在白名单里的名字。
///
/// 取 MachineGuid（安装 Windows 时生成，重装系统才变）加机器名做 SHA-256，
/// 只取前 10 字节 —— 输出的 20 位十六进制刚好能抄在一张纸条上，而完整摘要
/// 反倒没人愿意抄。哈希里带了固定前缀，免得同一个值将来被别的用途复用。
///
/// 它**不是**安全边界：知道算法的人可以伪造一个。它的作用是让"谁进来了"
/// 这件事可以被班主任看见并且需要点头 —— 口令仍然是第一道门。
///
/// 对外只发哈希，从不发 MachineGuid 本身：那一串能查到机器的出厂信息。
/// </summary>
public static class DeviceFingerprint
{
    private const string Prefix = "classpoints-device-v1";

    private static readonly Lazy<string> Cached = new(Compute, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>小写 20 位十六进制，协议里传这个。</summary>
    public static string Raw => Cached.Value;

    /// <summary>4 位一段、全大写，给人看和抄。</summary>
    public static string Display => Format(Raw);

    /// <summary>把协议里的值转成好念的形状：a1b2c3… → A1B2-C3D4-…</summary>
    public static string Format(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "—";
        var clean = new string(raw.Where(c => Uri.IsHexDigit(c)).ToArray()).ToUpperInvariant();
        if (clean.Length == 0) return "—";
        var parts = new List<string>((clean.Length + 3) / 4);
        for (var i = 0; i < clean.Length; i += 4)
            parts.Add(clean.Substring(i, Math.Min(4, clean.Length - i)));
        return string.Join('-', parts);
    }

    private static string Compute()
    {
        var machineGuid = ReadMachineGuid();
        var material = $"{Prefix}|{machineGuid}|{Environment.MachineName.ToLowerInvariant()}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material));
        return Convert.ToHexString(hash.AsSpan(0, 10)).ToLowerInvariant();
    }

    /// <summary>
    /// 读 MachineGuid。读不到（极少数精简系统、被策略锁死的注册表）就退回机器名：
    /// 稳定性差一些，但总比"整台机器无法配对"好 —— 那种失败用户根本无从下手。
    /// </summary>
    private static string ReadMachineGuid()
    {
        // Data 项目是跨平台 TFM（net10.0），注册表 API 只在 Windows 有。
        // 这个分支实际永远为 false，写出来是为了让平台分析器认账 ——
        // 否则 CA1416 会给三条警告，而本项目要求零警告。
        if (!OperatingSystem.IsWindows()) return "no-machine-guid";

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            if (key?.GetValue("MachineGuid") is string s && s.Trim().Length > 0) return s.Trim();
        }
        catch (Exception)
        {
            // 注册表读不了不是错误，是环境差异；下面统一走回退路径
        }
        return "no-machine-guid";
    }
}
