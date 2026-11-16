using ClassPoints.Data;

namespace ClassPoints.Tests;

/// <summary>
/// 「本机重置」能不能真的把库清掉。
///
/// 报障长这样：点「本机重置」→ 弹「重置失败：The process cannot access the file because it is
/// being used by another process.」。删不掉，然后改名也删不掉，老师就被卡在"重置不了"上。
///
/// 根因不在删除那一步，而在备份里：`Database.Scalar(OpenRead(), …)` 把连接对象随手丢掉、
/// 从没 Dispose，它就永远挂在连接池里算"使用中"，连 ClearAllPools 都收不回来 ——
/// 而重置之前恰好要先做一次备份。所以这两件事必须一起测：
/// ① 备份过之后，清池就该能删库；② 真删不掉时，就地清空这条路要能走通。
/// </summary>
internal static class ResetTests
{
    public static void Run(Action<string, bool, string> report)
    {
        void Check(string name, bool ok, string detail = "") => report(name, ok, detail);

        var dir = Path.Combine(Path.GetTempPath(), "cp-reset-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var (backupOk, backupDetail) = BackupThenDelete(dir);
            Check("备份过之后能删库（清池就把句柄放掉了）", backupOk, backupDetail);

            var (wipeOk, wipeDetail) = WipeInPlace(dir);
            Check("就地清空 = 刚装好的样子（重新建表 + 重新播种）", wipeOk, wipeDetail);

            var (busyOk, busyDetail) = WipeWhileBusy(dir);
            Check("库被别的连接占着时也能就地清空", busyOk, busyDetail);
        }
        finally
        {
            try { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); } catch (Exception) { }
            try { Directory.Delete(dir, recursive: true); } catch (Exception) { }
        }
    }

    // ---------------- ① 备份之后必须能删库 ----------------

    private static (bool Ok, string Detail) BackupThenDelete(string dir)
    {
        var path = Path.Combine(dir, "backup-case.db");
        var backupDir = Path.Combine(dir, "backups");
        Directory.CreateDirectory(backupDir);

        var db = new Database(path);
        db.Initialize();
        db.SetSetting("backup_dir", backupDir);

        using (var conn = db.Open())
            Database.Exec(conn,
                "INSERT INTO students (id, name, updated_at, created_at) VALUES ('s1', '张三', 1, 1)");

        try
        {
            // 和「本机重置」一模一样的顺序：先备份，再清池，再删库
            new BackupService(db).BackupNow("before-reset", null);
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            File.Delete(path);
            return (true, "");
        }
        catch (Exception ex)
        {
            return (false, "备份后删库失败：" + ex.GetType().Name + " — " + ex.Message);
        }
    }

    // ---------------- ② 删不掉时，就地清空要顶得上 ----------------

    private static (bool Ok, string Detail) WipeInPlace(string dir)
    {
        var path = Path.Combine(dir, "wipe-case.db");
        var db = new Database(path);
        db.Initialize();

        using (var conn = db.Open())
            Database.Exec(conn,
                "INSERT INTO students (id, name, updated_at, created_at) VALUES ('s1', '张三', 1, 1)");
        db.SetSetting("class_name", "初二（3）班");

        db.WipeInPlace();

        // 清空之后当场看：表已经没了，结构版本归零
        using (var conn = db.Open())
        {
            var tables = Convert.ToInt32(Database.Scalar(conn,
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN ('students','entries','periods')"));
            if (tables != 0) return (false, "清空后还剩 " + tables + " 张表");

            var version = Convert.ToInt32(Database.Scalar(conn, "PRAGMA user_version"));
            if (version != 0) return (false, "结构版本没归零：" + version);
        }

        // 再走一次启动流程（新连接、新对象），应当等同于刚装好
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var fresh = new Database(path);
        fresh.Initialize();

        using (var conn = fresh.Open())
        {
            var students = Convert.ToInt32(Database.Scalar(conn, "SELECT COUNT(*) FROM students"));
            if (students != 0) return (false, "重新初始化后还有 " + students + " 个学生");

            var version = Convert.ToInt32(Database.Scalar(conn, "PRAGMA user_version"));
            if (version != Sql.SchemaVersion) return (false, "重新初始化后结构版本不对：" + version);

            // 播种要重来：预设加分项回来了，向导里才能直接开始用
            var rules = Convert.ToInt32(Database.Scalar(conn, "SELECT COUNT(*) FROM rules"));
            if (rules == 0) return (false, "预设规则没有重新播种");
        }

        // 播种会把 class_name 写回空串（不是 "初二（3）班"），用户填的内容确实没了
        if (!string.IsNullOrEmpty(fresh.GetSetting("class_name"))) return (false, "班级名还留着");

        return (true, "");
    }

    // ---------------- ③ 库被占着（连接没释放）时也要能清 ----------------

    private static (bool Ok, string Detail) WipeWhileBusy(string dir)
    {
        var path = Path.Combine(dir, "busy-case.db");
        var db = new Database(path);
        db.Initialize();

        // 故意留一条打开的连接 —— 这正是"删不掉"的现实：文件被占着。
        // 就地清空只走 SQL，不需要拿文件独占权，所以照样要成功。
        using var busy = db.Open();

        try
        {
            db.WipeInPlace();
        }
        catch (Exception ex)
        {
            return (false, "有别的连接开着时清空失败：" + ex.GetType().Name + " — " + ex.Message);
        }

        var left = Convert.ToInt32(Database.Scalar(busy,
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN ('students','entries','periods')"));
        if (left != 0) return (false, "清空后表还在");

        return (true, "");
    }
}
