using System.Globalization;
using ClassPoints.Core;
using ClosedXML.Excel;
using NPOI.SS.UserModel;

namespace ClassPoints.Data;

/// <summary>
/// Excel 读写。读用 NPOI（.xls / .xlsx 一把梭），写用 ClosedXML。
/// 读的时候只做一件事：把第一个工作表变成 SheetGrid —— 剩下所有"哪列是姓名、哪列是总分"的判断都在 Core 里，
/// 因为它要能被 Linux 上的单测覆盖。
/// </summary>
public static class ExcelReader
{
    public static IReadOnlyList<string> SheetNames(string path)
    {
        using var fs = File.OpenRead(path);
        using var wb = WorkbookFactory.Create(fs);
        var list = new List<string>();
        for (var i = 0; i < wb.NumberOfSheets; i++) list.Add(wb.GetSheetName(i));
        return list;
    }

    public static SheetGrid Read(string path, int sheetIndex = 0)
    {
        using var fs = File.OpenRead(path);
        using var wb = WorkbookFactory.Create(fs);
        if (wb.NumberOfSheets == 0) return new SheetGrid(Path.GetFileName(path), Array.Empty<IReadOnlyList<string>>());

        var idx = System.Math.Clamp(sheetIndex, 0, wb.NumberOfSheets - 1);
        var sheet = wb.GetSheetAt(idx);
        var formatter = new DataFormatter();

        IFormulaEvaluator? evaluator = null;
        try { evaluator = wb.GetCreationHelper().CreateFormulaEvaluator(); }
        catch (Exception) { evaluator = null; }   // 个别文件公式链坏了，退回原始文本

        var rows = new List<IReadOnlyList<string>>();
        var lastRow = sheet.LastRowNum;
        for (var r = 0; r <= lastRow; r++)
        {
            var row = sheet.GetRow(r);
            if (row is null)
            {
                rows.Add(Array.Empty<string>());
                continue;
            }

            var cells = new List<string>();
            var lastCell = row.LastCellNum;
            for (var c = 0; c < lastCell; c++)
            {
                var cell = row.GetCell(c);
                cells.Add(CellText(cell, formatter, evaluator));
            }
            rows.Add(cells);
        }

        // 尾部空行会让"表头扫描 10 行"的判断飘，去掉
        while (rows.Count > 0 && rows[^1].All(string.IsNullOrWhiteSpace)) rows.RemoveAt(rows.Count - 1);

        return new SheetGrid(wb.GetSheetName(idx), rows);
    }

    private static string CellText(ICell? cell, DataFormatter formatter, IFormulaEvaluator? evaluator)
    {
        if (cell is null) return "";
        try
        {
            // 日期按 yyyy-MM-dd 出，不然 45000 这种序列号会进到界面里
            if (cell.CellType == CellType.Numeric && DateUtil.IsCellDateFormatted(cell))
                return cell.DateCellValue?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "";

            var text = evaluator is not null
                ? formatter.FormatCellValue(cell, evaluator)
                : formatter.FormatCellValue(cell);
            return text.Trim();
        }
        catch (Exception)
        {
            return "";
        }
    }
}

/// <summary>一次导出需要的全部材料。故意做成"把数据装进来"，而不是让构建器自己去查库。</summary>
public sealed record ExportBundle(
    RankingResult Ranking,
    IReadOnlyList<Exam> Exams,
    IReadOnlyList<ExamScore> Scores,
    IReadOnlyList<Entry> Entries,
    Scheme Scheme,
    Period Period,
    AbsencePolicy Policy,
    string TermKey,
    IReadOnlyList<Student> Students,
    IReadOnlyDictionary<string, double> Weights,
    string ExportedBy,
    DateTime ExportedAt,
    IReadOnlyList<string>? Warnings = null);

/// <summary>
/// 四张表：总排名 / 成绩明细 / 积分流水 / 参数说明。
/// 「平时分」和「积分」只出一列 —— 它们是同一个数，并排出两列的第一个问题就是"为什么重复"。
/// </summary>
public static class WorkbookBuilder
{
    private static readonly XLColor HeaderBg = XLColor.FromHtml("#1C222B");
    private static readonly XLColor HeaderFg = XLColor.FromHtml("#F2F5F9");
    private static readonly XLColor WarnBg = XLColor.FromHtml("#FFF7E0");

    public static byte[] Build(ExportBundle b)
    {
        using var wb = new XLWorkbook();
        BuildRankSheet(wb, b);
        BuildDetailSheet(wb, b);
        BuildEntriesSheet(wb, b);
        BuildInfoSheet(wb, b);

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    public static void Save(ExportBundle b, string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllBytes(path, Build(b));
    }

    // ---------------- 1. 总排名 ----------------
    private static void BuildRankSheet(XLWorkbook wb, ExportBundle b)
    {
        var ws = wb.Worksheets.Add("总排名");
        var r = b.Ranking;
        var normalized = b.Scheme.Normalize;

        // 备注只在真有人缺考时才出这一列。平时导出的是一张发给家长看的表，
        // 底下永远空着一列"缺考"，看着像表格做坏了，还得挨个解释这是什么。
        var hasAbsent = r.Rows.Any(x => x.AbsentExams.Count > 0);

        var headers = new List<string>
        {
            "名次", "姓名", "考试总分", normalized ? "考试百分制" : "考试总分（加权）",
            "平时分（积分）", normalized ? "总评（百分制）" : "总评（加权和，非百分制）",
        };
        if (hasAbsent) headers.Add("备注");

        for (var c = 0; c < headers.Count; c++)
        {
            var cell = ws.Cell(1, c + 1);
            cell.Value = headers[c];
            cell.Style.Font.Bold = true;
            cell.Style.Font.FontColor = HeaderFg;
            cell.Style.Fill.BackgroundColor = HeaderBg;
            cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            cell.Style.Alignment.WrapText = true;
        }
        ws.Row(1).Height = 42;

        var row = 2;
        foreach (var item in r.Rows)
        {
            ws.Cell(row, 1).Value = item.Rank;
            ws.Cell(row, 2).Value = item.Name;
            ws.Cell(row, 3).Value = System.Math.Round(item.ExamRaw, 1);
            ws.Cell(row, 4).Value = System.Math.Round(item.ExamPercent, 1);
            ws.Cell(row, 5).Value = item.Points;
            ws.Cell(row, 6).Value = System.Math.Round(item.Total, 1);

            if (hasAbsent)
            {
                ws.Cell(row, headers.Count).Value = item.AbsentExams.Count == 0
                    ? ""
                    : $"缺考 · {string.Join("、", item.AbsentExams)}（{PolicyShort(b.Policy)}）";
            }

            if (item.AbsentExams.Count > 0)
                ws.Range(row, 1, row, headers.Count).Style.Fill.BackgroundColor = WarnBg;

            row++;
        }

        // 口径说明只留在「参数说明」页，总排名页保持"表头 + 数据"干干净净
        ws.SheetView.FreezeRows(1);
        var widths = new List<int> { 8, 10, 12, 16, 15, 15 };
        if (hasAbsent) widths.Add(13);
        SheetFit.Columns(ws, headers.Count, widths);
        ws.RangeUsed()?.SetAutoFilter();
    }

    // ---------------- 2. 成绩明细 ----------------
    private static void BuildDetailSheet(XLWorkbook wb, ExportBundle b)
    {
        var ws = wb.Worksheets.Add("成绩明细");
        var exams = b.Exams;
        var weights = SchemeMath.BuildExamWeights(exams, b.Weights);
        var scoreMap = b.Scores.ToDictionary(x => (x.ExamId, x.StudentId), x => x);

        var headers = new List<string> { "姓名" };
        foreach (var e in exams) headers.Add($"{e.Name}（满分 {e.FullScore:0.#}，权重 {weights.GetValueOrDefault(e.Id) * 100:0.#}%）");
        headers.Add("考试总分（加权）");
        headers.Add("平时分（积分）");
        headers.Add("总评");

        // 和总排名页同一条规矩：没人缺考就不出「备注」这一列
        var hasAbsent = b.Ranking.Rows.Any(x => x.AbsentExams.Count > 0);
        if (hasAbsent) headers.Add("备注");

        for (var c = 0; c < headers.Count; c++)
        {
            var cell = ws.Cell(1, c + 1);
            cell.Value = headers[c];
            cell.Style.Font.Bold = true;
            cell.Style.Font.FontColor = HeaderFg;
            cell.Style.Fill.BackgroundColor = HeaderBg;
            cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            cell.Style.Alignment.WrapText = true;
        }
        ws.Row(1).Height = 42;

        var rows = b.Ranking.Rows.ToDictionary(x => x.StudentId);
        var row = 2;
        foreach (var st in b.Students.Where(x => x.Active && x.DeletedAt is null))
        {
            if (!rows.TryGetValue(st.Id, out var rank)) continue;

            ws.Cell(row, 1).Value = st.Name;

            var col = 2;
            foreach (var e in exams)
            {
                if (scoreMap.TryGetValue((e.Id, st.Id), out var sc) && !sc.Absent && sc.Score is double v)
                {
                    ws.Cell(row, col).Value = System.Math.Round(v, 1);
                }
                else
                {
                    // 只写「缺考」，缺考政策由右边那列统一说明 —— 每格都带一遍政策，会把这一列撑成全场最宽
                    var cell = ws.Cell(row, col);
                    cell.Value = "缺考";
                    cell.Style.Font.FontColor = XLColor.FromHtml("#B45309");
                }
                col++;
            }

            ws.Cell(row, col).Value = System.Math.Round(rank.ExamRaw, 1);
            ws.Cell(row, col + 1).Value = rank.Points;
            ws.Cell(row, col + 2).Value = System.Math.Round(rank.Total, 1);

            // 备注只给真缺考的人写。以前每行都抄一遍"缺考记 0 分"，
            // 五十行全是同一句话，反而看不出到底是哪个人缺了哪一科。
            if (hasAbsent)
            {
                ws.Cell(row, col + 3).Value = rank.AbsentExams.Count == 0
                    ? ""
                    : $"缺考 · {string.Join("、", rank.AbsentExams)}（{PolicyShort(b.Policy)}）";
            }

            row++;
        }

        ws.SheetView.FreezeRows(1);
        ws.SheetView.FreezeColumns(1);

        var mins = new List<int> { 10 };
        // 单场成绩列给 20：表头是「期中（满分 100，权重 30%）」这种长句，16 会折成三行、被行高切掉
        for (var i = 0; i < exams.Count; i++) mins.Add(20);
        mins.Add(16);   // 考试总分（加权）
        mins.Add(16);   // 平时分（积分）
        mins.Add(12);   // 总评
        if (hasAbsent) mins.Add(15);   // 备注
        SheetFit.Columns(ws, headers.Count, mins);
        ws.RangeUsed()?.SetAutoFilter();
    }

    // ---------------- 3. 积分流水 ----------------
    private static void BuildEntriesSheet(XLWorkbook wb, ExportBundle b)
    {
        var ws = wb.Worksheets.Add("积分流水");
        string[] headers = { "日期时间", "学生", "项目", "分值", "记录人", "备注", "状态" };

        for (var c = 0; c < headers.Length; c++)
        {
            var cell = ws.Cell(1, c + 1);
            cell.Value = headers[c];
            cell.Style.Font.Bold = true;
            cell.Style.Font.FontColor = HeaderFg;
            cell.Style.Fill.BackgroundColor = HeaderBg;
            cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        }

        var names = b.Students.ToDictionary(s => s.Id, s => s.Name);
        var row = 2;
        foreach (var e in b.Entries.Where(x => !x.IsReversal).OrderBy(x => x.OccurredAt))
        {
            ws.Cell(row, 1).Value = Clock.ToLocal(e.OccurredAt).ToString("yyyy-MM-dd HH:mm");
            ws.Cell(row, 2).Value = names.TryGetValue(e.StudentId, out var n) ? n : e.StudentName;
            ws.Cell(row, 3).Value = e.RuleLabel ?? "";

            var delta = ws.Cell(row, 4);
            delta.Value = e.Delta;
            delta.Style.NumberFormat.Format = "+0;-0;0";

            ws.Cell(row, 5).Value = e.OperatorName;
            ws.Cell(row, 6).Value = e.Note ?? "";
            ws.Cell(row, 7).Value = e.IsReversed ? $"已撤销（{e.ReversedBy}）" : "";
            row++;
        }

        ws.SheetView.FreezeRows(1);
        SheetFit.Columns(ws, headers.Length, new[] { 16, 10, 16, 8, 10, 24, 16 });
        ws.RangeUsed()?.SetAutoFilter();
    }

    // ---------------- 4. 参数说明 ----------------
    private static void BuildInfoSheet(XLWorkbook wb, ExportBundle b)
    {
        var ws = wb.Worksheets.Add("参数说明");
        var s = b.Scheme;
        var weights = SchemeMath.BuildExamWeights(b.Exams, b.Weights);

        var lines = new List<(string Key, string Value)>
        {
            ("折算公式", s.FormulaText),
            ("总评口径", s.Normalize ? "百分制（已归一）" : "原值加权和（未归一）"),
            ("归一基准", s.Normalize
                ? $"班内最高 {b.Ranking.MaxPoints} 分（{b.Ranking.MaxPointsWho}）"
                : "—（未归一）"),
            ("平时分口径", "累计积分（历次周期相加，不封顶、不保底，加分扣分都原样保留）"),
            ("考试权重合计", $"{weights.Values.Sum() * 100:0.#}%"),
            ("缺考政策", PolicyLong(b.Policy)),
            ("统计周期", $"{b.Period.TitleText}（{b.Period.RangeText}）"),
            ("归档键", b.TermKey),
            ("导出时间", b.ExportedAt.ToString("yyyy-MM-dd HH:mm")),
            ("导出人", string.IsNullOrWhiteSpace(b.ExportedBy) ? "—" : b.ExportedBy),
        };

        var row = 1;
        foreach (var (key, value) in lines)
        {
            ws.Cell(row, 1).Value = key;
            ws.Cell(row, 1).Style.Font.Bold = true;
            ws.Cell(row, 2).Value = value;
            ws.Cell(row, 2).Style.Alignment.WrapText = true;
            row++;
        }

        row++;
        ws.Cell(row, 1).Value = "各场考试权重明细";
        ws.Cell(row, 1).Style.Font.Bold = true;
        row++;

        foreach (var e in b.Exams)
        {
            ws.Cell(row, 1).Value = e.Name;
            ws.Cell(row, 2).Value =
                $"满分 {e.FullScore:0.#} · 权重 {weights.GetValueOrDefault(e.Id) * 100:0.#}% · 日期 {e.ExamDate ?? "—"}";
            row++;
        }

        if (b.Exams.Count == 0)
        {
            ws.Cell(row, 1).Value = "（尚未导入任何考试，总评只由积分构成）";
            row++;
        }

        row++;
        ws.Cell(row, 1).Value = s.Normalize
            ? "注：总评为百分制，考试与积分各按满分折算后加权；会改变部分学生名次。"
            : "注：本表总评为加权和，不是百分制分数，不可与考试成绩直接比较；同一表内排名有效。";
        ws.Cell(row, 1).Style.Font.Italic = true;

        if (b.Warnings is { Count: > 0 })
        {
            row += 2;
            ws.Cell(row, 1).Value = "导出时的问题";
            ws.Cell(row, 1).Style.Font.Bold = true;
            foreach (var w in b.Warnings)
            {
                row++;
                ws.Cell(row, 1).Value = "· " + w;
            }
        }

        ws.Column(1).Width = 18;
        ws.Column(2).Width = 96;
    }

    public static string PolicyShort(AbsencePolicy p) => p switch
    {
        AbsencePolicy.Zero => "记 0 分",
        AbsencePolicy.ClassAverage => "按均值",
        _ => "权重再分配",
    };

    public static string PolicyLong(AbsencePolicy p) => p switch
    {
        AbsencePolicy.Zero => "缺考按 0 分计入",
        AbsencePolicy.ClassAverage => "缺考按该场全班平均分替代",
        _ => "缺考按权重再分配（缺考那场不计，其权重按比例摊给其余场次）",
    };
}

/// <summary>
/// 列宽自己算。ClosedXML 的 <c>AdjustToContents</c> 拿默认字体量文本，汉字按一个字符宽算，
/// 结果就是姓名列窄到装不下三个字、数字列反而空出一大片（界面报过这个）。
/// 这里按"汉字/全角标点占两个字符宽"逐格量，再夹在 [下限, 上限] 之间：
/// 下限保证表头两行放得下，上限防止一格长备注把整列拉出去。
/// </summary>
internal static class SheetFit
{
    public static void Columns(IXLWorksheet ws, int columns, IReadOnlyList<int> minWidths, double max = 40)
    {
        var used = ws.RangeUsed();
        if (used is null) return;

        var last = used.LastRow().RowNumber();
        for (var c = 1; c <= columns; c++)
        {
            // 表头允许换行的列只按数据量宽度 —— 否则一句"期中（满分 100，权重 30%）"会把整列撑到上限
            var from = ws.Cell(1, c).Style.Alignment.WrapText ? 2 : 1;

            var widest = 0.0;
            for (var r = from; r <= last; r++)
            {
                var text = ws.Cell(r, c).GetFormattedString();
                if (text.Length == 0) continue;
                widest = System.Math.Max(widest, TextWidth(text));
            }

            var min = c <= minWidths.Count ? minWidths[c - 1] : 8;
            ws.Column(c).Width = System.Math.Clamp(System.Math.Ceiling(widest) + 2.6, min, max);
        }
    }

    private static double TextWidth(string text)
    {
        var w = 0.0;
        foreach (var ch in text) w += ch >= '\u2E80' ? 2 : 1;   // CJK、假名、全角标点都是双宽
        return w;
    }
}
