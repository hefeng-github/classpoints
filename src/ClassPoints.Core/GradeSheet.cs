namespace ClassPoints.Core;

/// <summary>成绩表里的一个科目（或"总分"）：Label 是它上方的分组标题，Column 是"分数"那一列。</summary>
public sealed record GradeColumn(string Label, int Column);

/// <summary>
/// 带「班级」列的成绩表 —— 学校统考下发的表基本都是这个形状，先挑班、再挑科。
///
/// 两种表头都要认：
///
///   两行表头（合并单元格，每科后面跟一串名次列）：
///     学校 | 班级 | 考号 | 姓名 | 总分(物理) | … | 语文 | … | 化学(赋分) | …
///          |      |      |      |  分数 班名次 校名次 全体名次 市级名次 | …
///
///   单行表头（一个班或几个班一张表，每科就一列）：
///     学校 | 班级 | 考号 | 姓名 | 总分 | 语文 | 数学 | 英语 | …
///
/// 所以这里只做三件事：认出班级列、认出姓名列、认出每一科"分数"那一列 ——
/// 名次列一概不管（它们不进总分、也不该进任何统计）。
///
/// 为什么放在 Core：这是纯文本判定，不碰数据库也不碰界面，能直接写单测钉住。
/// </summary>
public sealed class GradeSheet
{
    private static readonly string[] NameHeaders = { "姓名", "学生姓名", "名字" };
    private static readonly string[] ClassHeaders = { "班级", "行政班", "班别", "班号", "班级名称" };
    private static readonly string[] NoHeaders = { "考号", "学号", "编号" };

    /// <summary>不是科目的列标题：这些列的分数没有意义。</summary>
    private static readonly string[] IgnoredLabels = { "学校", "班级", "行政班", "考号", "学号", "姓名", "序号", "选科" };

    /// <summary>子表头里"这一格是分数"的写法。</summary>
    private static readonly string[] ScoreCells = { "分数", "赋分", "原始分", "得分", "成绩" };

    /// <summary>
    /// 同一科有多列分数时（原始分 + 赋分），优先导哪一列。
    /// 赋分是排名的口径，先选它；原始分放最后兜底。
    /// </summary>
    private static readonly string[] ScorePreference = { "赋分", "分数", "得分", "成绩", "原始分" };

    private readonly SheetGrid _grid;

    private GradeSheet(SheetGrid grid, int headerRow, int classColumn, int nameColumn, int noColumn,
        bool twoRowHeader, IReadOnlyList<string> classes, IReadOnlyList<GradeColumn> subjects)
    {
        _grid = grid;
        HeaderRow = headerRow;
        ClassColumn = classColumn;
        NameColumn = nameColumn;
        NoColumn = noColumn;
        TwoRowHeader = twoRowHeader;
        FirstDataRow = headerRow + (twoRowHeader ? 2 : 1);
        Classes = classes;
        Subjects = subjects;
    }

    /// <summary>表头所在行（"学校/班级/考号/姓名…"那一行）。</summary>
    public int HeaderRow { get; }

    /// <summary>是不是两行表头（第二行是"分数/名次"那行，常常在合并单元格里）。</summary>
    public bool TwoRowHeader { get; }

    /// <summary>子表头那一行；单行表头时就是表头本身。</summary>
    public int SubHeaderRow => TwoRowHeader ? HeaderRow + 1 : HeaderRow;

    public int ClassColumn { get; }
    public int NameColumn { get; }
    public int NoColumn { get; }
    public int FirstDataRow { get; }

    /// <summary>表里出现过的班级（去重、按数字大小排："18" 排在 "17" 后面）。</summary>
    public IReadOnlyList<string> Classes { get; }

    /// <summary>可导入的科目，顺序即表里的顺序。</summary>
    public IReadOnlyList<GradeColumn> Subjects { get; }

    public int RowCount => _grid.RowCount;

    public string Cell(int row, int col) => _grid.Cell(row, col).Trim();

    public string ClassOf(int row) => Cell(row, ClassColumn);

    public string NameOf(int row) => Cell(row, NameColumn);

    public string NoOf(int row) => NoColumn >= 0 ? Cell(row, NoColumn) : "";

    public string ScoreOf(int row, GradeColumn column) => Cell(row, column.Column);

    public bool IsDataRow(int row)
    {
        if (row < FirstDataRow || row >= RowCount) return false;
        return NameOf(row).Length > 0;
    }

    public IEnumerable<int> DataRows(string? className)
    {
        for (var r = FirstDataRow; r < RowCount; r++)
        {
            if (!IsDataRow(r)) continue;
            if (className is { Length: > 0 } && ClassOf(r) != className) continue;
            yield return r;
        }
    }

    public int CountIn(string? className) => DataRows(className).Count();

    /// <summary>在所有班级里按姓名找（给"再加一个别班的学生"用）。</summary>
    public IEnumerable<int> Search(string? keyword, int limit = 50)
    {
        var key = NameNormalizer.NormalizeKey(keyword ?? "");
        if (key.Length == 0) yield break;

        var n = 0;
        for (var r = FirstDataRow; r < RowCount && n < limit; r++)
        {
            if (!IsDataRow(r)) continue;
            if (!NameNormalizer.NormalizeKey(NameOf(r)).Contains(key)) continue;
            n++;
            yield return r;
        }
    }

    /// <summary>
    /// 把「某个班 + 某一科」投影成一张两列小表（姓名 / 分数），交给 <see cref="ScoreSheetParser"/>。
    ///
    /// 这样成绩导入只用维护一条管线：真正的匹配、同名、缺考、汇总行判定都在原有的
    /// ScoreSheetParser 里，这里不重写一遍 —— 重写一遍就是两套规则，迟早对不上。
    /// </summary>
    public SheetGrid Project(string? className, GradeColumn subject)
    {
        var rows = new List<IReadOnlyList<string>> { new[] { "姓名", "分数" } };
        foreach (var r in DataRows(className))
        {
            var name = NameOf(r);
            if (name.Length == 0) continue;
            rows.Add(new[] { name, ScoreOf(r, subject) });
        }
        return new SheetGrid(_grid.Name, rows);
    }

    /// <summary>
    /// 认这张表是不是"带班级的成绩表"。要求同时满足：
    /// 某一行表头里既有「班级」（或行政班）又有「姓名」，剩下认出至少一科、班级列真有值。
    /// 缺任何一条都当作普通表格走老路。
    /// </summary>
    public static bool TryParse(SheetGrid grid, out GradeSheet? sheet)
    {
        sheet = null;
        if (grid.RowCount < 3 || grid.ColumnCount < 3) return false;

        var scan = System.Math.Min(5, grid.RowCount - 1);
        for (var header = 0; header < scan; header++)
        {
            if (grid.IsRowEmpty(header)) continue;

            var nameCol = -1;
            var classCol = -1;
            var noCol = -1;

            for (var c = 0; c < grid.ColumnCount; c++)
            {
                var key = NameNormalizer.NormalizeKey(grid.Cell(header, c));
                if (key.Length == 0) continue;
                if (nameCol < 0 && IsHeaderWord(key, NameHeaders)) nameCol = c;
                else if (classCol < 0 && IsHeaderWord(key, ClassHeaders)) classCol = c;
                else if (noCol < 0 && IsHeaderWord(key, NoHeaders)) noCol = c;
            }

            if (nameCol < 0 || classCol < 0) continue;

            var twoRow = LooksLikeTwoRowHeader(grid, header, nameCol);
            var subjects = twoRow
                ? ReadSubjectsFromSubHeader(grid, header, nameCol, classCol, noCol)
                : ReadSubjectsFromHeader(grid, header, nameCol, classCol, noCol);
            if (subjects.Count == 0) continue;

            var firstData = header + (twoRow ? 2 : 1);
            var classes = ReadClasses(grid, firstData, classCol, nameCol);
            if (classes.Count == 0) continue;

            sheet = new GradeSheet(grid, header, classCol, nameCol, noCol, twoRow, classes, subjects);
            return true;
        }

        return false;
    }

    /// <summary>
    /// 表头词判定。不能用宽松的 Contains：标题行「学生选科组合排名(赋分-物理类)」里
    /// 就有"学生"两个字，按 Contains 会把整张表的标题当表头行（v1.3.5 的现场错误）。
    /// </summary>
    private static bool IsHeaderWord(string key, string[] set)
    {
        if (key.Length == 0 || key.Length > 8) return false;
        if (key.Contains("名次")) return false;
        return set.Any(h => key == h || key.StartsWith(h) || key.EndsWith(h));
    }

    private static bool IsIgnoredLabel(string key) =>
        IgnoredLabels.Any(x => NameNormalizer.NormalizeKey(x) == key);

    /// <summary>
    /// 两行还是单行表头：看**下一行的姓名列**。写着人名（"张权峰"）就是单行表头，
    /// 空着或又写一遍"姓名"就是两行表头 —— 合并单元格的第二行姓名列必然是空的。
    /// 再加一条：下一行得真有"分数/赋分"这类格子，否则宁可当单行处理。
    /// </summary>
    private static bool LooksLikeTwoRowHeader(SheetGrid grid, int header, int nameCol)
    {
        var sub = header + 1;
        if (sub >= grid.RowCount) return false;

        var subName = NameNormalizer.NormalizeKey(grid.Cell(sub, nameCol));
        var looksEmpty = subName.Length == 0 || IsHeaderWord(subName, NameHeaders);
        if (!looksEmpty) return false;

        for (var c = 0; c < grid.ColumnCount; c++)
        {
            var key = NameNormalizer.NormalizeKey(grid.Cell(sub, c));
            if (ScoreCells.Any(s => key == s || key.StartsWith(s))) return true;
        }

        return false;
    }

    /// <summary>两行表头：分数的列写在第二行（"分数 / 赋分 / 原始分"），科目名是第一行的合并标题。</summary>
    private static List<GradeColumn> ReadSubjectsFromSubHeader(SheetGrid grid, int header,
        int nameCol, int classCol, int noCol)
    {
        var best = new List<(string Label, int Column, int Priority)>();

        for (var c = 0; c < grid.ColumnCount; c++)
        {
            if (c == nameCol || c == classCol || c == noCol) continue;

            var cellKey = NameNormalizer.NormalizeKey(grid.Cell(header + 1, c));
            if (cellKey.Length == 0) continue;
            var priority = System.Array.FindIndex(ScorePreference, p => cellKey.Contains(p));
            if (priority < 0) continue;

            // 科目名在两行表头里靠"往左找最近一个非空"还原 ——
            // 合并单元格在 NPOI/ClosedXML 里只有左边第一格有值。
            var label = "";
            for (var k = c; k >= 0; k--)
            {
                var text = grid.Cell(header, k).Trim();
                if (text.Length == 0) continue;
                label = text;
                break;
            }

            if (label.Length == 0) label = "第 " + ColumnOption.IndexToLetter(c) + " 列";
            if (IsIgnoredLabel(NameNormalizer.NormalizeKey(label))) continue;

            // 同一科既有"原始分"又有"赋分"时只留一列（赋分优先），否则科目下拉里会出现两个"生物"
            var same = best.FindIndex(x => x.Label == label);
            if (same >= 0)
            {
                if (priority < best[same].Priority) best[same] = (label, c, priority);
                continue;
            }

            best.Add((label, c, priority));
        }

        return best.Select(x => new GradeColumn(x.Label, x.Column)).ToList();
    }

    /// <summary>
    /// 单行表头：科目名就是表头本身，每科一列。只认"这一列当真是数字"的列，
    /// 否则「学校」「选科」这种文字列会被当成科目。
    /// </summary>
    private static List<GradeColumn> ReadSubjectsFromHeader(SheetGrid grid, int header,
        int nameCol, int classCol, int noCol)
    {
        var list = new List<GradeColumn>();

        for (var c = 0; c < grid.ColumnCount; c++)
        {
            if (c == nameCol || c == classCol || c == noCol) continue;

            var label = grid.Cell(header, c).Trim();
            if (label.Length == 0) continue;

            var key = NameNormalizer.NormalizeKey(label);
            if (key.Contains("名次") || IsIgnoredLabel(key)) continue;
            if (!ColumnHasNumbers(grid, c, header + 1)) continue;

            list.Add(new GradeColumn(label, c));
        }

        return list;
    }

    private static bool ColumnHasNumbers(SheetGrid grid, int col, int from)
    {
        var end = System.Math.Min(grid.RowCount, from + 30);
        for (var r = from; r < end; r++)
            if (ScoreSheetParser.TryParseScore(grid.Cell(r, col), out _)) return true;
        return false;
    }

    private static List<string> ReadClasses(SheetGrid grid, int firstData, int classCol, int nameCol)
    {
        var set = new List<string>();
        for (var r = firstData; r < grid.RowCount; r++)
        {
            if (grid.Cell(r, nameCol).Trim().Length == 0) continue;
            var value = grid.Cell(r, classCol).Trim();
            if (value.Length == 0) continue;
            if (!set.Contains(value)) set.Add(value);
        }

        // "17" 排在 "18" 前面，而不是按字符串（"10" 会跑到 "9" 前面）
        return set
            .OrderBy(x => double.TryParse(x, out var n) ? n : double.MaxValue)
            .ThenBy(x => x, StringComparer.Ordinal)
            .ToList();
    }
}
