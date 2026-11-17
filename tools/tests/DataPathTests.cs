using ClassPoints.Data;

namespace ClassPoints.Tests;

/// <summary>
/// 数据目录该落在哪（<see cref="DataPaths"/>）。
///
/// 为什么单测这一块：它决定的是"库、日志、备份、同步凭据"整串东西的位置，
/// 判断错一次不是崩溃，而是**换了台机器启动发现名单空了**——用户以为数据丢了。
/// 这里把几条分支各自钉住：已有库优先、老库搬家、只读的 Program Files、全新机器、指针文件。
/// 全部在临时沙箱里跑：判断与搬家都接受注入的目录，绝不碰这台机器上真的用户数据。
/// </summary>
internal static class DataPathTests
{
    public static void Run(Action<string, bool, string> check)
    {
        var sandboxes = new List<string>();
        (string Root, string Exe, string Local) Make()
        {
            var root = Path.Combine(Path.GetTempPath(), "cp-paths-" + Guid.NewGuid().ToString("N")[..8]);
            var exe = Path.Combine(root, "app");
            var local = Path.Combine(root, "localapp");
            Directory.CreateDirectory(exe);
            Directory.CreateDirectory(local);
            sandboxes.Add(root);
            return (root, exe, local);
        }

        // 临时目录在系统盘上，所以它的盘符就是"系统盘"；另一个盘符只是拿来比字符串。
        var system = Path.GetPathRoot(Path.GetTempPath()) ?? @"C:\";
        const string OtherDrive = @"Z:\";

        string Old(string local) => Path.Combine(local, DataPaths.LocalFolder);

        try
        {
            // ① 程序目录里已经有库 —— 便携版、以及装到非系统盘之后都走这里
            {
                var (_, exe, local) = Make();
                var portable = Path.Combine(exe, DataPaths.PortableFolder);
                Directory.CreateDirectory(portable);
                File.WriteAllText(Path.Combine(portable, DataPaths.DbName), "x");

                var choice = DataPaths.Resolve(exe, local, null, system);
                check("程序目录里已有库时就用它（跟盘符无关）",
                    DataPaths.SameDir(choice.Root, portable), choice.Root);
            }

            // ①b 装在 Program Files 下（任何盘）：库里就算有数据也不能直接用。
            // 那个库是管理员建出来的，普通权限写的时候要建 -wal、库被删了要新建，
            // 都被目录权限挡住 —— 现场报的就是 SQLITE_CANTOPEN(14)，程序进不去。
            {
                var (_, _, local) = Make();
                var programFilesExe = Path.Combine(@"E://Program Files", "ClassPoints");
                var portable = Path.Combine(programFilesExe, DataPaths.PortableFolder);

                var choice = DataPaths.Resolve(programFilesExe, local, null, system,
                    hasDb: dir => DataPaths.SameDir(dir, portable));

                check("Program Files 里的库要改放用户目录",
                    DataPaths.SameDir(choice.Root, Old(local)), choice.Root + " · " + choice.Reason);
                check("搬迁来源是 Program Files 里那个库",
                    choice.AdoptFrom is not null && DataPaths.SameDir(choice.AdoptFrom, portable),
                    choice.AdoptFrom ?? "null");
            }

            check("Program Files 下的目录一律判为不可写",
                !DataPaths.CanCreate(Path.Combine(@"E://Program Files", "ClassPoints", "data")), "");
            check("Windows 目录同理",
                !DataPaths.CanCreate(Path.Combine(@"C://Windows", "System32", "ClassPoints", "data")), "");

            // ② 老库在 %LOCALAPPDATA%，程序装在非系统盘：搬过去，原件不删
            {
                var (_, exe, local) = Make();
                var oldRoot = Old(local);
                Directory.CreateDirectory(oldRoot);
                File.WriteAllText(Path.Combine(oldRoot, DataPaths.DbName), "old");
                File.WriteAllText(Path.Combine(oldRoot, "sync.json"), "{}");

                var portable = Path.Combine(exe, DataPaths.PortableFolder);

                // 判断这一步不许碰磁盘
                var plan = DataPaths.Resolve(exe, local, null, OtherDrive);
                check("判断阶段就指出该搬哪份过来",
                    plan.AdoptFrom is { } from && DataPaths.SameDir(from, oldRoot), plan.AdoptFrom ?? "null");
                check("判断阶段不建目录（跑测试不该改动机器状态）",
                    !Directory.Exists(portable), "");

                var choice = DataPaths.Prepare(exe, local, null, OtherDrive);
                check("老库会自动搬到程序目录（非系统盘）",
                    DataPaths.SameDir(choice.Root, portable), choice.Root);
                check("搬过去的库真的在", File.Exists(Path.Combine(portable, DataPaths.DbName)), "");
                check("同步凭据也跟着走（不然新位置要重新配对）",
                    File.Exists(Path.Combine(portable, "sync.json")), "");
                check("原目录里的库不删（搬错了还能找回）",
                    File.Exists(Path.Combine(oldRoot, DataPaths.DbName)), "");

                // 下次启动：靠"本账号搬过"这条记录认出程序目录里那份是自己的
                var again = DataPaths.Prepare(exe, local, null, OtherDrive, adopted: DataPaths.ReadAdopted(local));
                check("重启后还是那份，不会又读回旧库",
                    DataPaths.SameDir(again.Root, portable), again.Root);

                // 同一份安装上的另一个账号：没搬过、自己也有旧数据 → 用自己的
                var other = DataPaths.Prepare(exe, local, null, OtherDrive);
                check("另一个账号没搬过，就不会被顶成别人的班",
                    DataPaths.SameDir(other.Root, oldRoot), other.Root);
            }

            // ②b 同一台机器第二个账号：别人搬过去的库在那儿，这人自己有旧数据、也没搬过
            {
                var (_, exe, local) = Make();
                var portable = Path.Combine(exe, DataPaths.PortableFolder);
                Directory.CreateDirectory(portable);
                File.WriteAllText(Path.Combine(portable, DataPaths.DbName), "another-teacher");

                var oldRoot = Old(local);
                Directory.CreateDirectory(oldRoot);
                File.WriteAllText(Path.Combine(oldRoot, DataPaths.DbName), "mine");

                var choice = DataPaths.Resolve(exe, local, null, OtherDrive);
                check("第二个账号看到的是自己的班，不是别人搬过去的那份",
                    DataPaths.SameDir(choice.Root, oldRoot), choice.Root);
            }

            // ③ 程序目录在系统盘（典型：C:\Program Files）：不搬，沿用旧位置
            {
                var (_, exe, local) = Make();
                var oldRoot = Old(local);
                Directory.CreateDirectory(oldRoot);
                File.WriteAllText(Path.Combine(oldRoot, DataPaths.DbName), "old");

                var choice = DataPaths.Resolve(exe, local, null, system);
                check("装在系统盘时沿用 %LOCALAPPDATA%（搬家不省 C 盘，白折腾）",
                    DataPaths.SameDir(choice.Root, oldRoot), choice.Root);
                check("装在系统盘时不提搬家", choice.AdoptFrom is null, choice.AdoptFrom ?? "");
            }

            // ④ 全新机器 + 程序目录在非系统盘：数据就跟程序走
            {
                var (_, exe, local) = Make();
                var choice = DataPaths.Prepare(exe, local, null, OtherDrive);
                var portable = Path.Combine(exe, DataPaths.PortableFolder);
                check("全新机器优先用程序目录（非系统盘）",
                    DataPaths.SameDir(choice.Root, portable), choice.Root);
                check("全新机器也不在判断阶段建目录（交给数据库启动时建）",
                    !Directory.Exists(portable), "");
            }

            // ⑤ 全新机器 + 系统盘：退回 %LOCALAPPDATA%
            {
                var (_, exe, local) = Make();
                var choice = DataPaths.Resolve(exe, local, null, system);
                check("全新机器装在系统盘时退回 %LOCALAPPDATA%",
                    DataPaths.SameDir(choice.Root, Old(local)), choice.Root);
            }

            // ⑥ 程序目录不可写（真·Program Files）：不搬，也不会搬一半
            {
                var (_, exe, local) = Make();
                var oldRoot = Old(local);
                Directory.CreateDirectory(oldRoot);
                File.WriteAllText(Path.Combine(oldRoot, DataPaths.DbName), "old");

                var choice = DataPaths.Prepare(exe, local, null, OtherDrive,
                    hasDb: dir => File.Exists(Path.Combine(dir, DataPaths.DbName)),
                    canCreate: _ => false);
                check("程序目录写不进去时退回旧位置，不会两头落空",
                    DataPaths.SameDir(choice.Root, oldRoot), choice.Root);
                check("程序目录写不进去时不留空 data 目录",
                    !Directory.Exists(Path.Combine(exe, DataPaths.PortableFolder)), "");
                check("没搬成就不写指针（否则下次启动去一个不存在的库）",
                    DataPaths.ReadPointer(local) is null, DataPaths.ReadPointer(local) ?? "null");
            }

            // ⑦ 指针指到哪就用哪，哪怕那个目录现在不在（盘没插上）
            //    这时宁可启动报"数据库打不开"，也不能悄悄在别处建一个空库让老师以为名单没了。
            {
                var (root, exe, local) = Make();
                var gone = Path.Combine(root, "moved-away");
                var choice = DataPaths.Resolve(exe, local, gone, OtherDrive);
                check("指针目录不在也认它，不偷偷换地方",
                    DataPaths.SameDir(choice.Root, gone), choice.Root);
            }

            // ⑧ 指针跟着程序目录走，不抢别人的
            {
                var (root, exe, local) = Make();
                var other = Path.Combine(root, "other-app");
                Directory.CreateDirectory(other);
                var target = Path.Combine(root, "moved");
                Directory.CreateDirectory(target);

                DataPaths.SavePointer(target, exe, local);
                check("指针写在程序目录里", File.Exists(DataPaths.PointerFile(exe)), "");
                check("这份程序读得到自己的指针",
                    DataPaths.ReadPointer(exe, local) is { } written && DataPaths.SameDir(written, target),
                    DataPaths.ReadPointer(exe, local) ?? "null");
                check("另一份程序读不到别人的指针（否则开发构建会把真安装拐跑）",
                    DataPaths.ReadPointer(other, local) is null, DataPaths.ReadPointer(other, local) ?? "null");
            }

            // ⑨ 盘符判断本身
            check("同一块盘比得出", DataPaths.SameDrive(Path.Combine(system, "a", "b"), system), "");
            check("不同盘比得出", !DataPaths.SameDrive(Path.Combine(OtherDrive, "a"), system), "");

            // ⑩ 设置里「更改位置」走的那条路：复制一致性副本 + 写指针，原目录不删
            {
                var (root, _, local) = Make();
                var from = Path.Combine(root, "data-from");
                var to = Path.Combine(root, "data-to");
                Directory.CreateDirectory(from);

                var db = new Database(Path.Combine(from, DataPaths.DbName));
                db.Initialize();
                var backups = new BackupService(db);

                var moved = backups.MoveDataRoot(to);
                check("更改数据位置：成功", moved.Ok, moved.Message);
                check("新目录里有库", File.Exists(Path.Combine(to, DataPaths.DbName)), "");
                check("指针指向新目录",
                    DataPaths.ReadPointer(AppContext.BaseDirectory, local) is { } p && DataPaths.SameDir(p, to),
                    DataPaths.ReadPointer(AppContext.BaseDirectory, local) ?? "null");
                check("原目录的库不删", File.Exists(Path.Combine(from, DataPaths.DbName)), "");
                check("目标已有数据时拒绝搬（不会覆盖别人的库）",
                    !backups.MoveDataRoot(to).Ok, "");
                check("搬到原处时拒绝（没意义且容易误删）",
                    !backups.MoveDataRoot(from).Ok, "");

                DataPaths.ClearPointer(AppContext.BaseDirectory, local);
                check("清理指针", DataPaths.ReadPointer(AppContext.BaseDirectory, local) is null, "");
            }
        }
        finally
        {
            foreach (var dir in sandboxes)
            {
                try { Directory.Delete(dir, true); } catch { /* 临时目录，删不掉无所谓 */ }
            }
        }
    }
}
