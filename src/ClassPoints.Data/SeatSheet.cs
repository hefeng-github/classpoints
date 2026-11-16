using System.Text;
using ClassPoints.Core;
using ClosedXML.Excel;

namespace ClassPoints.Data;

/// <summary>座次表导出的一行：座位号 + 坐在这儿的人。</summary>
public sealed record SeatChartEntry(int Seat, string Name, string? StudentNo, int Points, bool Pinned);

/// <summary>
/// 一张座次表的全部材料。做成"把数据装进来"，导出器自己去查库的话，
/// 界面显示的和导出的就会有两份真相。
/// </summary>
public sealed record SeatChart(
    string ClassName,
    SeatLayout Layout,
    IReadOnlyList<SeatChartEntry> Seats,
    IReadOnlyList<SeatChartEntry> Unseated,
    DateTime ExportedAt);

/// <summary>
/// 座次表导出：Excel（给打印/贴在讲台上）和 SVG（给课件/公众号）。
///
/// 两张表都不含"给程序再读回来"的隐藏结构：列就是列、空位就是空位，
/// 老师手工改完再按布局导入，坐标仍然对得上。
/// </summary>
public static class SeatSheetBuilder
{
    private static readonly XLColor HeaderBg = XLColor.FromHtml("#1C222B");
    private static readonly XLColor HeaderFg = XLColor.FromHtml("#F2F5F9");
    private static readonly XLColor SeatBg = XLColor.FromHtml("#FFFFFF");
    private static readonly XLColor SeatStroke = XLColor.FromHtml("#C3CDDA");
    private static readonly XLColor EmptyBg = XLColor.FromHtml("#F1F4F8");
    private static readonly XLColor MutedFg = XLColor.FromHtml("#5B6675");
    private static readonly XLColor PodiumBg = XLColor.FromHtml("#E3F0FC");

    public static byte[] BuildExcel(SeatChart chart)
    {
        using var wb = new XLWorkbook();
        BuildChartSheet(wb, chart);
        BuildListSheet(wb, chart);

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    public static void SaveExcel(SeatChart chart, string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllBytes(path, BuildExcel(chart));
    }

    private static void BuildChartSheet(XLWorkbook wb, SeatChart chart)
    {
        var ws = wb.Worksheets.Add("座次表");
        var layout = chart.Layout;
        var width = layout.Groups * layout.PerGroup + layout.Aisles;

        // 标题与说明
        ws.Cell(1, 1).Value = chart.ClassName + " 座次表";
        ws.Range(1, 1, 1, width).Merge();
        ws.Cell(1, 1).Style.Font.Bold = true;
        ws.Cell(1, 1).Style.Font.FontSize = 18;
        ws.Cell(1, 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

        ws.Cell(2, 1).Value = $"{layout}（共 {layout.Capacity} 座） · 导出 {chart.ExportedAt:yyyy-MM-dd HH:mm}";
        ws.Range(2, 1, 2, width).Merge();
        ws.Cell(2, 1).Style.Font.FontColor = MutedFg;
        ws.Cell(2, 1).Style.Font.FontSize = 10;
        ws.Cell(2, 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

        // 讲台
        ws.Cell(3, 1).Value = "讲　台";
        ws.Range(3, 1, 3, width).Merge();
        ws.Range(3, 1, 3, width).Style.Fill.SetBackgroundColor(PodiumBg);
        ws.Range(3, 1, 3, width).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        ws.Cell(3, 1).Style.Font.Bold = true;
        ws.Cell(3, 1).Style.Font.FontColor = MutedFg;
        ws.Row(3).Height = 22;

        // 每一排先算好：第 0 列放「N 排」，之后是组内座位，组之间插一条窄走廊。
        var bySeat = chart.Seats.ToDictionary(e => e.Seat);
        var firstRow = 5;
        for (var row = 1; row <= layout.Rows; row++)
        {
            var sheetRow = firstRow + (row - 1) * 1;
            ws.Cell(sheetRow, 1).Value = $"{row} 排";
            ws.Cell(sheetRow, 1).Style.Font.FontColor = MutedFg;
            ws.Cell(sheetRow, 1).Style.Font.FontSize = 9;
            ws.Cell(sheetRow, 1).Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
            ws.Row(sheetRow).Height = 34;

            var col = 2;
            for (var g = 0; g < layout.Groups; g++)
            {
                if (g > 0)
                {
                    ws.Column(col).Width = 3.2;
                    ws.Cell(sheetRow, col).Style.Fill.SetBackgroundColor(EmptyBg);
                    col++;
                }

                for (var c = 0; c < layout.PerGroup; c++)
                {
                    ws.Column(col).Width = 11.5;
                    if (layout.SeatNo(row, g * layout.PerGroup + c + 1) is int seat)
                    {
                        var cell = ws.Cell(sheetRow, col);
                        if (bySeat.TryGetValue(seat, out var entry))
                        {
                            cell.Value = string.IsNullOrWhiteSpace(entry.StudentNo)
                                ? $"{entry.Name}\n{entry.Points} 分"
                                : $"{entry.Name}\n{entry.StudentNo}";
                            cell.Style.Font.Bold = true;
                            cell.Style.Fill.SetBackgroundColor(SeatBg);
                        }
                        else
                        {
                            cell.Value = "空";
                            cell.Style.Font.FontColor = MutedFg;
                            cell.Style.Fill.SetBackgroundColor(EmptyBg);
                        }

                        cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                        cell.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
                        cell.Style.Alignment.WrapText = true;
                        cell.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                        cell.Style.Border.OutsideBorderColor = SeatStroke;
                    }
                    col++;
                }
            }
        }

        // 未排座与约束说明
        var footer = firstRow + layout.Rows + 1;
        ws.Cell(footer, 1).Value = chart.Unseated.Count == 0
            ? "未排座：无"
            : "未排座：" + string.Join("、", chart.Unseated.Select(e => e.Name));
        ws.Range(footer, 1, footer, width).Merge();
        ws.Cell(footer, 1).Style.Font.FontColor = MutedFg;
        ws.Cell(footer, 1).Style.Font.FontSize = 10;
        ws.Cell(footer, 1).Style.Alignment.WrapText = true;

        ws.SheetView.FreezeRows(4);
    }

    private static void BuildListSheet(XLWorkbook wb, SeatChart chart)
    {
        var ws = wb.Worksheets.Add("名单");
        string[] headers = ["排", "列", "座位号", "姓名", "学号", "本周期积分", "固定"];

        for (var i = 0; i < headers.Length; i++)
        {
            var cell = ws.Cell(1, i + 1);
            cell.Value = headers[i];
            cell.Style.Font.Bold = true;
            cell.Style.Font.FontColor = HeaderFg;
            cell.Style.Fill.SetBackgroundColor(HeaderBg);
            cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
        }

        var row = 2;
        foreach (var entry in chart.Seats.OrderBy(e => e.Seat))
        {
            if (chart.Layout.Position(entry.Seat) is not { } pos) continue;
            ws.Cell(row, 1).Value = pos.Row;
            ws.Cell(row, 2).Value = pos.Col;
            ws.Cell(row, 3).Value = entry.Seat;
            ws.Cell(row, 4).Value = entry.Name;
            ws.Cell(row, 5).Value = entry.StudentNo ?? "";
            ws.Cell(row, 6).Value = entry.Points;
            ws.Cell(row, 7).Value = entry.Pinned ? "是" : "";
            row++;
        }

        foreach (var entry in chart.Unseated)
        {
            ws.Cell(row, 4).Value = entry.Name;
            ws.Cell(row, 5).Value = entry.StudentNo ?? "";
            ws.Cell(row, 6).Value = entry.Points;
            row++;
        }

        SheetFit.Columns(ws, headers.Length, new[] { 5, 5, 8, 11, 15, 13, 7 });
        ws.SheetView.FreezeRows(1);
    }

    // ---------------- SVG ----------------

    public static string BuildSvg(SeatChart chart)
    {
        const int tileW = 124;
        const int tileH = 66;
        const int gap = 8;
        const int aisle = 26;
        const int labelW = 58;
        const int pad = 24;

        var layout = chart.Layout;
        var gridW = labelW + layout.Columns * tileW + layout.Aisles * aisle + (layout.Columns - 1) * gap;
        var width = gridW + pad * 2;
        var height = pad * 2 + 40 + 26 + 24 + layout.Rows * (tileH + gap) + 30;

        var bySeat = chart.Seats.ToDictionary(e => e.Seat);
        var sb = new StringBuilder();
        sb.AppendLine($"""<svg xmlns="http://www.w3.org/2000/svg" width="{width}" height="{height}" viewBox="0 0 {width} {height}" font-family="Microsoft YaHei, Segoe UI, sans-serif">""");
        sb.AppendLine($"""<rect width="{width}" height="{height}" fill="#ffffff"/>""");
        sb.AppendLine($"""<text x="{pad}" y="{pad + 18}" font-size="20" font-weight="600" fill="#12161d">{Escape(chart.ClassName)} 座次表</text>""");
        sb.AppendLine($"""<text x="{pad}" y="{pad + 38}" font-size="11" fill="#5b6675">{Escape(layout.ToString())}（共 {layout.Capacity} 座） · {chart.ExportedAt:yyyy-MM-dd HH:mm}</text>""");

        var top = pad + 52;
        sb.AppendLine($"""<rect x="{pad}" y="{top}" width="{gridW}" height="26" rx="6" fill="#e3f0fc"/>""");
        sb.AppendLine($"""<text x="{pad + gridW / 2}" y="{top + 17}" font-size="12" text-anchor="middle" fill="#5b6675">讲　台</text>""");

        var gridTop = top + 26 + 16;
        for (var row = 1; row <= layout.Rows; row++)
        {
            var y = gridTop + (row - 1) * (tileH + gap);
            sb.AppendLine($"""<text x="{pad + labelW - 8}" y="{y + tileH / 2 + 4}" font-size="10" text-anchor="end" fill="#5b6675">{row} 排</text>""");

            for (var col = 1; col <= layout.Columns; col++)
            {
                if (layout.SeatNo(row, col) is not int seat) continue;

                var group = layout.GroupOf(col);
                var x = pad + labelW + (col - 1) * (tileW + gap) + group * aisle;
                var filled = bySeat.TryGetValue(seat, out var entry);
                var fill = filled ? "#ffffff" : "#f1f4f8";
                var stroke = filled ? "#c3cdda" : "#e1e6ed";

                sb.AppendLine($"""<rect x="{x}" y="{y}" width="{tileW}" height="{tileH}" rx="9" fill="{fill}" stroke="{stroke}"/>""");

                if (filled)
                {
                    sb.AppendLine($"""<text x="{x + tileW / 2}" y="{y + 27}" font-size="14" font-weight="600" text-anchor="middle" fill="#12161d">{Escape(entry!.Name)}</text>""");
                    var sub = string.IsNullOrWhiteSpace(entry.StudentNo)
                        ? $"{entry.Points} 分"
                        : $"{Escape(entry.StudentNo)} · {entry.Points} 分";
                    sb.AppendLine($"""<text x="{x + tileW / 2}" y="{y + 46}" font-size="10" text-anchor="middle" fill="#5b6675">{sub}</text>""");
                }
                else
                {
                    sb.AppendLine($"""<text x="{x + tileW / 2}" y="{y + 27}" font-size="12" text-anchor="middle" fill="#5b6675">空</text>""");
                    sb.AppendLine($"""<text x="{x + tileW / 2}" y="{y + 46}" font-size="9" text-anchor="middle" fill="#8a95a5">{row} 排 {col} 座</text>""");
                }
            }
        }

        var footerY = gridTop + layout.Rows * (tileH + gap) + 8;
        var footer = chart.Unseated.Count == 0
            ? "未排座：无"
            : "未排座：" + string.Join("、", chart.Unseated.Select(e => e.Name));
        sb.AppendLine($"""<text x="{pad}" y="{footerY}" font-size="10" fill="#5b6675">{Escape(footer)}</text>""");
        sb.AppendLine("</svg>");
        return sb.ToString();
    }

    private static string Escape(string text) => text
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;")
        .Replace("\"", "&quot;");
}
