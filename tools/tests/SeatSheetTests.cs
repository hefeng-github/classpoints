using ClassPoints.Core;
using ClassPoints.Data;

namespace ClassPoints.Tests;

/// <summary>
/// 座次表导出：Excel 的格子位置要和教室对得上，SVG 要是能打开的完整文档。
///
/// 这张表是拿去打印、贴在讲台上的，位置错一格就是"把张三的名字贴到李四桌上"。
/// 所以不只检查"生成成功"，还把导出的 xlsx 读回来核对工作表名、标题行、讲台行、
/// 第 1 排第 1 座的姓名。
/// </summary>
internal static class SeatSheetTests
{
    public static void Run(Action<string, bool, string> report)
    {
        void Check(string name, bool ok, string detail = "") => report(name, ok, detail);

        var dir = Path.Combine(Path.GetTempPath(), "cp-sheet-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);

        try
        {
            var layout = SeatLayout.Create(2, 2, 2); // 2 排 × 2 组 × 2 座 = 8 个座位
            var chart = new SeatChart(
                "初二（3）班",
                layout,
                [
                    new SeatChartEntry(1, "王小明", "01", 120, false),
                    new SeatChartEntry(2, "李思远", "02", 80, true),
                    new SeatChartEntry(3, "张雨欣", "03", 60, false),
                    new SeatChartEntry(5, "陈子涵", "04", 40, false),
                ],
                [new SeatChartEntry(0, "转学生", null, 0, false)],
                new DateTime(2026, 11, 15, 9, 30, 0));

            var path = Path.Combine(dir, "chart.xlsx");
            SeatSheetBuilder.SaveExcel(chart, path);

            Check("导出文件真的落盘了", File.Exists(path) && new FileInfo(path).Length > 0);

            var sheet = ExcelReader.Read(path, 0);
            Check("第一张表叫「座次表」", sheet.Name == "座次表", sheet.Name);
            Check("标题带班级名", sheet.Cell(0, 0).Contains("初二（3）班"), sheet.Cell(0, 0));
            Check("讲台在第 3 行", sheet.Cell(2, 0).Contains("讲"), sheet.Cell(2, 0));

            // 第 5 行是第 1 排；第 1 列是「N 排」，第 2 列是第 1 座。
            Check("第 1 排第 1 座是王小明", sheet.Cell(4, 1).Contains("王小明"), sheet.Cell(4, 1));
            Check("第 1 排第 2 座是李思远", sheet.Cell(4, 2).Contains("李思远"), sheet.Cell(4, 2));
            Check("第 2 排第 1 座是陈子涵", sheet.Cell(5, 1).Contains("陈子涵"), sheet.Cell(5, 1));

            // 2 组 × 每组 2 座：A 列是排号，B/C 是第 1 组，D 列是走廊，E/F 是第 2 组。
            Check("组间走廊占了一列（第 4 列为空）", sheet.Cell(4, 3).Trim().Length == 0, sheet.Cell(4, 3));
            Check("第 1 排第 2 组第 1 座在走廊右侧（张雨欣）", sheet.Cell(4, 4).Contains("张雨欣"), sheet.Cell(4, 4));

            var list = ExcelReader.Read(path, 1);
            Check("第二张表叫「名单」", list.Name == "名单", list.Name);
            Check("名单表头含「座位号」", list.Cell(0, 2).Contains("座位号"), list.Cell(0, 2));
            Check("未排座的人也在名单里",
                Enumerable.Range(1, 10).Any(r => list.Cell(r, 3).Contains("转学生")));

            var svg = SeatSheetBuilder.BuildSvg(chart);
            Check("SVG 是完整文档", svg.StartsWith("<svg") && svg.TrimEnd().EndsWith("</svg>"));
            Check("SVG 里有姓名与讲台", svg.Contains("王小明") && svg.Contains("讲　台"));
            Check("SVG 里没有未转义的裸 &",
                !System.Text.RegularExpressions.Regex.IsMatch(svg, "&(?!amp;|lt;|gt;|quot;|#)"));
        }
        finally
        {
            try
            {
                Directory.Delete(dir, true);
            }
            catch (Exception)
            {
                // 临时目录清不掉不影响结论。
            }
        }
    }
}
