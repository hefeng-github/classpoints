using ClassPoints.Data.Sync;

namespace ClassPoints.Tests;

/// <summary>
/// 设备特征码。
///
/// 这里能测的只有两件事：形状对不对、同一台机器算出来是不是同一个值。
/// 「换一台机器会不会变」测不了 —— 那正是它的定义，测试机只有一台。
/// 所以重点放在边界上：空值、带分隔符的输入、大小写，这些是老师手工
/// 从网页抄回来时会碰到的形状。
/// </summary>
internal static class FingerprintTests
{
    public static void Run(Action<string, bool, string> check)
    {
        var raw = DeviceFingerprint.Raw;
        check("特征码是 20 位十六进制", raw.Length == 20 && raw.All(Uri.IsHexDigit), raw);
        check("特征码是小写", raw == raw.ToLowerInvariant(), raw);
        check("同一台机器两次算出来一致", DeviceFingerprint.Raw == raw, raw);
        check("展示态是 4 位一组", DeviceFingerprint.Display.Split('-').Length == 5, DeviceFingerprint.Display);
        check("展示态全大写", DeviceFingerprint.Display == DeviceFingerprint.Display.ToUpperInvariant(), DeviceFingerprint.Display);

        check("格式化：小写转大写分组",
            DeviceFingerprint.Format("a1b2c3d4e5f60718293a") == "A1B2-C3D4-E5F6-0718-293A",
            DeviceFingerprint.Format("a1b2c3d4e5f60718293a"));
        check("格式化：已经带分隔符的输入是幂等的",
            DeviceFingerprint.Format("A1B2-C3D4-E5F6-0718-293A") == "A1B2-C3D4-E5F6-0718-293A",
            DeviceFingerprint.Format("A1B2-C3D4-E5F6-0718-293A"));
        check("格式化：空格也能吃进去",
            DeviceFingerprint.Format(" a1b2 c3d4 e5f6 0718 293a ") == "A1B2-C3D4-E5F6-0718-293A",
            DeviceFingerprint.Format(" a1b2 c3d4 e5f6 0718 293a "));
        check("格式化：空值给占位符", DeviceFingerprint.Format(null) == "—" && DeviceFingerprint.Format("") == "—", "");
        check("格式化：非十六进制字符被忽略而不是抛异常",
            DeviceFingerprint.Format("zzA1B2") == "A1B2", DeviceFingerprint.Format("zzA1B2"));
        check("格式化：全非十六进制也给占位符", DeviceFingerprint.Format("zzz") == "—", DeviceFingerprint.Format("zzz"));
    }
}
