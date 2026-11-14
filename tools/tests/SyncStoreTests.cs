using ClassPoints.Data.Sync;

namespace ClassPoints.Tests;

/// <summary>
/// 同步凭据的存取。测的是「什么该落盘、什么不该」。
///
/// 为什么单独测它：这个文件里存着 refresh token，它是唯一能长期换到访问权的东西。
/// 存错地方的后果不是崩溃，而是安静地把一个可以直接用的凭据留在了磁盘上 ——
/// 这类问题没人会去读代码发现，只有测例能钉住。
/// </summary>
internal static class SyncStoreTests
{
    public static void Run(Action<string, bool, string> check)
    {
        var dir = Path.Combine(Path.GetTempPath(), "cp-syncstore-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "sync.json");
            new SyncStore(path).Save(new SyncCredentials(
                "https://sync.example.com:8443/edge", "cid-1", "access-in-memory", "refresh-1", "ABC12345"));

            // 新实例读，模拟「重启后从文件加载」
            var loaded = new SyncStore(path).Load();
            check("重启后能读回地址（含端口与子路径）",
                loaded?.BaseUrl == "https://sync.example.com:8443/edge", loaded?.BaseUrl ?? "null");
            check("班级 id、班级码、refresh 都还在",
                loaded?.ClassId == "cid-1" && loaded.ClassCode == "ABC12345" && loaded.Refresh == "refresh-1", "");

            var raw = File.ReadAllText(path);
            check("refresh 是密文落盘", !raw.Contains("refresh-1"), "");
            check("access 不落盘（只在内存里活 12 小时）", !raw.Contains("access-in-memory"), "");
            check("默认不是只读", loaded?.ReadOnly == false, loaded?.ReadOnly.ToString() ?? "null");

            // 只读标记必须落盘：重启后界面不能又从"待审核"跳回"可写"，
            // 否则老师会在一个根本没权限的界面上改半天，最后发现一条都没同步上去。
            new SyncStore(path).Save(new SyncCredentials(
                "https://sync.example.com", "cid-1", "a", "refresh-2", "ABC12345", ReadOnly: true));
            check("只读标记能存回来", new SyncStore(path).Load()?.ReadOnly == true, "");

            // 老版本的 sync.json 里没有这个字段 —— 不能因此把老设备当成只读。
            // 做法是把新文件里的 readOnly 行删掉，其余原样保留（refresh 仍是密文）。
            var legacyPath = Path.Combine(dir, "legacy.json");
            new SyncStore(legacyPath).Save(new SyncCredentials(
                "https://sync.example.com", "cid-1", "a", "refresh-3", "ABC12345", ReadOnly: true));
            File.WriteAllLines(legacyPath,
                File.ReadAllLines(legacyPath).Where(line => !line.Contains("\"readOnly\"")));
            var legacy = new SyncStore(legacyPath).Load();
            check("老文件没有 readOnly 字段时按可写处理（不误伤老设备）",
                legacy?.ReadOnly == false && legacy.Refresh == "refresh-3",
                $"readOnly={legacy?.ReadOnly} refresh={(legacy is null ? "null" : "ok")}");

            // 文件不存在 = 没配对，不是异常
            check("没有文件时返回 null", new SyncStore(Path.Combine(dir, "absent.json")).Load() is null, "");

            // 损坏的文件同样当成没配对：宁可让老师重新配一次，也不要每次启动都抛
            var broken = Path.Combine(dir, "broken.json");
            File.WriteAllText(broken, "{ 这不是 json");
            check("文件损坏时返回 null 而不是抛异常", new SyncStore(broken).Load() is null, "");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* 临时目录，删不掉无所谓 */ }
        }
    }
}
