using ClassPoints.App.Controls;
using ClassPoints.App.Services;
using ClassPoints.Core;
using ClassPoints.Data;
using Windows.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using ClassPoints.App.Theme;

namespace ClassPoints.App.Board;

/// <summary>
/// 记分台。整章的手感都在这个文件里：
/// 长按 400 ms 进框选、矩形实时预览、松手才并入选择、短按补选、边缘自动滚。
///
/// 三个容易写错的点，代码里都留了注释：
/// 1) 400 ms 定时器要被"移动 12 DIP"取消，而不是被"抬手"取消；
/// 2) 预览(_preview)与已选(_selected)必须是两个集合，否则矩形缩回去高亮不会消失；
/// 3) 框选态必须拦掉 ContextRequested，不然长按会弹系统右键菜单。
/// </summary>
public sealed partial class BoardScorePage : Page
{
    private const double Gap = 10;
    private const double MinCardW = 210;
    private const double MinCardH = 120;
    private const double MoveCancelThreshold = 12;
    private const int HoldMs = 400;
    private const int ButtonHoldMs = 500;

    private static readonly SolidColorBrush Positive = Pal.Positive;
    private static readonly SolidColorBrush Negative = Pal.Negative;
    private static readonly SolidColorBrush Warn = Pal.Warn;
    private static readonly SolidColorBrush PositiveBg = Pal.PositiveBg;
    private static readonly SolidColorBrush NegativeBg = Pal.NegativeBg;
    private static readonly SolidColorBrush Stroke = Pal.Stroke;
    private static readonly SolidColorBrush TextPrimary = Pal.Primary;
    private static readonly SolidColorBrush TextSecondary = Pal.Secondary;

    private readonly List<Student> _students = new();
    private readonly List<StudentCard> _cards = new();
    private readonly Dictionary<string, int> _totals = new();
    private readonly Dictionary<string, int> _today = new();
    private readonly HashSet<int> _selected = new();
    private readonly HashSet<int> _preview = new();
    private readonly Dictionary<string, int> _indexById = new();

    private readonly DispatcherTimer _holdTimer = new() { Interval = TimeSpan.FromMilliseconds(HoldMs) };
    private readonly DispatcherTimer _autoScroll = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly DispatcherTimer _resizeDebounce = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private readonly DispatcherTimer _barTimer = new();
    private readonly DispatcherTimer _refreshTimer = new() { Interval = TimeSpan.FromSeconds(20) };

    private PointerRoutedEventArgs? _downArgs;
    private Point _downPoint;
    private Point _downCanvas;
    private Point _lastPointer;
    private bool _pointerDown;
    /// <summary>上一下压到的是一整只手掌（接触面积过大），紧接着那次 Tapped 要作废。</summary>
    private bool _palmRejected;
    private bool _lassoing;
    private int _columns = 8;
    private double _cellW = 220;
    private double _cellH = 125;
    private bool _suppressClick;

    private List<Rule> _rules = new();
    private Rule? _padRule;
    private PendingAward? _pending;
    private string? _undoneBatchId;
    private BarMode _barMode = BarMode.Hidden;

    private enum BarMode
    {
        Hidden,
        Confirm,
        Undo,
    }

    private sealed record PendingAward(Rule Rule, int Delta, List<string> StudentIds, string? Note, bool ForceOverCap, int Count);

    public BoardScorePage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        Host.SizeChanged += OnHostSizeChanged;

        _holdTimer.Tick += OnHoldElapsed;
        _autoScroll.Tick += OnAutoScrollTick;
        _resizeDebounce.Tick += (_, _) =>
        {
            _resizeDebounce.Stop();
            LayoutCards();
        };
        _refreshTimer.Tick += (_, _) => ReloadData(keepSelection: true);
        _barTimer.Tick += (_, _) =>
        {
            _barTimer.Stop();
            // 只有撤销条会自己消失：它只是"还能反悔一会儿"的提示，超时等于收回去，没有代价。
            //
            // 确认条绝不能自动关。v1.3 报告里那条 P2-4 就是它：老师在教室后面被叫住，
            // 回来一看确认条没了，第一反应是"刚才应该记上了吧" —— 这是最贵的一种误解，
            // 因为当场没人会去核对，等到家长会才发现少了 80 分。
            if (_barMode == BarMode.Undo) HideBar();
        };

        CardGrid.PointerPressed += OnPointerPressed;
        CardGrid.PointerMoved += OnPointerMoved;
        CardGrid.PointerReleased += OnPointerReleased;
        CardGrid.PointerCanceled += OnPointerReleased;
        CardGrid.PointerCaptureLost += OnPointerCaptureLost;
        CardGrid.ContextRequested += OnContextRequested;
        // 「点一下选中」走 Tapped，不走 PointerReleased：
        // 手指按在 ScrollViewer 里时，指针常常被滚动手势抢走，PointerReleased 收不到，
        // 于是触屏上单击永远选不中（鼠标正常）。Tapped 是框架判完"这是一次点击"之后
        // 才给的，鼠标和手指都走它。
        CardGrid.Tapped += OnCardTapped;
        Scroll.SizeChanged += (_, _) =>
        {
            _resizeDebounce.Stop();
            _resizeDebounce.Start();
        };
    }

    private AppServices Services => AppServices.Instance;

    private BoardSession Session => Services.Session;

    // ================= 生命周期 =================

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Sounds.Enabled = Services.Settings.SoundOn;

        RefreshIdentityText();
        Session.Changed += OnSessionChanged;

        BuildRuleButtons();
        ReloadData(keepSelection: false);
        _refreshTimer.Start();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        Session.Changed -= OnSessionChanged;
        _refreshTimer.Stop();
        _holdTimer.Stop();
        _autoScroll.Stop();
        _barTimer.Stop();
    }

    private void OnSessionChanged()
    {
        RefreshIdentityText();

        // 快捷项是按人分的：换了身份，按钮就该换成那个人的那一套
        BuildRuleButtons();
    }

    private void OnHostSizeChanged(object sender, SizeChangedEventArgs e)
    {
        // 拖窗口边缘时 SizeChanged 每帧都发：150 ms 防抖，松手才重排
        _resizeDebounce.Stop();
        _resizeDebounce.Start();
    }

    // ================= 数据 =================

    private void ReloadData(bool keepSelection)
    {
        var period = Services.CurrentPeriod;
        var active = Services.Students.ListActive();
        var totals = Services.Entries.PeriodTotals(period.Id);
        var today = Services.Entries.TodayDeltas(period.Id, DateTime.Today);

        var idsBefore = keepSelection
            ? _selected.Where(i => i < _cards.Count).Select(i => _cards[i].Student?.Id).Where(s => s is not null).ToHashSet()
            : new HashSet<string?>();

        _students.Clear();
        _students.AddRange(active);
        _totals.Clear();
        foreach (var (k, v) in totals) _totals[k] = v;
        _today.Clear();
        foreach (var (k, v) in today) _today[k] = v;

        _indexById.Clear();
        for (var i = 0; i < _students.Count; i++) _indexById[_students[i].Id] = i;

        BuildCards();

        _selected.Clear();
        if (keepSelection)
            foreach (var id in idsBefore)
                if (id is not null && _indexById.TryGetValue(id, out var idx)) _selected.Add(idx);

        LayoutCards();
        RefreshVisual();
        UpdateEmptyState();
        RefreshIdentityText();
    }

    private void BuildCards()
    {
        CardGrid.Children.Clear();
        _cards.Clear();

        for (var i = 0; i < _students.Count; i++)
        {
            var student = _students[i];
            var card = new StudentCard { Index = i };
            card.Bind(student, _totals.GetValueOrDefault(student.Id), _today.GetValueOrDefault(student.Id));
            card.DoubleTapped += OnCardDoubleTapped;
            // 卡片是自绘的（没有可读的控件语义），名字和分数得自己报给辅助功能
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(card,
                $"{student.Name}，累计 {_totals.GetValueOrDefault(student.Id)} 分");
            CardGrid.Children.Add(card);
            _cards.Add(card);
        }
    }

    private void UpdateEmptyState()
    {
        var empty = _students.Count == 0;
        Empty.Show(empty);
        if (!empty) return;

        Empty.Configure("\uE716",
            "还没有学生。粘一列姓名就能开始。",
            "粘贴一列姓名", "刷新");

        // 刷新计时器每 20 秒调一次这里：先退订再挂，否则"粘贴"会一次弹出好几个框
        Empty.PrimaryInvoked -= OnPasteNames;
        Empty.SecondaryInvoked -= OnEmptyRefresh;
        Empty.PrimaryInvoked += OnPasteNames;
        Empty.SecondaryInvoked += OnEmptyRefresh;
    }

    private void OnEmptyRefresh() => ReloadData(keepSelection: false);

    private async void OnPasteNames()
    {
        var box = new TextBox
        {
            AcceptsReturn = true,
            PlaceholderText = "每行一个姓名，例如：\n王小明\n李思远\n张雨欣",
            Height = 260,
            TextWrapping = TextWrapping.Wrap,
        };
        var dialog = new ContentDialog
        {
            Title = "粘贴姓名",
            Content = box,
            PrimaryButtonText = "导入",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };

        if (await Dialogs.ShowAsync(dialog) != ContentDialogResult.Primary) return;

        // TextBox 里的换行是 \r，从 Excel 粘贴是 \r\n —— 用 TextLines 统一切，
        // 以前这里只按 \n 切，8 行姓名会粘成 1 个超长名字的学生。
        var lines = TextLines.Split(box.Text);
        var added = Services.Students.ImportNames(lines);
        Toast(added > 0 ? $"已加入 {added} 名学生" : "没读到姓名");
        ReloadData(keepSelection: false);
    }

    // ================= 布局 =================

    /// <summary>
    /// 列数只跟窗口走，不跟屏幕走。卡片有下限，放不下就滚动，宁可滚也不把字压小。
    /// </summary>
    private void LayoutCards()
    {
        if (_cards.Count == 0)
        {
            _columns = Services.Settings.SeatCols;
            return;
        }

        var availW = System.Math.Max(320, CardGrid.ActualWidth > 0 ? CardGrid.ActualWidth : Host.ActualWidth - 24);
        var availH = System.Math.Max(240, Scroll.ActualHeight > 0 ? Scroll.ActualHeight : Host.ActualHeight - 240);

        // ScrollViewer 里的 Grid 宽度靠 HorizontalAlignment=Stretch 时拿不到实际宽度，用视口宽度减滚动条
        var viewportW = Scroll.ViewportWidth > 0 ? Scroll.ViewportWidth : availW;
        var viewportH = Scroll.ViewportHeight > 0 ? Scroll.ViewportHeight : availH;

        _columns = System.Math.Clamp((int)((viewportW - Gap + Gap) / (MinCardW + Gap)), 4, 12);
        var capacityRows = System.Math.Clamp((int)((viewportH + Gap) / (MinCardH + Gap)), 2, 8);

        _cellW = System.Math.Max(120, (viewportW - Gap * (_columns - 1)) / _columns);
        _cellH = System.Math.Clamp((viewportH - Gap * (capacityRows - 1)) / capacityRows, 96, 172);

        var neededRows = (int)System.Math.Ceiling(_cards.Count / (double)_columns);

        CardGrid.ColumnDefinitions.Clear();
        CardGrid.RowDefinitions.Clear();
        for (var c = 0; c < _columns; c++)
            CardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(_cellW) });
        for (var r = 0; r < neededRows; r++)
            CardGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(_cellH) });

        CardGrid.Width = _columns * _cellW + Gap * (_columns - 1);
        CardGrid.Height = neededRows * _cellH + Gap * (neededRows - 1);

        for (var i = 0; i < _cards.Count; i++)
        {
            var card = _cards[i];
            var row = i / _columns;
            var col = i % _columns;
            Grid.SetRow(card, row);
            Grid.SetColumn(card, col);
            card.Margin = new Thickness(0, 0, col == _columns - 1 ? 0 : Gap, row == neededRows - 1 ? 0 : Gap);
            card.Width = double.NaN;
            card.Height = double.NaN;
            card.HorizontalAlignment = HorizontalAlignment.Stretch;
            card.VerticalAlignment = VerticalAlignment.Stretch;
        }

        // 卡片尺寸变了要重新算字号（分数永不低于 28）
        DispatcherQueue.TryEnqueue(() =>
        {
            foreach (var card in _cards) card.ApplyTypography();
        });
    }

    // ================= 类型按钮 =================

    /// <summary>
    /// 底部的项目按钮。所有按钮同一个尺寸（220×56），一行放得下 icon + 名称 + 分值。
    ///
    /// 以前是"按内容自适应宽度 + 三行文字"，长名字会把分值挤到按钮外面，
    /// 看着像两个按钮叠在一起；现在名称超长就省略，封顶信息只在鼠标悬停时给。
    /// </summary>
    private void BuildRuleButtons()
    {
        RulePanel.Children.Clear();

        // 全班共用 + 我自己那几项。换个人刷卡，多出来的是他自己的按钮。
        _rules = Services.Rules.ListForMember(Services.Operator.Id);

        foreach (var rule in _rules)
        {
            var button = new Button
            {
                Width = 220,
                Height = 56,
                Padding = new Thickness(14, 0, 14, 0),
                CornerRadius = new CornerRadius(12),
                BorderThickness = new Thickness(1),
                BorderBrush = Stroke,
                Background = rule.Delta >= 0 ? PositiveBg : NegativeBg,
                Tag = rule,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Center,
            };

            ToolTipService.SetToolTip(button,
                rule.HasCap ? $"{rule.Label} · {rule.CapText}" : $"{rule.Label} · 不封顶");

            // 按钮的内容是自己拼的 Grid，读屏软件只能读到一个"无名按钮" ——
            // 名字必须显式给，否则辅助功能树里这一排全是 button。
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
                button, $"{rule.Label} {rule.DeltaText}");

            var panel = new Grid { ColumnSpacing = 8 };
            panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var icon = new FontIcon
            {
                Glyph = rule.Glyph,
                FontFamily = new FontFamily(IconCatalog.FontFamily),
                FontSize = 16,
                Foreground = TextSecondary,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(icon, 0);
            panel.Children.Add(icon);

            var label = new TextBlock
            {
                Text = rule.Label,
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                Foreground = TextPrimary,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 1,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(label, 1);
            panel.Children.Add(label);

            var delta = new TextBlock
            {
                Text = rule.DeltaText,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 20,
                FontWeight = FontWeights.Bold,
                Foreground = rule.Delta >= 0 ? Positive : Negative,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(delta, 2);
            panel.Children.Add(delta);

            button.Content = panel;
            button.Click += OnRuleClick;
            AttachLongPress(button, rule);
            RulePanel.Children.Add(button);
        }

        if (_rules.Count == 0)
            RulePanel.Children.Add(new TextBlock
            {
                Text = "还没有加减分项。点「管理快捷项」建一个，或到设置里新建。",
                Foreground = TextSecondary,
                FontSize = 16,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0),
            });

        RulePanel.Children.Add(BuildOtherButton());
    }

    /// <summary>
    /// 「其他」：这一次临时起意的加分。名称和分值都手输，
    /// 不落库、不进按钮 —— 下节课还在的项目请走「管理快捷项」。
    /// </summary>
    private Button BuildOtherButton()
    {
        var button = new Button
        {
            Width = 150,
            Height = 56,
            Padding = new Thickness(14, 0, 14, 0),
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            BorderBrush = Stroke,
            Background = Pal.TileBg,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        ToolTipService.SetToolTip(button, "手动输入名称与分值，只记这一次");

        var panel = new Grid { ColumnSpacing = 8 };
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new FontIcon
        {
            Glyph = "\uE710",
            FontFamily = new FontFamily(IconCatalog.FontFamily),
            FontSize = 16,
            Foreground = TextSecondary,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(icon, 0);
        panel.Children.Add(icon);

        var label = new TextBlock
        {
            Text = "其他",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = TextPrimary,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(label, 1);
        panel.Children.Add(label);

        button.Content = panel;
        button.Click += OnOtherClick;
        // 自绘按钮在 UIA 里没有可读的名字，读屏软件只会念"按钮"
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, "其他：手动输入名称与分值");
        return button;
    }

    private async void OnOtherClick(object sender, RoutedEventArgs e)
    {
        if (_selected.Count == 0)
        {
            Toast("先点几个学生，再选加分项");
            return;
        }
        if (!EnsureWritable()) return;

        var nameBox = new TextBox { Header = "项目名称", PlaceholderText = "例如：帮老师搬作业" };
        // 不用 NumberBox：它要等焦点离开才把文本写进 Value，而这里点完"下一步"
        // 就直接读值 —— 自动化 setValue 会落在镜像元素上，读数还是 10（v1.3 报告 P3-1）。
        var deltaBox = new NumberInput
        {
            Header = "分值（负数表示扣分）",
            Value = 10,
            Minimum = -500,
            Maximum = 500,
            Step = 5,
        };

        var panel = new StackPanel { Spacing = 10, Width = 360 };
        panel.Children.Add(nameBox);
        panel.Children.Add(deltaBox);
        panel.Children.Add(new TextBlock
        {
            Text = "只记这一次，不会加到按钮里。",
            Foreground = TextSecondary,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
        });

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "其他",
            Content = panel,
            PrimaryButtonText = "下一步",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        // 校验放在弹窗里做，不放在"弹窗关了之后"做：
        // 关掉再提示「名称不能为空」，人已经输了的分值也跟着没了，
        // 只能从头再输一遍。所以这里循环重开同一个弹窗，输入原样保留。
        string label;
        int delta;
        while (true)
        {
            if (await Dialogs.ShowAsync(dialog) != ContentDialogResult.Primary) return;

            label = NameNormalizer.Clean(nameBox.Text);
            if (label.Length == 0)
            {
                nameBox.Header = "项目名称（不能为空）";
                continue;
            }

            deltaBox.Commit();
            delta = (int)deltaBox.Value;
            if (delta == 0)
            {
                deltaBox.Header = "分值（不能为 0）";
                continue;
            }

            break;
        }

        // Id 留空 = 一次性项：入账时 rule_id 记空、source 记 custom，
        // 台账上仍看得到项目名，但按钮不会多出来。
        ShowConfirm(new Rule { Label = label, Delta = delta }, delta, null, SelectedIds(), forceOverCap: false);
    }

    private async void OnQuickRulesClick(object sender, RoutedEventArgs e)
    {
        // 不用管理密码：这里能改的只有「我自己的」按钮，改不到班级的分值项，
        // 也改不到别人的。班委给自己加两个常用项，正是这个功能要解决的事。
        if (!Session.CanWrite)
        {
            Toast(Session.ReadOnly ? Session.WriteBlockedText : "请先刷卡（或输入密码）再管理快捷项");
            return;
        }

        var dialog = new QuickRulesDialog(XamlRoot, Services.Operator.Id);
        if (await dialog.ShowAsync()) BuildRuleButtons();
    }

    /// <summary>长按类型按钮 500 ms = 输入自定义分值。单击走默认分，都要过确认条。</summary>
    private void AttachLongPress(Button button, Rule rule)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ButtonHoldMs) };
        var fired = false;

        timer.Tick += (_, _) =>
        {
            timer.Stop();
            fired = true;
            _suppressClick = true;
            Sounds.Play(Cue.Thud);
            OpenPad(rule);
        };

        button.PointerPressed += (_, _) =>
        {
            // 每次按下都清掉上一次长按留下的标记：长按后在按钮外面松手不会产生 Click，
            // 标记就会一直挂着，把下一次正常点选吞掉（老师会以为第一次点没反应）。
            _suppressClick = false;
            fired = false;
            timer.Start();
        };
        button.PointerReleased += (_, _) => timer.Stop();
        button.PointerExited += (_, _) => timer.Stop();
        button.PointerCaptureLost += (_, _) => timer.Stop();
        button.Click += (_, _) =>
        {
            if (fired)
            {
                fired = false;
                return;
            }
        };
    }

    private void OnRuleClick(object sender, RoutedEventArgs e)
    {
        if (_suppressClick)
        {
            _suppressClick = false;
            return;
        }
        if (sender is not Button b || b.Tag is not Rule rule) return;

        if (_selected.Count == 0)
        {
            Toast("先点几个学生，再选加分项");
            return;
        }

        if (!EnsureWritable()) return;
        ShowConfirm(rule, rule.Delta, null, SelectedIds(), forceOverCap: false);
    }

    private bool EnsureWritable()
    {
        if (Session.CanWrite) return true;

        // 不弹"请先登录"的模态框：让人知道我点到了，只是没权限
        Toast(Session.ReadOnly ? Session.WriteBlockedText : "请先刷卡（或输入密码）再记分");
        Sounds.Play(Cue.Error);
        return false;
    }

    // ================= 数字键盘 =================

    private void OpenPad(Rule rule, bool forceOverCap = false)
    {
        if (_selected.Count == 0)
        {
            Toast("先点几个学生，再长按加分项");
            return;
        }
        if (!EnsureWritable()) return;

        Session.Interaction = InteractionState.Composing;
        var already = rule.HasCap && rule.CapScope == CapScope.Period && _selected.Count == 1
            ? Services.Entries.StudentRuleTotal(_students[_selected.First()].Id, rule.Id, Services.CurrentPeriod.Id)
            : 0;

        _padRule = rule;
        Pad.Show(rule, _selected.Count, already);
        Pad.Confirmed -= OnPadConfirmed;
        Pad.Confirmed += OnPadConfirmed;
        Pad.OverCapRequested -= OnPadOverCap;
        Pad.OverCapRequested += OnPadOverCap;
        Pad.Cancelled -= OnPadCancelled;
        Pad.Cancelled += OnPadCancelled;
    }

    private void OnPadConfirmed(int delta, string? note)
    {
        Pad.Hide();
        Session.Interaction = InteractionState.Scoring;
        if (_pending is null && _selected.Count == 0) return;

        var rule = _padRule;
        if (rule is null) return;
        ShowConfirm(rule, delta, note, SelectedIds(), forceOverCap: false);
    }

    private async void OnPadOverCap(int delta, string? note)
    {
        Pad.Hide();
        var shell = App.Host;
        if (shell is null) return;

        // 软上限：默认拦住，人可以让它过 —— 硬上限的意思是"系统比老师大"，教育场景一定出事
        var ok = await shell.AskPinAsync("超出上限", "这次按原值计入，需要管理密码", requireManager: true);
        if (!ok) return;

        Session.EnterAdmin();
        var rule = _padRule;
        if (rule is null) return;
        ShowConfirm(rule, delta, note, SelectedIds(), forceOverCap: true);
    }

    private void OnPadCancelled()
    {
        Session.Interaction = _selected.Count > 0 ? InteractionState.Scoring : InteractionState.Idle;
    }

    // ================= 确认条 / 撤销条 =================

    private List<string> SelectedIds() =>
        _selected.OrderBy(i => i).Where(i => i >= 0 && i < _cards.Count)
            .Select(i => _cards[i].Student!.Id).ToList();

    private void ShowConfirm(Rule rule, int delta, string? note, List<string> studentIds, bool forceOverCap)
    {
        if (studentIds.Count == 0) return;

        // 新的一次点选直接顶掉旧的确认（旧的按取消处理），不做队列
        HideBar();

        _pending = new PendingAward(rule, delta, studentIds, note, forceOverCap, studentIds.Count);
        _barMode = BarMode.Confirm;

        var sign = delta >= 0 ? "+" : "";
        ConfirmText.Text = studentIds.Count == 1
            ? $"{_students[_indexById[studentIds[0]]].Name} · {rule.Label} {sign}{delta}" +
              (string.IsNullOrWhiteSpace(note) ? "" : $"（{note}）")
            : $"给 {studentIds.Count} 人 · {rule.Label} {sign}{delta} · 合计 {sign}{delta * studentIds.Count}" +
              (string.IsNullOrWhiteSpace(note) ? "" : $"（{note}）");

        ConfirmHost.Visibility = Visibility.Visible;

        // 这里刻意不启动 _barTimer：确认条一直等到人按"确定"或点空白处取消。
        // 之前 8 秒超时按取消处理，看上去很谨慎（怕误记账），实际换来的是
        // "人离开一会儿，回来以为已经记上了"——静默失败比误记难查得多。
    }

    private void ShowUndoBar(string batchId, string text)
    {
        HideBar();
        _undoneBatchId = batchId;
        _barMode = BarMode.Undo;
        UndoText.Text = text;
        UndoHost.Visibility = Visibility.Visible;

        _barTimer.Interval = TimeSpan.FromSeconds(5);
        _barTimer.Start();
    }

    private void HideBar()
    {
        _barTimer.Stop();
        _barMode = BarMode.Hidden;
        ConfirmHost.Visibility = Visibility.Collapsed;
        UndoHost.Visibility = Visibility.Collapsed;
        _pending = null;
    }

    /// <summary>点遮罩 = 取消。误触的代价是"这次没记上"，比"确认了"轻得多。</summary>
    private void OnConfirmScrimTapped(object sender, TappedRoutedEventArgs e)
    {
        if (_barMode != BarMode.Confirm) return;
        HideBar();
        Session.Interaction = _selected.Count > 0 ? InteractionState.Scoring : InteractionState.Idle;
    }

    private void OnBarCancelClick(object sender, RoutedEventArgs e)
    {
        if (_barMode == BarMode.Undo)
        {
            UndoBatch();
            return;
        }
        HideBar();
        Session.Interaction = _selected.Count > 0 ? InteractionState.Scoring : InteractionState.Idle;
    }

    private void OnBarOkClick(object sender, RoutedEventArgs e)
    {
        if (_barMode == BarMode.Confirm)
        {
            CommitPending();
            return;
        }
        HideBar();
    }

    private void CommitPending()
    {
        var pending = _pending;
        if (pending is null) return;

        _barTimer.Stop();
        ConfirmHost.Visibility = Visibility.Collapsed;
        UndoHost.Visibility = Visibility.Collapsed;
        _barMode = BarMode.Hidden;

        try
        {
            var lines = pending.StudentIds
                .Select(id => new AwardLine(id, pending.Rule, pending.Delta, pending.ForceOverCap, pending.Note))
                .ToList();

            var outcome = Services.Entries.ApplyBatch(
                lines, Services.Operator,
                Session.Identity == IdentityState.Admin ? "board.admin" : "board.card",
                Services.CurrentPeriod.Id, sharedNote: pending.Note);

            ApplyOutcomeVisuals(outcome);
            _selected.Clear();
            RefreshVisual();
            Session.Interaction = InteractionState.Idle;
            Session.Touch();
            Sounds.Play(Cue.Ding);

            ShowUndoBar(outcome.BatchId, $"已{outcome.SummaryText}");

            if (outcome.Warnings.Count > 0) Toast(string.Join("；", outcome.Warnings.Take(2)), 4);

            _maybeBackupCounter += outcome.Count;
            if (_maybeBackupCounter >= 50)
            {
                _maybeBackupCounter = 0;
                _ = Task.Run(() => Services.TryAutoBackup("50 entries"));
            }
        }
        catch (Exception ex)
        {
            Services.LogError("commit-award", ex);
            Toast("没能记上：" + ex.Message, 5);
        }
        finally
        {
            _pending = null;
        }
    }

    private int _maybeBackupCounter;

    private void ApplyOutcomeVisuals(AwardOutcome outcome)
    {
        // 重新拉总分，再把每张卡的分数滚过去
        var totals = Services.Entries.PeriodTotals(Services.CurrentPeriod.Id);
        foreach (var (id, value) in totals) _totals[id] = value;

        var today = Services.Entries.TodayDeltas(Services.CurrentPeriod.Id, DateTime.Today);
        _today.Clear();
        foreach (var (k, v) in today) _today[k] = v;

        // 先更新「今日变化」，再让分数滚过去 —— 顺序反了 Bind 会把动画结果直接盖掉
        foreach (var card in _cards)
            if (card.Student is not null) card.UpdateToday(_today.GetValueOrDefault(card.Student.Id));

        foreach (var entry in outcome.Entries)
        {
            if (!_indexById.TryGetValue(entry.StudentId, out var idx) || idx >= _cards.Count) continue;
            var card = _cards[idx];
            card.RollTo(_totals.GetValueOrDefault(entry.StudentId));
            card.FloatDelta(entry.Delta);
            if (entry.Delta < 0) card.FlashNegative();
        }
    }

    private void UndoBatch()
    {
        var batchId = _undoneBatchId;
        HideBar();
        if (string.IsNullOrEmpty(batchId)) return;

        try
        {
            var now = Clock.NowMs();
            var ok = Services.Entries.TryPhysicalUndo(batchId, now);
            if (!ok) Services.Entries.ReverseBatch(batchId, Services.Operator, "board.undo");

            Sounds.Play(Cue.Undo);
            Toast(ok ? "已撤回（这事等于没发生过）" : "已撤销这一批");
            ReloadData(keepSelection: false);
        }
        catch (Exception ex)
        {
            Services.LogError("undo-batch", ex);
            Toast("撤销失败：" + ex.Message);
        }
    }

    private void OnBarAuxClick(object sender, RoutedEventArgs e)
    {
        if (_barMode != BarMode.Undo || string.IsNullOrEmpty(_undoneBatchId)) return;
        OpenNotePicker();
    }

    // ================= 补标签 =================

    private void OpenNotePicker()
    {
        NotePanel.Children.Clear();
        foreach (var note in Services.Notes.Top(8))
        {
            var chip = new Button
            {
                Content = note.Text,
                Height = 56,
                MinWidth = 100,
                Padding = new Thickness(18, 0, 18, 0),
                CornerRadius = new CornerRadius(28),
                FontSize = 18,
                Background = Pal.TileBg,
                Foreground = Pal.Primary,
                BorderBrush = Pal.Stroke,
                BorderThickness = new Thickness(1),
                Tag = note,
            };
            chip.Click += OnNoteChipClick;
            NotePanel.Children.Add(chip);
        }
        NoteLayer.Visibility = Visibility.Visible;
    }

    private void OnNoteChipClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not NoteTemplate note) return;
        if (string.IsNullOrEmpty(_undoneBatchId)) return;

        try
        {
            Services.Entries.AppendNote(_undoneBatchId, note.Text, note.Id);
            Toast($"已标注「{note.Text}」");
        }
        catch (Exception ex)
        {
            Services.LogError("append-note", ex);
        }
        NoteLayer.Visibility = Visibility.Collapsed;
    }

    private void OnNoteCloseClick(object sender, RoutedEventArgs e) => NoteLayer.Visibility = Visibility.Collapsed;

    private void OnNoteDoneClick(object sender, RoutedEventArgs e) => NoteLayer.Visibility = Visibility.Collapsed;

    // ================= 手势 =================

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        Session.Touch();

        var point = e.GetCurrentPoint(CardGrid);
        var canvasPoint = e.GetCurrentPoint(LassoCanvas);

        // 掌压过滤：接触面积通常是手指的 5 倍以上
        var contact = point.Properties.ContactRect;
        if (e.Pointer.PointerDeviceType == PointerDeviceType.Touch && contact.Width * contact.Height > 2400)
        {
            // 手掌压上来也会产生 Tapped，标记一下让那一次点击作废
            _palmRejected = true;
            return;
        }

        _downArgs = e;
        _downPoint = point.Position;
        _downCanvas = canvasPoint.Position;
        _lastPointer = _downCanvas;
        _pointerDown = true;

        // 每次按下都清掉上一次长按/框选留下的"吞掉下一次点击"标记：
        // 长按后在别处松手不会产生 Tapped，标记会一直挂着。
        _suppressClick = false;

        if (e.Pointer.PointerDeviceType == PointerDeviceType.Mouse)
        {
            // 鼠标不需要"长按"：按住左键拖一片是桌面上最自然的框选。
            // 但按下就立刻进框选会让"单击选一个人"变成只能加不能减 ——
            // 所以先记下起点，真移动了（见 OnPointerMoved）才进框选。
            return;
        }

        // 触屏/触笔：只起计时，不抢 Capture，也不碰滚动 ——
        // 400 ms 内一动没动，ScrollViewer 的拖拽阈值也还没突破
        _holdTimer.Start();
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_pointerDown) return;

        var point = e.GetCurrentPoint(CardGrid);
        var canvasPoint = e.GetCurrentPoint(LassoCanvas);
        _lastPointer = canvasPoint.Position;

        if (!_lassoing)
        {
            var dx = point.Position.X - _downPoint.X;
            var dy = point.Position.Y - _downPoint.Y;
            var dragged = System.Math.Abs(dx) > MoveCancelThreshold || System.Math.Abs(dy) > MoveCancelThreshold;
            if (!dragged) return;

            if (e.Pointer.PointerDeviceType == PointerDeviceType.Mouse)
            {
                // 鼠标拖 = 框选（这一个分支以前缺失，所以"按住拖一片选人"只在触屏上能用）
                BeginLasso();
                UpdateLasso(point.Position, canvasPoint.Position);
                return;
            }

            // 手指动了就说明用户想滚动，放弃这次长按（阈值 12 DIP，介于抖动和真实拖动之间）
            _holdTimer.Stop();
            _pointerDown = false;
            return;
        }

        UpdateLasso(point.Position, canvasPoint.Position);
    }

    private void OnHoldElapsed(object? sender, object e)
    {
        _holdTimer.Stop();
        if (!_pointerDown) return;
        BeginLasso();
    }

    /// <summary>进入框选态：抢指针、停滚动、把按下的那一格先选上。</summary>
    private void BeginLasso()
    {
        if (_lassoing || !_pointerDown) return;

        _lassoing = true;
        _suppressClick = true;
        Session.Interaction = InteractionState.Scoring;

        try { if (_downArgs is not null) CardGrid.CapturePointer(_downArgs.Pointer); } catch (Exception) { }
        Scroll.VerticalScrollMode = ScrollMode.Disabled;
        Scroll.HorizontalScrollMode = ScrollMode.Disabled;

        LassoRect.Visibility = Visibility.Visible;
        LassoBadge.Visibility = Visibility.Visible;

        if (HitIndex(_downPoint) is int i) _selected.Add(i);
        RefreshVisual();
        Sounds.Play(Cue.Thud);
        _autoScroll.Start();
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _holdTimer.Stop();

        if (!_lassoing)
        {
            // 单击选中交给 OnCardTapped：这里只管收尾，不再判一次，
            // 否则同一只手会"选上再取消"，表现成点了没反应。
            _pointerDown = false;
            return;
        }

        EndLasso(e, commit: true);
    }

    /// <summary>
    /// 单击卡片 = 选中 / 取消。鼠标、手指、触笔都走这里。
    /// 框选拖拽（_lassoing）和长按后的那次抬手不算点击。
    /// </summary>
    private void OnCardTapped(object sender, TappedRoutedEventArgs e)
    {
        if (_palmRejected)
        {
            _palmRejected = false;
            return;
        }
        if (_lassoing) return;
        if (_suppressClick)
        {
            _suppressClick = false;
            return;
        }

        if (HitIndex(e.GetPosition(CardGrid)) is not int idx) return;
        ToggleIndex(idx);
        e.Handled = true;
    }

    private void OnPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (_lassoing) EndLasso(e, commit: true);
        _pointerDown = false;
        _holdTimer.Stop();
    }

    private void EndLasso(PointerRoutedEventArgs args, bool commit)
    {
        _lassoing = false;
        _autoScroll.Stop();

        if (commit) _selected.UnionWith(_preview);   // 松手才并入，拖错可以缩回去
        _preview.Clear();

        try { CardGrid.ReleasePointerCapture(args.Pointer); } catch (Exception) { }
        Scroll.VerticalScrollMode = ScrollMode.Auto;
        Scroll.HorizontalScrollMode = ScrollMode.Disabled;

        LassoRect.Visibility = Visibility.Collapsed;
        LassoBadge.Visibility = Visibility.Collapsed;

        RefreshVisual();
        Session.Interaction = _selected.Count > 0 ? InteractionState.Scoring : InteractionState.Idle;
        Session.Touch();
    }

    private void UpdateLasso(Point gridPoint, Point canvasPoint)
    {
        var rect = RectFrom(_downCanvas, canvasPoint);
        Canvas.SetLeft(LassoRect, rect.X);
        Canvas.SetTop(LassoRect, rect.Y);
        LassoRect.Width = rect.Width;
        LassoRect.Height = rect.Height;
        LassoRect.Visibility = Visibility.Visible;

        var gridRect = RectFrom(_downPoint, gridPoint);
        _preview.Clear();
        foreach (var idx in IndicesIn(gridRect)) _preview.Add(idx);

        RefreshVisual();
    }

    private static Rect RectFrom(Point a, Point b) => new(
        System.Math.Min(a.X, b.X),
        System.Math.Min(a.Y, b.Y),
        System.Math.Abs(a.X - b.X),
        System.Math.Abs(a.Y - b.Y));

    /// <summary>矩形 → 行列区间 → 索引。不遍历 UI 树，48 张卡也只是几十次算术。</summary>
    private IEnumerable<int> IndicesIn(Rect r)
    {
        var c0 = System.Math.Max(0, (int)(r.Left / (_cellW + Gap)));
        var c1 = System.Math.Min(_columns - 1, (int)(r.Right / (_cellW + Gap)));
        var r0 = System.Math.Max(0, (int)(r.Top / (_cellH + Gap)));
        var r1 = System.Math.Max(0, (int)(r.Bottom / (_cellH + Gap)));

        for (var row = r0; row <= r1; row++)
            for (var col = c0; col <= c1; col++)
            {
                var idx = row * _columns + col;
                if (idx >= 0 && idx < _cards.Count) yield return idx;
            }
    }

    private int? HitIndex(Point p)
    {
        if (_cards.Count == 0) return null;
        var col = (int)(p.X / (_cellW + Gap));
        var row = (int)(p.Y / (_cellH + Gap));
        if (col < 0 || col >= _columns || row < 0) return null;
        var idx = row * _columns + col;
        return idx >= 0 && idx < _cards.Count ? idx : null;
    }

    private void OnAutoScrollTick(object? sender, object e)
    {
        if (!_lassoing) return;

        const double edge = 48;
        const double maxStep = 12;
        var y = _lastPointer.Y;
        var height = LassoCanvas.ActualHeight;
        if (height <= 0) return;

        double delta = 0;
        if (y < edge) delta = -maxStep * (1 - y / edge);
        else if (y > height - edge) delta = maxStep * (1 - (height - y) / edge);
        if (System.Math.Abs(delta) < 0.5) return;

        var before = Scroll.VerticalOffset;
        Scroll.ChangeView(null, before + delta, null, disableAnimation: true);
        var after = Scroll.VerticalOffset;
        var moved = after - before;

        // 矩形起点要跟着内容走，否则它会"粘"在屏幕上，选不到滚动出来的那一行
        if (System.Math.Abs(moved) > 0.1)
        {
            _downPoint.Y -= moved;
            _downCanvas.Y -= moved;
            UpdateLasso(_lastPointer, _lastPointer);
        }
    }

    private void OnContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        // 长按会触发系统右键菜单，框选态必须拦掉
        if (_lassoing) args.Handled = true;
    }

    private void ToggleIndex(int index)
    {
        if (!_selected.Add(index)) _selected.Remove(index);
        Session.Interaction = _selected.Count > 0 ? InteractionState.Scoring : InteractionState.Idle;
        Session.Touch();
        Sounds.Play(Cue.Tap);
        RefreshVisual();
    }

    private void RefreshVisual()
    {
        for (var i = 0; i < _cards.Count; i++)
            _cards[i].SetSelected(_selected.Contains(i) || _preview.Contains(i));

        var count = _selected.Count + _preview.Count;
        SelectedText.Text = $"已选 {count} 人";
        PreviewText.Text = _lassoing ? "（框选中，抬手确认）" : "";
    }

    // ================= 按钮 =================

    private void OnLassoButtonClick(object sender, RoutedEventArgs e)
    {
        // 给"我就想框一片"的人：直接进框选态，一个都不选
        Toast("按住 0.4 秒再拖是框选");
        Session.Interaction = _selected.Count > 0 ? InteractionState.Scoring : InteractionState.Idle;
    }

    private void OnSelectAllClick(object sender, RoutedEventArgs e)
    {
        // 只对当前可见范围生效，所以文案是「全选本屏」
        var top = Scroll.VerticalOffset;
        var visibleBottom = top + Scroll.ViewportHeight;

        for (var i = 0; i < _cards.Count; i++)
        {
            var row = i / _columns;
            var cardTop = row * (_cellH + Gap);
            if (cardTop + _cellH >= top && cardTop <= visibleBottom) _selected.Add(i);
        }

        Session.Interaction = InteractionState.Scoring;
        RefreshVisual();
    }

    private void OnSelectClassClick(object sender, RoutedEventArgs e)
    {
        // 全班：不管滚到哪儿。一班 45 人分两屏时，全选本屏只框得住 40 个。
        for (var i = 0; i < _cards.Count; i++) _selected.Add(i);

        Session.Interaction = InteractionState.Scoring;
        Session.Touch();
        RefreshVisual();
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        _selected.Clear();
        _preview.Clear();
        Session.Interaction = InteractionState.Idle;
        RefreshVisual();
    }

    private void OnUndoLastClick(object sender, RoutedEventArgs e)
    {
        var batchId = _undoneBatchId;
        if (string.IsNullOrEmpty(batchId))
        {
            Toast("没有可撤销的上一批记录");
            return;
        }
        UndoBatch();
    }

    // ================= 学生档案 =================

    private void OnCardDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (sender is not StudentCard card || card.Student is null) return;

        var student = card.Student;
        ProfileName.Text = student.Name;
        ProfileSub.Text = $"本周期 {_totals.GetValueOrDefault(student.Id)} 分 · 累计 {Services.Entries.CumulativeTotals().GetValueOrDefault(student.Id)} 分";

        ProfileList.Children.Clear();
        foreach (var entry in Services.Entries.EntriesForStudent(student.Id, 8))
        {
            var row = new Grid { ColumnSpacing = 10, Padding = new Thickness(0, 6, 0, 6) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var time = new TextBlock
            {
                Text = Clock.ToLocal(entry.OccurredAt).ToString("MM-dd HH:mm"),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 14,
                Foreground = Pal.Secondary,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(time, 0);

            var label = new TextBlock
            {
                Text = entry.RuleLabel + (string.IsNullOrEmpty(entry.Note) ? "" : $" · {entry.Note}"),
                FontSize = 16,
                Foreground = Pal.Primary,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(label, 1);

            var delta = new TextBlock
            {
                Text = entry.DeltaText + (entry.IsReversed ? "（已撤销）" : ""),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                Foreground = entry.IsReversed
                    ? Pal.Secondary
                    : entry.Delta >= 0 ? Positive : Negative,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(delta, 2);

            row.Children.Add(time);
            row.Children.Add(label);
            row.Children.Add(delta);
            ProfileList.Children.Add(row);
        }

        if (ProfileList.Children.Count == 0)
            ProfileList.Children.Add(new TextBlock
            {
                Text = "还没有这个人的记录。",
                Foreground = Pal.Secondary,
                FontSize = 16,
            });

        ProfileLayer.Visibility = Visibility.Visible;
    }

    private void OnProfileCloseClick(object sender, RoutedEventArgs e) => ProfileLayer.Visibility = Visibility.Collapsed;

    // ================= 杂项 =================

    private void RefreshIdentityText()
    {
        // 同步状态和班级名都归外壳顶部的常驻状态条管（那里还挂着日期和时钟），
        // 这页只写"现在是谁在记分" —— 三处各写一遍，教室里看到的就是同一段话重复三次。
        // SyncEnabled 交给 SyncPump 按"这台机器有没有配对"统一写，别在这里猜。

        // 左上角只给一条手感提示：班级名、周期、身份都在外壳顶栏上，重复三遍反而更难扫。
        WhoText.Text = "点一下选中，按住拖一片是框选";
    }

    private void Toast(string message, double seconds = 3) => ToastLayer.Show(message, seconds);
}
