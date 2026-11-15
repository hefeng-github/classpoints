using ClassPoints.Core;
using ClassPoints.Data;
using ClassPoints.App.Theme;

namespace ClassPoints.App.Services;

/// <summary>设置的强类型门面。所有 key 只在这里出现一次，界面里不再出现裸字符串。</summary>
public sealed class AppSettings(Database db)
{
    private string? _cachedClassCode;

    public string ClassName
    {
        get => db.GetSetting("class_name") ?? "";
        set => db.SetSetting("class_name", value);
    }

    /// <summary>班级码：首次用到时生成，纯随机、不带年份。云端未接线时它只是给人看的一个标识。</summary>
    public string ClassCode
    {
        get
        {
            var v = db.GetSetting("class_code");
            if (!string.IsNullOrWhiteSpace(v))
            {
                // 老库里的「C3-2026」换掉：带年份既泄露信息又容易撞号。
                if (!ClassCodes.LooksLikeOldScheme(v)) return v!;
                var fresh = ClassCodes.New();
                db.SetSetting("class_code", fresh);
                return fresh;
            }

            if (_cachedClassCode is not null) return _cachedClassCode;
            _cachedClassCode = ClassCodes.New();
            db.SetSetting("class_code", _cachedClassCode);
            return _cachedClassCode;
        }
        set => db.SetSetting("class_code", value);
    }

    /// <summary>
    /// 向导里勾过「先用示例数据」。留个标记是为了能在设置页一键清空，
    /// 而不是让人手动从 48 个假学生里一个个删。
    /// </summary>
    public bool DemoData
    {
        get => db.GetBoolSetting("demo_data", false);
        set => db.SetSetting("demo_data", value ? "1" : "0");
    }

    public int SeatCols
    {
        get => System.Math.Clamp(db.GetIntSetting("seat_cols", 8), 4, 12);
        set => db.SetSetting("seat_cols", System.Math.Clamp(value, 4, 12).ToString());
    }

    public int SeatRows
    {
        get => System.Math.Clamp(db.GetIntSetting("seat_rows", 6), 2, 10);
        set => db.SetSetting("seat_rows", System.Math.Clamp(value, 2, 10).ToString());
    }

    // ---------------- 可选功能：座位表 ----------------

    /// <summary>
    /// 座位表默认关闭。关着的时候整个程序里不该出现"座位"两个字 ——
    /// 导航没有这一页、名单没有这一列、设置里只剩下面这一个开关本身。
    /// 打开后座位数据（seat_no）一直保留，关掉再打开不会丢。
    /// </summary>
    public bool SeatEnabled
    {
        get => db.GetBoolSetting("seat_enabled", false);
        set => db.SetSetting("seat_enabled", value ? "1" : "0");
    }

    /// <summary>
    /// 教室布局：排 × 组 × 每组座位。
    ///
    /// 老库里只有 seat_rows / seat_cols 两个数（v1.3 之前「座位布局」留下的），
    /// 读的时候当成"一个不分组的大块"，写的时候两个老键跟着一起写 ——
    /// 记分台一屏放几列读的就是 seat_cols，不能只更新新键。
    /// </summary>
    public ClassPoints.Core.SeatLayout SeatLayout
    {
        get
        {
            var stored = db.GetSetting("seat_layout");
            if (!string.IsNullOrWhiteSpace(stored)) return ClassPoints.Core.SeatLayout.Parse(stored);

            if (db.GetSetting("seat_rows") is not null || db.GetSetting("seat_cols") is not null)
                return ClassPoints.Core.SeatLayout.Create(
                    db.GetIntSetting("seat_rows", 6), 1, db.GetIntSetting("seat_cols", 8));

            return ClassPoints.Core.SeatLayout.Default;
        }
        set
        {
            db.SetSetting("seat_layout", value.ToSetting());
            db.SetSetting("seat_rows", value.Rows.ToString());
            db.SetSetting("seat_cols", value.Columns.ToString());
        }
    }

    /// <summary>
    /// 固定座位：自动排座时不动的那些人。只存 id，学生被删掉后读回来时自然失效。
    /// 这个不需要同步 —— 它是"这一次排座的意图"，不是班里的数据。
    /// </summary>
    public HashSet<string> SeatPinned
    {
        get
        {
            var raw = db.GetSetting("seat_pins") ?? "";
            return raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .ToHashSet(StringComparer.Ordinal);
        }
        set => db.SetSetting("seat_pins", string.Join(",", value));
    }

    /// <summary>排座的约束：谁要坐前/中/后、谁必须或不能和谁同桌。全体共用一份。</summary>
    public ClassPoints.Core.SeatRules SeatRules
    {
        get => ClassPoints.Core.SeatRules.Parse(
            db.GetSetting("seat_zones"), db.GetSetting("seat_links"));
        set
        {
            db.SetSetting("seat_zones", value.ZonesValue);
            db.SetSetting("seat_links", value.LinksValue);
        }
    }

    /// <summary>
    /// 自动排座的偏好：尽量不坐上次那个区域。靠最近一份快照判断，没有快照时等于没开。
    /// 默认关 —— 有的班就是要按成绩固定前后，轮换得由老师自己决定。
    /// </summary>
    public bool SeatPreferRotate
    {
        get => db.GetBoolSetting("seat_prefer_rotate", false);
        set => db.SetSetting("seat_prefer_rotate", value ? "1" : "0");
    }

    /// <summary>班委会话：30 秒无操作回看板。有选中项时不计时（方案 9.9）。</summary>
    public int IdleTimeoutSec
    {
        get => System.Math.Clamp(db.GetIntSetting("idle_timeout_sec", 30), 10, 600);
        set => db.SetSetting("idle_timeout_sec", value.ToString());
    }

    /// <summary>管理模式：5 分钟无操作只退管理态，不退身份。</summary>
    public int LockTimeoutSec
    {
        get => System.Math.Clamp(db.GetIntSetting("lock_timeout_sec", 300), 60, 3600);
        set => db.SetSetting("lock_timeout_sec", value.ToString());
    }

    /// <summary>查分页：30 秒严格计时，退出时清屏。</summary>
    public int LookupTimeoutSec
    {
        get => System.Math.Clamp(db.GetIntSetting("lookup_timeout_sec", 30), 10, 300);
        set => db.SetSetting("lookup_timeout_sec", value.ToString());
    }

    public bool SoundOn
    {
        get => db.GetBoolSetting("sound_on", true);
        set => db.SetSetting("sound_on", value ? "1" : "0");
    }

    public int BoardRotateSec
    {
        get => System.Math.Clamp(db.GetIntSetting("board_rotate_sec", 20), 8, 120);
        set => db.SetSetting("board_rotate_sec", value.ToString());
    }

    public bool OnboardingDone
    {
        get => db.GetBoolSetting("onboarding_done", false);
        set => db.SetSetting("onboarding_done", value ? "1" : "0");
    }

    /// <summary>
    /// 这台机器的角色（大屏 / 电脑 / 只读）。null = 还没选过，启动时进向导。
    /// 只写一次错一次的那类设置：改它等于改整台机器的用法，所以要重跑向导。
    /// </summary>
    public AppMode? AppMode
    {
        get => AppModeNames.Parse(db.GetSetting("app_mode"));
        set => db.SetSetting("app_mode", value?.ToDb() ?? "");
    }

    /// <summary>
    /// 外观：light（默认）/ dark / system。
    /// 默认浅色是因为教室投影仪普遍偏暗，浅底黑字投出来最清楚；夜里想省眼可以切深色。
    /// </summary>
    public AppTheme Theme
    {
        get => (db.GetSetting("theme") ?? "").ToLowerInvariant() switch
        {
            "dark" => AppTheme.Dark,
            "system" => AppTheme.System,
            _ => AppTheme.Light,
        };
        set => db.SetSetting("theme", value switch
        {
            AppTheme.Dark => "dark",
            AppTheme.System => "system",
            _ => "light",
        });
    }

    public AbsencePolicy AbsencePolicy
    {
        get => ExamRepository.ParsePolicy(db.GetSetting("absence_policy"));
        set => db.SetSetting("absence_policy", ExamRepository.PolicyToDb(value));
    }

    public string ReaderPort
    {
        get => db.GetSetting("reader_port") ?? "";
        set => db.SetSetting("reader_port", value);
    }

    /// <summary>上次和读卡器握手成功的波特率（0 = 还没成功过）。只用于下次直接命中，不写进界面。</summary>
    public int ReaderBaud
    {
        get => int.TryParse(db.GetSetting("reader_baud"), out var v) ? v : 0;
        set => db.SetSetting("reader_baud", value.ToString());
    }

    /// <summary>
    /// 读卡器类型。两种设备外观几乎一样、协议完全不同：
    /// PN532 需要主机发命令（默认），自动上报型只会在刷卡时自己吐卡号。
    /// 选错的表现是"串口通了但一个字节都没回"，跟坏了一模一样 —— 所以这一项要能手动切。
    /// </summary>
    public ReaderProtocol ReaderProtocol
    {
        get => db.GetSetting("reader_protocol") switch
        {
            "auto" => ReaderProtocol.AutoUid,
            _ => ReaderProtocol.Pn532,
        };
        set => db.SetSetting("reader_protocol", value == ReaderProtocol.AutoUid ? "auto" : "pn532");
    }

    public string RecoveryCodeHash
    {
        get => db.GetSetting("recovery_hash") ?? "";
        set => db.SetSetting("recovery_hash", value);
    }

    /// <summary>恢复码明文只在生成那一刻出现一次，之后只留哈希。</summary>
    public bool HasRecovery => !string.IsNullOrEmpty(RecoveryCodeHash);

    public void SetWindow(double w, double h, int? x, int? y)
    {
        db.SetSetting("window_w", ((int)w).ToString());
        db.SetSetting("window_h", ((int)h).ToString());
        db.SetSetting("window_x", x?.ToString() ?? "");
        db.SetSetting("window_y", y?.ToString() ?? "");
    }

    public (int W, int H, int? X, int? Y) GetWindow()
    {
        var w = db.GetIntSetting("window_w", 0);
        var h = db.GetIntSetting("window_h", 0);
        int? x = int.TryParse(db.GetSetting("window_x"), out var xv) ? xv : null;
        int? y = int.TryParse(db.GetSetting("window_y"), out var yv) ? yv : null;
        return (w, h, x, y);
    }

    /// <summary>上一次备份超过 3 天要提醒，超过 7 天要弹一次。</summary>
    public long LastBackupAt
    {
        get => long.TryParse(db.GetSetting("last_backup_at"), out var v) ? v : 0;
        set => db.SetSetting("last_backup_at", value.ToString());
    }

    // ---------------- 更新 ----------------

    /// <summary>
    /// 公开版本库根地址。留空时用编译期内置地址，所以正常部署根本不用填 ——
    /// 这里留着是为了换分发仓库时不用重打包。
    /// </summary>
    public string UpdateFeed
    {
        get => db.GetSetting("update_feed") ?? "";
        set => db.SetSetting("update_feed", value.Trim());
    }

    /// <summary>启动后台检查。默认开：老师不会主动去点"检查更新"。</summary>
    public bool UpdateAutoCheck
    {
        get => db.GetBoolSetting("update_auto_check", true);
        set => db.SetSetting("update_auto_check", value ? "1" : "0");
    }

    public long UpdateLastCheckAt
    {
        get => long.TryParse(db.GetSetting("update_last_check_at"), out var v) ? v : 0;
        set => db.SetSetting("update_last_check_at", value.ToString());
    }

    /// <summary>被明确跳过的那一版。「跳过」是用户的决定，不该下次启动又问一遍。</summary>
    public string UpdateSkippedVersion
    {
        get => db.GetSetting("update_skipped_version") ?? "";
        set => db.SetSetting("update_skipped_version", value);
    }

    /// <summary>已经弹过提示的版本。同一次启动里只提醒一次，别一直叮。</summary>
    public string UpdateSeenVersion
    {
        get => db.GetSetting("update_seen_version") ?? "";
        set => db.SetSetting("update_seen_version", value);
    }
}
