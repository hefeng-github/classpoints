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
    }
}
