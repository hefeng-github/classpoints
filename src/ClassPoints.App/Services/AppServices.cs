using System.Reflection;
using ClassPoints.Core;
using ClassPoints.Data;
using ClassPoints.Data.Sync;

namespace ClassPoints.App.Services;

/// <summary>
/// 启动期参数。只剩一个 --db（换库路径，给测试和"两台机器共用一份库"用）。
///
/// 形态（大屏/电脑/只读）和预配置包以前也是参数，现在都归首次启动向导管：
/// 老师不会记命令行，而参数一旦写死在快捷方式里，换台机器就会静默进错形态。
/// CP_MODE / CP_SNAP 这两个环境变量只服务验收脚本，不参与正常业务。
/// </summary>
public sealed class StartupOptions
{
    public string? DbPath { get; init; }

    /// <summary>验收脚本用：绕过设置强制某个形态，只影响这一次运行，不写库。</summary>
    public AppMode? ModeOverride { get; init; }

    /// <summary>
    /// 解析启动参数。两种写法都认：<c>--db=x.db</c> 和 <c>--db x.db</c>。
    ///
    /// 空格形式是文档和验收脚本里一直用的写法，但旧实现只 Split('=') —— 于是
    /// `ClassPoints.exe --db demo/demo.db` 会**静默**忽略路径，直接开默认库。
    /// 那种错不会报任何东西，只表现为"指了新库，界面还是老数据"，还顺手把默认库的数据
    /// 复制到了当前目录。所以空格形式必须当成一等写法。
    /// </summary>
    public static StartupOptions Parse(string[] args)
    {
        string? dbPath = null;

        for (var i = 0; i < args.Length; i++)
        {
            var raw = args[i].Trim();
            var arg = raw.TrimStart('-', '/');
            var parts = arg.Split('=', 2);
            if (!parts[0].Equals("db", StringComparison.OrdinalIgnoreCase)) continue;

            if (parts.Length > 1)
            {
                dbPath = parts[1].Trim('"');
            }
            else if (i + 1 < args.Length && !args[i + 1].StartsWith('-'))
            {
                // 下一个参数当值。以 '-' 开头说明是另一个开关，别把它当成路径
                dbPath = args[++i].Trim('"');
            }
        }

        return new StartupOptions
        {
            DbPath = string.IsNullOrWhiteSpace(dbPath) ? null : dbPath,
            ModeOverride = AppModeNames.Parse(Environment.GetEnvironmentVariable("CP_MODE")),
        };
    }
}

/// <summary>
/// 组合根。界面只从这里拿东西，不自己 new 仓储 —— 换数据库、换读卡器只改这一个文件。
/// </summary>
public sealed class AppServices
{
    private AppServices(Database db)
    {
        Db = db;
        Outbox = new SyncOutbox(db);
        Audit = new AuditRepository(db);
        Students = new StudentRepository(db);
        SeatSnapshots = new SeatSnapshotRepository(db);
        Rules = new RuleRepository(db);
        Counters = new CounterRepository(db);
        Members = new MemberRepository(db);
        Notes = new NoteTemplateRepository(db);
        Purge = new PurgeService(db);
        Periods = new PeriodRepository(db);
        Entries = new EntryService(db, Audit);
        Exams = new ExamRepository(db);
        Backups = new BackupService(db);
        Settings = new AppSettings(db);
        Sync = new SyncService(db, Outbox);
        Config = new ConfigService(this);
        Pump = new SyncPump(this);
        Reader = new Pn532Serial();

        // 握手成功后记住这个波特率：模块被 Arduino 例程改过速率的情况很常见，
        // 每轮重试都从 115200 一个个试到 9600，会白等好几秒才连上。
        Reader.StatusChanged += status =>
        {
            if (status.State != ReaderState.Running || status.Baud <= 0) return;
            if (Settings.ReaderBaud == status.Baud) return;
            try { Settings.ReaderBaud = status.Baud; } catch (Exception) { }
        };
    }

    public static AppServices Instance { get; private set; } = null!;

    public Database Db { get; }
    public SyncOutbox Outbox { get; }
    public AuditRepository Audit { get; }
    public StudentRepository Students { get; }
    public SeatSnapshotRepository SeatSnapshots { get; }
    public RuleRepository Rules { get; }
    public CounterRepository Counters { get; }
    public MemberRepository Members { get; }
    public NoteTemplateRepository Notes { get; }
    /// <summary>彻底删除：归档项与停用项真删（连带子表），并推一条 purge 让所有设备跟着删。</summary>
    public PurgeService Purge { get; }
    public PeriodRepository Periods { get; }
    public EntryService Entries { get; }
    public ExamRepository Exams { get; }
    public BackupService Backups { get; }
    public AppSettings Settings { get; }
    public SyncService Sync { get; }
    public ConfigService Config { get; }
    public SyncPump Pump { get; }
    public Pn532Serial Reader { get; }
    public BoardSession Session { get; private set; } = null!;

    /// <summary>
    /// 原数据目录打不开、改用用户目录时的一句话。启动后由外壳提示一次 ——
    /// 这条必须让人看见：数据换了地方，老师得知道去哪儿找、为什么。
    /// </summary>
    public string? DataDirFallbackNotice { get; private set; }

    public static AppServices Initialize(StartupOptions options)
    {
        // 先定数据目录：判断 + （必要时）把旧库搬过去 + 记下选择。
        // 用 --db 指了具体文件时以那个文件为准，不去认别的目录 —— 那是测试和
        // "两台机器共用一份库"的场景，不该被自动搬家搅一下。
        var location = options.DbPath is null
            ? DataPaths.Prepare()
            : new DataPaths.Choice(Database.RootDir, "--db 指定");
        Database.UseRoot(location.Root);

        Database db;
        string? fallbackNotice = null;

        try
        {
            db = new Database(options.DbPath);
            db.Initialize();
        }
        catch (Exception ex) when (options.DbPath is null && IsDataDirUnusable(ex))
        {
            // 选中的目录打不开（最常见：装在 Program Files 下、库又落在里面，
            // 普通权限建不了文件）。这时**必须**自己退到用户目录：
            // 报"数据库打不开"然后只给一个「退出」按钮，等于让老师彻底进不了程序。
            var fallback = DataPaths.LocalRoot(DataPaths.LocalAppData);
            Database.UseRoot(fallback);
            db = new Database(null);
            db.Initialize();

            fallbackNotice = $"原数据目录不可写（{location.Root}），已改用 {fallback}。" +
                             "这里的名单和积分是空的，旧数据还在原目录里，没有删。";
            db.SetSetting("data_dir_fallback", fallback);
        }

        var services = new AppServices(db) { DataDirFallbackNotice = fallbackNotice };
        services.Session = new BoardSession(services.Members);
        // 启动时先把只读态摆好：sync.json 里记着"这台还在等通过"，
        // 界面第一帧就得是只读，不能先放人进去写两条再切回来。
        services.Session.ReadOnly = services.Sync.IsReadOnly;

        // 验收脚本用：强制进只读态，好截"待审核"那一版界面。
        // 和 CP_MODE / CP_SNAP 一样只影响这一次运行、不写库，也不参与正常业务。
        if (Environment.GetEnvironmentVariable("CP_FORCE_READONLY") == "1")
            services.Session.ReadOnly = true;

        // 只读是服务端说了算的，变化有两个来源：配对/续期/拉取里发现状态变了。
        // 统一在这里落到会话上，顺带刷新外壳 —— 页面不必知道同步的内部细节。
        services.Sync.ReadOnlyChanged += () =>
        {
            var readOnly = services.Sync.IsReadOnly;
            services.Session.ReadOnly = readOnly;
            App.Host?.RefreshChrome();
            if (!readOnly)
                App.Host?.Toast("班主任已通过这台设备，记分和改名单恢复了", 8);
        };

        Instance = services;

        // 已经配过对的机器：sync.json 里的班级码才是云端认的。老版本建班/配对只写了
        // sync.json，本机设置里还留着建班之前懒生成的那个随机码 —— 设置页显示的、
        // 老师念给另一台机器的那个码因此一直是错的。启动时对齐一次，把老机器也修好。
        if (services.Sync.PairedClassCode is { } pairedCode)
            services.AdoptClassCode(pairedCode);

        services.Log($"启动 db={db.DbPath} 数据目录={Database.RootDir}（{location.Reason}）" +
                     $" schema={Sql.SchemaVersion} mode={services.Settings.AppMode?.ToDb() ?? "(未选)"}");
        return services;
    }

    /// <summary>打开库失败里，哪些属于"这个目录不能用"，可以退到用户目录重试。</summary>
    private static bool IsDataDirUnusable(Exception ex) => ex switch
    {
        UnauthorizedAccessException => true,
        IOException => true,
        // SQLITE_READONLY = 8、SQLITE_FULL = 13、SQLITE_CANTOPEN = 14
        Microsoft.Data.Sqlite.SqliteException sqlite => sqlite.SqliteErrorCode is 8 or 13 or 14,
        _ => false,
    };

    // ---------------- 常用查询 ----------------

    public Period CurrentPeriod => Periods.Active() ?? new Period
    {
        Id = "p_fallback",
        Name = "当前周期",
        TermKey = Database.TermKeyFor(DateTime.Now),
        StartAt = Clock.NowMs(),
    };

    /// <summary>
    /// 界面上的版本号。只在这一个地方出现，省得侧栏写 1.2、安装包写 1.1。
    ///
    /// 注意它是**计算属性**而不是带初始化器的静态字段：字段初始化器按声明顺序执行，
    /// 写成字段的话 AppVersion 会在 CurrentVersion 之前求值，读到 (0,0,0) ——
    /// 界面上就是"班级积分 v0.0"，而且这种错只会在界面上露出来，测试永远发现不了。
    /// </summary>
    public static string AppVersion => CurrentVersion.ToString();

    /// <summary>当前运行的版本，比较用。解析不出时归零，任何真实版本都比它新。</summary>
    public static SemanticVersion CurrentVersion { get; } = ReadVersion();

    private static SemanticVersion ReadVersion()
    {
        var assembly = typeof(AppServices).Assembly;

        // InformationalVersion 默认会带上 "+<commit sha>"，先截掉再解析。
        var info = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            var plus = info.IndexOf('+');
            if (plus > 0) info = info[..plus];
            if (SemanticVersion.TryParse(info, out var parsed)) return parsed;
        }

        var fallback = assembly.GetName().Version;
        return fallback is null
            ? SemanticVersion.Zero
            : new SemanticVersion(fallback.Major, fallback.Minor, Math.Max(fallback.Build, 0));
    }

    public string ClassName =>
        string.IsNullOrWhiteSpace(Settings.ClassName) ? "我的班级" : Settings.ClassName;

    public string ClassTitle
    {
        get
        {
            var name = ClassName;
            var period = Periods.Active();
            return period is null ? name : $"{name} · {period.TitleText}";
        }
    }

    /// <summary>当前周期名。大屏看板的标题里已经在显班级名了，副标题只要周期，不然一行里班级名出现两遍。</summary>
    public string PeriodName => Periods.Active()?.TitleText ?? "";

    /// <summary>大屏上任何写操作的操作者。未认人时不该走到这里，兜底用一个明确的占位。</summary>
    public Member Operator => Session.Current ?? DeskOperator;

    private Member? _deskOperator;

    /// <summary>电脑端免密：所有记录记在班主任名下。</summary>
    public Member DeskOperator
    {
        get
        {
            if (_deskOperator is not null) return _deskOperator;
            var owner = Members.Owner();
            if (owner is not null) return _deskOperator = owner;

            _deskOperator = Members.Save(new Member
            {
                DisplayName = "班主任",
                Role = MemberRole.Owner,
            }, pin: null);
            return _deskOperator;
        }
    }

    public int StudentCount => Students.ListActive().Count;

    /// <summary>
    /// 云端已经确定了班级码（建班返回的、或配对时用的那个），本机设置跟着改成同一个。
    ///
    /// 起因是一个现场问题：老师在这台电脑上建了班，提示「班级码 ABCD-1234」，
    /// 可「设置 → 班级信息」里显示的还是另一个 —— 本机没连云端时懒生成的那个随机码。
    /// 建班只写了 sync.json，没写 settings；于是照着设置页念给另一台机器，配对只会
    /// 得到 class_not_found，而老师完全看不出这两个码为什么不是一回事。
    ///
    /// 配对同理：输进去的码才是云端认的，本机那份要跟上。
    /// </summary>
    public void AdoptClassCode(string? code)
    {
        var target = ClassCodes.Align(code, Settings.ClassCode);
        if (target is null) return;

        Log($"班级码对齐：本机 {Settings.ClassCode} → 云端 {target}");
        Settings.ClassCode = target;
    }

    /// <summary>
    /// 库里有没有任何一个能用来认人的东西（密码或卡）。
    ///
    /// 没有的话必须放行并提示去补密码 —— 否则刚装好、还没设密码的机器
    /// 会被"进设置要身份、身份要在设置里设"卡死，那是最难解释的一种死锁。
    /// </summary>
    public bool HasAnyCredential =>
        Members.ListAll().Any(m => m.HasPin || Members.Identities(m.Id).Count > 0);

    /// <summary>
    /// 有没有"能用密码证明自己是班主任/班长"的人。
    /// 只发卡没设密码的机器，管理密码校验永远过不去 —— 这时要放行进管理态，否则名单和设置就锁死了。
    /// </summary>
    public bool HasManagerPin =>
        Members.ListAll().Any(m => m.IsManager && m.HasPin);

    /// <summary>当前周期总分。</summary>
    public Dictionary<string, int> PeriodTotals() => Entries.PeriodTotals(CurrentPeriod.Id);

    /// <summary>累计积分（总评用这个，不是当前周期）。</summary>
    public Dictionary<string, int> CumulativeTotals() => Entries.CumulativeTotals();

    /// <summary>导出/打印需要的整包材料。</summary>
    public ExportBundle BuildExportBundle(AbsencePolicy? policy = null, IReadOnlyList<string>? warnings = null)
    {
        var period = CurrentPeriod;
        var scheme = Exams.ActiveScheme();
        var students = Students.ListActive();
        var termPoints = Entries.CumulativeTotals();
        var usePolicy = policy ?? Settings.AbsencePolicy;
        var ranking = Exams.BuildRanking(students, termPoints, scheme, usePolicy);

        // 排名用累计口径（全部考试），成绩明细与参数说明也必须用同一份场次，
        // 否则表里只有一个周期的考试，"总评"却把它们加在一起 —— 数字对不上。
        var exams = Exams.ListAllExams();

        return new ExportBundle(
            ranking,
            exams,
            Exams.ScoresForTerm(period.TermKey, exams.Select(e => e.Id).ToList()),
            Entries.Entries(period.Id, limit: 100_000),
            scheme,
            period,
            usePolicy,
            period.TermKey,
            students,
            // 各场考试权重恒为均分（见 ExamRepository.BuildRanking 的注释）
            new Dictionary<string, double>(),
            DeskOperator.DisplayName,
            DateTime.Now,
            warnings);
    }

    // ---------------- 日志 ----------------

    private static readonly object LogGate = new();

    public void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(Database.LogDir);
            var path = Path.Combine(Database.LogDir, $"app-{DateTime.Now:yyyyMMdd}.log");
            lock (LogGate)
                File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff}  {message}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // 日志写不进去不能影响主流程
        }
    }

    public void LogError(string where, Exception ex) =>
        Log($"ERROR {where}: {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");

    /// <summary>
    /// 自动备份：一天最多做一份，失败不抛（界面只提示）。
    ///
    /// 以前是"每天首次启动一份 + 每记满 50 人次再一份"——一条午休前后的班，
    /// 一节课就能记 50 人次，一天下来七八份，而它们之间只差几十条流水。
    /// 备份在这里防的是**库文件损坏、误删名单、误重置**，不是防丢那几十笔记录
    /// （流水写入即落库，崩溃不会丢）。所以改成：当天已经有一份就跳过。
    ///
    /// 关键操作前的那几份不走这里（周期重置、本机重置、手动、向导完成），
    /// 那些是按事件必须留的，不受"一天一份"限制。
    /// </summary>
    public string? TryAutoBackup(string reason)
    {
        try
        {
            var periodic = reason is "startup" or "50 entries";
            var last = Settings.LastBackupAt;
            var lastDate = last <= 0 ? DateTime.MinValue : Clock.LocalDate(last).Date;
            if (periodic && lastDate == DateTime.Today) return null;

            var path = Backups.BackupNow(reason, null);
            Log($"自动备份完成 {path}");
            return path;
        }
        catch (Exception ex)
        {
            LogError("auto-backup", ex);
            return null;
        }
    }
}
