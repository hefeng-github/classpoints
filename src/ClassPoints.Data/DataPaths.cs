namespace ClassPoints.Data;

/// <summary>
/// 数据（库、日志、同步凭据、更新包、默认备份）到底落在哪个目录。
///
/// 三条现实约束决定了这里的顺序：
/// ① 装到 Program Files 时安装目录是只读的，硬把库放进去就是"一启动就报错"；
/// ② 教室一体机的 C 盘往往很小，几百兆的更新包和每天的备份不该再往里塞；
/// ③ 老用户的数据已经在 %LOCALAPPDATA% 里，改默认值不能让它突然"消失"。
///
/// 所以：**程序目录能写、又不在系统盘**时，数据跟着程序走（安装目录\data）；
/// 否则退回 %LOCALAPPDATA%\ClassPoints。中途换过位置就把选择写进指针文件，
/// 之后不管从哪个 exe 副本启动，读到的都是同一份数据。
///
/// 这里刻意分成两步：<see cref="Resolve"/> 只判断、不碰磁盘（测试可以直接调），
/// <see cref="Prepare"/> 才真的建目录、搬文件 —— 它只在应用启动时调用一次。
/// 混成一步的后果很实际：跑一遍单测，就把这台机器上真的用户数据搬去了测试输出目录。
/// </summary>
public static class DataPaths
{
    /// <summary>程序目录下的数据子目录名。不直接摊在程序目录里 —— 那儿全是 DLL。</summary>
    public const string PortableFolder = "data";

    public const string LocalFolder = "ClassPoints";

    /// <summary>指针文件名。位置固定在 %LOCALAPPDATA%\ClassPoints，不能跟着数据跑。</summary>
    public const string PointerName = "classpoints.dir";

    public const string DbName = "classpoints.db";

    /// <summary>搬家时要一起带走的小文件。备份、日志、更新包不带：体积大且可重建。</summary>
    private static readonly string[] Carried =
    [
        DbName, DbName + "-wal", DbName + "-shm", "sync.json", "session.bin", "recovery.bin",
        "classpoints.cpcfg",
    ];

    /// <summary>
    /// 判断结果。<see cref="AdoptFrom"/> 非空表示"要先把那个目录里的库搬过来"，
    /// 这一步由 <see cref="Prepare"/> 执行 —— 判断本身不动磁盘。
    /// </summary>
    public readonly record struct Choice(string Root, string Reason, string? AdoptFrom = null);

    /// <summary>系统盘，例如 <c>C:\</c>。取不到就按 C 盘算（Windows 上几乎总是它）。</summary>
    public static string SystemDrive
    {
        get
        {
            try
            {
                var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                var root = Path.GetPathRoot(windows);
                return string.IsNullOrEmpty(root) ? @"C:\" : root;
            }
            catch (Exception)
            {
                return @"C:\";
            }
        }
    }

    public static string LocalAppData
    {
        get
        {
            try
            {
                var dir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                return string.IsNullOrEmpty(dir) ? @"C:\Users\Public" : dir;
            }
            catch (Exception)
            {
                return @"C:\Users\Public";
            }
        }
    }

    /// <summary>程序目录下那个数据目录（不管存不存在）。</summary>
    public static string PortableRoot(string exeDir) => Path.Combine(exeDir, PortableFolder);

    /// <summary>老位置（也是装到 Program Files 时的唯一可用位置）。</summary>
    public static string LocalRoot(string localApp) => Path.Combine(localApp, LocalFolder);

    /// <summary>指针文件在程序目录里的那一份。优先读它：一份程序一份记录，互不干扰。</summary>
    public static string PointerFile(string dir) => Path.Combine(dir, PointerName);

    /// <summary>程序目录写不进去（装在 Program Files）时的备用位置。</summary>
    public static string LocalPointerFile(string localApp) => Path.Combine(LocalRoot(localApp), PointerName);

    /// <summary>
    /// "这个账号把旧数据搬到程序目录去了"的记录。按账号放在 %LOCALAPPDATA% 下：
    /// 同一份安装被两个账号用的时候，只有搬过的那个人认程序目录里那份数据，
    /// 另一个人的班还在自己的 AppData 里，不会被顶掉。
    /// </summary>
    public static string AdoptFile(string localApp) => Path.Combine(LocalRoot(localApp), "adopted.txt");

    /// <summary>
    /// 判断该用哪个目录。纯函数式的：除了 <see cref="File.Exists"/> 不碰磁盘，
    /// 也不会建目录，测试可以直接调它（见 DataPathTests）。
    /// </summary>
    public static Choice Resolve(string exeDir, string localApp, string? pointer, string systemDrive,
        Func<string, bool>? hasDb = null, Func<string, bool>? canCreate = null, string? adopted = null)
    {
        hasDb ??= HasDb;
        canCreate ??= CanCreate;

        var portable = PortableRoot(exeDir);
        var local = LocalRoot(localApp);

        // ① 指针文件：用户手选过（设置里「更改位置」）。它说了算 —— 不然从别的 exe 副本启动
        //    就会读回旧库。指到哪就是哪，哪怕那块盘现在没插上（启动时会明确报"打不开"，
        //    比悄悄在别处建一个空库、让老师以为名单没了要好）。
        if (!string.IsNullOrWhiteSpace(pointer)) return new(pointer.Trim(), "记下的位置");

        // ② 程序目录里已经有库 —— 便携版、以及搬过一次的安装版都走这里。
        if (hasDb(portable))
        {
            var mine = !string.IsNullOrWhiteSpace(adopted) && SameDir(adopted, portable);

            // 同一台机器两个 Windows 账号共用一份安装（机房里很常见）：这份数据是
            // 上一个账号搬过去的，而这人自己还有旧数据、也没搬过 —— 那就还是用他自己的，
            // 不能把别人班的名单顶到他眼前。
            if (!mine && hasDb(local) && !SameDir(portable, local)) return new(local, "本账号原有的位置");

            return new(portable, "程序目录");
        }

        // ③ 老库在 %LOCALAPPDATA% 里。装到非系统盘就顺手搬过去（只复制、不删原件），
        //    既省 C 盘又不至于让人以为数据丢了；装到 Program Files 时没得选，照旧。
        if (hasDb(local))
        {
            // 条件只看目标：搬到程序目录能省下系统盘空间才算值得搬。源一定在系统盘
            // （AppData 就在那儿），拿它当条件等于永远不搬。
            if (!SameDrive(portable, systemDrive) && canCreate(portable))
                return new(portable, "从旧位置迁到程序目录", local);

            return new(local, "旧位置");
        }

        // ④ 全新机器：优先程序目录（非系统盘），否则退回 %LOCALAPPDATA%。
        if (!SameDrive(portable, systemDrive) && canCreate(portable)) return new(portable, "程序目录（新建）");
        return new(local, "新建于旧位置");
    }

    /// <summary>
    /// 启动时执行一次：判断 + 搬家。**只有在应用启动时调**。
    ///
    /// 搬家之后不写指针文件（只写一条按账号的 adopted.txt）：程序目录里已经有库了，
    /// 下次启动第②条就能认出来。指针只属于"用户手选了别处"这一种情况，
    /// 少一种写指针的场合就少一种被带偏的可能。
    /// </summary>
    public static Choice Prepare()
    {
        var exeDir = AppContext.BaseDirectory;
        var localApp = LocalAppData;
        return Prepare(exeDir, localApp, ReadPointer(exeDir, localApp), SystemDrive,
            adopted: ReadAdopted(localApp));
    }

    public static Choice Prepare(string exeDir, string localApp, string? pointer, string systemDrive,
        Func<string, bool>? hasDb = null, Func<string, bool>? canCreate = null, string? adopted = null)
    {
        var choice = Resolve(exeDir, localApp, pointer, systemDrive, hasDb, canCreate, adopted);
        if (choice.AdoptFrom is not { } from) return choice;

        if (TryAdopt(from, choice.Root))
        {
            // 记下"是本账号搬的"。不写这个，下次启动就会以为程序目录里那份是别人的，
            // 又缩回旧库 —— 表现为"每次启动都少一段记录"。
            MarkAdopted(choice.Root, localApp);
            return choice;
        }

        // 搬不过去（目标盘只读、旧库被占用……）就老实待在原地：
        // 搬到一半的状态比不搬危险得多。
        return new(LocalRoot(localApp), "旧位置（搬迁失败）");
    }

    public static void MarkAdopted(string root, string? localApp = null)
    {
        try
        {
            var file = AdoptFile(localApp ?? LocalAppData);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, root);
        }
        catch (Exception)
        {
            // 记不下就下次再搬一次：TryAdopt 不会覆盖已有文件，也不会丢数据
        }
    }

    public static string? ReadAdopted(string? localApp = null)
    {
        try
        {
            var file = AdoptFile(localApp ?? LocalAppData);
            if (!File.Exists(file)) return null;
            var text = File.ReadAllText(file).Trim();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>这个路径在不在系统盘上。系统盘通常就是空间最紧张的那一个。</summary>
    public static bool IsOnSystemDrive(string path) => SameDrive(path, SystemDrive);

    public static bool SameDrive(string path, string driveRoot)
    {
        try
        {
            var own = Path.GetPathRoot(Path.GetFullPath(path)) ?? "";
            var other = Path.GetPathRoot(Path.GetFullPath(driveRoot)) ?? driveRoot;
            return own.Length > 0 && other.Length > 0 &&
                   string.Equals(own.TrimEnd('\\'), other.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static bool SameDir(string a, string b)
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

    public static bool HasDb(string dir)
    {
        try
        {
            return File.Exists(Path.Combine(dir, DbName));
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 能不能在这里建目录。探的是**上一层**（程序目录本身），不是候选的 data 目录 ——
    /// 探完就把空目录留下，最后没用上它就成了一个永远空着的 data，看着像程序出过毛病。
    /// </summary>
    public static bool CanCreate(string dir)
    {
        try
        {
            if (Directory.Exists(dir)) return true;
            var parent = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
            return !string.IsNullOrEmpty(parent) && CanWriteIn(parent);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 把旧目录里的小文件复制过来。全部成功才算成功 —— 只拷到一半就换目录，
    /// 换过去的是"少了最近几笔记录"的库，而人还以为数据完整。原件不动：出问题还能找回去。
    /// </summary>
    public static bool TryAdopt(string from, string to)
    {
        var copied = new List<string>();
        try
        {
            if (!HasDb(from)) return false;
            Directory.CreateDirectory(to);

            foreach (var name in Carried)
            {
                var source = Path.Combine(from, name);
                if (!File.Exists(source)) continue;

                var target = Path.Combine(to, name);
                if (File.Exists(target)) continue;

                File.Copy(source, target, overwrite: false);
                copied.Add(target);
            }

            if (File.Exists(Path.Combine(to, DbName))) return true;
        }
        catch (Exception)
        {
            // 走到这里说明源目录读不了或目标写不了，宁可退回旧位置
        }

        foreach (var file in copied)
        {
            try { File.Delete(file); } catch (Exception) { }
        }

        return false;
    }

    /// <summary>
    /// 记下"这台程序的用户把数据挪到哪了"。
    ///
    /// 放在程序目录里而不是 %LOCALAPPDATA%：一台电脑上可能同时有安装版、便携版、
    /// 甚至一份开发构建，共用全局一份指针的话，谁最后启动谁就把别人的数据位置改掉 ——
    /// 那种错不会报任何异常，只会让另一份程序读到"别人的班"。
    /// 程序目录只读时才退回 %LOCALAPPDATA%（那时也不会有自动搬家）。
    /// </summary>
    public static void SavePointer(string root, string? exeDir = null, string? localApp = null)
    {
        var exe = exeDir ?? AppContext.BaseDirectory;
        var file = CanWriteIn(exe)
            ? PointerFile(exe)
            : LocalPointerFile(localApp ?? LocalAppData);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, root);
        }
        catch (Exception)
        {
            // 写不进去只是下次启动要重新判断一遍，不影响这次运行
        }
    }

    public static string? ReadPointer(string? exeDir = null, string? localApp = null)
    {
        var exe = exeDir ?? AppContext.BaseDirectory;
        return ReadFirst(PointerFile(exe)) ?? ReadFirst(LocalPointerFile(localApp ?? LocalAppData));

        static string? ReadFirst(string file)
        {
            try
            {
                if (!File.Exists(file)) return null;
                var text = File.ReadAllText(file).Trim();
                return string.IsNullOrWhiteSpace(text) ? null : text;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    public static void ClearPointer(string? exeDir = null, string? localApp = null)
    {
        var exe = exeDir ?? AppContext.BaseDirectory;
        foreach (var file in new[] { PointerFile(exe), LocalPointerFile(localApp ?? LocalAppData) })
        {
            try
            {
                if (File.Exists(file)) File.Delete(file);
            }
            catch (Exception)
            {
            }
        }
    }

    /// <summary>能不能往这个已存在的目录里写文件（探一个临时文件，随即删掉）。</summary>
    public static bool CanWriteIn(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return false;
            var probe = Path.Combine(dir, $".w-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
