using System.Runtime.InteropServices;
using System.Text;

namespace ClassPoints.App.Services;

/// <summary>一个「文件类型」下拉项。Patterns 是 Win32 的写法，例如 *.xlsx。</summary>
public sealed record FileFilter(string Label, string[] Patterns)
{
    public string Pattern => string.Join(";", Patterns);

    public static FileFilter Excel => new("Excel 工作簿", ["*.xlsx", "*.xlsm", "*.xls"]);
    public static FileFilter Sheet => new("成绩表 / 名单", ["*.xlsx", "*.xlsm", "*.xls", "*.csv"]);
    public static FileFilter Zip => new("整包备份", ["*.zip"]);
    public static FileFilter Config => new("班级积分配置包", ["*.cpcfg"]);
    public static FileFilter Text => new("文本文件", ["*.txt"]);
}

/// <summary>
/// 文件选择框。
///
/// 不用 Windows.Storage.Pickers：那一套在「非打包 + 以管理员身份运行」时会抛一个
/// message 为空的 COMException（日志里只剩下 <c>import-students: COMException:</c>），
/// 表现就是「点『从 Excel 导入』直接失败」「导出 Excel 直接失败」。装到 Program Files
/// 之后从快捷方式启动，不少机器上就是提权进程 —— 所以这条路必须绕开。
///
/// comdlg32 的公共对话框不依赖包标识、也不经过拾取器中介进程，提权进程里照常工作，
/// 返回的永远是真实文件系统路径（而不是 StorageFile 那种可能为空串的 Path）。
/// </summary>
public static class FileDialogs
{
    private const int OFN_OVERWRITEPROMPT = 0x00000002;
    private const int OFN_HIDEREADONLY = 0x00000004;
    private const int OFN_NOCHANGEDIR = 0x00000008;
    private const int OFN_PATHMUSTEXIST = 0x00000800;
    private const int OFN_FILEMUSTEXIST = 0x00001000;
    private const int OFN_EXPLORER = 0x00080000;

    private const int BufferSize = 2048;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenFileName
    {
        public int StructSize;
        public IntPtr Owner;
        public IntPtr Instance;
        public string? Filter;
        public string? CustomFilter;
        public int MaxCustomFilter;
        public int FilterIndex;
        [MarshalAs(UnmanagedType.LPWStr)] public StringBuilder File;
        public int MaxFile;
        public string? FileTitle;
        public int MaxFileTitle;
        public string? InitialDir;
        public string? Title;
        public int Flags;
        public short FileOffset;
        public short FileExtension;
        public string? DefaultExt;
        public IntPtr CustomData;
        public IntPtr Hook;
        public string? TemplateName;
        public IntPtr Reserved;
        public int ReservedInt;
        public int FlagsEx;
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetOpenFileNameW(ref OpenFileName dialog);

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetSaveFileNameW(ref OpenFileName dialog);

    /// <summary>选一个已有的文件。取消返回 null。</summary>
    public static string? Open(string title, params FileFilter[] filters)
    {
        var dialog = Build(title, filters, defExt: null);
        dialog.Flags |= OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST;
        return GetOpenFileNameW(ref dialog) ? dialog.File.ToString() : null;
    }

    /// <summary>选一个保存位置。取消返回 null。</summary>
    public static string? Save(string title, string suggestedName, FileFilter filter, string defaultExtension)
    {
        var dialog = Build(title, [filter], defaultExtension);
        dialog.File = new StringBuilder(suggestedName, BufferSize);
        dialog.Flags |= OFN_OVERWRITEPROMPT | OFN_PATHMUSTEXIST;
        return GetSaveFileNameW(ref dialog) ? dialog.File.ToString() : null;
    }

    /// <summary>拼 filter 串：每组是「说明|通配符」，整串以两个 \0 结尾。</summary>
    private static OpenFileName Build(string title, FileFilter[] filters, string? defExt)
    {
        var filter = new StringBuilder();
        foreach (var group in filters)
            filter.Append(group.Label).Append('\0').Append(group.Pattern).Append('\0');
        filter.Append('\0');

        return new OpenFileName
        {
            StructSize = Marshal.SizeOf<OpenFileName>(),
            Owner = App.Window is null ? IntPtr.Zero : WinRT.Interop.WindowNative.GetWindowHandle(App.Window),
            Filter = filter.ToString(),
            FilterIndex = 1,
            File = new StringBuilder(BufferSize),
            MaxFile = BufferSize,
            FileTitle = null,
            MaxFileTitle = 0,
            InitialDir = null,
            Title = title,
            Flags = OFN_EXPLORER | OFN_NOCHANGEDIR | OFN_HIDEREADONLY,
            DefaultExt = defExt,
            TemplateName = null,
        };
    }
}
