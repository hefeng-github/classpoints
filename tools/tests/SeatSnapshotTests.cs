using ClassPoints.Core;
using ClassPoints.Data;

namespace ClassPoints.Tests;

/// <summary>
/// 座位快照的存取与上限。
///
/// 快照是"排过的几版方案"，出问题最烦人的地方是恢复回来少了几个人、或者第 31 份
/// 悄悄把第 1 份挤掉了却没提示。这里钉住三件事：座位图原样存取、最新的排在最前、
/// 超过 30 份自动删旧的（不是删新的）。
/// </summary>
internal static class SeatSnapshotTests
{
    public static void Run(Action<string, bool, string> report)
    {
        void Check(string name, bool ok, string detail = "") => report(name, ok, detail);

        var dir = Path.Combine(Path.GetTempPath(), "cp-snap-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);

        try
        {
            var db = new Database(Path.Combine(dir, "t.db"));
            db.Initialize();
            var repo = new SeatSnapshotRepository(db);
            var layout = SeatLayout.Create(2, 1, 2);

            var first = repo.Save("第一次", layout, new Dictionary<int, string>
            {
                [1] = "a", [2] = "b", [3] = "c",
            });

            Check("存进去能读回来", repo.List().Count == 1 && repo.Latest()?.Id == first.Id);
            Check("布局原样保存", first.LayoutValue == layout, first.LayoutValue.ToString());

            var map = repo.Latest()!.SeatMap();
            Check("座位图原样保存（3 个人、座位号不串位）",
                map.Count == 3 && map[1] == "a" && map[2] == "b" && map[3] == "c",
                string.Join(",", map.Select(kv => kv.Key + ":" + kv.Value)));

            var unnamed = repo.Save("   ", layout, new Dictionary<int, string> { [4] = "d" });
            Check("不给名字时自动按时间命名", unnamed.Name.StartsWith("未命名"), unnamed.Name);
            Check("最新一份排在最前", repo.Latest()!.Id == unnamed.Id);

            repo.Delete(first.Id);
            Check("删除后列表里就没了", repo.List().Count == 1 && repo.List().All(s => s.Id != first.Id));

            // 上限：存 35 份，只留最近 30 份，而且留下的是新的那批。
            var saved = new List<string>();
            for (var i = 0; i < 35; i++)
            {
                Thread.Sleep(2); // created_at 是毫秒：不睡一下同一毫秒内的先后就不稳
                saved.Add(repo.Save($"第 {i} 份", layout, new Dictionary<int, string> { [1] = "x" }).Id);
            }

            var all = repo.List(limit: 50);
            Check("最多保留 30 份快照", all.Count == SeatSnapshotRepository.Keep, all.Count.ToString());
            Check("留下的是最近的 30 份（最新那份还在）",
                all.Any(s => s.Id == saved[^1]) && all.All(s => s.Id != saved[0]));
        }
        finally
        {
            try
            {
                Directory.Delete(dir, true);
            }
            catch (Exception)
            {
                // 清理失败不影响结论：临时目录会被系统清掉。
            }
        }
    }
}
