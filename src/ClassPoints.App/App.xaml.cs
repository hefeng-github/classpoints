using ClassPoints.App.Services;
using ClassPoints.Core;
using Microsoft.UI.Xaml;

namespace ClassPoints.App;

public partial class App : Application
{
    public static AppServices Services { get; private set; } = null!;

    public static StartupOptions Options { get; private set; } = new();

    /// <summary>这台机器这次以什么形态运行。启动时从设置里读，向导里选定后当场改。</summary>
    public static AppMode Mode { get; private set; } = AppMode.Desk;

    /// <summary>形态是否已经定过。false 时启动直接进向导，而不是猜一个。</summary>
    public static bool ModeKnown { get; private set; }

    public static void SetMode(AppMode mode)
    {
        Mode = mode;
        ModeKnown = true;
        Services.Settings.AppMode = mode;
    }

    /// <summary>只给验收脚本用：不改库，只改这一次的形态。</summary>
    public static void OverrideMode(AppMode mode) => Mode = mode;

    /// <summary>设置页的「重新运行首次配置」：把形态清掉，让向导重新问一遍。</summary>
    public static void ForgetMode()
    {
        Services.Settings.AppMode = null;
        Services.Settings.OnboardingDone = false;
        ModeKnown = false;
    }

    public static Board.BoardShell? Board { get; set; }

    public static Desk.DeskShell? Desk { get; set; }

    /// <summary>
    /// 当前外壳。页面只通过它做 Toast / 跳转 / 认人 ——
    /// 这样同一个页面搬到大屏或电脑端都不用改一行。
    /// </summary>
    public static IShellHost? Host => (IShellHost?)Board ?? Desk;

    public static MainWindow? Window { get; set; }

    /// <summary>
    /// 切换外观。三件事必须一起做，少一件就会出现"一半深一半浅"：
    /// ① 记住偏好；② 改 C# 共享画刷的颜色（已经挂在界面上的元素会跟着变）；
    /// ③ 改根元素的 RequestedTheme（让 XAML 里的 ThemeResource 重新解析）。
    /// </summary>
    public static void ApplyTheme(Theme.AppTheme theme)
    {
        Services.Settings.Theme = theme;
        Theme.Pal.Apply(theme);

        if (Window?.Content is FrameworkElement root)
        {
            root.RequestedTheme = theme switch
            {
                Theme.AppTheme.Light => ElementTheme.Light,
                Theme.AppTheme.Dark => ElementTheme.Dark,
                _ => ElementTheme.Default,
            };
        }

        Window?.RefreshTitleBar();
    }

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            try
            {
                Services?.LogError("unhandled", e.Exception);
            }
            catch (Exception)
            {
                // 日志本身出错就放弃，不能让异常处理再抛一次
            }
        };

        // 下面三条是"静默退出"的取证网。
        //
        // XAML 的 UnhandledException 只兜 UI 线程上的托管异常。实测中遇到过一次
        // 「点一下输入框，进程直接没了：没有 ERROR 日志、没有 WER 事件、没有 dump」——
        // 那种情况要么异常在 native 回调里先被吃了，要么进程连托管异常都没走到。
        // 有了这三条，下次再发生至少能留下一行可查的证据。
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            try
            {
                var ex = e.ExceptionObject as Exception;
                Services?.LogError("appdomain-unhandled" + (e.IsTerminating ? "（进程即将终止）" : ""),
                    ex ?? new Exception(e.ExceptionObject?.ToString() ?? "未知异常对象"));
            }
            catch (Exception)
            {
            }
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            try { Services?.LogError("unobserved-task", e.Exception); } catch (Exception) { }
            e.SetObserved();
        };

        // native 层 fail-fast 之前，托管侧往往先抛一次 COM/SEH 异常。
        // 数量封顶：这个回调很吵，正常流程里也有被 try 吃掉的一次性异常。
        _firstChanceBudget = 40;
        AppDomain.CurrentDomain.FirstChanceException += (_, e) =>
        {
            if (_firstChanceBudget <= 0) return;
            var ex = e.Exception;
            if (ex is not (System.Runtime.InteropServices.COMException
                or System.Runtime.InteropServices.SEHException
                or AccessViolationException)) return;

            _firstChanceBudget--;
            try { Services?.LogError("first-chance " + ex.GetType().Name, ex); } catch (Exception) { }
        };

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { Services?.Log($"进程退出 code={Environment.ExitCode}"); } catch (Exception) { }
        };
    }

    private static int _firstChanceBudget;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Options = StartupOptions.Parse(Environment.GetCommandLineArgs());

        try
        {
            Services = AppServices.Initialize(Options);
        }
        catch (Exception ex)
        {
            // 库打不开时不能静默退出，否则老师看到的是"点了没反应"
            ShowFatal(ex);
            return;
        }

        // 形态：设置里定过就用它；验收脚本的 CP_MODE 只覆盖这一次，不算"定过"
        if (Services.Settings.AppMode is AppMode stored)
        {
            Mode = stored;
            ModeKnown = true;
        }
        if (Options.ModeOverride is AppMode forced) Mode = forced;

        try
        {
            Window = new MainWindow();
            Window.Activate();
            Services.Log("主窗口已创建");
        }
        catch (Exception ex)
        {
            Services.LogError("create-window", ex);
            ShowFatal(ex);
        }
    }

    private static void ShowFatal(Exception ex)
    {
        // 报错时给的是"这台电脑实际用的那个库"，不是默认路径 ——
        // 用 --db 指定过文件时，两者不是一个地方，指错会让人去修一个不存在的文件。
        var dbPath = string.IsNullOrWhiteSpace(Options.DbPath)
            ? Data.Database.DefaultDbPath
            : Path.GetFullPath(Options.DbPath);

        var dialog = new Microsoft.UI.Xaml.Controls.ContentDialog
        {
            Title = "数据库打不开",
            Content = $"{ex.Message}\n\n数据库位置：{dbPath}\n\n" +
                      "文件被占用就关掉那个程序再启动；损坏就到备份目录挑最近的复制回来。",
            CloseButtonText = "退出",
        };

        var window = new MainWindow(dialog);
        window.Activate();
    }
}
