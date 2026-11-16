using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClassPoints.Core;
using Microsoft.Data.Sqlite;

namespace ClassPoints.Data;

public sealed record BackupFile(string Path, DateTime At, long SizeBytes, bool IsPack)
{
    public string Name => System.IO.Path.GetFileName(Path);

    /// <summary>列表的主信息。备份是给人翻的，人先找的是「什么时候那一份」，不是文件名叫什么。</summary>
    public string AtText
    {
        get
        {
            var today = DateTime.Today;
            if (At.Date == today) return $"今天 {At:HH:mm}";
            if (At.Date == today.AddDays(-1)) return $"昨天 {At:HH:mm}";
            return At.ToString("yyyy-MM-dd HH:mm");
        }
    }

    public string SizeText => SizeBytes > 1024 * 1024
        ? $"{SizeBytes / 1024.0 / 1024.0:0.0} MB"
        : $"{SizeBytes / 1024.0:0} KB";

    public string KindText => IsPack ? "整包" : "快照";

    /// <summary>次要信息：类型、大小、文件名。</summary>
    public string DetailText => $"{KindText} · {SizeText} · {Name}";
}

public sealed record RestoreCheck(bool Ok, string Message);

/// <summary>
/// 备份与整包迁移。
/// 备份用 VACUUM INTO 而不是 File.Copy —— 后者可能拷到一个写了一半的 WAL，恢复出来是坏库。
/// </summary>
public sealed class BackupService(Database db)
{
    public string BackupRoot
    {
        get
        {
            var custom = db.GetSetting("backup_dir");
            if (string.IsNullOrWhiteSpace(custom)) return Database.DefaultBackupDir;

            // 老库把默认值写成了绝对路径；认出它就不再当年是"用户自己选的"。
            return Same(custom, Database.BackupRoot) || Same(custom, Database.LegacyBackupRoot)
                ? Database.DefaultBackupDir
                : custom!;
        }
    }

    private static bool Same(string a, string b)
    {
        try
        {
            return DataPaths.SameDir(a, b);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 当前目录 + 历史上用过的几个目录。默认目录换过两次（先是 %LOCALAPPDATA%\backups，
    /// 再是「文档」，现在跟着数据目录走），每换一次都会留下一批旧备份 —— 不列出来就等于丢了。
    /// </summary>
    private List<string> Roots()
    {
        var list = new List<string>();
        foreach (var root in new[]
                 {
                     BackupRoot, Database.BackupRoot, Database.LegacyBackupRoot, Database.DocumentsBackupDir,
                 })
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            if (list.Any(x => Same(x, root))) continue;
            list.Add(root);
        }

        return list;
    }

    /// <summary>
    /// 换成别的目录（空串 = 回到默认）。返回 false 表示这个目录建不出来 ——
    /// 调用方要把失败说出来：存下一个写不进去的路径，之后每次备份都只是"没有报错的失败"。
    /// </summary>
    public bool SetBackupRoot(string? path)
    {
        var target = string.IsNullOrWhiteSpace(path) ? Database.DefaultBackupDir : path.Trim();
        try
        {
            Directory.CreateDirectory(target);
        }
        catch (Exception)
        {
            return false;
        }

        db.SetSetting("backup_dir", string.IsNullOrWhiteSpace(path) ? "" : target);
        return true;
    }

    public long LastBackupAt => long.TryParse(db.GetSetting("last_backup_at"), out var v) ? v : 0;

    public string LastBackupText
    {
        get
        {
            if (LastBackupAt <= 0) return "还没有备份过";
            var days = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - LastBackupAt) / 86_400_000.0;
            var when = Clock.ToLocal(LastBackupAt);
            if (when.Date == DateTime.Today) return $"今天 {when:HH:mm}";
            if (when.Date == DateTime.Today.AddDays(-1)) return $"昨天 {when:HH:mm}";
            return days > 7 ? $"{when:yyyy-MM-dd HH:mm}（已超过 7 天）" : $"{when:yyyy-MM-dd HH:mm}";
        }
    }

    /// <summary>超过 3 天变橙、超过 7 天变红 —— 给非技术用户的提醒比功能重要。</summary>
    public int StalenessLevel
    {
        get
        {
            if (LastBackupAt <= 0) return 2;
            var days = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - LastBackupAt) / 86_400_000.0;
            return days > 7 ? 2 : days > 3 ? 1 : 0;
        }
    }

    public string BackupNow(string reason, ExportBundle? snapshot = null)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var dir = System.IO.Path.Combine(BackupRoot, stamp);
        Directory.CreateDirectory(dir);

        var dbCopy = System.IO.Path.Combine(dir, "classpoints.db");
        VacuumInto(dbCopy);

        if (snapshot is not null)
        {
            try { WorkbookBuilder.Save(snapshot, System.IO.Path.Combine(dir, "积分快照.xlsx")); }
            catch (Exception) { /* 快照失败不影响数据库副本，副本才是命根子 */ }
        }

        var entries = CountEntries();
        using (var conn = db.Open())
            Database.Exec(conn,
                "INSERT INTO backups (at, path, entry_count, ok) VALUES ($at, $p, $c, 1)",
                ("$at", Clock.NowMs()), ("$p", dir), ("$c", entries));

        db.SetSetting("last_backup_at", Clock.NowMs().ToString());
        WriteMarker(dir, reason);
        Cleanup();

        return dir;
    }

    /// <summary>一致性副本。目标文件已存在会抛，所以先删。</summary>
    public void VacuumInto(string targetPath)
    {
        var dir = System.IO.Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        if (File.Exists(targetPath)) File.Delete(targetPath);

        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "VACUUM INTO $path";
        cmd.Parameters.AddWithValue("$path", targetPath);
        cmd.ExecuteNonQuery();
    }

    private void WriteMarker(string dir, string reason)
    {
        try
        {
            File.WriteAllText(System.IO.Path.Combine(dir, "说明.txt"),
                $"班级积分 · 自动备份\n时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}\n原因：{reason}\n\n" +
                "classpoints.db 是一致性数据库副本；积分快照.xlsx 是给人看的。\n恢复：设置 → 备份与恢复 → 选择这个文件。\n",
                Encoding.UTF8);
        }
        catch (Exception) { }
    }

    /// <summary>保留最近 30 天 + 每月 1 号那份。</summary>
    public void Cleanup()
    {
        try
        {
            if (!Directory.Exists(BackupRoot)) return;
            var keep = new HashSet<string>();
            var cutoff = DateTime.Today.AddDays(-30);

            foreach (var d in Directory.GetDirectories(BackupRoot))
            {
                var name = System.IO.Path.GetFileName(d);
                if (!DateTime.TryParseExact(name, "yyyyMMdd-HHmmss", null,
                        System.Globalization.DateTimeStyles.None, out var at))
                {
                    keep.Add(d);
                    continue;
                }
                if (at >= cutoff || at.Day == 1) keep.Add(d);
            }

            foreach (var d in Directory.GetDirectories(BackupRoot))
                if (!keep.Contains(d))
                {
                    try { Directory.Delete(d, recursive: true); } catch (Exception) { }
                }
        }
        catch (Exception) { }
    }

    /// <summary>
    /// 列出备份。除了当前目录，也把历史上用过的几个目录里的列进来 ——
    /// 默认目录换过几次，之前那些备份还躺在那儿，看不见就等于丢了。
    /// </summary>
    public List<BackupFile> List(int take = 40)
    {
        var list = new List<BackupFile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in Roots())
        {
            if (!Directory.Exists(root)) continue;

            foreach (var d in Directory.GetDirectories(root))
            {
                var file = System.IO.Path.Combine(d, "classpoints.db");

                // 备份目录是用户自己选的，里面可能还有别的文件夹（Documents 就是默认值）。
                // 没有库文件的目录不是备份，列出来只会让人以为备份坏了。
                if (!File.Exists(file)) continue;
                if (!seen.Add(file)) continue;

                var info = new DirectoryInfo(d);
                list.Add(new BackupFile(file, info.LastWriteTime, new FileInfo(file).Length, false));
            }

            foreach (var f in Directory.GetFiles(root, "*.zip"))
            {
                if (!seen.Add(f)) continue;
                var fi = new FileInfo(f);
                list.Add(new BackupFile(f, fi.LastWriteTime, fi.Length, true));
            }
        }

        return list.OrderByDescending(x => x.At).Take(take).ToList();
    }

    /// <summary>旧目录里还有多少份（界面上用它提示"这些还在原来的位置"）。</summary>
    public int LegacyCount => CountIn(LegacyRoots);

    /// <summary>当前目录之外、真的还存着东西的那些旧备份目录。</summary>
    public List<string> LegacyRoots =>
        Roots().Skip(1).Where(root =>
        {
            try
            {
                return Directory.Exists(root) &&
                       (Directory.GetDirectories(root).Length > 0 || Directory.GetFiles(root, "*.zip").Length > 0);
            }
            catch (Exception)
            {
                return false;
            }
        }).ToList();

    private static int CountIn(IEnumerable<string> roots)
    {
        var count = 0;
        foreach (var root in roots)
        {
            try
            {
                count += Directory.GetDirectories(root).Length + Directory.GetFiles(root, "*.zip").Length;
            }
            catch (Exception)
            {
                // 目录不可读就少算一份，不值得为它把整个设置页打挂
            }
        }

        return count;
    }

    /// <summary>恢复：先把当前库改名并排（不覆盖），再把候选库拷回来。装错了包还能退。</summary>
    public RestoreCheck Restore(string sourceDbPath)
    {
        if (!File.Exists(sourceDbPath)) return new(false, "文件不存在");

        try
        {
            using (var fs = File.OpenRead(sourceDbPath))
            {
                var head = new byte[16];
                if (fs.Read(head, 0, 16) != 16 || Encoding.ASCII.GetString(head, 0, 15) != "SQLite format 3")
                    return new(false, "这不是一个 SQLite 数据库文件");
            }

            using (var probe = new SqliteConnection($"Data Source={sourceDbPath};Mode=ReadOnly"))
            {
                probe.Open();
                using var cmd = probe.CreateCommand();
                cmd.CommandText = "PRAGMA integrity_check";
                var result = cmd.ExecuteScalar()?.ToString() ?? "";
                if (!result.Equals("ok", StringComparison.OrdinalIgnoreCase))
                    return new(false, $"数据库完整性检查未通过：{result}");

                cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('students','entries','periods')";
                if (Convert.ToInt32(cmd.ExecuteScalar()) < 3)
                    return new(false, "文件里缺少必要的表（students / entries / periods）");

                cmd.CommandText = "PRAGMA user_version";
                var version = Convert.ToInt32(cmd.ExecuteScalar());
                if (version > Sql.SchemaVersion)
                    return new(false, $"这份数据的结构版本是 {version}，比当前程序（{Sql.SchemaVersion}）新，请先升级程序");
            }

            var bak = db.DbPath + ".bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            SqliteConnection.ClearAllPools();
            File.Move(db.DbPath, bak, overwrite: true);
            File.Copy(sourceDbPath, db.DbPath, overwrite: true);
            foreach (var suffix in new[] { "-wal", "-shm" })
                if (File.Exists(db.DbPath + suffix)) File.Delete(db.DbPath + suffix);
            SqliteConnection.ClearAllPools();

            return new(true, $"已恢复。原库已改名保留在 {System.IO.Path.GetFileName(bak)}");
        }
        catch (Exception ex)
        {
            return new(false, "恢复失败：" + ex.Message);
        }
    }

    // ---------------- 换数据目录 ----------------

    /// <summary>
    /// 把整个数据目录搬到别处（装到系统盘之后想搬到 D 盘，或者反过来）。
    ///
    /// 库走 <see cref="VacuumInto"/> 做一致性副本，不是 File.Copy —— 正在写的库带 WAL，
    /// 直接拷出来的文件可能是"写了一半"的样子，换过去就是一份看不出坏了的坏库。
    /// 校验通过才写指针；指针写不下去就当场失败，绝不留"看起来搬好了、重启又回原处"的局面。
    /// 原目录一个文件都不删：搬完人自己确认，删不删由他决定。
    /// </summary>
    public RestoreCheck MoveDataRoot(string targetRoot)
    {
        if (string.IsNullOrWhiteSpace(targetRoot)) return new(false, "没有选择目录");

        try
        {
            if (DataPaths.SameDir(targetRoot, Database.RootDir)) return new(false, "数据已经在这个目录里了");

            var targetDb = System.IO.Path.Combine(targetRoot, DataPaths.DbName);
            if (File.Exists(targetDb))
                return new(false, "这个目录里已经有一份班级积分数据，换一个空目录");

            Directory.CreateDirectory(targetRoot);
            VacuumInto(targetDb);

            // 同步凭据、免密会话、恢复码跟着走，否则新目录里要重新配对、重新认人
            foreach (var name in new[] { "sync.json", "session.bin", "recovery.bin" })
            {
                var source = System.IO.Path.Combine(Database.RootDir, name);
                if (File.Exists(source)) File.Copy(source, System.IO.Path.Combine(targetRoot, name), overwrite: true);
            }

            if (CheckDbFile(targetDb) is { } problem)
            {
                try { File.Delete(targetDb); } catch (Exception) { }
                return new(false, "复制出来的库没通过检查：" + problem + "，已放弃，数据还在原处");
            }

            DataPaths.SavePointer(targetRoot);
            if (DataPaths.ReadPointer() is not { } written || !DataPaths.SameDir(written, targetRoot))
                return new(false, "目录已复制好，但记录位置失败（%LOCALAPPDATA% 可能被安全软件挡住），重启后会回原处");

            return new(true, targetRoot);
        }
        catch (Exception ex)
        {
            return new(false, "搬移失败：" + ex.Message);
        }
    }

    /// <summary>能不能当数据文件用。返回 null 表示没问题，否则是给人看的原因。</summary>
    private static string? CheckDbFile(string path)
    {
        try
        {
            using var probe = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
            probe.Open();

            using var cmd = probe.CreateCommand();
            cmd.CommandText = "PRAGMA integrity_check";
            var result = cmd.ExecuteScalar()?.ToString() ?? "";
            if (!result.Equals("ok", StringComparison.OrdinalIgnoreCase)) return "完整性检查未通过：" + result;

            cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name IN ('students','entries','periods')";
            if (Convert.ToInt32(cmd.ExecuteScalar()) < 3) return "文件里缺少必要的表";

            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    // ---------------- 整包迁移 ----------------

    /// <summary>classpoints-yyyyMMdd-HHmm.zip：manifest + db + settings + README。</summary>
    public string ExportPack(string? targetDir = null)
    {
        var dir = targetDir ?? BackupRoot;
        Directory.CreateDirectory(dir);
        var zipPath = System.IO.Path.Combine(dir, $"classpoints-{DateTime.Now:yyyyMMdd-HHmm}.zip");
        if (File.Exists(zipPath)) zipPath = zipPath.Replace(".zip", $"-{DateTime.Now:ss}.zip");

        // 打包用的库副本放在数据目录下的 tmp 里，不用系统 %TEMP%：
        // 那在 C 盘，而且清理工具可能正好删在压缩到一半的时候。
        Directory.CreateDirectory(Database.TempDir);
        var tmpDb = System.IO.Path.Combine(Database.TempDir, $"cp-pack-{Guid.NewGuid():N}.db");
        try
        {
            VacuumInto(tmpDb);
            var entryCount = CountEntries();
            var studentCount = CountStudents();
            var sha = Crypto.Sha256Hex(File.ReadAllBytes(tmpDb));

            var manifest = new JsonObject
            {
                ["app"] = "ClassPoints",
                ["app_version"] = typeof(BackupService).Assembly.GetName().Version?.ToString() ?? "1.0.0",
                ["schema_version"] = Sql.SchemaVersion,
                ["exported_at"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ["entry_count"] = entryCount,
                ["student_count"] = studentCount,
                ["sha256_db"] = sha,
                ["class_name"] = db.GetSetting("class_name") ?? "",
            };

            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                zip.CreateEntryFromFile(tmpDb, "classpoints.db", CompressionLevel.Optimal);

                var m = zip.CreateEntry("manifest.json", CompressionLevel.Optimal);
                using (var w = new StreamWriter(m.Open(), new UTF8Encoding(false)))
                    w.Write(manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

                var s = zip.CreateEntry("settings.json", CompressionLevel.Optimal);
                using (var w = new StreamWriter(s.Open(), new UTF8Encoding(false)))
                    w.Write(BuildSettingsJson());

                var r = zip.CreateEntry("README.txt", CompressionLevel.Optimal);
                using (var w = new StreamWriter(r.Open(), new UTF8Encoding(false)))
                    w.Write("""
                        班级积分 · 迁移包

                        这份文件包含全部周期的积分记录、名单与配置。
                        换机/重装后：打开「班级积分」→ 设置 → 备份与恢复 → 导入迁移包 → 选中这个 zip。

                        导入前程序会校验四件事：
                          1. 数据库校验和与 manifest 一致（排除拷贝损坏）
                          2. 结构版本不高于当前程序（排除降级）
                          3. 记录条数与库内实际行数一致（排除半截包）
                          4. 现有库会被改名并排保留，不会被覆盖（装错了还能退回去）

                        有任何疑问，把这份 zip 和自己的说明一起交给懂电脑的人即可。
                        """);
            }

            return zipPath;
        }
        finally
        {
            try { if (File.Exists(tmpDb)) File.Delete(tmpDb); } catch (Exception) { }
        }
    }

    private string BuildSettingsJson()
    {
        var keys = new[]
        {
            "class_name", "class_code", "seat_cols", "seat_rows", "idle_timeout_sec",
            "lock_timeout_sec", "lookup_timeout_sec", "sound_on", "board_rotate_sec",
            "absence_policy", "onboarding_done",
        };
        var obj = new JsonObject();
        foreach (var k in keys)
            if (db.GetSetting(k) is string v) obj[k] = v;
        return obj.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    public RestoreCheck ImportPack(string zipPath)
    {
        if (!File.Exists(zipPath)) return new(false, "文件不存在");

        var tmpDir = System.IO.Path.Combine(Database.TempDir, $"cp-import-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);
        try
        {
            try { ZipFile.ExtractToDirectory(zipPath, tmpDir); }
            catch (Exception ex) { return new(false, "压缩包读不开：" + ex.Message); }

            var dbFile = System.IO.Path.Combine(tmpDir, "classpoints.db");
            var manifestFile = System.IO.Path.Combine(tmpDir, "manifest.json");
            if (!File.Exists(dbFile)) return new(false, "压缩包里没有 classpoints.db");
            if (!File.Exists(manifestFile)) return new(false, "压缩包里没有 manifest.json");

            JsonNode? manifest;
            try { manifest = JsonNode.Parse(File.ReadAllText(manifestFile, Encoding.UTF8)); }
            catch (Exception ex) { return new(false, "manifest.json 读不开：" + ex.Message); }
            if (manifest is null) return new(false, "manifest.json 是空的");

            // 校验 1：校验和
            var expected = manifest["sha256_db"]?.GetValue<string>() ?? "";
            var actual = Crypto.Sha256Hex(File.ReadAllBytes(dbFile));
            if (!string.IsNullOrEmpty(expected) && !expected.Equals(actual, StringComparison.OrdinalIgnoreCase))
                return new(false, "校验和不一致，文件可能在传输中损坏了");

            // 校验 2：结构版本不能高于当前程序
            var schema = manifest["schema_version"]?.GetValue<int>() ?? 0;
            if (schema > Sql.SchemaVersion)
                return new(false, $"这份数据的结构版本是 {schema}，比当前程序（{Sql.SchemaVersion}）新，请先升级程序");

            // 校验 3：记录条数与库内实际行数一致
            var declared = manifest["entry_count"]?.GetValue<int>() ?? -1;
            var real = CountEntries(dbFile);
            if (declared >= 0 && real != declared)
                return new(false, $"记录条数对不上：清单写 {declared} 条，库里实际 {real} 条，压缩包不完整");

            // 校验 4：现有库改名并排保留
            var restore = Restore(dbFile);
            if (!restore.Ok) return restore;

            var settingsFile = System.IO.Path.Combine(tmpDir, "settings.json");
            if (File.Exists(settingsFile))
            {
                try
                {
                    var node = JsonNode.Parse(File.ReadAllText(settingsFile, Encoding.UTF8)) as JsonObject;
                    if (node is not null)
                        foreach (var (k, v) in node)
                            if (v is not null) db.SetSetting(k, v.ToString());
                }
                catch (Exception) { }
            }

            return new(true, $"已导入 {real} 条记录，{manifest["student_count"]?.GetValue<int>() ?? 0} 名学生");
        }
        finally
        {
            try { Directory.Delete(tmpDir, recursive: true); } catch (Exception) { }
        }
    }

    private static int CountEntries(string dbPath)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM entries";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    /// <summary>
    /// 数条数用的读连接**必须用完就关**。
    /// 以前写成 `Database.Scalar(OpenRead(), …)`，连接对象随手丢掉、没人 Dispose：
    /// 它就一直挂在池子里算"使用中"，连 ClearAllPools 都收不回来 ——
    /// 于是「本机重置」删库时永远报"文件正被另一进程使用"（2026-11-16 的报障）。
    /// </summary>
    private int CountEntries()
    {
        using var conn = OpenRead();
        return Convert.ToInt32(Database.Scalar(conn, "SELECT COUNT(*) FROM entries"));
    }

    private int CountStudents()
    {
        using var conn = OpenRead();
        return Convert.ToInt32(Database.Scalar(conn,
            "SELECT COUNT(*) FROM students WHERE deleted_at IS NULL"));
    }

    private SqliteConnection OpenRead() => db.Open();
}
