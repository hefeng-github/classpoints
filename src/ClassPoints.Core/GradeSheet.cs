namespace ClassPoints.Core;

/// <summary>成绩表里的一个科目（或"总分"）：Label 是它上方的分组标题，Column 是"分数"那一列。</summary>
public sealed record GradeColumn(string Label, int Column);

/// <summary>
/// 全校成绩大表（学校统考那种）：两行表头 + 一列班级。
///
///    学校 | 班级 | 考号 | 姓名 | 总分(物理) | … | 语文 | … | 化学(赋分) | …
///         |      |      |      |  分数 班名次 校名次 全体名次 市级名次 | …
///
/// 这种表一个班一屏放不下：一个年级几百人、二十几科、每科后面还跟着四五列名次。
/// 老师要的从来不是"整张表导进来"，而是**先挑一个班、再挑一科**。
/// 所以这里只做三件事：认出班级列、认出姓名列、认出每一科"分数"那一列 ——
/// 名次列一概不管（它们不进总分、也不该进任何统计）。
///
/// 为什么放在 Core：这是纯文本判定，不碰数据库也不碰界面，能直接写单测钉住。
/// </summary>
public sealed class GradeSheet
{
    private static readonly string[] NameHeaders = { "姓名", "学生姓名", "名字" };
    private static readonly string[] ClassHeaders = { "班级", "班号", "班别" };
    private static readonly string[] NoHeaders = { "考号", "学号", "编号" };
    private static readonly string[] IgnoredLabels = { "学校", "班级", "考号", "学号", "姓名", "序号" };

    private readonly SheetGrid _grid;

    private GradeSheet(SheetGrid grid, int headerRow, int classColumn, int nameColumn, int noColumn,
        IReadOnlyList<string> classes, IReadOnlyList<GradeColumn> subjects)
    {
        _grid = grid;
        HeaderRow = headerRow;
        ClassColumn = classColumn;
        NameColumn = nameColumn;
        NoColumn = noColumn;
        FirstDataRow = headerRow + 2;
        Classes = classes;
        Subjects = subjects;
    }

    /// <summary>第一行表头（"学校/班级/考号/姓名…"那一行）。</summary>
    public int HeaderRow { get; }

    /// <summary>表头下面那一行（"分数/班名次/…"），可能在合并单元格里。</summary>
    public int SubHeaderRow => HeaderRow + 1;

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
        if (row <= SubHeaderRow || row >= RowCount) return false;
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
    /// 认这张表是不是"全校大表"。要求同时满足：
    /// 表头行里既有「班级」又有「姓名」，下面一行至少有一个「分数」；
    /// 并且班级列真的有值、能认出至少一科。缺任何一条都当作普通表格走老路。
    /// </summary>
    public static bool TryParse(SheetGrid grid, out GradeSheet? sheet)
    {
        sheet = null;
        if (grid.RowCount < 3 || grid.ColumnCount < 4) return false;

        var scan = System.Math.Min(5, grid.RowCount - 2);
        for (var header = 0; header < scan; header++)
        {
            var nameCol = -1;
            var classCol = -1;
            var noCol = -1;

            for (var c = 0; c < grid.ColumnCount; c++)
            {
                var key = NameNormalizer.NormalizeKey(grid.Cell(header, c));
                if (key.Length == 0) continue;
                if (nameCol < 0 && NameHeaders.Any(key.Contains)) nameCol = c;
                else if (classCol < 0 && ClassHeaders.Any(key.Contains)) classCol = c;
                else if (noCol < 0 && NoHeaders.Any(key.Contains)) noCol = c;
            }

            if (nameCol < 0 || classCol < 0) continue;

            var sub = header + 1;
            var hasScoreCell = false;
            for (var c = 0; c < grid.ColumnCount; c++)
                if (NameNormalizer.NormalizeKey(grid.Cell(sub, c)) == "分数") { hasScoreCell = true; break; }

            if (!hasScoreCell) continue;

            var subjects = ReadSubjects(grid, header, sub, nameCol, classCol, noCol);
            if (subjects.Count == 0) continue;

            var classes = ReadClasses(grid, sub + 1, classCol, nameCol);
            if (classes.Count == 0) continue;

            sheet = new GradeSheet(grid, header, classCol, nameCol, noCol, classes, subjects);
            return true;
        }

        return false;
    }

    private static List<GradeColumn> ReadSubjects(SheetGrid grid, int header, int sub,
        int nameCol, int classCol, int noCol)
    {
        var list = new List<GradeColumn>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var c = 0; c < grid.ColumnCount; c++)
        {
            if (NameNormalizer.NormalizeKey(grid.Cell(sub, c)) != "分数") continue;

            // 分组标题在两行表头里靠"往左找最近一个非空"还原 ——
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
            if (IgnoredLabels.Any(x => label == x)) continue;
            if (c == nameCol || c == classCol || c == noCol) continue;
            if (!seen.Add(label)) continue;

            list.Add(new GradeColumn(label, c));
        }

        return list;
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
