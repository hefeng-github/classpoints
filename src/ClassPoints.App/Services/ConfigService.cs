using ClassPoints.Core;
using ClassPoints.Core.Config;
using ClassPoints.Data.Config;
using ClassPoints.Data.Sync;

namespace ClassPoints.App.Services;

/// <summary>应用结果：给界面一句人话，外加能不能继续。</summary>
public sealed record ConfigApplyResult(bool Ok, string Message, string? Pulled)
{
    public static ConfigApplyResult Fail(string message) => new(false, message, null);
}

/// <summary>
/// 预配置包的家务活。
///
/// 「家里配好、学校只填分数」这条流程由三个动作拼成：
/// 导出（在家，带上云端地址、班级码、**名单**和可选的管理密码）→ 拷过去 → 导入
/// （在学校，一次把班级名、座位、名单、同步、密码全配好）。
/// </summary>
public sealed class ConfigService(AppServices services)
{
    /// <summary>当前这份安装能导出什么。没配对过就没有云端部分。</summary>
    public ConfigBundle Build(string? pairingPin, string? adminPin, string passphrase, string note)
    {
        var credentials = new SyncStore().Load();
        var active = services.Periods.Active();

        var bundle = new ConfigBundle
        {
            CreatedAt = Clock.NowMs(),
            Note = note,
            ClassName = services.Settings.ClassName,
            TermKey = active?.TermKey ?? "",
            SeatRows = services.Settings.SeatRows,
            SeatCols = services.Settings.SeatCols,
            Roster = services.Students.ListActive()
                .Select(s => new ConfigStudent { Name = s.Name, StudentNo = s.StudentNo, SeatNo = s.SeatNo })
                .ToList(),
            Sync = credentials is null
                ? null
                : new ConfigSync { BaseUrl = credentials.BaseUrl, ClassCode = credentials.ClassCode },
        };

        bundle.Protect(
            string.IsNullOrWhiteSpace(pairingPin) ? null : pairingPin.Trim(),
            string.IsNullOrWhiteSpace(adminPin) ? null : adminPin.Trim(),
            passphrase);

        return bundle;
    }

    /// <summary>给人看的摘要，导出前确认、导入前预览都用它。</summary>
    public string Describe(ConfigBundle bundle)
    {
        var sync = bundle.Sync is null
            ? "未连接云端（纯本地）"
            : $"{bundle.Sync.BaseUrl} · 班级码 {bundle.Sync.ClassCode}";

        var secret = bundle.NeedsPassphrase
            ? "含配对密码" + (bundle.Roster.Count > 0 ? " / 管理密码（口令加密）" : "（口令加密）")
            : "不含密码";

        var roster = bundle.Roster.Count == 0 ? "不带名单" : $"带 {bundle.Roster.Count} 人名单";

        return $"{bundle.ClassName} · {roster}\n" +
               $"{sync}\n{secret}";
    }

    /// <summary>
    /// 把配置包落到这台机器：先写本地设置，再拿班级码 + 口令去云端换令牌。
    /// 口令错、地址不通都会抛 ConfigException，界面直接把话说给老师听。
    /// </summary>
    public async Task<ConfigApplyResult> ApplyAsync(ConfigBundle bundle, string? passphrase,
        string deviceKind, CancellationToken ct = default)
    {
        var problems = bundle.Validate();
        if (problems.Count > 0) throw new ConfigException(string.Join("；", problems));

        // 先解密：口令不对就别改任何设置，免得留下半套配置
        var pin = bundle.RevealPin(passphrase);
        var adminPin = bundle.RevealAdminPin(passphrase);

        services.Settings.ClassName = bundle.ClassName;

        // 配置包只带「排数 / 列数」两个老字段：当成一个不分组的大块恢复。
        // 分组走廊是可选功能打开之后在座位页里调的，包里没有也不该猜。
        services.Settings.SeatLayout = ClassPoints.Core.SeatLayout.Create(
            bundle.SeatRows, 1, bundle.SeatCols);

        var imported = ImportRoster(bundle);

        // 包里带了管理密码就一并设上 —— 这是"家里配好"里最省事的一项：
        // 到学校不用再想一个密码，也不至于因为嫌麻烦干脆不设。
        if (adminPin is { Length: 6 })
        {
            var owner = services.Members.Owner() ?? new Member { DisplayName = "班主任", Role = MemberRole.Owner };
            services.Members.Save(owner, adminPin.Trim());
        }

        var extra = imported > 0 ? $"，导入 {imported} 人名单" : "";

        if (bundle.Sync is not { } sync)
            return new ConfigApplyResult(true, $"已套用「{bundle.ClassName}」的本地配置{extra}。", null);

        services.Settings.ClassCode = sync.ClassCode;

        if (string.IsNullOrEmpty(pin))
            return new ConfigApplyResult(true,
                $"已套用「{bundle.ClassName}」{extra}。包里没有配对密码，去「设置 → 同步」手动配对。", null);

        try
        {
            var report = await services.Sync.PairAsync(sync.BaseUrl, sync.ClassCode, pin, deviceKind, ct);
            if (!report.Ok)
                return ConfigApplyResult.Fail($"本机配置已写入{extra}，但云端配对失败：" + (report.Error ?? "未知原因"));

            var pull = await services.Sync.RunOnceAsync(ct: ct);
            services.Pump.Start();

            var pulled = pull.Ok
                ? $"拉取 {pull.Pulled} 条"
                : $"拉取失败（{pull.Error}），稍后会自动重试";

            // 待审设备：配置套上了、云端也连上了，但这台机器暂时写不了。
            // 不能只说"已连接"，否则老师以为能记分了，点下去才发现全灰。
            if (report.ReadOnly)
                return new ConfigApplyResult(true,
                    $"已连接 {sync.BaseUrl}，班级码 {sync.ClassCode}{extra}。",
                    "这台设备还在等班主任通过，暂时只能查看；通过后会自动恢复记分，不用重新配对。");

            return new ConfigApplyResult(true, $"已连接 {sync.BaseUrl}，班级码 {sync.ClassCode}{extra}。", pulled);
        }
        catch (Exception ex) when (ex is not ConfigException)
        {
            return ConfigApplyResult.Fail($"本机配置已写入{extra}，但云端配对失败：" + ex.Message);
        }
    }

    /// <summary>按姓名去重地导入名单。已经有的不动，返回真正新增的人数。</summary>
    private int ImportRoster(ConfigBundle bundle)
    {
        if (bundle.Roster.Count == 0) return 0;

        var existing = services.Students.ListActive()
            .Select(s => s.Name)
            .ToHashSet(StringComparer.Ordinal);

        var seat = services.Students.ListActive().Select(s => s.SeatNo ?? 0).DefaultIfEmpty(0).Max();
        var added = 0;

        foreach (var row in bundle.Roster)
        {
            var name = NameNormalizer.Clean(row.Name);
            if (name.Length == 0 || !existing.Add(name)) continue;

            services.Students.Insert(new Student
            {
                Name = name,
                StudentNo = row.StudentNo,
                SeatNo = row.SeatNo ?? ++seat,
            });
            added++;
        }

        return added;
    }

    /// <summary>导出后顺手留一份在数据目录，换 U 盘也不丢。</summary>
    public static string? SaveAndKeep(ConfigBundle bundle, string path)
    {
        ConfigBundleFile.Save(bundle, path);
        ConfigBundleFile.KeepCopy(bundle, path);
        return path;
    }
}
