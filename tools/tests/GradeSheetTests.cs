using ClassPoints.Core;

namespace ClassPoints.Tests;

/// <summary>
/// 全校大表（两行表头 + 班级列）的识别与投影。
///
/// 钉住的三件事：班级被认出来并按数字排序、科目标签靠"往左找最近的非空"还原、
/// 投影出来的两列小表能直接喂给成绩解析器（不然成绩导入就得另写一套匹配规则）。
/// </summary>
internal static class GradeSheetTests
{
    public static void Run(Action<string, bool, string> report)
    {
        void Check(string name, bool ok, string detail = "") => report(name, ok, detail);

        // 结构照抄真表：第 1 行分组标题（合并单元格只有左边一格有值），第 2 行子标题，
        // 数据从第 3 行开始，每科后面跟着 4 列名次。
        var grid = new SheetGrid("全校成绩.xlsx", new List<IReadOnlyList<string>>
        {
            new[] { "学校", "班级", "考号", "姓名", "总分(物理)", "", "", "", "", "语文", "", "", "", "", "化学(赋分)", "" },
            new[] { "", "", "", "", "分数", "班名次", "校名次", "全体名次", "市级名次", "分数", "班名次", "校名次", "全体名次", "市级名次", "等级", "分数" },
            new[] { "会宁县第二中学", "18", "3677181814", "朱子涵", "661.5", "1", "1", "10", "5", "122", "12", "17", "49", "38", "A", "92" },
            new[] { "会宁县第二中学", "17", "3677171719", "李晓婷", "661", "1", "2", "11", "6", "121", "5", "21", "59", "45", "A", "92" },
            new[] { "会宁县第二中学", "17", "3677171704", "席夏宁", "654.5", "2", "3", "15", "7", "110.5", "32", "130", "543", "313", "A", "97" },
            new[] { "会宁县第二中学", "9", "3677171705", "九个班的人", "600", "3", "30", "150", "70", "100", "40", "140", "553", "323", "A", "80" },
        });

        if (!GradeSheet.TryParse(grid, out var sheet) || sheet is null)
        {
            Check("能认出全校大表", false, "TryParse 返回 false");
            return;
        }

        Check("能认出全校大表", true);
        Check("班级列、姓名列、考号列都认出来了",
            sheet.ClassColumn == 1 && sheet.NameColumn == 3 && sheet.NoColumn == 2,
            $"{sheet.ClassColumn}/{sheet.NameColumn}/{sheet.NoColumn}");
        Check("数据从第 3 行开始（两行表头）", sheet.FirstDataRow == 2, sheet.FirstDataRow.ToString());

        Check("班级去重并按数字排（9 在 17 前面，不是按字符串）",
            string.Join(",", sheet.Classes) == "9,17,18", string.Join(",", sheet.Classes));

        var labels = string.Join("|", sheet.Subjects.Select(s => s.Label));
        Check("科目按分组标题还原（合并单元格往左找最近的）",
            labels == "总分(物理)|语文|化学(赋分)", labels);
        Check("每一科取的是「分数」那一列，不是等级/名次",
            sheet.Subjects[2].Column == 15 && sheet.ScoreOf(2, sheet.Subjects[2]) == "92",
            $"col={sheet.Subjects[2].Column}");

        Check("按班级数人数", sheet.CountIn("17") == 2 && sheet.CountIn("9") == 1, sheet.CountIn("17").ToString());

        // 跨班搜索：老师在"17 班"这一屏里要能把 18 班的一个人加进来
        var hits = sheet.Search("朱子").ToList();
        Check("跨班按姓名搜到人", hits.Count == 1 && sheet.ClassOf(hits[0]) == "18",
            hits.Count == 1 ? sheet.ClassOf(hits[0]) : hits.Count.ToString());
        Check("搜索空关键字不返回任何人", !sheet.Search("").Any());

        // 投影：成绩导入直接把这张两列表交给 ScoreSheetParser，匹配规则只有一套
        var projected = sheet.Project("17", sheet.Subjects[1]);
        Check("投影出表头 + 该班的人", projected.RowCount == 3, projected.RowCount.ToString());
        Check("投影的第二列是该科分数",
            projected.Cell(1, 0) == "李晓婷" && projected.Cell(1, 1) == "121",
            projected.Cell(1, 0) + "/" + projected.Cell(1, 1));

        var students = new List<Student>
        {
            new() { Id = "s1", Name = "李晓婷" },
            new() { Id = "s2", Name = "席夏宁" },
        };
        var parsed = ScoreSheetParser.Parse(projected, students);
        Check("投影表能直接过着名匹配（不用另写一套规则）",
            parsed.Rows.Count(r => r.Kind == ScoreMatchKind.Matched) == 2, parsed.SummaryText);

        // 普通两列表不能被误认成全校大表，否则名单导入会走错分支
        var plain = new SheetGrid("名单.xlsx", new List<IReadOnlyList<string>>
        {
            new[] { "姓名", "学号" },
            new[] { "王小明", "01" },
        });
        Check("普通两列名单不会被误判成全校大表", !GradeSheet.TryParse(plain, out _));

        // ---- 单行表头：学校下发的成绩单多数是这个形状（班级列 + 每科一列）----
        // 以前只认两行表头，这种表整个走普通导入：名单页没有班级筛选，
        // 成绩页把"总分"当唯一一列，老师问的"班级筛选呢"就是这个。
        var single = new SheetGrid("高一期末考试成绩单.xls", new List<IReadOnlyList<string>>
        {
            new[] { "学校", "班级", "考号", "姓名", "总分", "语文", "数学" },
            new[] { "会宁二中", "17", "21170104", "张权峰", "878", "100", "147" },
            new[] { "会宁二中", "18", "21180112", "李雅博", "852", "125", "135" },
            new[] { "会宁二中", "17", "21170107", "刘阳", "864", "102", "150" },
        });

        Check("单行表头的成绩单也能认出来（班级筛选不再缺席）",
            GradeSheet.TryParse(single, out var flat) && flat is not null);
        if (flat is not null)
        {
            Check("单行表头：数据从表头下一行开始", !flat.TwoRowHeader && flat.FirstDataRow == 1,
                $"{flat.TwoRowHeader}/{flat.FirstDataRow}");
            Check("单行表头：班级列 / 姓名列", flat.ClassColumn == 1 && flat.NameColumn == 3,
                $"{flat.ClassColumn}/{flat.NameColumn}");
            var flatLabels = string.Join("|", flat.Subjects.Select(s => s.Label));
            Check("单行表头：科目就是表头本身，且排除学校/考号这类文字列",
                flatLabels == "总分|语文|数学", flatLabels);
            Check("单行表头：分数取的是本行本列", flat.ScoreOf(1, flat.Subjects[0]) == "878");
            Check("单行表头：按班级数人数", flat.CountIn("17") == 2 && flat.CountIn("18") == 1,
                flat.CountIn("17").ToString());
        }

        // ---- 标题行 + 两行表头 + 「行政班」+「赋分/原始分」----
        // 现场那份「学生选科组合排名(赋分-物理类).xlsx」：标题行占第 0 行，
        // 班级列写的是"行政班"，分数列写的是"赋分 / 原始分"。
        // 旧逻辑三条都不认，于是整表落到普通成绩单分支：姓名列取成"学校"、
        // 分数列取成考号，预览里一排「分数无法识别」，满分被猜成 2147483647。
        var ranked = new SheetGrid("学生选科组合排名(赋分-物理类).xlsx", new List<IReadOnlyList<string>>
        {
            new[] { "学生选科组合排名(赋分-物理类)", "", "", "", "", "", "", "", "" },
            new[] { "学校", "行政班", "姓名", "考号", "选科", "总分", "总分", "总分", "总分" },
            new[] { "学校", "行政班", "姓名", "考号", "选科", "赋分", "原始分", "组合全体名次", "组合班名次" },
            new[] { "会宁县第二中学", "高一17班", "刘阳", "6212250701", "物化生", "685.5", "673.5", "1", "1" },
            new[] { "会宁县第二中学", "高一18班", "冉雅婷", "6212250709", "物化生", "682.5", "658.5", "2", "1" },
        });

        Check("带标题行、写「行政班」的表也认得出", GradeSheet.TryParse(ranked, out var skip) && skip is not null);
        if (skip is not null)
        {
            Check("跳过标题行：表头在第 1 行、数据从第 3 行开始",
                skip.HeaderRow == 1 && skip.FirstDataRow == 3, $"{skip.HeaderRow}/{skip.FirstDataRow}");
            Check("「行政班」认成班级列", skip.ClassColumn == 1, skip.ClassColumn.ToString());

            var only = skip.Subjects[0];
            Check("同一科有赋分和原始分时只留一列，取赋分",
                skip.Subjects.Count == 1 && only.Column == 5, $"{skip.Subjects.Count}/{only.Column}");
            Check("投影出的是赋分那一列", skip.ScoreOf(3, only) == "685.5", skip.ScoreOf(3, only));
        }

        // 兜底路径（没有班级列，只能猜姓名/分数）也不许把标题行当表头
        Check("标题行不会被当成表头行",
            ScoreSheetParser.DetectHeaderRow(ranked) == 1,
            ScoreSheetParser.DetectHeaderRow(ranked).ToString());
    }
}
