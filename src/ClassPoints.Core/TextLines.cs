namespace ClassPoints.Core;

/// <summary>
/// 多行文本 → 一行一个的数组。
///
/// 这件事看起来只有一行代码，但踩过坑：WinUI 的 TextBox 里换行是 <c>\r</c>，
/// 从 Excel 粘过来是 <c>\r\n</c>，而只按 <c>\n</c> 切的话整段文本会被当成一行 ——
/// 界面上明明显示了 8 行，导入结果却是 1 个名字长到离谱的学生。
///
/// 所以所有"粘贴一列姓名"的入口都走这里，别各写各的 Split。
/// </summary>
public static class TextLines
{
    /// <summary>常见换行符：CR、LF、VT、FF、NEL、行分隔符、段分隔符。</summary>
    private static readonly char[] Breaks = ['\r', '\n', '\u000B', '\u000C', '\u0085', '\u2028', '\u2029'];

    public static string[] Split(string? text)
    {
        if (string.IsNullOrEmpty(text)) return [];
        return text.Split(Breaks, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    }
}
