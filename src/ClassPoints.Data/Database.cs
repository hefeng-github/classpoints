using ClassPoints.Core;
using Microsoft.Data.Sqlite;

namespace ClassPoints.Data;

/// <summary>
/// 本地库。路径由 <see cref="DataPaths"/> 挑 —— unpackaged 应用不能用 ApplicationData.Current
/// （会抛 COMException），所以全部自己拼，规则集中在那一处。
/// </summary>
public sealed class Database
{
    /// <summary>
    /// 数据根目录。默认按 <see cref="DataPaths"/> 的规则算一个（纯判断，不碰磁盘）；
    /// 应用启动时 <see cref="UseRoot"/> 会把真正定下来的那个覆盖上来。
    /// </summary>
    private static string _root = DataPaths.Resolve(
        AppContext.BaseDirectory, DataPaths.LocalAppData, null, DataPaths.SystemDrive).Root;

    public static string RootDir => _root;

    /// <summary>由启动流程设定（<see cref="DataPaths.Prepare"/> 的结果）。</summary>
    public static void UseRoot(string root)
    {
        if (!string.IsNullOrWhiteSpace(root)) _root = Path.GetFullPath(root);
    }

    public static string DefaultDbPath => Path.Combine(RootDir, "classpoints.db");

    /// <summary>老位置里的备份目录。换了默认目录之后它只用于"把旧备份也列出来"。</summary>
    public static string BackupRoot => Path.Combine(RootDir, "backups");

    /// <summary>上一版默认备份目录（文档\班级积分备份）。同样只用于找回旧备份。</summary>
    public static string DocumentsBackupDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "班级积分备份");

    /// <summary>最早那版默认备份目录（%LOCALAPPDATA%\ClassPoints\backups）。数据跟着程序搬走以后，
    /// 它和 <see cref="BackupRoot"/> 就不是一个地方了，旧备份照旧要能列出来、能恢复。</summary>
    public static string LegacyBackupRoot =>
        Path.Combine(DataPaths.LocalRoot(DataPaths.LocalAppData), "backups");

    /// <summary>
    /// 备份默认落在哪。跟着数据目录走 —— 数据在非系统盘时，备份也在那儿，
    /// 一次整目录拷贝就能把"库 + 备份"全带走，也不用再占 C 盘。
    /// 只有数据还在 C 盘（装进了 Program Files）时才用「文档」，因为 AppData 是隐藏目录，
    /// 老师找不到就等于没有备份。
    /// </summary>
    public static string DefaultBackupDir => DataPaths.IsOnSystemDrive(RootDir)
        ? DocumentsBackupDir
        : Path.Combine(RootDir, "backups");

    public static string SessionPath => Path.Combine(RootDir, "session.bin");
    public static string RecoveryPath => Path.Combine(RootDir, "recovery.bin");
    public static string LogDir => Path.Combine(RootDir, "logs");

    /// <summary>临时落地处（打包用的库副本、解压出来的导入包）。不放系统 %TEMP%：
    /// 那在 C 盘，还会被清理工具顺手删掉，正好删在导入到一半的时候。</summary>
    public static string TempDir => Path.Combine(RootDir, "tmp");

    private readonly string _connectionString;

    public Database(string? dbPath = null)
    {
        DbPath = dbPath is null ? DefaultDbPath : Path.GetFullPath(dbPath);

        // 用 --db 指到别处时，日志、同步凭据、更新包也跟着那个库走 ——
        // 否则一个目录里是数据、另一个目录里是它的日志，"这台机器用的到底是哪份"就说不清了。
        if (dbPath is not null && Path.GetDirectoryName(DbPath) is { Length: > 0 } dir) _root = dir;

        Directory.CreateDirectory(Path.GetDirectoryName(DbPath)!);
        Directory.CreateDirectory(LogDir);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DbPath,
            Pooling = true,
            DefaultTimeout = 5,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();
    }

    public string DbPath { get; }

    public SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = Sql.Pragmas;
        cmd.ExecuteNonQuery();
        return conn;
    }

    /// <summary>建表与写种子分两步：建表看 user_version，种子看 settings.seeded。
    /// 混在一起会出事——用户把预设全删了，重启一次它们会全部复活。</summary>
    public void Initialize()
    {
        using var conn = Open();

        var version = Convert.ToInt32(Scalar(conn, "PRAGMA user_version"));
        Exec(conn, Sql.CreateTables);

        // 先补种子再迁移：迁移里要按"有没有种过"判断，新库和旧库走同一条路
        if (GetSetting(conn, "seeded") is null)
        {
            using var tx = conn.BeginTransaction();
            Seed(conn, tx);
            tx.Commit();
        }

        Migrate(conn, version);

        if (version < Sql.SchemaVersion)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"PRAGMA user_version = {Sql.SchemaVersion}";
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// 逐版迁移。每段都要能重复执行（幂等）：这一步出错就打不开库，
    /// 而老师那边看到的只是"点了没反应"，所以宁可多做一次无副作用的检查。
    /// </summary>
    private static void Migrate(SqliteConnection conn, int version)
    {
        // v2：图标从 emoji 换成 Segoe MDL2 字形；新增事件计数。
        // 图标归一化每次都做：行数只有个位数，还能顺手修好从别处同步过来的旧数据。
        NormalizeIcons(conn);

        if (GetSetting(conn, "counters_seeded") is null)
            SeedCounters(conn, null);

        if (version < 2) SetSetting(conn, null, "schema_note", "v2: 图标字体化 + 事件计数");

        // v3：周期改用序号（「第 N 周期」），学期那条线拿掉；分组功能移除。
        RenumberPeriods(conn);
        if (version < 3) SetSetting(conn, null, "schema_note", "v3: 周期按序号 + 移除分组");

        // v4：备份默认目录从 %LOCALAPPDATA%\ClassPoints\backups 挪到「文档\班级积分备份」。
        // 老库里这一项被写成了绝对路径，不清掉就会一直沿用 C 盘那个隐藏目录。
        // 数据目录本身搬过家（装到非系统盘）时，BackupRoot 已经不是那个老路径了，
        // 所以两个都要认 —— 否则备份会继续往旧位置写。
        if (GetSetting(conn, "backup_dir") is { Length: > 0 } dir &&
            (SamePath(dir, BackupRoot) || SamePath(dir, LegacyBackupRoot)))
            SetSetting(conn, null, "backup_dir", "");

        // v4：加分项可以只属于某个成员（「我的快捷项」）。NULL = 全班共用，
        // 所以老库补一列就够，不需要回填。
        if (EnsureColumn(conn, "rules", "owner_id", "TEXT"))
            SetSetting(conn, null, "schema_note", "v4: 加分项可归属个人");
        Exec(conn, "CREATE INDEX IF NOT EXISTS idx_rules_owner ON rules(owner_id)");

        // v5：座位快照表。建表语句在 CreateTables 里（IF NOT EXISTS，老库也会补上），
        // 这里只留一句结构说明，升级后「关于」页能看到库是哪一代。
        if (version < 5) SetSetting(conn, null, "schema_note", "v5: 座位快照（可选功能）");

        // v6：事件计数进云同步 —— outbox 要接纳两种新 kind（tick / counter）。
        // outbox.kind 上有 CHECK 约束，SQLite 改不了 CHECK，只能重建表。
        if (RebuildOutboxKinds(conn))
            SetSetting(conn, null, "schema_note", "v6: 事件计数参与同步");
    }

    /// <summary>
    /// 把 outbox 的 kind 约束换成宽的那一版（加 tick / counter）。
    ///
    /// SQLite 没有"改 CHECK"，只能建新表、搬数据、改名。老库升级必须走过这一步，
    /// 否则打点入队直接抛约束错误 —— 而那个异常发生在"点一下计数"的路径上，
    /// 老师看到的会是"点了没反应"。
    ///
    /// 幂等：先看 sqlite_master 里那条建表语句有没有 'tick'，有就什么都不做。
    /// </summary>
    private static bool RebuildOutboxKinds(SqliteConnection conn)
    {
        string? sql;
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'outbox'";
            sql = cmd.ExecuteScalar() as string;
        }
        if (sql is null || sql.Contains("'tick'", StringComparison.Ordinal)) return false;

        using var tx = conn.BeginTransaction();
        Exec(conn, tx, """
            CREATE TABLE outbox_new (
              id         TEXT PRIMARY KEY,
              kind       TEXT NOT NULL CHECK (kind IN ('entry','reverse','tick','student','rule','period','exam','member','note','scheme','counter')),
              payload    TEXT NOT NULL,
              attempts   INTEGER NOT NULL DEFAULT 0,
              next_at    INTEGER NOT NULL,
              created_at INTEGER NOT NULL
            )
            """);
        Exec(conn, tx, "INSERT INTO outbox_new (id, kind, payload, attempts, next_at, created_at) SELECT id, kind, payload, attempts, next_at, created_at FROM outbox");
        Exec(conn, tx, "DROP TABLE outbox");
        Exec(conn, tx, "ALTER TABLE outbox_new RENAME TO outbox");
        Exec(conn, tx, "CREATE INDEX IF NOT EXISTS idx_outbox_next ON outbox(next_at)");
        tx.Commit();
        return true;
    }

    /// <summary>两个路径是不是同一个地方。比较前统一成绝对路径、去掉末尾分隔符。</summary>
    private static bool SamePath(string a, string b)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 补一列（SQLite 没有 ADD COLUMN IF NOT EXISTS）。返回 true 表示这次真的补了。
    /// 老库升级必须先走这里，否则 SELECT * 的结果里少一列，映射器直接抛异常。
    /// </summary>
    private static bool EnsureColumn(SqliteConnection conn, string table, string column, string type)
    {
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = $"PRAGMA table_info({table})";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                if (string.Equals(r.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                    return false;
        }

        Exec(conn, $"ALTER TABLE {table} ADD COLUMN {column} {type}");
        return true;
    }

    /// <summary>
    /// 把老库里自动生成的「2026 秋季学期」这类周期名改成「第 N 周期」。
    /// 按开始时间排序编号，保证同一个库反复跑结果一样；人工改过的名字不动。
    /// </summary>
    private static void RenumberPeriods(SqliteConnection conn)
    {
        var rows = new List<(string Id, string Name)>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT id, name FROM periods ORDER BY start_at, created_at";
            using var r = cmd.ExecuteReader();
            while (r.Read()) rows.Add((r.GetString(0), r.GetString(1)));
        }

        for (var i = 0; i < rows.Count; i++)
        {
            if (!PeriodNames.LooksAutoGenerated(rows[i].Name)) continue;
            var wanted = PeriodNames.Format(i + 1);
            if (wanted == rows[i].Name) continue;
            Exec(conn, "UPDATE periods SET name = $name WHERE id = $id",
                ("$id", rows[i].Id), ("$name", wanted));
        }
    }

    private static void NormalizeIcons(SqliteConnection conn)
    {
        var rows = new List<(string Id, string Icon)>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT id, COALESCE(icon, '') FROM rules";
            using var r = cmd.ExecuteReader();
            while (r.Read()) rows.Add((r.GetString(0), r.GetString(1)));
        }

        foreach (var (id, icon) in rows)
        {
            var glyph = IconCatalog.Normalize(icon);
            if (glyph != icon)
                Exec(conn, "UPDATE rules SET icon = $icon WHERE id = $id", ("$id", id), ("$icon", glyph));
        }
    }

    /// <summary>事件预设：只开两个最常用的。老师要别的自己加，删了也不会复活（有 counters_seeded 标记）。</summary>
    public static readonly (string Id, string Label, string Icon, string Preset, int Sort)[] CounterPresets =
    {
        ("c_late", "迟到", IconCatalog.Flag, "std.late", 10),
        ("c_homework", "作业未交", IconCatalog.Document, "std.homework", 20),
        ("c_discipline", "课堂违纪", IconCatalog.Warning, "std.discipline", 30),
    };

    public static void SeedCounters(SqliteConnection conn, SqliteTransaction? tx)
    {
        var now = Clock.NowMs();
        foreach (var c in CounterPresets)
        {
            Exec(conn, tx, """
                INSERT OR IGNORE INTO counters (id, label, icon, sort, preset_key, rev, updated_at)
                VALUES ($id, $label, $icon, $sort, $preset, 1, $now)
                """,
                ("$id", c.Id), ("$label", c.Label), ("$icon", c.Icon),
                ("$sort", c.Sort), ("$preset", c.Preset), ("$now", now));
        }
        SetSetting(conn, tx, "counters_seeded", "1");
    }

    private static void Seed(SqliteConnection conn, SqliteTransaction tx)
    {
        var now = Clock.NowMs();

        // 只有三条预设，不加别的
        (string Id, string Label, int Delta, string Icon, int Sort, string Preset, int? Cap, string? Scope)[] rules =
        {
            ("r_speak", "回答问题", 10, IconCatalog.Message, 10, "std.speak", null, null),
            ("r_recite", "早读检查背课文", 50, IconCatalog.Reading, 20, "std.recite", 50, "single"),
            ("r_board", "课上黑板演题", 100, IconCatalog.Edit, 30, "std.board", null, null),
        };

        foreach (var r in rules)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT OR IGNORE INTO rules (id, label, delta, icon, sort, preset_key, cap_value, cap_scope, rev, updated_at)
                VALUES ($id, $label, $delta, $icon, $sort, $preset, $cap, $scope, 1, $now)
                """;
            cmd.Parameters.AddWithValue("$id", r.Id);
            cmd.Parameters.AddWithValue("$label", r.Label);
            cmd.Parameters.AddWithValue("$delta", r.Delta);
            cmd.Parameters.AddWithValue("$icon", r.Icon);
            cmd.Parameters.AddWithValue("$sort", r.Sort);
            cmd.Parameters.AddWithValue("$preset", r.Preset);
            cmd.Parameters.AddWithValue("$cap", (object?)r.Cap ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$scope", (object?)r.Scope ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$now", now);
            cmd.ExecuteNonQuery();
        }

        // 常用理由标签：内置约 12 个通用标签，可增可删
        string[] notes =
        {
            "背诵流利", "略有卡顿", "一题多解", "纠错到位", "主动补做", "思路清晰",
            "书写工整", "帮助同学", "课堂违纪", "作业未交", "迟到", "值日认真",
        };
        var i = 0;
        foreach (var text in notes)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT OR IGNORE INTO note_templates (id, text, use_count, last_used_at, rev, updated_at)
                VALUES ($id, $text, 1, $now, 1, $now)
                """;
            cmd.Parameters.AddWithValue("$id", $"nt_{i:D2}");
            cmd.Parameters.AddWithValue("$text", text);
            cmd.Parameters.AddWithValue("$now", now);
            cmd.ExecuteNonQuery();
            i++;
        }

        // 第一个周期。名字是「第 1 周期」—— 周期是唯一的时间单位，不再有学期。
        var termKey = TermKeyFor(DateTime.Now);
        var firstPeriod = new Period
        {
            Id = "p_" + termKey,
            Name = PeriodNames.Format(1),
            TermKey = termKey,
            StartAt = Clock.FromLocal(DateTime.Now.Date),
            Status = PeriodStatus.Active,
            CreatedAt = now,
        };
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT OR IGNORE INTO periods (id, name, term_key, start_at, status, created_at)
                VALUES ($id, $name, $term, $start, 'active', $now)
                """;
            cmd.Parameters.AddWithValue("$id", firstPeriod.Id);
            cmd.Parameters.AddWithValue("$name", firstPeriod.Name);
            cmd.Parameters.AddWithValue("$term", firstPeriod.TermKey);
            cmd.Parameters.AddWithValue("$start", firstPeriod.StartAt);
            cmd.Parameters.AddWithValue("$now", now);
            cmd.ExecuteNonQuery();
        }

        // 首周期**不在这里**入队。
        //
        // 队列只该装"要发出去的东西"，而这台机器此刻可能压根没配对：
        // 留一条永远发不出去的记录，未配对设备的「待推送」就永远是 1 条。
        // 真正需要补推的时机只有两个，都在同步层做（见 SyncService.QueuePeriods）：
        // 建班/配对成功之后，以及老库升级的那一次迁移。

        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT OR IGNORE INTO schemes (id, name, exam_weight, daily_weight, normalize, is_active, created_at)
                VALUES ('sch_default', '默认方案', 0.7, 0.3, 0, 1, $now)
                """;
            cmd.Parameters.AddWithValue("$now", now);
            cmd.ExecuteNonQuery();
        }

        (string Key, string Value)[] settings =
        {
            ("seeded", "1"),
            ("onboarding_done", "0"),
            ("app_mode", ""),
            ("class_name", ""),
            ("class_code", ""),
            ("seat_cols", "8"),
            ("seat_rows", "6"),
            ("idle_timeout_sec", "30"),
            ("lock_timeout_sec", "300"),
            ("lookup_timeout_sec", "30"),
            ("sound_on", "1"),
            ("board_rotate_sec", "20"),
            ("absence_policy", "redistribute"),
            // 留空 = 跟随默认（文档\班级积分备份）。写死绝对路径会让默认值以后再也改不动。
            ("backup_dir", ""),
            ("last_backup_at", "0"),
            ("window_w", "1920"),
            ("window_h", "1048"),
            ("window_x", ""),
            ("window_y", ""),
        };
        foreach (var (k, v) in settings)
            SetSetting(conn, tx, k, v);
    }

    /// <summary>
    /// 归档桶的键。界面上不再出现学期，这里保留是因为老库的 entries 已经按它分过桶，
    /// 换掉等于让历史数据的归属变化。新库永远只有桶，不会再有新桶。
    /// </summary>
    public static string TermKeyFor(DateTime date)
    {
        var y = date.Month >= 9 ? date.Year : date.Year - 1;
        var season = date.Month is 9 or 10 or 11 or 12 or 1 ? "AUT" : "SPR";
        return $"{y}-{season}";
    }

    // ---------- 小工具 ----------

    public static object Scalar(SqliteConnection conn, string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        return cmd.ExecuteScalar() ?? DBNull.Value;
    }

    /// <summary>事务内查询必须显式带上事务，Microsoft.Data.Sqlite 会校验。</summary>
    public static object ScalarTx(SqliteConnection conn, SqliteTransaction tx, string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        return cmd.ExecuteScalar() ?? DBNull.Value;
    }

    public static void Exec(SqliteConnection conn, string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public static void Exec(SqliteConnection conn, SqliteTransaction? tx, string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public string? GetSetting(string key)
    {
        using var conn = Open();
        return GetSetting(conn, key);
    }

    public static string? GetSetting(SqliteConnection conn, string key)
    {
        var v = Scalar(conn, Sql.SettingGet, ("$key", key));
        return v is DBNull or null ? null : v.ToString();
    }

    public void SetSetting(string key, string value)
    {
        using var conn = Open();
        SetSetting(conn, null, key, value);
    }

    public static void SetSetting(SqliteConnection conn, SqliteTransaction? tx, string key, string value) =>
        Exec(conn, tx, Sql.SettingSet, ("$key", key), ("$value", value));

    public int GetIntSetting(string key, int fallback) =>
        int.TryParse(GetSetting(key), out var v) ? v : fallback;

    public bool GetBoolSetting(string key, bool fallback) =>
        GetSetting(key) is string s ? s == "1" : fallback;

    public string GetSyncState(string key, string fallback = "")
    {
        using var conn = Open();
        var v = Scalar(conn, Sql.SyncStateGet, ("$key", key));
        return v is DBNull or null ? fallback : v.ToString()!;
    }

    public void SetSyncState(string key, string value)
    {
        using var conn = Open();
        Exec(conn, Sql.SyncStateSet, ("$key", key), ("$value", value));
    }

    /// <summary>
    /// 就地把库清成"没初始化过"：所有表/视图删掉、结构版本归零，下次 <see cref="Initialize"/> 重新建表 + 播种。
    ///
    /// 这是「本机重置」的兜底方案：库文件被别的进程占着（另一份安装同时开着、杀软正在扫）时，
    /// 删不掉也改不了名，但 SQL 还能写。文件名留着，内容和刚装好的一样 —— 对老师来说结果没区别。
    /// </summary>
    public void WipeInPlace()
    {
        using var conn = Open();

        // 先关外键约束再删表，否则删到被引用的那张就停下（子表反而留着）
        Exec(conn, "PRAGMA foreign_keys = OFF;");

        // 删掉的内容就地覆盖，别让它以旧页的形式留在空闲页里 —— "清空"就该是清空
        Exec(conn, "PRAGMA secure_delete = ON;");

        var drops = new List<string>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT type, name FROM sqlite_master " +
                "WHERE type IN ('table', 'view') AND name NOT LIKE 'sqlite_%'";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var kind = reader.GetString(0) == "view" ? "VIEW" : "TABLE";
                var name = reader.GetString(1).Replace("\"", "\"\"");
                drops.Add($"DROP {kind} IF EXISTS \"{name}\";");
            }
        }

        foreach (var sql in drops) Exec(conn, sql);

        Exec(conn, "PRAGMA user_version = 0;");

        // WAL 里可能还压着旧页；截断掉，别让下一次启动把它们恢复回来
        try { Exec(conn, "PRAGMA wal_checkpoint(TRUNCATE);"); }
        catch (Exception) { /* 有别的读连接时截断会失败，不影响"表已删光"这个结论 */ }
    }
}
