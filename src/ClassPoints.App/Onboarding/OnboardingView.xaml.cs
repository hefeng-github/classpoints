using ClassPoints.App.Controls;
using ClassPoints.App.Services;
using ClassPoints.App.Theme;
using ClassPoints.Core;
using ClassPoints.Core.Config;
using ClassPoints.Data;
using ClassPoints.Data.Config;
using ClassPoints.Data.Sync;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ClassPoints.App.Onboarding;

/// <summary>
/// 首次启动向导。它不是产品介绍页，是配置向导。
///
/// 三条硬规矩：
/// ①「完成」之前一个字节都不写库（配置包除外 —— 那是老师明确点了「套用」才落盘的）；
/// ② 两端问的是同一套问题：大屏和电脑功能一样，向导也就没有理由分叉；
/// ③ 校验不过不是"没反应"，是把那一格标红并说清楚缺什么 ——
///    大屏前面站着一个人，光标不会自己跳到没填的地方。
/// </summary>
public sealed partial class OnboardingView : UserControl
{
    private enum StepKind
    {
        Mode,
        Config,
        Class,
        Names,
        Rules,
        Account,
        SelfCheck,
    }

    private sealed class RuleDraft
    {
        public string? ExistingId { get; init; }
        public string? PresetKey { get; init; }
        public string Icon { get; set; } = IconCatalog.Default;
        public string Label { get; set; } = "";
        public int Delta { get; set; } = 10;
        public int? CapValue { get; set; }
        public CapScope CapScope { get; set; } = CapScope.None;
        public int Sort { get; set; } = 100;
        public bool Removed { get; set; }
    }

    private static readonly int MinCardW = 210;
    private static readonly int MinCardH = 120;
    private static readonly int Gap = 12;

    private readonly List<StepKind> _steps = [];
    private readonly List<string> _titles = [];
    private readonly List<RuleDraft> _rules = [];
    private readonly List<Student> _previewStudents = [];

    private AppMode _mode = AppMode.Board;
    private bool _modeChosen;
    private int _step;

    private string _className = "";
    private List<string> _names = [];
    private string _pin = "";
    private string _pinAgain = "";
    private string? _recoveryCode;
    private string _classCode = "";
    private bool _pinFromBundle;

    /// <summary>示例数据：只有在欢迎页明确勾了才用。默认不勾 —— 谁也不想开学第一天先删 48 个假人。</summary>
    private bool _demoData;

    private string? _configPath;
    private string? _configApplied;

    private int _awardedIndex = -1;
    private int _awardedDelta;
    private int _columns = 8;

    public OnboardingView()
    {
        InitializeComponent();

        _mode = App.Mode;
        _modeChosen = App.ModeKnown;
        _classCode = App.Services.Settings.ClassCode;
        _className = App.Services.Settings.ClassName;

        foreach (var rule in App.Services.Rules.ListAll())
        {
            _rules.Add(new RuleDraft
            {
                ExistingId = rule.Id,
                PresetKey = rule.PresetKey,
                Icon = rule.Glyph,
                Label = rule.Label,
                Delta = rule.Delta,
                CapValue = rule.CapValue,
                CapScope = rule.CapScope,
                Sort = rule.Sort,
            });
        }

        // 验收脚本用：CP_DEMO=1 预勾示例数据，CP_PAD=1 在自检页自动打开加分面板。
        // 和 CP_MODE / CP_SNAP 一样只服务截图，不参与正常业务。
        _demoData = Environment.GetEnvironmentVariable("CP_DEMO") == "1";

        RebuildSteps();
        ShowStep(0);
    }

    public event EventHandler? Completed;

    /// <summary>从设置里重跑向导时才有「退出配置」：第一次装机器的时候没有可回的地方。</summary>
    public bool AllowCancel { get; set; }

    public event EventHandler? Cancelled;

    private void OnCancelSetup(object sender, RoutedEventArgs e) =>
        Cancelled?.Invoke(this, EventArgs.Empty);

    /// <summary>验收用：直接跳到某一步（CP_SNAP 抓向导各步截图时用，正常流程不会碰它）。</summary>
    public void DebugGoToStep(int step) => ShowStep(step);

    public int StepCount => _titles.Count;

    private static AppServices Services => App.Services;

    private StepKind Current => _steps[_step];

    private bool IsSelfCheck => Current == StepKind.SelfCheck;

    // ---------------- 步骤表 ----------------

    private void RebuildSteps()
    {
        _steps.Clear();
        _titles.Clear();

        // 形态这一步永远在：即使这次启动已经知道形态（预配置过、或从设置里重跑），
        // 也给得回一步 —— "1. 预配置"底下没有「上一步」按钮，看着像界面坏了。
        _steps.Add(StepKind.Mode);
        _titles.Add("这台电脑做什么");

        _steps.Add(StepKind.Config);
        _titles.Add("预配置");
        _steps.Add(StepKind.Class);
        _titles.Add("班级");
        _steps.Add(StepKind.Names);
        _titles.Add("名单");
        _steps.Add(StepKind.Rules);
        _titles.Add("加减分项");
        _steps.Add(StepKind.Account);
        _titles.Add("密码与恢复码");

        _steps.Add(StepKind.SelfCheck);
        _titles.Add("自检");
    }

    // ---------------- 步骤控制 ----------------

    private void ShowStep(int step)
    {
        _step = System.Math.Clamp(step, 0, _titles.Count - 1);

        TitleText.Text = $"{_step + 1}. {_titles[_step]}";
        KickerText.Text = _modeChosen
            ? $"班级积分 · {_mode.ToDisplay()} · 首次配置"
            : "班级积分 · 首次配置";

        RenderDots();

        FormScroll.Visibility = IsSelfCheck ? Visibility.Collapsed : Visibility.Visible;
        SelfCheckScroll.Visibility = IsSelfCheck ? Visibility.Visible : Visibility.Collapsed;

        Body.Children.Clear();
        ClearHint();

        if (IsSelfCheck) BuildSelfCheck();
        else
        {
            switch (Current)
            {
                case StepKind.Mode: BuildModeStep(); break;
                case StepKind.Config: BuildConfigStep(); break;
                case StepKind.Class: BuildClassStep(); break;
                case StepKind.Names: BuildNamesStep(); break;
                case StepKind.Rules: BuildRulesStep(); break;
                default: BuildAccountStep(); break;
            }
        }

        TryPanel.Visibility = IsSelfCheck ? Visibility.Visible : Visibility.Collapsed;
        BackButton.Visibility = _step == 0 ? Visibility.Collapsed : Visibility.Visible;
        CancelButton.Visibility = AllowCancel ? Visibility.Visible : Visibility.Collapsed;
        SkipAllButton.Visibility = IsSelfCheck ? Visibility.Collapsed : Visibility.Visible;
        SkipAllButton.Content = "跳到自检";
        NextButton.Content = IsSelfCheck ? "完成，开始用" : "下一步";

        LeftAlignSizedChildren(Body);
    }

    /// <summary>
    /// 显式给了 Width 的控件，在 Stretch 对齐下会被摆到容器正中 ——
    /// 表现是"标签在最左、输入框飘到中间"。这里统一改成左对齐，省得每加一个输入框就忘一次。
    /// </summary>
    private static void LeftAlignSizedChildren(Panel panel)
    {
        foreach (var child in panel.Children)
        {
            if (child is FrameworkElement element &&
                element.HorizontalAlignment == HorizontalAlignment.Stretch &&
                !double.IsNaN(element.Width))
                element.HorizontalAlignment = HorizontalAlignment.Left;

            switch (child)
            {
                case Panel nested:
                    LeftAlignSizedChildren(nested);
                    break;
                case Border { Child: Panel bordered }:
                    LeftAlignSizedChildren(bordered);
                    break;
            }
        }
    }

    private void RenderDots()
    {
        StepDots.Children.Clear();
        for (var i = 0; i < _titles.Count; i++)
        {
            var done = i < _step;
            var active = i == _step;
            StepDots.Children.Add(new Border
            {
                Width = active ? 26 : 10,
                Height = 10,
                CornerRadius = new CornerRadius(5),
                Background = active ? Pal.Accent : done ? Pal.Positive : Pal.Stroke,
            });
        }
    }

    private void OnBack(object sender, RoutedEventArgs e)
    {
        ShowStep(_step - 1);
    }

    private void OnNext(object sender, RoutedEventArgs e)
    {
        if (!ValidateStep()) return;

        if (IsSelfCheck)
        {
            Finish();
            return;
        }

        ShowStep(_step + 1);
    }

    // ---------------- 校验：红字 + 标红那一格 ----------------

    private void ClearHint()
    {
        HintText.Text = "";
        HintText.Foreground = Palette.Secondary;
    }

    private void ShowError(string message, FrameworkElement? focus = null)
    {
        HintText.Text = message;
        HintText.Foreground = Pal.Danger;

        if (focus is null) return;
        if (focus is Control control)
        {
            control.BorderBrush = Pal.Danger;
            control.BorderThickness = new Thickness(2);
            control.Focus(FocusState.Programmatic);
        }
    }

    private static void ClearError(params FrameworkElement[] fields)
    {
        foreach (var field in fields)
        {
            if (field is not Control control) continue;
            control.ClearValue(Control.BorderBrushProperty);
            control.ClearValue(Control.BorderThicknessProperty);
        }
    }

    private bool ValidateStep()
    {
        ClearHint();

        switch (Current)
        {
            case StepKind.SelfCheck:
            case StepKind.Config:
            case StepKind.Rules:
                return true;

            case StepKind.Names:
                // 以前这里直接 return true，输入框从头到尾没人读 ——
                // 老师粘了一整列姓名，自检页却显示"还没有名单"。离开这一步时先收进来。
                CollectNames();
                return true;

            case StepKind.Mode:
                if (_modeChosen) return true;
                ShowError("先选一种用法，选错可以在设置里重来。");
                return false;

            case StepKind.Class:
                if (NameInput is not null && NameNormalizer.Clean(NameInput.Text).Length > 0)
                {
                    _className = NameNormalizer.Clean(NameInput.Text);
                    return true;
                }
                ShowError("班级名还没填。它是大屏左上角显示的那一行。", NameInput);
                return false;

            default:
                return ValidateAccount();
        }
    }

    private bool ValidateAccount()
    {
        if (_pin.Length == 0 && _pinFromBundle) return true;

        if (_pin.Length != 6)
        {
            ShowError("管理密码要 6 位数字。班主任和班长都用它进管理模式。", PinBoxInput);
            return false;
        }

        if (_pin != _pinAgain)
        {
            ShowError("两次输入的密码不一样，再对一遍。", PinAgainInput);
            return false;
        }

        return true;
    }

    private void OnSkipAll(object sender, RoutedEventArgs e) => ShowStep(_titles.Count - 1);

    // ---------------- 第 0 步：形态 ----------------

    private void BuildModeStep()
    {
        Body.Children.Add(new TextBlock
        {
            Text = "这台电脑平时放在教室还是办公室。功能完全一样，只是排版不同。",
            Style = Styled("DeskBodyText"),
            TextWrapping = TextWrapping.Wrap,
        });

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        row.Children.Add(ModeCard(AppMode.Board, "\uE7F4", "教室大屏",
            "触屏操作，字大按钮厚，两米外看得清。"));
        row.Children.Add(ModeCard(AppMode.Desk, "\uE977", "班主任电脑",
            "键鼠操作，信息密度高，改名单与出成绩更顺手。"));
        Body.Children.Add(row);

        // 「示例数据」必须在欢迎页显式勾选：不勾就不写任何假学生进库。
        var demo = new CheckBox
        {
            Content = "先用示例数据试试（48 个示例学生，随时可清空）",
            IsChecked = _demoData,
            Margin = new Thickness(0, 6, 0, 0),
        };
        demo.Checked += (_, _) => _demoData = true;
        demo.Unchecked += (_, _) => _demoData = false;
        Body.Children.Add(demo);

        Body.Children.Add(new TextBlock
        {
            Text = "形态存在本机，之后可在「设置 → 这台电脑」里更换。",
            Style = Styled("SmallText"),
        });
    }

    private Button ModeCard(AppMode mode, string glyph, string title, string hint)
    {
        var selected = _modeChosen && _mode == mode;

        var card = new Button
        {
            Width = 320,
            Height = 172,
            Padding = new Thickness(18, 16, 18, 16),
            CornerRadius = new CornerRadius(14),
            BorderThickness = new Thickness(selected ? 2 : 1),
            BorderBrush = selected ? Pal.Accent : Pal.Stroke,
            Background = selected ? Pal.AccentBg : Pal.Card,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            VerticalContentAlignment = VerticalAlignment.Top,
        };

        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(new FontIcon
        {
            Glyph = glyph,
            FontFamily = new FontFamily(IconCatalog.FontFamily),
            FontSize = 26,
            Foreground = selected ? Pal.Accent : Pal.Secondary,
        });
        stack.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Foreground = Pal.Primary,
        });
        stack.Children.Add(new TextBlock
        {
            Text = hint,
            Style = Styled("SmallText"),
            TextWrapping = TextWrapping.Wrap,
        });

        card.Content = stack;
        card.Click += (_, _) =>
        {
            _mode = mode;
            _modeChosen = true;
            RebuildSteps();
            ShowStep(0);
        };
        return card;
    }

    // ---------------- 1. 预配置 ----------------

    private void BuildConfigStep()
    {
        _configPath ??= ConfigBundleFile.Discover();

        Body.Children.Add(new TextBlock
        {
            Text = "配置包（.cpcfg）里带着班级名、云端地址与名单，套用后本机能少填几项。没有就跳过。",
            Style = Styled("DeskBodyText"),
            TextWrapping = TextWrapping.Wrap,
        });

        var pathBox = new TextBox
        {
            Header = "配置包路径",
            Text = _configPath ?? "",
            PlaceholderText = @"例如 U:\班级.cpcfg，或本程序目录下的 classpoints.cpcfg",
            Width = 520,
        };

        // 「选文件」贴着路径框、「套用」贴口令框 —— 按钮挨着它作用的那个输入框。
        var browse = new Button
        {
            Content = "选文件…",
            Style = Styled("TouchSecondaryButton"),
            VerticalAlignment = VerticalAlignment.Bottom,
        };
        var pathRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        pathRow.Children.Add(pathBox);
        pathRow.Children.Add(browse);
        Body.Children.Add(pathRow);

        var pass = new PasswordBox
        {
            Header = "配置包口令（设过才要）",
            PlaceholderText = "没设口令就留空",
            PasswordChar = "●",
            Width = 260,
        };

        var summary = new TextBlock
        {
            Style = Styled("SmallText"),
            TextWrapping = TextWrapping.Wrap,
            Text = string.IsNullOrWhiteSpace(_configPath) ? "没找到配置包，可以手动选文件。" : "点「套用」先看一眼里面是什么。",
        };

        var apply = new Button
        {
            Content = "套用",
            Style = Styled("TouchPrimaryButton"),
            MinWidth = 120,
            VerticalAlignment = VerticalAlignment.Bottom,
        };

        var passRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        passRow.Children.Add(pass);
        passRow.Children.Add(apply);

        Body.Children.Add(passRow);
        Body.Children.Add(summary);

        if (_configApplied is not null)
            summary.Text = "已套用：" + _configApplied + "。下一步会接着问还缺什么。";

        browse.Click += async (_, _) =>
        {
            var picked = await PickConfigFileAsync();
            if (picked is null) return;
            _configPath = picked;
            pathBox.Text = picked;
            summary.Text = "已选中，点「套用」。";
        };

        apply.Click += async (_, _) =>
        {
            var path = pathBox.Text.Trim();
            if (path.Length == 0)
            {
                summary.Text = "先选一个配置文件。";
                return;
            }

            ConfigBundle bundle;
            try
            {
                bundle = ConfigBundleFile.Load(path);
            }
            catch (Exception ex) when (ex is ConfigException or IOException or UnauthorizedAccessException)
            {
                summary.Text = "读不了这个文件：" + ex.Message;
                return;
            }

            summary.Text = Services.Config.Describe(bundle);

            try
            {
                var kind = _mode.ToDb();
                var result = await Services.Config.ApplyAsync(bundle, pass.Password, kind);
                if (!result.Ok)
                {
                    summary.Text = result.Message + (result.Pulled is null ? "" : "（" + result.Pulled + "）");
                    return;
                }

                ConfigBundleFile.KeepCopy(bundle, path);
                _configPath = path;
                _configApplied = bundle.ClassName + "，" +
                                 (bundle.Roster.Count > 0 ? $"{bundle.Roster.Count} 人名单" : "无名单") + "，" +
                                 (bundle.Sync is null ? "未连云端" : "已连云端");
                _className = bundle.ClassName;
                _classCode = Services.Settings.ClassCode;

                if (bundle.NeedsPassphrase && pass.Password.Length > 0)
                {
                    var admin = bundle.RevealAdminPin(pass.Password);
                    if (admin is { Length: 6 })
                    {
                        _pin = admin;
                        _pinAgain = admin;
                        _pinFromBundle = true;
                    }
                }

                summary.Text = "已套用：" + _configApplied + (result.Pulled is null ? "" : "；" + result.Pulled);
                HintText.Text = "班级名" + (bundle.Roster.Count > 0 ? "、名单" : "") +
                                (_pinFromBundle ? "、管理密码" : "") + "都已就位。";
                HintText.Foreground = Pal.Positive;
            }
            catch (ConfigException ex)
            {
                summary.Text = "口令不对或文件被改过：" + ex.Message;
            }
            catch (Exception ex)
            {
                Services.LogError("wizard-config", ex);
                summary.Text = "套用失败：" + ex.Message;
            }
        };

        Body.Children.Add(BuildClassCodeLink());
    }

    /// <summary>
    /// 没有配置文件时的第二条路：填班级码 + 班主任密码，直接连云端把名单和配置拉下来。
    /// 以前只有"导入文件"一条路，于是没带 U 盘的那次就得先跳过、装完再回设置里配对。
    /// </summary>
    private UIElement BuildClassCodeLink()
    {
        var urlBox = new TextBox
        {
            Header = "服务器地址",
            PlaceholderText = "https://classpoints.example.com",
            Text = "http://127.0.0.1:8787",
            Width = 420,
        };
        var codeBox = new TextBox { Header = "班级码", PlaceholderText = "8 位", Width = 200 };
        var pinBox = new PasswordBox
        {
            Header = "班主任密码",
            PasswordChar = "●",
            Width = 200,
        };
        var link = new Button
        {
            Content = "连接并拉取",
            Style = Styled("TouchSecondaryButton"),
            VerticalAlignment = VerticalAlignment.Bottom,
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        row.Children.Add(codeBox);
        row.Children.Add(pinBox);
        row.Children.Add(link);

        var panel = new StackPanel { Spacing = 10, HorizontalAlignment = HorizontalAlignment.Left };
        panel.Children.Add(urlBox);
        panel.Children.Add(row);
        // 首次配置时就把特征码摊开：班级开了设备审核的话，接下来那一步
        // 会卡在"等班主任通过"，而老师需要有个东西能念给对方听。
        panel.Children.Add(new TextBlock
        {
            Text = $"本机特征码 {DeviceFingerprint.Display}",
            Style = Styled("SmallText"),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 620,
        });
        var status = new TextBlock { Style = Styled("SmallText"), TextWrapping = TextWrapping.Wrap, MaxWidth = 620 };
        panel.Children.Add(status);

        link.Click += async (_, _) =>
        {
            var url = urlBox.Text.Trim();
            var code = codeBox.Text.Trim();
            var pin = pinBox.Password;

            if (url.Length == 0 || code.Length == 0 || pin.Length < 6)
            {
                status.Text = "地址、班级码和 6 位密码都要填。";
                return;
            }

            link.IsEnabled = false;
            status.Text = "正在连接…";
            try
            {
                var report = await Services.Sync.PairAsync(url, code, pin, _mode.ToDb());
                if (!report.Ok)
                {
                    status.Text = "连接失败：" + (report.Error ?? "地址或班级码不对");
                    return;
                }

                _classCode = code;
                Services.Settings.ClassCode = code;
                Services.Pump.Start();

                var pull = await Services.Sync.RunOnceAsync();
                if (report.ReadOnly)
                {
                    // 待审设备：连上了，能看，写不了。别写成"已连接，拉取 N 条"让人以为完事了。
                    status.Text = report.PendingMessage
                        ?? "已连接到云端，但这台设备还在等班主任通过，暂时只能查看。";
                    HintText.Text = "云端已接入，等班主任通过设备审核。";
                    HintText.Foreground = Pal.Warn;
                }
                else
                {
                    status.Text = pull.Ok
                        ? $"已连接，拉取 {pull.Pulled} 条记录。名单那一步可以跳过。"
                        : $"已连接，但这次没拉到数据（{pull.Error}），稍后会自动重试。";
                    HintText.Text = "云端已接入这台机器。";
                    HintText.Foreground = Pal.Positive;
                }
            }
            catch (Exception ex)
            {
                Services.LogError("wizard-pair", ex);
                status.Text = "连接失败：" + ex.Message;
            }
            finally
            {
                link.IsEnabled = true;
            }
        };

        return Section("或者：用班级码直接连云端", null, panel);
    }

    private Task<string?> PickConfigFileAsync()
    {
        try
        {
            return Task.FromResult(FileDialogs.Open("选择配置包", FileFilter.Config));
        }
        catch (Exception ex)
        {
            Services.LogError("wizard-pick", ex);
            HintText.Text = "文件选择框打不开，把路径粘贴进上面的输入框就行。";
            HintText.Foreground = Pal.Warn;
            return Task.FromResult<string?>(null);
        }
    }

    // ---------------- 2. 班级 ----------------

    private TextBox? NameInput { get; set; }

    private void BuildClassStep()
    {
        NameInput = new TextBox
        {
            Header = "班级名称",
            Text = _className,
            PlaceholderText = "例如：初二 (3) 班",
            Width = 420,
        };
        Body.Children.Add(NameInput);

        Body.Children.Add(new TextBlock
        {
            Text = "周期不用填：每开一次新周期，按「第 1、2、3…」自动编号。",
            Style = Styled("SmallText"),
        });

        var existing = Services.Students.ListActive().Count;
        if (existing > 0)
            Body.Children.Add(new TextBlock
            {
                Text = $"本机已有 {existing} 人，名单那一步可以跳过。",
                Style = Styled("SmallText"),
            });
    }

    // ---------------- 3. 名单 ----------------

    private TextBox? NamesInput { get; set; }

    /// <summary>把输入框里的文本收成姓名列表。切行、去空行、去重都在这里统一做。</summary>
    private void CollectNames()
    {
        if (NamesInput is null) return;

        _names = TextLines.Split(NamesInput.Text)
            .Select(NameNormalizer.Clean)
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private void BuildNamesStep()
    {
        var existing = Services.Students.ListActive().Count;
        if (existing > 0)
        {
            Body.Children.Add(new TextBlock
            {
                Text = $"本机已有 {existing} 人。这里贴的姓名会追加在后面，重名的自动跳过。",
                Style = Styled("DeskBodyText"),
                TextWrapping = TextWrapping.Wrap,
            });
        }

        NamesInput = new TextBox
        {
            Header = "粘贴一列姓名（一行一个）",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 220,
            Width = 520,
            PlaceholderText = "王小明\n李思远\n张伟\n…",
        };
        if (_names.Count > 0) NamesInput.Text = string.Join("\n", _names);

        Body.Children.Add(NamesInput);
        Body.Children.Add(new TextBlock
        {
            Text = "带学号的导入在「名单」页做，可直接读 Excel。",
            Style = Styled("SmallText"),
        });

        var later = new Button { Content = "稍后再说，先看界面", Style = Styled("TouchSecondaryButton") };
        later.Click += (_, _) =>
        {
            _names.Clear();
            ShowStep(_step + 1);
        };
        Body.Children.Add(later);
    }

    // ---------------- 4. 加减分项 ----------------

    private void BuildRulesStep()
    {
        Body.Children.Add(new TextBlock
        {
            Text = "项目名称、默认分值与封顶都由你定。负数是扣分，封顶默认关闭。",
            Style = Styled("DeskBodyText"),
            TextWrapping = TextWrapping.Wrap,
        });

        var visible = _rules.Where(r => !r.Removed).ToList();

        // 先给一行表头：没有它，那一排输入框只能靠猜——哪个是分值、哪个是上限，谁也不确定
        Body.Children.Add(BuildRuleHeader());

        foreach (var draft in visible)
            Body.Children.Add(BuildRuleRow(draft));

        if (visible.Count == 0)
            Body.Children.Add(new TextBlock
            {
                Text = "一项都没有。至少加一项，记分台才有按钮可按。",
                Style = Styled("SmallText"),
                Foreground = Pal.Warn,
            });

        var footer = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(0, 6, 0, 0) };

        var addPlus = new Button { Content = "＋ 加分项", Style = Styled("TouchSecondaryButton"), MinWidth = 150 };
        addPlus.Click += (_, _) =>
        {
            _rules.Add(new RuleDraft { Label = "", Delta = 10, Sort = NextSort() });
            ShowStep(_step);
        };
        footer.Children.Add(addPlus);

        var addMinus = new Button { Content = "－ 扣分项", Style = Styled("TouchSecondaryButton"), MinWidth = 150 };
        addMinus.Click += (_, _) =>
        {
            _rules.Add(new RuleDraft { Label = "", Delta = -10, Sort = NextSort() });
            ShowStep(_step);
        };
        footer.Children.Add(addMinus);

        Body.Children.Add(footer);
    }

    private int NextSort() => _rules.Count == 0 ? 10 : _rules.Max(r => r.Sort) + 10;

    /// <summary>列宽必须和 BuildRuleRow 里的一模一样，否则表头会跟下面的输入框错开。</summary>
    private static UIElement BuildRuleHeader()
    {
        var grid = new Grid { ColumnSpacing = 10, Padding = new Thickness(0, 4, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        string[] headers = ["图标", "项目名称", "分值（负数为扣分）", "封顶方式", "上限", "操作"];
        for (var i = 0; i < headers.Length; i++)
        {
            var text = new TextBlock { Text = headers[i], Style = Styled("DeskColumnHeader") };
            Grid.SetColumn(text, i);
            if (i == headers.Length - 1) text.HorizontalAlignment = HorizontalAlignment.Center;
            grid.Children.Add(text);
        }

        return grid;
    }

    /// <summary>
    /// 一行 = 一个项目。分值是输入框（不是 ±5 两枚按钮）：改 10 分是"改成 10"，
    /// 不是"点两下"；封顶是「不封顶 / 单次 / 每周期」加一个数字。
    /// </summary>
    private UIElement BuildRuleRow(RuleDraft draft)
    {
        var grid = new Grid { ColumnSpacing = 10, Padding = new Thickness(0, 6, 0, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });   // 图标
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // 名称
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) }); // 分值
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) }); // 封顶方式
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) }); // 上限值
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });     // 删除

        var iconButton = new Button
        {
            Width = 40,
            Height = 40,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(10),
            Background = Pal.TileBg,
            BorderBrush = Pal.Stroke,
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
        };
        iconButton.Content = new FontIcon
        {
            Glyph = draft.Icon,
            FontFamily = new FontFamily(IconCatalog.FontFamily),
            FontSize = 18,
            Foreground = Pal.Primary,
        };
        iconButton.Click += async (_, _) =>
        {
            var icon = draft.Icon;
            var picked = await IconChooserDialogAsync(icon);
            if (picked is null) return;
            draft.Icon = picked;
            ShowStep(_step);
        };
        Grid.SetColumn(iconButton, 0);
        grid.Children.Add(iconButton);

        var nameBox = new TextBox
        {
            PlaceholderText = "项目名称，例如「回答问题」",
            Text = draft.Label,
            Height = 40,
            VerticalAlignment = VerticalAlignment.Center,
        };
        nameBox.TextChanged += (_, _) => draft.Label = nameBox.Text;
        Grid.SetColumn(nameBox, 1);
        grid.Children.Add(nameBox);

        // NumberBox 要等焦点离开才写 Value，而向导这一屏没有"保存"按钮 ——
        // 填完分值直接点"下一步"，草稿里还是旧值。换成 NumberInput。
        var delta = new NumberInput
        {
            Value = draft.Delta,
            Minimum = -500,
            Maximum = 500,
            Step = 5,
            BoxWidth = 76,
            VerticalAlignment = VerticalAlignment.Center,
        };
        delta.ValueChanged += v => draft.Delta = (int)v;
        Grid.SetColumn(delta, 2);
        grid.Children.Add(delta);

        var capCombo = new ComboBox
        {
            Height = 40,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        capCombo.Items.Add("不封顶");
        capCombo.Items.Add("单次最多");
        capCombo.Items.Add("每周期最多");
        capCombo.SelectedIndex = draft.CapScope switch
        {
            CapScope.Single => 1,
            CapScope.Period => 2,
            _ => 0,
        };
        Grid.SetColumn(capCombo, 3);
        grid.Children.Add(capCombo);

        var capValue = new NumberInput
        {
            Value = draft.CapValue ?? System.Math.Max(10, System.Math.Abs(draft.Delta)),
            Minimum = 1,
            Maximum = 10000,
            Step = 10,
            BoxWidth = 76,
            VerticalAlignment = VerticalAlignment.Center,
            IsEnabled = draft.CapScope != CapScope.None,
        };
        capValue.ValueChanged += v => draft.CapValue = (int)v;
        capCombo.SelectionChanged += (_, _) =>
        {
            draft.CapScope = capCombo.SelectedIndex switch
            {
                1 => CapScope.Single,
                2 => CapScope.Period,
                _ => CapScope.None,
            };
            draft.CapValue = draft.CapScope == CapScope.None ? null : (int)capValue.Value;
            capValue.IsEnabled = draft.CapScope != CapScope.None;
        };
        Grid.SetColumn(capValue, 4);
        grid.Children.Add(capValue);

        // 「启用」勾选框换成删除：勾掉又留着行的那种状态，谁也说不清它到底算不算数。
        var remove = new Button
        {
            Content = "删除",
            Height = 40,
            MinWidth = 64,
            CornerRadius = new CornerRadius(8),
            FontSize = 14,
            Background = Pal.NegativeBg,
            Foreground = Pal.Negative,
            BorderBrush = Pal.Negative,
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
        };
        remove.Click += (_, _) =>
        {
            draft.Removed = true;
            ShowStep(_step);
        };
        Grid.SetColumn(remove, 5);
        grid.Children.Add(remove);

        return grid;
    }

    private async Task<string?> IconChooserDialogAsync(string current)
    {
        var picked = current;
        var picker = Controls.IconChooser.Build(IconCatalog.RuleIcons, current, g => picked = g);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "选一个图标",
            Content = picker,
            PrimaryButtonText = "用这个",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        return await Dialogs.ShowAsync(dialog) == ContentDialogResult.Primary ? picked : null;
    }

    // ---------------- 5. 密码与恢复码 ----------------

    private PasswordBox? PinBoxInput { get; set; }
    private PasswordBox? PinAgainInput { get; set; }

    private void BuildAccountStep()
    {
        // 班级码：随机、不带年份。以前是「C3-2026」这种，年份一露就等于告诉了别人
        // 这个班哪年建的，而且两台机器同年建班很容易撞出一样的编号。
        var codeText = new TextBlock
        {
            Text = _classCode,
            FontFamily = (FontFamily)Application.Current.Resources["NumberFont"],
            FontSize = 28,
            FontWeight = FontWeights.SemiBold,
            Foreground = Pal.Primary,
            VerticalAlignment = VerticalAlignment.Center,
            IsTextSelectionEnabled = true,
        };

        var reroll = new Button { Content = "换一个", Style = Styled("TouchSecondaryButton"), MinWidth = 110 };
        reroll.Click += (_, _) =>
        {
            _classCode = ClassCodes.New();
            Services.Settings.ClassCode = _classCode;
            codeText.Text = _classCode;
            HintText.Text = "已换新班级码。另一台设备要用新的这个配对。";
            HintText.Foreground = Pal.Secondary;
        };

        var codeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
        codeRow.Children.Add(codeText);
        codeRow.Children.Add(reroll);

        Body.Children.Add(Section("班级码", null, codeRow));
        Body.Children.Add(new TextBlock
        {
            Text = "两台设备配对时用它。未连云端时只是本机编号。",
            Style = Styled("SmallText"),
        });

        var pinLabel = new StackPanel { Spacing = 4, Width = 220 };
        pinLabel.Children.Add(new TextBlock { Text = "管理密码（6 位数字）", Style = Styled("SmallText") });
        PinBoxInput = new PasswordBox { MaxLength = 6, PasswordChar = "●", Width = 200, Password = _pin };
        pinLabel.Children.Add(PinBoxInput);

        var againLabel = new StackPanel { Spacing = 4, Width = 220 };
        againLabel.Children.Add(new TextBlock { Text = "再输一次", Style = Styled("SmallText") });
        PinAgainInput = new PasswordBox { MaxLength = 6, PasswordChar = "●", Width = 200, Password = _pinAgain };
        againLabel.Children.Add(PinAgainInput);

        PinBoxInput.PasswordChanged += (_, _) =>
        {
            _pin = new string(PinBoxInput.Password.Where(char.IsDigit).ToArray());
            _pinFromBundle = false;
        };
        PinAgainInput.PasswordChanged += (_, _) =>
            _pinAgain = new string(PinAgainInput.Password.Where(char.IsDigit).ToArray());

        var pinRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        pinRow.Children.Add(pinLabel);
        pinRow.Children.Add(againLabel);
        Body.Children.Add(Section("管理密码", null, pinRow));
        Body.Children.Add(new TextBlock
        {
            Text = _pinFromBundle
                ? "配置包里已有管理密码，留空即沿用；如需更换在此重设。"
                : "大屏上进管理模式时要输它；电脑端认出班主任/班长后就不再问，班委只能记分。",
            Style = Styled("SmallText"),
        });

        var codeLabel = new TextBlock
        {
            Text = _recoveryCode ?? "还没有生成",
            FontFamily = (FontFamily)Application.Current.Resources["NumberFont"],
            FontSize = 24,
            FontWeight = FontWeights.SemiBold,
            Foreground = Pal.Warn,
            IsTextSelectionEnabled = true,
        };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };

        var generate = new Button
        {
            Content = _recoveryCode is null ? "生成恢复码" : "重新生成",
            Style = Styled("TouchSecondaryButton"),
        };
        generate.Click += (_, _) =>
        {
            _recoveryCode = RecoveryCode.Generate();
            codeLabel.Text = _recoveryCode;
            HintText.Text = "已生成。点「保存为 txt」存一份，或者抄到纸上。";
            HintText.Foreground = Pal.Secondary;
        };
        buttons.Children.Add(generate);

        var saveTxt = new Button { Content = "保存为 txt", Style = Styled("TouchSecondaryButton") };
        saveTxt.Click += async (_, _) =>
        {
            if (_recoveryCode is null)
            {
                HintText.Text = "先生成恢复码，再保存。";
                HintText.Foreground = Pal.Danger;
                return;
            }

            try
            {
                var path = FileDialogs.Save("保存恢复码",
                    $"恢复码-{_className}-{DateTime.Now:yyyyMMdd}", FileFilter.Text, "txt");
                if (path is null) return;

                await File.WriteAllTextAsync(path, $"""
                    班级积分 · 恢复码
                    班级：{_className}
                    生成时间：{DateTime.Now:yyyy-MM-dd HH:mm}

                    {_recoveryCode}

                    用途：忘记管理密码、或换一台电脑接管这个班时，用它重置。
                    请抄一份纸质留存，不要留在学生能看到的屏幕上。
                    """);

                HintText.Text = "已保存到 " + path;
                HintText.Foreground = Pal.Positive;
            }
            catch (Exception ex)
            {
                Services.LogError("wizard-save-recovery", ex);
                HintText.Text = "保存失败：" + ex.Message;
                HintText.Foreground = Pal.Danger;
            }
        };
        buttons.Children.Add(saveTxt);

        var recoverRow = new StackPanel { Spacing = 10 };
        recoverRow.Children.Add(codeLabel);
        recoverRow.Children.Add(buttons);
        Body.Children.Add(Section("恢复码", null, recoverRow));
        Body.Children.Add(new TextBlock
        {
            Text = "明文只显示一次，库里只存哈希。忘记密码或换机器时用它。",
            Style = Styled("SmallText"),
        });

        Body.Children.Add(Section("之后再做", new[]
        {
            "「设置 → 成员与权限」给班长与班委发卡或设密码。",
            "「设置 → 密码与恢复码」可把恢复码存成文本文件，另存一份留档。",
        }));
    }

    // ---------------- 最后一步：自检 ----------------

    private void BuildSelfCheck()
    {
        BuildPreviewStudents();

        // 没有名单时不画空卡片：一张撑满全屏的白板看起来像"界面坏了"，
        // 而这里恰恰要给一句明确的下一步。
        EmptyPreview.Visibility = _previewStudents.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SelfCheckHost.Visibility = _previewStudents.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

        if (_previewStudents.Count == 0)
        {
            VerdictText.Text = "还没有名单。上一步贴一列姓名，或者现在用示例数据看一眼排版。";
            TryPanel.Visibility = Visibility.Collapsed;
            return;
        }

        RenderSelfCheckGrid();

        if (Environment.GetEnvironmentVariable("CP_PAD") == "1")
            DispatcherQueue.TryEnqueue(() => OnTryAward(this, new RoutedEventArgs()));

        SelfCheckHost.SizeChanged -= OnSelfCheckSizeChanged;
        SelfCheckHost.SizeChanged += OnSelfCheckSizeChanged;
        SelfCheckScroll.SizeChanged -= OnSelfCheckSizeChanged;
        SelfCheckScroll.SizeChanged += OnSelfCheckSizeChanged;
        UpdateVerdict();
    }

    private void OnSelfCheckSizeChanged(object sender, SizeChangedEventArgs e) => RenderSelfCheckGrid();

    private void OnUseDemoClick(object sender, RoutedEventArgs e)
    {
        _demoData = true;
        BuildSelfCheck();
    }

    private void BuildPreviewStudents()
    {
        _previewStudents.Clear();

        if (_names.Count > 0)
        {
            for (var i = 0; i < _names.Count; i++)
                _previewStudents.Add(new Student { Id = "preview-" + i, Name = _names[i], SeatNo = i + 1 });
            return;
        }

        var existing = Services.Students.ListActive();
        if (existing.Count > 0)
        {
            _previewStudents.AddRange(existing);
            return;
        }

        // 只有勾了「先用示例数据」才铺演示卡片。
        if (!_demoData) return;

        for (var i = 1; i <= 48; i++)
            _previewStudents.Add(new Student { Id = "demo-" + i, Name = $"学生 {i:D2}", SeatNo = i });
    }

    private void RenderSelfCheckGrid()
    {
        var width = SelfCheckHost.ActualWidth - 16;
        if (width <= 100) return;

        _columns = System.Math.Clamp((int)((width + Gap) / (MinCardW + Gap)), 4, 12);

        var grid = new Grid { ColumnSpacing = Gap, RowSpacing = Gap };
        for (var c = 0; c < _columns; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var rows = (_previewStudents.Count + _columns - 1) / _columns;
        for (var r = 0; r < rows; r++)
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(MinCardH) });

        for (var i = 0; i < _previewStudents.Count; i++)
        {
            var card = new Controls.StudentCard { Index = i };
            card.Bind(_previewStudents[i], i * 7 % 120, i % 3 == 0 ? 10 : 0);
            Grid.SetRow(card, i / _columns);
            Grid.SetColumn(card, i % _columns);
            grid.Children.Add(card);

            if (i == _awardedIndex) card.FloatDelta(_awardedDelta);
        }

        SelfCheckHost.Child = grid;
        DispatcherQueue.TryEnqueue(UpdateVerdict);
    }

    private void UpdateVerdict()
    {
        if (!IsSelfCheck) return;

        var rows = (_previewStudents.Count + _columns - 1) / _columns;
        var fitsHeight = SelfCheckScroll.ScrollableHeight <= 1;
        var count = _previewStudents.Count;

        VerdictText.Text = $"当前 {_columns} 列 × {rows} 行，{count} 人" +
                           (fitsHeight ? "，一屏放得下。" : "，放不下要滚动 —— 拖窄窗口会自动少一列。");
    }

    private async void OnTryAward(object sender, RoutedEventArgs e)
    {
        if (_previewStudents.Count == 0) return;

        var rule = _rules.FirstOrDefault(r => !r.Removed) ?? new RuleDraft { Label = "回答问题", Delta = 10 };
        var realRule = new Rule
        {
            Id = "preview",
            Label = string.IsNullOrWhiteSpace(rule.Label) ? "加分项" : rule.Label,
            Delta = rule.Delta,
            Icon = rule.Icon,
            CapValue = rule.CapValue,
            CapScope = rule.CapScope,
        };

        Pad.Show(realRule, 1);
        await Task.CompletedTask;

        Pad.Confirmed -= OnPadConfirmed;
        Pad.Confirmed += OnPadConfirmed;
    }

    private void OnPadConfirmed(int delta, string? note)
    {
        if (_previewStudents.Count == 0) return;

        _awardedIndex = 0;
        _awardedDelta = delta;
        _previewStudents[0].Points = System.Math.Max(0, _previewStudents[0].Points + delta);

        RenderSelfCheckGrid();
        TryResultText.Text = $"刚才加的是 {_previewStudents[0].Name} {(delta >= 0 ? "+" : "")}{delta}" +
                             (string.IsNullOrWhiteSpace(note) ? "" : $"（{note}）") + "，只在内存里。";
        UndoButton.Visibility = Visibility.Visible;
    }

    private void OnTryUndo(object sender, RoutedEventArgs e)
    {
        if (_awardedIndex < 0) return;

        _previewStudents[_awardedIndex].Points =
            System.Math.Max(0, _previewStudents[_awardedIndex].Points - _awardedDelta);
        _awardedIndex = -1;
        RenderSelfCheckGrid();
        TryResultText.Text = "已撤销，卡上的分数回到原样。";
        UndoButton.Visibility = Visibility.Collapsed;
        ToastLayer.Show("真实记分台上也会这样出现一条「已撤销」提示条", 4);
    }

    // ---------------- 落盘 ----------------

    private void Finish()
    {
        try
        {
            ApplyAll();
            Completed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Services.LogError("onboarding-finish", ex);
            ToastLayer.Show("保存失败：" + ex.Message, 8);
        }
    }

    private void ApplyAll()
    {
        var settings = Services.Settings;

        App.SetMode(_mode);
        if (_className.Length > 0) settings.ClassName = _className;
        if (!string.IsNullOrWhiteSpace(_classCode)) settings.ClassCode = _classCode;

        // 示例数据：只有勾了才写，并记一个标记，设置页里能一键清掉。
        if (_demoData && Services.Students.ListActive().Count == 0)
        {
            for (var i = 1; i <= 48; i++)
                Services.Students.Insert(new Student { Name = $"学生 {i:D2}", SeatNo = i });
            settings.DemoData = true;
            Services.Log("向导写入示例数据 48 人");
        }

        if (_names.Count > 0)
        {
            var added = Services.Students.ImportNames(_names, Services.Students.ListActive()
                .Select(s => s.SeatNo ?? 0).DefaultIfEmpty(0).Max() + 1);
            Services.Log($"向导导入名单 {added} 人");
        }

        // 分值：先处理改动/新增，再处理删除 —— 顺序反了会把"删掉又新建"同名的两条都留下。
        var sort = 10;
        foreach (var draft in _rules.Where(r => !r.Removed))
        {
            if (string.IsNullOrWhiteSpace(draft.Label)) continue;

            Rule rule;
            if (draft.ExistingId is not null && Services.Rules.Get(draft.ExistingId) is { } existing)
            {
                rule = existing;
                rule.Delta = draft.Delta;
                rule.Icon = draft.Icon;
                rule.Label = draft.Label.Trim();
                rule.CapValue = draft.CapScope == CapScope.None ? null : draft.CapValue;
                rule.CapScope = draft.CapScope;
            }
            else
            {
                rule = new Rule
                {
                    Label = draft.Label.Trim(),
                    Delta = draft.Delta,
                    Icon = draft.Icon,
                    Sort = sort,
                    CapValue = draft.CapScope == CapScope.None ? null : draft.CapValue,
                    CapScope = draft.CapScope,
                };
            }
            Services.Rules.Save(rule);
            sort += 10;
        }

        foreach (var draft in _rules.Where(r => r.Removed && r.ExistingId is not null))
            Services.Rules.Delete(draft.ExistingId!);

        // 无论设没设密码，都要有一个班主任账号：否则这台机器连"进设置补密码"都做不到。
        var owner = Services.Members.Owner() ?? new Member { DisplayName = "班主任", Role = MemberRole.Owner };
        Services.Members.Save(owner, _pin.Length == 6 && !_pinFromBundle ? _pin : null);

        if (_recoveryCode is not null)
            settings.RecoveryCodeHash = PasswordHasher.Hash(RecoveryCode.Normalize(_recoveryCode));

        Services.Audit.Add("onboarding.done", null, null,
            $"{_mode.ToDb()}，班级 {settings.ClassName}，名单 {Services.Students.ListActive().Count} 人");
        Services.TryAutoBackup("onboarding");
    }

    // ---------------- 小工具 ----------------

    private static Style Styled(string key) => (Style)Application.Current.Resources[key];

    private static class Palette
    {
        public static readonly SolidColorBrush Secondary = Pal.Secondary;
    }

    private static UIElement Section(string title, IEnumerable<string>? bullets, UIElement? content = null)
    {
        var panel = new StackPanel { Spacing = 6, Padding = new Thickness(0, 8, 0, 8) };
        panel.Children.Add(new TextBlock { Text = title, Style = Styled("DeskH2Text") });

        if (content is not null) panel.Children.Add(content);

        if (bullets is not null)
            foreach (var line in bullets)
                panel.Children.Add(new TextBlock
                {
                    Text = "· " + line,
                    Style = Styled("SmallText"),
                    TextWrapping = TextWrapping.Wrap,
                });

        return panel;
    }
}
