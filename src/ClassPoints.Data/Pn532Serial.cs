using ClassPoints.Core;
using System.IO.Ports;
using System.Management;
using System.Runtime.Versioning;
using System.Text;

namespace ClassPoints.Data;

public sealed record CardRead(string Uid, DateTime At)
{
    public string ShortUid => Uid.Length <= 8 ? Uid : Uid[^8..];
}

public sealed record CardPortInfo(string PortName, string Description, bool LikelyPn532)
{
    /// <summary>
    /// 下拉里那一行字。
    ///
    /// 不能见到 CH340 就写"像是 PN532"：那是**转接板**的名字，读卡器在它后面，
    /// 说成同一个东西，老师会以为程序已经认出读卡器了，于是不再去看接线。
    /// </summary>
    public string Display
    {
        get
        {
            var hint = Description.Contains("PN532", StringComparison.OrdinalIgnoreCase) ? "（PN532）"
                : LikelyPn532 ? "（USB 转串口）"
                : "";
            return $"{PortName} · {Description}{hint}";
        }
    }
}

public enum ReaderState
{
    Stopped,
    Opening,
    Running,
    NoDevice,
    Error,
}

/// <summary>
/// 读卡器怎么说话。两种设备在淘宝上是两个品类，外观却几乎一样，接错了就是"串口通了、一句不回"。
/// </summary>
public enum ReaderProtocol
{
    /// <summary>PN532 模块：主机发命令，它回二进制帧。需要握手。</summary>
    Pn532,

    /// <summary>
    /// 自动上报型（CH340 一体式读卡器最常见）：**不接受命令**，刷卡时自己把卡号
    /// 当文本吐出来（多为 10 位十进制、9600 波特，也有 8 位十六进制）。
    ///
    /// 这一类设备用 PN532 的帧去问，永远不会有回应 —— 不是坏了，是根本不是同一套协议。
    /// </summary>
    AutoUid,
}

public sealed record ReaderStatus(
    ReaderState State,
    string Port,
    string Message,
    long LastCardAt,
    int CardsRead,
    bool PortOpen = false,
    int Baud = 0)
{
    public bool IsRunning => State == ReaderState.Running;

    /// <summary>
    /// 三个灯：设备 / 帧 / 卡。
    ///
    /// 「设备」看的是串口有没有打开，不是握手有没有通过 —— 这两件事必须分开显示。
    /// 以前握手失败时三个灯全灭，看着像"没插上"，实际串口是通的、问题在接线或模块拨码，
    /// 老师照着"没插上"去查驱动，永远查不出来。
    /// </summary>
    public (bool Device, bool Frame, bool Card) Lamps =>
        (PortOpen,
         State == ReaderState.Running,
         LastCardAt > 0 && (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - LastCardAt) < 60_000);
}

/// <summary>
/// PN532 串口读卡。
///
/// 为什么要自己拼帧：PN532 的 UART 接口不是键盘模拟，它只回二进制帧。
/// 键盘模拟那条路要求界面上始终有个隐藏输入框抢焦点，老师一点别处就失效 —— 一体机上不能这么干。
///
/// 四个坑（方案 2.4 + v1.3 实测）：
/// 1) 波特率不一定是 115200：出厂是 115200，但被 Arduino 例程改过的模块常停在 9600，
///    所以要逐个试（见 <see cref="BaudCandidates"/>），不能只赌一个；
/// 2) 每帧都要校验 LCS / DCS，丢一个字节就会把「有卡」读成「没卡」；
/// 3) 同一张卡在读卡器上放着会连续命中，必须按 UID 做时间窗去重，否则一次刷卡加 20 次分；
/// 4) **不要拉 DTR / RTS**：CH340 这两个脚常被引到模块的复位脚上，
///    一路拉高等于把 PN532 按在复位里 —— 表现为"一个字节都收不到"，但串口是好的。
/// </summary>
public sealed class Pn532Serial : IDisposable
{
    private const byte Preamble = 0x00;
    private const byte StartCode = 0xFF;
    private const byte Postamble = 0x00;
    private const byte HostToPn = 0xD4;
    private const byte PnToHost = 0xD5;

    private readonly object _gate = new();
    private SerialPort? _port;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private readonly Dictionary<string, long> _lastSeen = new(StringComparer.OrdinalIgnoreCase);
    private long _lastCardAt;
    private int _cardsRead;
    private string _statusMessage = "未启动";
    private string _portName = "";
    private ReaderState _state = ReaderState.Stopped;
    private bool _portOpen;
    private int _baud;

    /// <summary>读到一张卡（已去重）。回调在后台线程，界面要自己 DispatcherQueue 切回来。</summary>
    public event Action<CardRead>? CardDetected;

    public event Action<ReaderStatus>? StatusChanged;

    public ReaderStatus Status
    {
        get
        {
            lock (_gate)
                return new ReaderStatus(_state, _portName, _statusMessage, _lastCardAt, _cardsRead, _portOpen, _baud);
        }
    }

    /// <summary>去重窗口：同一张卡 1.5 秒内只认一次。</summary>
    public int DebounceMs { get; set; } = 1500;

    /// <summary>卡片离开后要静默这么久，才允许同一个 UID 再报一次（防止偶发漏读导致重复计分）。</summary>
    private const int AbsentGraceMs = 700;

    /// <summary>当前"贴着"感应区的卡。放卡不动时寻卡每轮都命中，靠它保证只报一次。</summary>
    private readonly HashSet<string> _present = new(StringComparer.OrdinalIgnoreCase);

    private long _lastPresenceAt;


    public int PollIntervalMs { get; set; } = 150;

    // WMI 查询只在 Windows 上有；Data 工程仍按 net10.0 编，好在 Linux 上跑 CI 的编译检查
    [SupportedOSPlatform("windows")]
    public static List<CardPortInfo> ListPorts()
    {
        var list = new List<CardPortInfo>();
        var descriptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, DeviceID FROM Win32_PnPEntity WHERE Name LIKE '%(COM%'");
            foreach (var obj in searcher.Get())
            {
                var name = obj["Name"]?.ToString() ?? "";
                var start = name.LastIndexOf("(COM", StringComparison.Ordinal);
                if (start < 0) continue;
                var end = name.IndexOf(')', start);
                if (end < 0) continue;
                var port = name.Substring(start + 1, end - start - 1);
                descriptions[port] = name[..start].Trim();
            }
        }
        catch (Exception)
        {
            // 拿不到 WMI 就只列串口名，不影响使用
        }

        foreach (var name in SerialPort.GetPortNames().OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            var desc = descriptions.TryGetValue(name, out var d) ? d : "串口设备";
            var likely = desc.Contains("PN532", StringComparison.OrdinalIgnoreCase)
                         || desc.Contains("CH340", StringComparison.OrdinalIgnoreCase)
                         || desc.Contains("CP210", StringComparison.OrdinalIgnoreCase)
                         || desc.Contains("FT232", StringComparison.OrdinalIgnoreCase)
                         || desc.Contains("USB-SERIAL", StringComparison.OrdinalIgnoreCase);
            list.Add(new CardPortInfo(name, desc, likely));
        }
        return list;
    }

    [SupportedOSPlatform("windows")]
    public static string? GuessPort()
    {
        var ports = ListPorts();
        return ports.FirstOrDefault(p => p.LikelyPn532)?.PortName ?? ports.FirstOrDefault()?.PortName;
    }

    [SupportedOSPlatform("windows")]
    public void Start(string? portName = null, int preferredBaud = 0, ReaderProtocol protocol = ReaderProtocol.Pn532)
    {
        Stop();
        _preferredBaud = preferredBaud;
        _protocol = protocol;
        _lockedBaud = protocol == ReaderProtocol.AutoUid ? preferredBaud : 0;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        // 自动上报型不需要握手，走另一条循环：那条循环的唯一任务是"听"，
        // 而 PN532 那条是"问—答"。混在一起写会让两边都难读。
        _loop = Task.Run(() => protocol == ReaderProtocol.AutoUid
            ? RunAutoLoopAsync(portName, token)
            : RunLoopAsync(portName, token), token);
    }

    private ReaderProtocol _protocol = ReaderProtocol.Pn532;

    /// <summary>自动上报型一旦收到过卡号，就把波特率钉死，不再轮换 —— 轮换等于每 8 秒聋一次。</summary>
    private int _lockedBaud;

    /// <summary>上次握手成功的波特率；0 = 还没成功过，从 115200 开始试。</summary>
    private int _preferredBaud;

    public void Stop()
    {
        try { _cts?.Cancel(); } catch (Exception) { }
        try { _loop?.Wait(600); } catch (Exception) { }
        ClosePort();
        _portOpen = false;
        _baud = 0;
        _cts?.Dispose();
        _cts = null;
        _loop = null;
        SetStatus(ReaderState.Stopped, "已停止", _portName);
    }


    /// <summary>没有硬件时的调试通道：直接喂一个 UID，走同一条认人链路。</summary>
    public void SimulateSwipe(string uid)
    {
        var normalized = Crypto.NormalizeUid(uid);
        if (normalized.Length == 0) return;
        Emit(new CardRead(normalized, DateTime.Now));
    }

    [SupportedOSPlatform("windows")]
    private async Task RunLoopAsync(string? preferredPort, CancellationToken token)
    {
        var failures = 0;
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (_port is null || !_port.IsOpen)
                {
                    var port = preferredPort ?? GuessPort();
                    if (port is null)
                    {
                        SetStatus(ReaderState.NoDevice, "没找到串口设备，插上读卡器后会自动重试", "");
                        await Task.Delay(2500, token);
                        continue;
                    }
                    SetStatus(ReaderState.Opening, $"正在打开 {port}", port);
                    OpenPort(port);
                    var (ok, baud, bytes) = await HandshakeAsync(token);
                    if (!ok)
                    {
                        failures++;
                        // 关端口前先把"串口是通的"这件事留下来，否则界面三个灯全灭
                        SetStatus(ReaderState.Error, Diagnose(bytes), port);
                        ClosePort();
                        await Task.Delay(System.Math.Min(8000, 1500 * failures), token);
                        continue;
                    }
                    failures = 0;
                    _baud = baud;
                    _preferredBaud = baud;      // 下一轮（拔插、换口）直接用这个
                    SetStatus(ReaderState.Running, $"读卡中（{baud}）", port);
                }

                var uid = await PollOnceAsync(token);
                if (uid is null)
                {
                    MarkAbsentIfGone();
                }
                else if (MarkPresent(uid))
                {
                    // 只有"这张卡刚出现"才报一次。
                    // 卡片一直贴在感应区时，寻卡帧每 150ms 就会命中一次 ——
                    // 直接 Emit 等于每 1.5 秒给同一个学生加一次分。
                    Emit(new CardRead(uid, DateTime.Now));
                }

                await Task.Delay(PollIntervalMs, token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                failures++;
                SetStatus(ReaderState.Error, "读卡异常：" + ex.Message, _portName);
                ClosePort();
                try { await Task.Delay(System.Math.Min(8000, 1500 * failures), token); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    /// <summary>
    /// 自动上报型的读卡循环：**只听，不问**。
    ///
    /// 这类读卡器（一块板子上 CH340 + MCU + 射频前端，USB-C 直插）刷卡时把卡号当文本吐出来，
    /// 平时完全静默。所以"通没通"的唯一依据是**有没有收到过字节**，不能像 PN532 那样靠握手：
    /// 握手命令对它只是一串无意义乱码，永远没有回应。
    ///
    /// 波特率不知道就轮着试（每档 8 秒），一旦认出卡号就钉死。
    /// </summary>
    [SupportedOSPlatform("windows")]
    private async Task RunAutoLoopAsync(string? preferredPort, CancellationToken token)
    {
        var failures = 0;
        var bauds = BaudCandidates(_lockedBaud);
        var baudIndex = 0;
        var silentSince = DateTime.UtcNow;
        var startedAt = DateTime.UtcNow;
        var bytesEver = false;

        while (!token.IsCancellationRequested)
        {
            try
            {
                if (_port is null || !_port.IsOpen)
                {
                    var port = preferredPort ?? GuessPort();
                    if (port is null)
                    {
                        SetStatus(ReaderState.NoDevice, "没找到串口设备，插上读卡器后会自动重试", "");
                        await Task.Delay(2500, token);
                        continue;
                    }

                    _baud = _lockedBaud > 0 ? _lockedBaud : bauds[baudIndex % bauds.Count];
                    SetStatus(ReaderState.Opening, $"正在打开 {port}（{_baud}）", port);
                    OpenPort(port);
                    _portOpen = true;
                    silentSince = DateTime.UtcNow;
                    SetStatus(ReaderState.Running, $"读卡中（自动上报 · {_baud}）", port);
                }

                var line = await ReadTextAsync(token);
                if (line is { Length: > 0 })
                {
                    bytesEver = true;
                    silentSince = DateTime.UtcNow;
                    var uid = ParseAutoUid(line);
                    if (uid.Length > 0)
                    {
                        _lockedBaud = _baud;       // 收到卡号 ⇒ 这一档就是对的，别再轮换
                        _preferredBaud = _baud;
                        Emit(new CardRead(uid, DateTime.Now));
                    }
                    else
                    {
                        // 收到字节但认不出卡号：如实说出来。老师至少能知道"这根线是通的"，
                        // 而不是对着"一个字节都没回"去查接线。
                        SetStatus(ReaderState.Running,
                            $"收到字节但没认出卡号：{Shorten(line, 24)}", _portName);
                    }
                    failures = 0;
                }
                else if (_lockedBaud == 0 && (DateTime.UtcNow - silentSince).TotalSeconds > 8)
                {
                    // 一整档都没听到任何东西：换下一个波特率再听。
                    // 这里故意不报错 —— 自动上报型在没人刷卡时本来就安静，报错只会把人吓去查线。
                    baudIndex++;
                    ClosePort();
                    silentSince = DateTime.UtcNow;
                }
                else if (!bytesEver && (DateTime.UtcNow - startedAt).TotalSeconds > 40)
                {
                    // 几档波特率都轮过一遍、40 秒一个字节都没有。
                    // 必须说出来：否则界面一直写"读卡中"，看着像在正常工作，其实什么也没收到。
                    SetStatus(ReaderState.Running,
                        "还没收到任何字节：先确认「读卡器类型」选对了，再换一个 USB 口/换根线试试", _portName);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                failures++;
                SetStatus(ReaderState.Error, "读卡异常：" + ex.Message, _portName);
                ClosePort();
                try { await Task.Delay(System.Math.Min(8000, 1500 * failures), token); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    /// <summary>读一段文本：到 CR/LF，或两个字节之间静默 250ms 为止。一个字节都没有时返回 null。</summary>
    private async Task<string?> ReadTextAsync(CancellationToken token)
    {
        var port = _port;
        if (port is null || !port.IsOpen) return null;

        var sb = new StringBuilder(32);
        var deadline = DateTime.UtcNow.AddMilliseconds(400);

        while (DateTime.UtcNow < deadline)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var one = port.ReadByte();
                if (one < 0) continue;

                var ch = (char)one;
                if (ch is '\r' or '\n')
                {
                    if (sb.Length > 0) return sb.ToString();
                    continue;
                }

                if (ch >= 32 && ch < 127) sb.Append(ch);

                // 有字节进来就往后延：一帧常被拆成几段到达
                deadline = DateTime.UtcNow.AddMilliseconds(250);
            }
            catch (TimeoutException)
            {
                // 这一段没数据，继续等到 deadline
            }
            catch (InvalidOperationException)
            {
                return null;    // 端口刚被关掉（换端口/换波特率）
            }
            await Task.Yield();
        }

        return sb.Length > 0 ? sb.ToString() : null;
    }

    private static readonly char[] AutoSeparators =
        [' ', '\t', ',', ';', ':', '-', '_', '.', '/', '\\', '|', '#', '=', '\r', '\n'];

    /// <summary>
    /// 从一行文本里捞卡号。
    ///
    /// 自动上报型读卡器的输出五花八门，见过这些写法：
    /// `1234567890` / `04A1B2C3` / `04 A1 B2 C3` / `UID: 04A1B2C3D4E5F6` / `CardID=1234567890\r\n`。
    /// 与其猜某一家厂商的格式，不如定一条规则：**取这一行里最长的、像卡号的那一段**。
    /// 长度 8–20 之间，纯十六进制字符（十进制数字天然是十六进制字符的子集，所以两种都能收）。
    /// 这样换一种读卡器也不用改代码。
    /// </summary>
    public static string ParseAutoUid(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";

        var found = new List<string>();
        var pairs = new StringBuilder();

        void FlushPairs()
        {
            if (pairs.Length is >= 8 and <= 20) found.Add(pairs.ToString());
            pairs.Clear();
        }

        foreach (var raw in text.Split(AutoSeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            if (IsHexToken(raw))
            {
                // `04 A1 B2 C3` 这种按字节分开写的，把相邻的两位组拼回去
                if (raw.Length <= 2) { pairs.Append(raw); continue; }

                FlushPairs();
                if (raw.Length is >= 8 and <= 20) found.Add(raw);
                continue;
            }

            FlushPairs();

            // 兜底：前缀字母粘在卡号上（`UID04A1B2C3`）—— 取最长的十六进制后缀
            var suffix = HexSuffix(raw);
            if (suffix.Length >= 8) found.Add(suffix);
        }
        FlushPairs();

        if (found.Count == 0) return "";
        var best = found.OrderByDescending(s => s.Length).First();
        return Crypto.NormalizeUid(best);
    }

    private static bool IsHexToken(string token)
    {
        if (token.Length == 0) return false;
        foreach (var ch in token) if (!char.IsAsciiHexDigit(ch)) return false;
        return true;
    }

    private static string HexSuffix(string token)
    {
        var start = token.Length;
        while (start > 0 && char.IsAsciiHexDigit(token[start - 1])) start--;
        var suffix = token[start..];

        // 粘在前缀里的最后一个字母也可能是十六进制字符（`UID` 的 D），
        // 长度是奇数就把头一个削掉，卡号不会是奇数个十六进制字符。
        if (suffix.Length % 2 == 1) suffix = suffix[1..];
        return suffix;
    }

    private static string Shorten(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";

    private void OpenPort(string portName)
    {
        // 自动上报型由循环自己决定这轮用哪档波特率（_baud 已经设好）；
        // PN532 走"记住上次成功的"逻辑。
        var baud = _protocol == ReaderProtocol.AutoUid && _baud > 0
            ? _baud
            : _preferredBaud > 0 ? _preferredBaud : DefaultBaud;

        var port = new SerialPort(portName, baud, Parity.None, 8, StopBits.One)
        {
            ReadTimeout = 400,
            WriteTimeout = 400,

            // DTR / RTS 一律不拉。
            //
            // 它们和 PN532 一点关系都没有，但在 CH340 这类 USB-TTL 板上，
            // 这两个信号常被引到扩展排针、甚至就是模块的复位脚。
            // 之前拉高 = 把模块按在复位里，表现是"串口打开了、一个字节都收不到"，
            // 而界面只写"检查波特率与接线"，查一天也查不出来。
            DtrEnable = false,
            RtsEnable = false,
        };
        port.Open();
        try { port.DiscardInBuffer(); } catch (Exception) { }
        _port = port;
        _portName = portName;
        _portOpen = true;
    }

    private void ClosePort()
    {
        try { _port?.Close(); } catch (Exception) { }
        try { _port?.Dispose(); } catch (Exception) { }
        _port = null;

        // 这里刻意不清 _portOpen：它表示"这次连接里串口确实打开过"，
        // 握手失败的场景要靠它把「设备」灯点亮 —— 亮的才是对的，
        // 灭着会让老师以为线没插好，去折腾驱动。
    }

    /// <summary>
    /// 开机握手：唤醒序列 + SAMConfiguration，**必须一次写出去**。
    ///
    /// 这 24 个字节是从官方 pcr532.exe 里读出来的（它连上后发的第一包就是这一串）。
    /// 和"先发唤醒、等一会儿、再发命令"的区别很关键：分成两次写、中间空 120ms，
    /// 这类国产模块经常把后面的命令整条忽略 —— 表现就是"串口通了，一个字节都不回"。
    ///
    /// 最后的 16、17 是两个 0x00，正好同时充当下面那一帧的帧头，
    /// 所以整串是 `55 55` + 14×`00` + `FF 03 FD D4 14 01 17 00`，一共 24 字节。
    /// </summary>
    public static byte[] BootSequence()
    {
        var seq = new byte[24];
        seq[0] = 0x55;
        seq[1] = 0x55;
        // 2..15 全 0：唤醒填充，末尾两个兼作帧头
        seq[16] = 0xFF;   // 起始码
        seq[17] = 0x03;   // LEN = TFI + CMD + 1 字节参数
        seq[18] = 0xFD;   // LCS = ~LEN + 1
        seq[19] = 0xD4;   // 主机 → PN532
        seq[20] = 0x14;   // SAMConfiguration
        seq[21] = 0x01;   // 正常模式
        seq[22] = 0x17;   // DCS
        return seq;
    }

    /// <summary>
    /// 握手的波特率候选。第一个是上次成功过的（拔插/重启后直接用），否则从 115200 开始。
    ///
    /// 为什么要试多个：PN532 出厂是 115200，但被 Arduino 例程改过的模块常停在 9600，
    /// 而模块上没有任何标识说明它现在是哪个。程序只赌 115200 的话，
    /// 遇到这种板子就是"永远连不上"，而用户完全无从下手。
    /// </summary>
    public static IReadOnlyList<int> BaudCandidates(int preferred)
    {
        var all = new List<int> { 115200, 9600, 38400, 57600, 19200, 230400 };
        if (preferred > 0 && all.Contains(preferred))
        {
            all.Remove(preferred);
            all.Insert(0, preferred);
        }
        return all;
    }

    public const int DefaultBaud = 115200;

    /// <summary>
    /// HSU 唤醒 / 同步序列：两个 0x55 加一串 0x00。
    ///
    /// PN532 的 UART 靠 0x55 的方波让主机和它对齐波特率；上电、被复位、
    /// 或者刚从 I2C 档切过来时，不发这一串它可能一直不吭声。
    /// 发完要给它一点时间（手册里是几毫秒，这里给 120ms 更保险）。
    /// </summary>
    public static byte[] WakeSequence()
    {
        var seq = new byte[16];
        seq[0] = 0x55;
        seq[1] = 0x55;
        return seq;
    }

    /// <summary>握手全失败时，按"到底收到过几个字节"分辨原因 —— 这两种情况的下一步动作完全不同。</summary>
    public static string Diagnose(int bytesSeen)
    {
        if (bytesSeen == 0)
            return "串口通了，但读卡器一个字节都没回：检查模块上的拨码是不是 UART(HSU) 档、" +
                   "TX/RX 有没有交叉（转接板的 TX 接模块 RX）、是否共地";
        return $"收到 {bytesSeen} 个字节但都不是有效帧：波特率大概率对不上，" +
               "或这个串口被别的程序占着";
    }

    /// <summary>握手：GetFirmwareVersion。有回应才算真的通了，光打开串口不算。</summary>
    private async Task<(bool Ok, int Baud, int Bytes)> HandshakeAsync(CancellationToken token)
    {
        var seen = 0;
        var lastBaud = _port is null ? DefaultBaud : (_preferredBaud > 0 ? _preferredBaud : DefaultBaud);

        foreach (var baud in BaudCandidates(_preferredBaud))
        {
            token.ThrowIfCancellationRequested();
            lastBaud = baud;

            if (_port is not null)
            {
                try
                {
                    if (_port.BaudRate != baud) _port.BaudRate = baud;
                    _port.DiscardInBuffer();
                }
                catch (Exception)
                {
                    // 改不了波特率就跳过这一档，不要让整个握手挂掉
                    continue;
                }
            }

            _baud = baud;

            // 第一包照官方软件的做法发：唤醒 + SAMConfiguration 一次性写出去。
            //
            // 以前这里是"先发 16 字节唤醒、等 120ms、再发 GetFirmwareVersion"。
            // 对这块板子（PCR532 一类的国产 PN532 兼容板）那样做是无效的：
            // 官方软件从不发 0x02，连上就发 SAMConfig，而且和唤醒连在同一笔写里。
            try
            {
                var boot = BootSequence();
                _port?.Write(boot, 0, boot.Length);
            }
            catch (Exception)
            {
                // 写不出去就当这一档没戏，下一档再试
            }

            var bootReply = await ReadFrameAsync(900, token);
            if (bootReply is { Length: >= 2 }) return (true, baud, bootReply.Length);

            // 有些板子只认 GetFirmwareVersion：再补两枪，两套都试过才算这一档失败
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var (resp, bytes) = await TransceiveAsync(new byte[] { 0x02 }, token);
                seen += bytes;
                if (resp is { Length: >= 6 } && resp[0] == 0x03) return (true, baud, seen);
                await Task.Delay(120, token);
            }
        }

        return (false, lastBaud, seen);
    }

    /// <summary>
    /// 只读、不写：等一帧完整回应。
    ///
    /// 需要它是因为开机握手那一包是"唤醒+命令"一起写的，
    /// 不能再走 <see cref="TransceiveAsync"/>（它自己还要再写一次命令）。
    /// </summary>
    private async Task<byte[]?> ReadFrameAsync(int waitMs, CancellationToken token)
    {
        var port = _port;
        if (port is null || !port.IsOpen) return null;

        var buffer = new List<byte>(64);
        var deadline = DateTime.UtcNow.AddMilliseconds(waitMs);

        while (DateTime.UtcNow < deadline)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var one = port.ReadByte();
                if (one >= 0 && buffer.Count < 4096)
                {
                    buffer.Add((byte)one);
                    if (ContainsFullFrame(buffer, out var payload)) return payload;
                }
            }
            catch (TimeoutException)
            {
                // 这段没数据，继续等到 deadline
            }
            catch (InvalidOperationException)
            {
                return null;    // 端口被关掉了（换端口/换波特率）
            }
            await Task.Yield();
        }
        return null;
    }

    private async Task WakeAsync(CancellationToken token)
    {
        try
        {
            var seq = WakeSequence();
            _port?.Write(seq, 0, seq.Length);
        }
        catch (Exception)
        {
            // 唤醒写不出去不影响后续判断（Transceive 会再失败一次）
        }
        try { await Task.Delay(120, token); } catch (OperationCanceledException) { throw; }
    }


    /// <summary>一次寻卡。成功返回 UID（十六进制大写），没有卡返回 null。</summary>
    private async Task<string?> PollOnceAsync(CancellationToken token)
    {
        var (resp, _) = await TransceiveAsync(new byte[] { 0x4A, 0x01, 0x00 }, token);
        return resp is null ? null : ParseUidFromInListResponse(resp);
    }

    /// <summary>
    /// 从 InListPassiveTarget 的应答里取 UID。
    ///
    /// 应答去掉 TFI 之后是这样（真机抓到的）：
    /// `4B | 01 | 01 | 00 04 | 08 | 04 | 59 5D 6A 4C`
    ///  命令  NbTg  Tg  ATQA   SAK  Len       UID
    ///
    /// **Len 在第 6 位**。以前写成第 5 位，取到的是 SAK(0x08)，于是要 8 个字节的 UID
    /// 却只有 4 个 —— 每次都被判成"没卡"。表现就是"读卡器连上了，刷卡一点反应都没有"，
    /// 而这种错只有拿真机跑才看得见，纯读代码很容易顺着注释点错。
    /// </summary>
    public static string ParseUidFromInListResponse(byte[] resp)
    {
        // 最少要有 命令+NbTg+Tg+ATQA2+SAK+Len+1字节UID
        if (resp.Length < 8) return "";
        if (resp[0] != 0x4B || resp[1] < 1) return "";

        const int lenIndex = 6;
        var idLen = resp[lenIndex];
        if (idLen <= 0 || idLen > 10) return "";                    // 106kbps 最多 10 字节（双/三倍 UID）
        if (resp.Length < lenIndex + 1 + idLen) return "";

        var sb = new StringBuilder(idLen * 2);
        for (var i = 0; i < idLen; i++) sb.Append(resp[lenIndex + 1 + i].ToString("X2"));
        return sb.ToString();
    }

    /// <summary>
    /// 发一帧、等回应。收到的原始字节数通过 <paramref name="bytesRead"/> 带出去 ——
    /// 握手失败时要靠它区分"一个字节都没回"和"回了但解不出来"。
    /// </summary>
    private async Task<(byte[]? Payload, int Bytes)> TransceiveAsync(
        byte[] command, CancellationToken token)
    {
        var bytesRead = 0;
        var port = _port;
        if (port is null || !port.IsOpen) return (null, 0);

        var frame = BuildFrame(command);
        try { port.Write(frame, 0, frame.Length); }
        catch (Exception) { return (null, 0); }

        var buffer = new List<byte>(64);
        var deadline = DateTime.UtcNow.AddMilliseconds(700);
        while (DateTime.UtcNow < deadline)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var one = port.ReadByte();
                if (one >= 0)
                {
                    buffer.Add((byte)one);
                    bytesRead = buffer.Count;
                    if (ContainsFullFrame(buffer, out var payload)) return (payload, bytesRead);
                }
            }
            catch (TimeoutException)
            {
                // 没数据就继续等到 deadline，别把超时当失败
            }
            catch (InvalidOperationException)
            {
                // 端口在这一瞬间被关掉了（用户切了端口）：当作没回应
                return (null, bytesRead);
            }
            await Task.Yield();
        }
        return (null, bytesRead);
    }

    /// <summary>00 00 FF LEN LCS D4 CMD ... DCS 00，DCS 是 TFI 起算的字节和取反加一。</summary>
    public static byte[] BuildFrame(byte[] data)
    {
        var len = data.Length + 1;                      // +TFI
        var lcs = (byte)(~(byte)len + 1);
        var frame = new List<byte>(len + 7) { Preamble, 0x00, StartCode, (byte)len, lcs, HostToPn };
        frame.AddRange(data);

        var sum = HostToPn;
        foreach (var b in data) sum += b;
        frame.Add((byte)(~(byte)sum + 1));
        frame.Add(Postamble);
        return frame.ToArray();
    }

    /// <summary>在字节流里找第一个完整且校验通过的响应帧。</summary>
    public static bool ContainsFullFrame(List<byte> buffer, out byte[] payload)
    {
        payload = Array.Empty<byte>();
        for (var i = 0; i + 5 < buffer.Count; i++)
        {
            if (buffer[i] != Preamble || buffer[i + 1] != 0x00 || buffer[i + 2] != StartCode) continue;

            var len = buffer[i + 3];
            var lcs = buffer[i + 4];
            if ((byte)(len + lcs) != 0) continue;               // 长度校验
            if (buffer.Count < i + 5 + len + 2) continue;       // 还没收全

            if (buffer[i + 5] != PnToHost) continue;

            var sum = 0;
            for (var j = 0; j < len; j++) sum += buffer[i + 5 + j];
            var dcs = buffer[i + 5 + len];
            if ((byte)(sum + dcs) != 0) continue;               // 数据校验

            var body = new byte[len - 1];
            for (var j = 0; j < body.Length; j++) body[j] = buffer[i + 6 + j];
            payload = body;
            return true;
        }
        return false;
    }

    /// <summary>卡片还在感应区里：返回 false 表示这张卡刚才就在，不用再报一次。</summary>
    private bool MarkPresent(string uid)
    {
        lock (_gate)
        {
            _lastPresenceAt = Clock.NowMs();
            return _present.Add(uid);
        }
    }

    /// <summary>这一轮没扫到卡。要连着一会儿都没有才算真拿走，避免一次漏读就重复计分。</summary>
    private void MarkAbsentIfGone()
    {
        lock (_gate)
        {
            if (_present.Count == 0) return;
            if (Clock.NowMs() - _lastPresenceAt < AbsentGraceMs) return;
            _present.Clear();
        }
    }

    private void Emit(CardRead read)
    {
        lock (_gate)
        {
            var now = Clock.NowMs();
            if (_lastSeen.TryGetValue(read.Uid, out var last) && now - last < DebounceMs) return;
            _lastSeen[read.Uid] = now;
            _lastCardAt = now;
            _cardsRead++;
            _statusMessage = $"刚读到 {read.ShortUid}";
            _state = ReaderState.Running;

            if (_lastSeen.Count > 64)
                foreach (var key in _lastSeen.Where(kv => now - kv.Value > 600_000).Select(kv => kv.Key).ToList())
                    _lastSeen.Remove(key);
        }

        CardDetected?.Invoke(read);
        RaiseStatus();
    }

    private void SetStatus(ReaderState state, string message, string port)
    {
        lock (_gate)
        {
            _state = state;
            _statusMessage = message;
            _portName = port;
        }
        RaiseStatus();
    }

    private void RaiseStatus()
    {
        try { StatusChanged?.Invoke(Status); } catch (Exception) { }
    }

    public void Dispose() => Stop();
}
