using ClassPoints.Core;
using ClassPoints.Data;

namespace ClassPoints.Tests;

/// <summary>
/// 成绩导出（XLSX）的版式测试。
///
/// 起因：老师把导出的表直接发到群里，总排名页「姓名」列窄到只剩一个字，
/// 底下还多挂一行斜体说明。两件事都不影响数据，但一张要给人看的表长成这样就不合格。
///
/// 钉住三件事：
/// ① 总排名页只有表头和数据，没有尾巴上的口径说明（说明留在「参数说明」页）；
/// ② 列宽按内容量出来 —— 汉字算两个字符宽，三字姓名要放得下；
/// ③ 允许换行的长表头不许把整列撑到上限（以前 AdjustToContents 就会这样）。
/// </summary>
internal static class ExcelExportTests
{
    public static void Run(Action<string, bool, string> report)
    {
        void Check(string name, bool ok, string detail = "") => report(name, ok, detail);

        var dir = Path.Combine(Path.GetTempPath(), "classpoints-export-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "成绩.xlsx");
            File.WriteAllBytes(path, WorkbookBuilder.Build(BuildBundle()));

            using var wb = new ClosedXML.Excel.XLWorkbook(path);

            // ---- 总排名 ----
            var rank = wb.Worksheet("总排名");
            var lastRow = rank.LastRowUsed()!.RowNumber();
            Check("总排名没有尾巴说明行（表头 + 8 个学生）", lastRow == 9, "末行 " + lastRow);

            var allText = string.Join("|", rank.CellsUsed().Select(c => c.GetFormattedString()));
            Check("口径说明已经从总排名页移除", !allText.Contains("不可与考试成绩直接比较", StringComparison.Ordinal));

            var nameWidth = rank.Column(2).Width;
            Check("姓名列放得下三个汉字", nameWidth >= 9, nameWidth.ToString("0.0"));

            var absentText = rank.Cell(9, 7).GetFormattedString();
            var absentWidth = rank.Column(7).Width;
            Check("缺考那一栏叫「备注」，且放得下缺考说明",
                rank.Cell(1, 7).GetFormattedString() == "备注" && absentWidth >= 12 && absentText.Length > 0,
                $"{rank.Cell(1, 7).GetFormattedString()} / {absentText} / 宽 {absentWidth:0.0}");

            var totalWidth = rank.Column(6).Width;
            Check("总评列没被长表头撑成巨宽", totalWidth is >= 12 and <= 26, totalWidth.ToString("0.0"));

            var filter = rank.AutoFilter;
            Check("筛选范围只框住表头和数据", filter.IsEnabled && filter.Range.LastRow().RowNumber() == lastRow,
                filter.IsEnabled ? filter.Range.RangeAddress.ToString() : "未启用");

            // ---- 成绩明细：长表头靠换行，不靠加宽 ----
            var detail = wb.Worksheet("成绩明细");
            var detailNameWidth = detail.Column(1).Width;
            Check("成绩明细的姓名列同样放得下三个汉字", detailNameWidth >= 9, detailNameWidth.ToString("0.0"));
            Check("成绩明细表头行够两行高", detail.Row(1).Height >= 36, detail.Row(1).Height.ToString("0.0"));
            var examWidth = detail.Column(2).Width;
            Check("单场成绩列宽度容得下两行表头", examWidth is >= 18 and <= 26, examWidth.ToString("0.0"));

            // 缺考格只写「缺考」，政策由最后一列统一说明；否则这一列会比别的场次宽一截
            Check("缺考格不重复写政策", detail.Cell(9, 2).GetFormattedString() == "缺考",
                detail.Cell(9, 2).GetFormattedString());
            Check("各场成绩列按统一宽度", System.Math.Abs(detail.Column(2).Width - 20) < 0.1,
                detail.Column(2).Width.ToString("0.0"));

            // 「备注」只写给真缺考的那一行，其他人留空
            var absentNote = detail.Cell(9, 6).GetFormattedString();
            Check("成绩明细的备注只写在缺考那一行",
                absentNote.Contains("缺考 · 期中", StringComparison.Ordinal) && detail.Cell(2, 6).GetFormattedString().Length == 0,
                absentNote);

            // ---- 参数说明：口径注还在这里 ----
            var info = wb.Worksheet("参数说明");
            var infoText = string.Join("|", info.CellsUsed().Select(c => c.GetFormattedString()));
            Check("参数说明页仍保留口径那一句", infoText.Contains("不可与考试成绩直接比较", StringComparison.Ordinal));

            // ---- 全班没人缺考：两页都不该留一列空的「备注」----
            var cleanPath = Path.Combine(dir, "成绩-无缺考.xlsx");
            File.WriteAllBytes(cleanPath, WorkbookBuilder.Build(BuildBundle(absent: false)));
            using (var clean = new ClosedXML.Excel.XLWorkbook(cleanPath))
            {
                static List<string> Heads(ClosedXML.Excel.IXLWorksheet ws) => Enumerable
                    .Range(1, 12)
                    .Select(c => ws.Cell(1, c).GetFormattedString())
                    .Where(s => s.Length > 0)
                    .ToList();

                var rankHeads = Heads(clean.Worksheet("总排名"));
                Check("没人缺考时总排名不出「备注」列", !rankHeads.Contains("备注"), string.Join("|", rankHeads));

                var detailHeads = Heads(clean.Worksheet("成绩明细"));
                Check("没人缺考时成绩明细也不出「备注」列", !detailHeads.Contains("备注"), string.Join("|", detailHeads));
            }
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (Exception) { /* 临时目录删不掉不影响结论 */ }
        }
    }

    /// <summary>给 `--dump-export=路径` 用：把同一份样例导出来，方便人眼看一下版式。</summary>
    public static void SaveSample(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllBytes(path, WorkbookBuilder.Build(BuildBundle()));
    }

    /// <summary>8 个学生、1 场考试、1 个缺考 —— 和老师报上来的那张截图同形。
    /// <paramref name="absent"/> 传 false 就是"全班都考了"的普通表，用来钉住"没缺考不出备注列"。</summary>
    private static ExportBundle BuildBundle(bool absent = true)
    {
        var names = new[] { "陈一", "孙二", "林三", "周四", "何五", "吴六", "钱七", "赵四" };
        var students = names.Select((n, i) => new Student
        {
            Id = "s" + (i + 1),
            Name = n,
            SeatNo = i + 1,
            Active = true,
        }).ToList();

        var points = new Dictionary<string, int>
        {
            ["s1"] = 114, ["s2"] = 88, ["s3"] = 86, ["s4"] = 82,
            ["s5"] = 76, ["s6"] = 70, ["s7"] = 67, ["s8"] = 16,
        };

        var exams = new List<Exam>
        {
            new() { Id = "e1", PeriodId = "p1", Name = "期中", FullScore = 100, ExamDate = "2026-10-20" },
        };

        var raw = new[] { 95.0, 91, 88, 83, 76, 58, 64 };
        var scores = raw.Select((v, i) => new ExamScore { ExamId = "e1", StudentId = "s" + (i + 1), Score = v })
                        .ToList();
        scores.Add(absent
            ? new ExamScore { ExamId = "e1", StudentId = "s8", Absent = true }
            : new ExamScore { ExamId = "e1", StudentId = "s8", Score = 72 });

        var scheme = new Scheme { Id = "sc", ExamWeight = 0.7, DailyWeight = 0.3 };
        var ranking = RankingCalculator.Compute(students, points, exams, scores,
            new Dictionary<string, double>(), scheme, AbsencePolicy.Redistribute);

        var period = new Period { Id = "p1", Name = "第 1 周期", TermKey = "2026-Q1" };

        return new ExportBundle(
            ranking,
            exams,
            scores,
            Array.Empty<Entry>(),
            scheme,
            period,
            AbsencePolicy.Redistribute,
            "2026-Q1",
            students,
            new Dictionary<string, double>(),
            "张老师",
            new DateTime(2026, 11, 16, 12, 0, 0));
    }
}
