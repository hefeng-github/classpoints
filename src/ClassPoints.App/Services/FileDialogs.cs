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
///
/// 结构体里全部用 IntPtr：只要有一个 string 字段，<c>Marshal.SizeOf</c> 在 .NET 10 上
/// 就会抛「cannot be marshaled as an unmanaged structure」，而这个异常只在真正点按钮时
/// 才出现 —— 编译期完全看不出来。字符串一律自己分配、自己释放。
/// </summary>
public static class FileDialogs
{
    private const int OFN_OVERWRITEPROMPT = 0x00000002;
    private const int OFN_HIDEREADONLY = 0x00000004;
    private const int OFN_NOCHANGEDIR = 0x00000008;
    private const int OFN_PATHMUSTEXIST = 0x00000800;
    private const int OFN_FILEMUSTEXIST = 0x00001000;
    private const int OFN_EXPLORER = 0x00080000;

    private const int BufferChars = 2048;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenFileName
    {
        public int StructSize;
        public IntPtr Owner;
        public IntPtr Instance;
        public IntPtr Filter;
        public IntPtr CustomFilter;
        public int MaxCustomFilter;
        public int FilterIndex;
        public IntPtr File;
        public int MaxFile;
        public IntPtr FileTitle;
        public int MaxFileTitle;
        public IntPtr InitialDir;
        public IntPtr Title;
        public int Flags;
        public short FileOffset;
        public short FileExtension;
        public IntPtr DefaultExt;
        public IntPtr CustomData;
        public IntPtr Hook;
        public IntPtr TemplateName;
        public IntPtr Reserved;
        public int ReservedInt;
        public int FlagsEx;
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetOpenFileNameW(ref OpenFileName dialog);

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetSaveFileNameW(ref OpenFileName dialog);

    /// <summary>选一个已有的文件。取消返回 null。</summary>
    public static string? Open(string title, params FileFilter[] filters) =>
        Show(title, filters, defExt: null, suggestedName: "", save: false);

    /// <summary>选一个保存位置。取消返回 null。</summary>
    public static string? Save(string title, string suggestedName, FileFilter filter, string defaultExtension) =>
        Show(title, [filter], defaultExtension, suggestedName, save: true);

    private static string? Show(string title, FileFilter[] filters, string? defExt,
        string suggestedName, bool save)
    {
        var filterPtr = Marshal.StringToHGlobalUni(BuildFilter(filters));
        var titlePtr = Marshal.StringToHGlobalUni(title);
        var defExtPtr = defExt is null ? IntPtr.Zero : Marshal.StringToHGlobalUni(defExt);
        var filePtr = Marshal.AllocHGlobal(BufferChars * 2);

        try
        {
            // 缓冲区自己清零：不清的话第一次打开会把随机内容当成默认文件名
            for (var i = 0; i < BufferChars; i++) Marshal.WriteInt16(filePtr, i * 2, 0);
            if (suggestedName.Length > 0)
            {
                var bytes = Encoding.Unicode.GetBytes(suggestedName);
                var count = System.Math.Min(bytes.Length, (BufferChars - 1) * 2);
                Marshal.Copy(bytes, 0, filePtr, count);
            }

            var dialog = new OpenFileName
            {
                StructSize = Marshal.SizeOf<OpenFileName>(),
                Owner = App.Window is null ? IntPtr.Zero : WinRT.Interop.WindowNative.GetWindowHandle(App.Window),
                Filter = filterPtr,
                FilterIndex = 1,
                File = filePtr,
                MaxFile = BufferChars,
                Title = titlePtr,
                DefaultExt = defExtPtr,
                Flags = OFN_EXPLORER | OFN_NOCHANGEDIR | OFN_HIDEREADONLY | OFN_PATHMUSTEXIST |
                        (save ? OFN_OVERWRITEPROMPT : OFN_FILEMUSTEXIST),
            };

            var ok = save ? GetSaveFileNameW(ref dialog) : GetOpenFileNameW(ref dialog);
            var picked = ok ? Marshal.PtrToStringUni(filePtr) : null;
            return string.IsNullOrWhiteSpace(picked) ? null : picked;
        }
        finally
        {
            Marshal.FreeHGlobal(filePtr);
            Marshal.FreeHGlobal(titlePtr);
            Marshal.FreeHGlobal(filterPtr);
            if (defExtPtr != IntPtr.Zero) Marshal.FreeHGlobal(defExtPtr);
        }
    }

    /// <summary>拼 filter 串：每组是「说明|通配符」，整串以两个 \0 结尾。</summary>
    private static string BuildFilter(FileFilter[] filters)
    {
        var builder = new StringBuilder();
        foreach (var group in filters)
            builder.Append(group.Label).Append('\0').Append(group.Pattern).Append('\0');

        // 末尾的终结符由 StringToHGlobalUni 补，所以这里只要再加一个 \0
        builder.Append('\0');
        return builder.ToString();
    }
}
