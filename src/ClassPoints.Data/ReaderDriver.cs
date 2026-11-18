using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security;

namespace ClassPoints.Data;

/// <summary>
/// CH340 驱动的"装没装、怎么装"。
///
/// 为什么要一个专门的服务：USB 读卡器用 CH340 做 USB 转串口，这颗芯片的驱动
/// 不随 Windows 分发（Win10 的驱动仓库里没有它）。驱动没装的机器上，设备枚举得到
/// 但没有 COM 口——老师看到的只是"没有发现串口设备"，跟读卡器坏了没法区分。
/// 驱动包随应用发布（drivers\CH341SER\，见该目录 README），这里负责检测和安装。
/// </summary>
public static class ReaderDriver
{
    /// <summary>INF 在发布目录里的相对位置。</summary>
    public static string InfPath =>
        Path.Combine(AppContext.BaseDirectory, "drivers", "CH341SER", "ch341ser.inf");

    /// <summary>驱动文件随包带了吗（没带就只给提示，不给按钮）。</summary>
    public static bool Bundled => File.Exists(InfPath);

    /// <summary>当前机器是不是"插着 CH340 但没有驱动"的状态。</summary>
    [SupportedOSPlatform("windows")]
    public static bool NeedsInstall => Pn532Serial.NeedsCh340Driver();

    /// <summary>
    /// 安装驱动。会弹一次 UAC（pnputil 需要管理员），用户点"否"算取消。
    ///
    /// 用 pnputil 而不是跑厂商的 CH341SER.EXE：那个安装器带 GUI、还会弹各种窗口，
    /// 老师在教室里点不明白；pnputil 是系统自带的、装完即生效，不需要重启。
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static async Task<bool> InstallAsync()
    {
        if (!Bundled) return false;

        // 只跑 add-driver，退出码就是安装结果。以前用 `&` 把 /scan-devices 串在后面，
        // 而 cmd 的退出码取**最后一条** —— add-driver 被拦、scan 成功，这里照样拿到 0，
        // 界面说"装好了"但 COM 口根本没出现，老师被引向最难排查的那种盲区。
        // 装完驱动 Windows 自己会重新枚举设备，单独的 scan 本来就是多余的。
        var args = $"/d /c pnputil /add-driver \"{InfPath}\" /install";
        var psi = new ProcessStartInfo("cmd.exe", args)
        {
            UseShellExecute = true,
            Verb = "runas",                     // 触发 UAC；用户拒绝时下面会收到 Win32Exception
            WindowStyle = ProcessWindowStyle.Hidden,
            CreateNoWindow = true,
        };

        try
        {
            using var proc = Process.Start(psi);
            if (proc is null) return false;

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            await proc.WaitForExitAsync(cts.Token);
            return proc.ExitCode == 0;
        }
        catch (Exception ex) when (ex is Win32Exception or SecurityException or OperationCanceledException)
        {
            // Win32Exception = 授权窗口点了"否"；OperationCanceledException = 超时
            return false;
        }
    }
}
