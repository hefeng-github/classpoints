using ClassPoints.Core;
using ClassPoints.Data;
using ClassPoints.Data.Sync;

namespace ClassPoints.Tests;

/// <summary>
/// 端到端同步：两台"设备"各自一个本地库，中间过一遍真的 Worker。
/// 这是唯一能证明"推送格式和云端契约对得上"的测试 ——
/// 单测只能证明本地算得对，证明不了线上那一半没被打断。
/// </summary>
internal static class SyncTests
{
    private sealed class Device : IDisposable
    {
        public Device(string dir)
        {
            Dir = dir;
            Db = new Database(Path.Combine(dir, "classpoints.db"));
            Db.Initialize();
            Outbox = new SyncOutbox(Db);
            Students = new StudentRepository(Db);
            Rules = new RuleRepository(Db);
            Members = new MemberRepository(Db);
            Periods = new PeriodRepository(Db);
            Entries = new EntryService(Db, new AuditRepository(Db));
            Exams = new ExamRepository(Db);
            Counters = new CounterRepository(Db);
            Store = new SyncStore(Path.Combine(dir, "sync.json"));
            Sync = new SyncService(Db, Outbox, Store);
        }

        public string Dir { get; }
        public Database Db { get; }
        public SyncOutbox Outbox { get; }
        public StudentRepository Students { get; }
        public RuleRepository Rules { get; }
        public MemberRepository Members { get; }
        public PeriodRepository Periods { get; }
        public EntryService Entries { get; }
        public ExamRepository Exams { get; }
        public CounterRepository Counters { get; }
        public SyncService Sync { get; }

        /// <summary>落盘的凭据。测试要故意把它弄坏，模拟"续期在别处被换掉"。</summary>
        public SyncStore Store { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Dir, true);
            }
            catch (Exception)
            {
                // 临时目录删不掉不影响结论
            }
        }
    }

    public static async Task RunAsync(string baseUrl, Action<string, bool, string> report)
    {
        // 断言工具的本地别名：Detail 可省，调用处才不至于到处写空字符串
        void check(string name, bool ok, string detail = "") => report(name, ok, detail);

        var root = Path.Combine(Path.GetTempPath(), "cp-sync-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);

        using var a = new Device(Path.Combine(root, "a"));
        using var b = new Device(Path.Combine(root, "b"));

        var periodA = a.Periods.Rollover();
        var ruleA = a.Rules.Save(new Rule { Label = "回答问题", Delta = 5, Icon = "✋" });
        var ownerA = a.Members.Save(new Member { DisplayName = "张老师", Role = MemberRole.Owner }, pin: "123456");

        var students = new[] { "杨欣怡", "陈芷若", "刘书豪" }
            .Select(n => a.Students.Insert(new Student { Name = n }))
            .ToList();

        var award = a.Entries.ApplyBatch(
            students.Select(s => new AwardLine(s.Id, ruleA, 5)).ToList(),
            ownerA, "pin", periodA.Id);
        check("本地先记账成功（不等网络）", award.Count == 3 && a.Outbox.Count() > 0,
            $"entries={award.Count} outbox={a.Outbox.Count()}");

        // ---- 建班 ----
        var created = await a.Sync.CreateClassAsync(baseUrl, "高二 (3) 班", "张老师", "123456", "REC-8888", "desk");
        var code = created.CreatedCode ?? "";
        check("建班拿到班级码", created.Ok && code.Length == 8, code);

        var pushA = await a.Sync.RunOnceAsync();
        check("第一轮推送成功", pushA.Ok, pushA.Error ?? "");
        if (!pushA.Ok) Console.WriteLine("      push error: " + pushA.Error);
        check("推完队列清空", a.Outbox.Count() == 0, a.Outbox.Count().ToString());
        check("确实推了东西", pushA.Pushed >= 3, pushA.Pushed.ToString());

        // ---- 另一端配对 + 拉取 ----
        var paired = await b.Sync.PairAsync(baseUrl, code, "123456", "desk");
        check("第二台设备配对成功", paired.Ok, paired.Error ?? "");

        var pullB = await b.Sync.RunOnceAsync();
        check("第二轮拉取成功", pullB.Ok, pullB.Error ?? "");
        var mine = b.Students.ListActive().Where(st => students.Any(x => x.Id == st.Id)).ToList();
        check("名单拉过来了", mine.Count == 3, mine.Count.ToString());

        var pulledPeriod = b.Periods.Active();
        check("周期拉过来了", pulledPeriod is not null);
        // 新配对设备本地自带一个「第 1 周期」，它不能把云端真正的当前周期顶掉 ——
        // 顶掉之后这台机器会停在一个空周期上，历史流水一条都看不见。
        check("配对设备没有顶掉云端的当前周期", pulledPeriod?.Id == periodA.Id,
            $"{pulledPeriod?.Id} vs {periodA.Id}");
        // D1 是全库共享的（一个部署一个班），多次跑测试会累积别的班的数据，
        // 所以只数本次造出来的那几个人
        var ids = students.Select(x => x.Id).ToHashSet();
        List<Entry> MineEntries() => pulledPeriod is null
            ? []
            : b.Entries.Entries(pulledPeriod.Id).Where(e => ids.Contains(e.StudentId)).ToList();
        var pulledEntries = MineEntries();
        check("流水拉过来了", pulledEntries.Count == 3, pulledEntries.Count.ToString());
        check("积分合计对得上", pulledEntries.Sum(e => e.Delta) == 15, pulledEntries.Sum(e => e.Delta).ToString());
        check("规则拉过来了", b.Rules.ListAll().Any(r => r.Label == "回答问题"));

        // ---- 事件计数：定义和打点都要跨机 ----
        // 教室大屏上点的次数，班主任在办公室电脑上得能看见；云端算次数用 SUM(value)，
        // 所以两台机器各 +1 加起来自然是 2，不需要在云端存一个会打架的"当前次数"。
        var counterA = a.Counters.Save(new Counter { Label = "迟到", Icon = IconCatalog.Flag });
        var first = a.Counters.AddTick(counterA.Id, students[0].Id, periodA.Id, 1, ownerA);
        var second = a.Counters.AddTick(counterA.Id, students[0].Id, periodA.Id, 1, ownerA);
        check("事件本地记两次", first == 1 && second == 2, $"{first}/{second}");

        var pushTick = await a.Sync.RunOnceAsync();
        check("事件定义与打点推送成功", pushTick.Ok, pushTick.Error ?? "");
        var pullTick = await b.Sync.RunOnceAsync();
        check("另一端收到事件定义",
            b.Counters.ListAll(true).Any(c => c.Id == counterA.Id && c.Label == "迟到"));
        var ticksB = b.Counters.Total(pulledPeriod!.Id, students[0].Id, counterA.Id);
        check("另一端次数一致（2 次）", ticksB == 2, ticksB.ToString());

        // 重推一遍：打点按 id 幂等，不能变成 4 次
        await a.Sync.RunOnceAsync();
        await b.Sync.RunOnceAsync();
        var ticksAgain = b.Counters.Total(pulledPeriod!.Id, students[0].Id, counterA.Id);
        check("重跑不会把次数算重", ticksAgain == 2, ticksAgain.ToString());

        // ---- 本地学号撞车：不许把整页拉取拖死 ----
        // 两台设备各自导入名单，同一个学号会落到两个不同的 id 上。以前这种行一插就撞
        // uq_students_no，整页事务回滚、游标不动，界面上只剩"同步失败"四个字 ——
        // 而且每一轮都从同一页重新失败，永远好不了。
        var numbered = a.Students.Get(students[0].Id)!;
        numbered.StudentNo = "01";
        a.Students.Update(numbered);
        var pushNo = await a.Sync.RunOnceAsync();
        check("学号推送成功", pushNo.Ok, pushNo.Error ?? "");

        using (var connSeed = b.Db.Open())
            Database.Exec(connSeed, """
                INSERT INTO students (id, name, student_no, active, rev, updated_at, created_at)
                VALUES ('local_dup', '本地插班生', '01', 1, 1, $now, $now)
                """, ("$now", Clock.NowMs()));

        var pullClash = await b.Sync.RunOnceAsync();
        check("学号撞车时整页照样落库（不是同步失败）", pullClash.Ok, pullClash.Error ?? "");
        check("撞车算进「已合并」，不是静默跳过", pullClash.Conflicts >= 1, pullClash.Conflicts.ToString());
        var clashRows = b.Students.ListAll(true).Where(x => x.Id == students[0].Id || x.Id == "local_dup").ToList();
        check("两个学生都还在（只是学号只归一个人）", clashRows.Count == 2, clashRows.Count.ToString());
        check("学号归云端那一行（云端是权威）",
            clashRows.FirstOrDefault(x => x.Id == students[0].Id)?.StudentNo == "01",
            clashRows.FirstOrDefault(x => x.Id == students[0].Id)?.StudentNo ?? "null");

        var pullAgain = await b.Sync.RunOnceAsync();
        check("同一处冲突不会每轮都算一次（第二次为 0）",
            pullAgain.Ok && pullAgain.Conflicts == 0, $"ok={pullAgain.Ok} conflicts={pullAgain.Conflicts}");

        // ---- 凭据被别处换掉：不问人，自己重配回来 ----
        // 现场就是这种情况：同一份数据目录被两台机器用，先续期的那台把后一台的
        // refresh token 顶掉。以前只能弹"请重新配对"，老师得停下去输十分钟前才输过的密码。
        var healed = 0;
        b.Sync.AutoRepaired += () => healed++;
        var credentials = b.Store.Load()!;
        b.Store.Save(credentials with { Access = "token-rotated-elsewhere", Refresh = "token-rotated-elsewhere" });

        var afterRotation = await b.Sync.RunOnceAsync();
        check("凭据被换掉后能自己接上（不需要人输密码）",
            afterRotation.Ok && healed == 1,
            $"ok={afterRotation.Ok} healed={healed} err={afterRotation.Error}");
        check("接上之后没把机器变成只读", !afterRotation.NeedsPairing && !b.Sync.IsReadOnly,
            $"pairing={afterRotation.Reason}");
        check("接上之后拿到的是新凭据",
            b.Store.Load()?.Refresh is { Length: > 0 } r && r != "token-rotated-elsewhere");

        // 再接不上就该好好说人话了：口令改过 → 明确说"用现在的密码再连一次"
        var wrong = b.Store.Load()!;
        b.Store.Save(wrong with { Access = "token-rotated-elsewhere", Refresh = "token-rotated-elsewhere", Secret = "000000" });
        var brokenRepair = await b.Sync.RunOnceAsync();
        check("自动重配失败时给出「密码改过了」而不是「网络不好」",
            brokenRepair.NeedsPairing && brokenRepair.Reason == PairingReason.SecretRejected,
            $"needsPairing={brokenRepair.NeedsPairing} reason={brokenRepair.Reason} err={brokenRepair.Error}");
        check("自动重配失败后本机数据一条不丢", b.Students.ListActive().Count >= 3, "");

        // 把对的凭据换回来，后面几段照常跑
        b.Store.Save(wrong);
        var restored = await b.Sync.RunOnceAsync();
        check("手动换回正确凭据后恢复同步", restored.Ok, restored.Error ?? "");

        // ---- 幂等：重推一次不应该多出流水 ----
        var replay = await a.Sync.RunOnceAsync();
        var afterReplay = await b.Sync.RunOnceAsync();
        var countAgain = MineEntries().Count;
        check("重跑同步不会重复计分", countAgain == 3, $"after={countAgain} replay={replay.Ok}/{afterReplay.Ok}");

        // ---- 成绩：一场考试 + 全部分数，整批一个事务 ----
        var imported = a.Exams.ImportScores(periodA.Id, "期中考试", 750, "2026-11-01",
        [
            (students[0].Id, 712d, false),
            (students[1].Id, 688d, false),
            (students[2].Id, null, true),
        ], "期中.xlsx");
        check("成绩本地导入", imported.Written == 2 && imported.Absent == 1);

        var pushExam = await a.Sync.RunOnceAsync();
        check("成绩推送成功", pushExam.Ok, pushExam.Error ?? "");

        var pullExam = await b.Sync.RunOnceAsync();
        var examB = b.Exams.ListExams(pulledPeriod!.Id).FirstOrDefault(e => e.Name == "期中考试");
        check("考试拉过来了", examB is not null);
        if (examB is not null)
        {
            var scoresB = b.Exams.Scores(examB.Id);
            check("成绩条数对得上", scoresB.Count == 3, scoresB.Count.ToString());
            check("缺考不存 0 分", scoresB.Count(s => s.Absent) == 1 && scoresB.First(s => s.Absent).Score is null);
        }

        // ---- 撤销：冲销行也要同步过去 ----
        var reversed = a.Entries.ReverseSingle(award.Entries[0].Id, ownerA, "pin");
        check("本地撤销成了一条冲销行", reversed > 0);

        var pushReverse = await a.Sync.RunOnceAsync();
        check("撤销推送成功", pushReverse.Ok, pushReverse.Error ?? "");
        var pullReverse = await b.Sync.RunOnceAsync();
        check("撤销推送成功（拉取那侧也正常）", pullReverse.Ok, pullReverse.Error ?? "");

        // 界面上看不到冲销行（列表按设计把 reverses_entry_id 非空的过滤掉了），
        // 所以这里直接查库：冲销行到没到、原行有没有被打上 reversed_at。
        using var connB = b.Db.Open();
        var reversalInB = Convert.ToInt32(Database.Scalar(connB,
            "SELECT COUNT(*) FROM entries WHERE reverses_entry_id = $id", ("$id", award.Entries[0].Id)));
        var reversedAtInB = Database.Scalar(connB,
            "SELECT reversed_at FROM entries WHERE id = $id", ("$id", award.Entries[0].Id));
        var totalAfter = Convert.ToInt32(Database.Scalar(connB,
            $"SELECT COALESCE(SUM(delta),0) FROM entries WHERE period_id = $p AND student_id IN ({string.Join(",", ids.Select((_, i) => "$s" + i))})",
            [("$p", pulledPeriod!.Id), .. ids.Select((id, i) => ("$s" + i, (object?)id))]));

        check("另一端收到了冲销行", reversalInB == 1, reversalInB.ToString());
        check("原行在另一端被标记为已撤销", reversedAtInB is long, reversedAtInB?.ToString() ?? "null");
        check("另一端的合计也跟着抵回来了", totalAfter == 10, totalAfter.ToString());

        // ---- 断网：没配对时同步必须安静地跳过，队列一条都不许丢 ----
        using var offline = new Device(Path.Combine(root, "c"));
        offline.Outbox.Enqueue("probe:" + Guid.NewGuid().ToString("N"), "student",
            new { action = "upsert", student = new Student { Id = "probe", Name = "断网探针" } });
        var brokenPair = new SyncService(offline.Db, offline.Outbox,
            new SyncStore(Path.Combine(root, "c", "sync.json")));
        var offlineReport = await brokenPair.RunOnceAsync();
        check("没配对时同步直接跳过、不抛异常", !offlineReport.Ok && offline.Outbox.Count() == 1);
        check("跳过原因说得清楚", offlineReport.Error is not null, offlineReport.Error ?? "");

        Console.WriteLine($"  . 测试目录：{root}");
    }
}
