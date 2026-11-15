using System.Text;
using ClassPoints.App.Services;
using ClassPoints.Core;
using ClassPoints.Data;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace ClassPoints.App.Desk;

/// <summary>
/// 座位表（可选功能，设置 → 可选功能 里打开）。
///
/// 教室按「排 × 组 × 每组座位」画，组与组之间留走廊、讲台在最前面。
/// 两个学生的座位对调、把未排座的人放进空位、按成绩自动排座、固定住某些人再排 ——
/// 这些都在内存里做，点「保存」才写回 students.seat_no；「撤销 / 重做」在同一份草稿上来回退。
///
/// 三道保护：切页自动落盘、排座前自动留一份快照、30 步撤销栈。
/// 为什么不做拖拽：教室里是手掌压屏，拖到一半滑出座位格子很常见；点一下选中、
/// 再点目标，鼠标和触屏是同一套动作，也不会误触。
/// </summary>
public sealed partial class DeskSeatsPage : Page
{
    private static AppServices Services => App.Services;

    private static IShellHost? Shell => App.Host;

    /// <summary>一个座位格子的控件引用。改内容比整页重建便宜，也不会把滚动位置弹回顶部。</summary>
    private sealed record SeatVisual(Border Root, TextBlock Name, TextBlock Sub, FontIcon Pin);

    private SeatLayout _layout = SeatLayout.Default;
    private SeatRules _rules = new();
    private List<Student> _students = [];
    private Dictionary<string, Student> _byId = [];
    private Dictionary<int, string> _seats = [];
    private Dictionary<int, string> _saved = [];
    private HashSet<string> _pins = [];
    private Dictionary<string, int> _points = [];
    private readonly List<Dictionary<int, string>> _undo = [];
    private readonly List<Dictionary<int, string>> _redo = [];
    private readonly Dictionary<int, SeatVisual> _visuals = [];
    private string? _selectedId;

    public DeskSeatsPage()
    {
        InitializeComponent();

        Loaded += (_, _) => Reload();

        // 教室大小跟着窗口变：座位格子按可用宽度算一次，太窄就保底 80，太宽也不铺成一条。
        SeatScroll.SizeChanged += (_, _) => FitTiles();

        // 座位格子的画刷是代码里取的，不吃 XAML 的 ThemeResource：切主题要重刷一遍，
        // 否则会停在上一套颜色上（白底黑字突然变白字白底）。
        ActualThemeChanged += (_, _) =>
        {
            if (_visuals.Count > 0) RefreshAll();
        };

        // 切页 / 关窗口前把没保存的落库。排了半小时的座位因为"忘了点保存"没了，
        // 是这个功能最容易挨骂的地方 —— 宁可悄悄写一次。
        Unloaded += (_, _) => SaveQuietly();
    }

    // ================= 载入 =================

    private void Reload()
    {
        try
        {
            _layout = Services.Settings.SeatLayout;
            _pins = Services.Settings.SeatPinned;
            _points = Services.PeriodTotals();

            _students = Services.Students.ListActive()
                .OrderBy(s => s.SeatNo ?? int.MaxValue)
                .ThenBy(s => s.Name, StringComparer.CurrentCulture)
                .ToList();
            _byId = _students.ToDictionary(s => s.Id);

            // 固定名单、区域、同桌关系里可能有已经转学 / 停用的人：只留还在册的。
            var pinCount = _pins.Count;
            _pins.IntersectWith(_byId.Keys);
            if (_pins.Count != pinCount) Services.Settings.SeatPinned = _pins;
            _rules = PruneRules(Services.Settings.SeatRules);

            _seats = ResolveSeats();
            _saved = new Dictionary<int, string>(_seats);
            _undo.Clear();
            _redo.Clear();
            _selectedId = null;

            BuildStrategyMenu();
            BuildSheetMenu();
            BuildGrid();
            FitTiles();
            RefreshAll();
        }
        catch (Exception ex)
        {
            Services.LogError("desk-seats", ex);
            Shell?.Toast("座位表读取失败：" + ex.Message, 6);
        }
    }

    /// <summary>规则里只保留还在册的学生；有变化才写回，别每次进页面都写一次库。</summary>
    private SeatRules PruneRules(SeatRules rules)
    {
        var pruned = rules.Clone();
        foreach (var id in pruned.Zones.Keys.Where(id => !_byId.ContainsKey(id)).ToList())
            pruned.Zones.Remove(id);

        var before = pruned.Links.Count;
        pruned.Links.RemoveAll(l => !_byId.ContainsKey(l.A) || !_byId.ContainsKey(l.B));

        if (pruned.Zones.Count != rules.Zones.Count || pruned.Links.Count != before)
            Services.Settings.SeatRules = pruned;

        return pruned;
    }

    /// <summary>
    /// 从名单还原座位图：只认本布局装得下的座位号，同一个号被两个人占着时先到先得。
    /// 越界的号不丢数据（students.seat_no 还在），只是这一版布局里显示为"未排座"，
    /// 老师把布局改回去就又能看见 —— 悄悄改库里的号比显示不出来更糟。
    /// </summary>
    private Dictionary<int, string> ResolveSeats()
    {
        var map = new Dictionary<int, string>();
        foreach (var s in _students)
        {
            if (s.SeatNo is not int n) continue;
            if (n < 1 || n > _layout.Capacity) continue;
            if (!map.TryAdd(n, s.Id))
                Services.Log($"座位 {n} 被 {s.Name} 和另一个学生同时占用，后者按未排座处理");
        }
        return map;
    }

    // ================= 工具条菜单 =================

    private void BuildStrategyMenu()
    {
        StrategyMenu.Items.Clear();
        foreach (var strategy in SeatArranger.All)
        {
            var item = new MenuFlyoutItem { Text = SeatArranger.Label(strategy) };
            ToolTipService.SetToolTip(item, SeatArranger.Hint(strategy));
            item.Click += async (_, _) => await ApplyStrategyAsync(strategy);
            StrategyMenu.Items.Add(item);
        }

        StrategyMenu.Items.Add(new MenuFlyoutSeparator());

        var rotate = new ToggleMenuFlyoutItem
        {
            Text = "尽量不重复上次的区域",
            IsChecked = Services.Settings.SeatPreferRotate,
        };
        ToolTipService.SetToolTip(rotate, "按最近一份快照判断：上次坐前排的人优先排到中/后排。只影响没被固定的座位。");
        rotate.Click += (_, _) =>
        {
            Services.Settings.SeatPreferRotate = rotate.IsChecked;
            Shell?.Toast(rotate.IsChecked ? "已开启：排座时尽量换个区域" : "已关闭：按策略直接排", 3);
        };
        StrategyMenu.Items.Add(rotate);
    }

    private void BuildSheetMenu()
    {
        SheetMenu.Items.Clear();

        SheetMenu.Items.Add(Item("导入座次表（按布局）…", () => _ = ImportSheetAsync()));
        SheetMenu.Items.Add(new MenuFlyoutSeparator());
        SheetMenu.Items.Add(Item("导出 Excel（.xlsx）", ExportExcel));
        SheetMenu.Items.Add(Item("导出 SVG（.svg）", ExportSvg));
        SheetMenu.Items.Add(Item("导出 CSV", ExportCsv));

        MenuFlyoutItem Item(string text, Action action)
        {
            var item = new MenuFlyoutItem { Text = text };
            item.Click += (_, _) =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    Services.LogError("seat-sheet-menu", ex);
                    Shell?.Toast("操作失败：" + ex.Message, 5);
                }
            };
            return item;
        }
    }

    // ================= 教室网格 =================

    private void BuildGrid()
    {
        SeatGrid.Children.Clear();
        SeatGrid.RowDefinitions.Clear();
        SeatGrid.ColumnDefinitions.Clear();
        _visuals.Clear();

        // 第 0 列放「N 排」，之后按 组 → (组内座位…) → 走廊 交替。
        SeatGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
        for (var g = 0; g < _layout.Groups; g++)
        {
            if (g > 0) SeatGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
            for (var c = 0; c < _layout.PerGroup; c++)
                SeatGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        }

        for (var row = 1; row <= _layout.Rows; row++)
        {
            SeatGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var label = new TextBlock
            {
                Text = $"{row} 排",
                Style = Styled("DeskSecondaryText"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 0),
            };
            Grid.SetRow(label, row - 1);
            Grid.SetColumn(label, 0);
            SeatGrid.Children.Add(label);

            for (var g = 0; g < _layout.Groups; g++)
            {
                for (var c = 0; c < _layout.PerGroup; c++)
                {
                    var col = g * _layout.PerGroup + c + 1;
                    if (_layout.SeatNo(row, col) is not int seat) continue;

                    var tile = BuildTile(seat);
                    Grid.SetRow(tile.Root, row - 1);
                    Grid.SetColumn(tile.Root, 1 + g * (_layout.PerGroup + 1) + c);
                    SeatGrid.Children.Add(tile.Root);
                    _visuals[seat] = tile;
                }
            }
        }
    }

    private SeatVisual BuildTile(int seat)
    {
        var panel = new StackPanel
        {
            Spacing = 1,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        var name = new TextBlock
        {
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1,
            Foreground = Br("TextPrimaryBrush"),
        };
        var sub = new TextBlock
        {
            FontSize = 11,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = Br("TextSecondaryBrush"),
            MaxLines = 1,
        };
        panel.Children.Add(name);
        panel.Children.Add(sub);

        var pin = new FontIcon
        {
            Glyph = "\uE72E",
            FontSize = 10,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Foreground = Br("WarnBrush"),
            Visibility = Visibility.Collapsed,
        };

        var content = new Grid();
        content.Children.Add(panel);
        content.Children.Add(pin);

        var border = new Border
        {
            Tag = seat,
            Child = content,
            Width = TileWidth(),
            MinHeight = 66,
            Margin = new Thickness(1),
            Padding = new Thickness(5, 4, 5, 4),
            CornerRadius = new CornerRadius(9),
            BorderThickness = new Thickness(1),
            BorderBrush = Br("CardStrokeBrush"),
            Background = Br("CardBgBrush"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        border.Tapped += OnSeatTapped;
        border.RightTapped += OnSeatRightTapped;

        return new SeatVisual(border, name, sub, pin);
    }

    /// <summary>
    /// 一格多宽。按可用宽度分，但不小于 80（再窄三字姓名就折行）、不大于 176
    /// （大屏上一格铺成一条反而不像座位）。
    /// </summary>
    private double TileWidth()
    {
        var available = SeatScroll.ViewportWidth;
        if (double.IsNaN(available) || available <= 0) available = SeatScroll.ActualWidth;
        if (available <= 0) return 104;

        var reserve = 52 + _layout.Aisles * 20 + _layout.Columns * 4 + 36; // 行标签 + 走廊 + 间距 + 边距
        return System.Math.Clamp((available - reserve) / _layout.Columns, 80, 176);
    }

    private void FitTiles()
    {
        if (_visuals.Count == 0) return;
        var width = TileWidth();
        foreach (var visual in _visuals.Values) visual.Root.Width = width;
    }

    private void OnSeatTapped(object sender, TappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: int seat }) return;
        e.Handled = true;
        TapSeat(seat);
    }

    /// <summary>右键（触屏长按）：固定 / 清空 / 换人 / 约束。只给鼠标用的快捷入口，左边点选也能做。</summary>
    private void OnSeatRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: int seat } element) return;
        e.Handled = true;

        var menu = new MenuFlyout();
        var occupant = _seats.GetValueOrDefault(seat);

        if (occupant is not null)
        {
            var pinned = _pins.Contains(occupant);
            var pinItem = new MenuFlyoutItem { Text = pinned ? "取消固定" : "固定此座" };
            pinItem.Click += (_, _) => TogglePin(occupant);
            menu.Items.Add(pinItem);

            var ruleItem = new MenuFlyoutItem { Text = "设置约束…" };
            ruleItem.Click += (_, _) =>
            {
                _selectedId = occupant;
                RefreshAll();
                _ = EditRulesAsync();
            };
            menu.Items.Add(ruleItem);

            var clear = new MenuFlyoutItem { Text = "清空此座" };
            clear.Click += (_, _) => ClearSeat(seat);
            menu.Items.Add(clear);
        }

        var pick = new MenuFlyoutItem { Text = "放一个未排座的学生…" };
        pick.Click += (_, _) => _ = PickStudentForAsync(seat);
        menu.Items.Add(pick);

        menu.ShowAt(element);
    }

    // ================= 交互 =================

    private void TapSeat(int seat)
    {
        var occupant = _seats.GetValueOrDefault(seat);

        // 还没有选中任何人：点有人的座位 = 选中他；点空位 = 挑人放进来。
        if (_selectedId is null)
        {
            if (occupant is not null) Select(occupant);
            else _ = PickStudentForAsync(seat);
            return;
        }

        // 点的是自己：取消选择。
        if (occupant == _selectedId)
        {
            _selectedId = null;
            RefreshAll();
            return;
        }

        PushUndo();
        var from = SeatOf(_selectedId);
        var moving = _selectedId;

        if (from is int oldSeat) _seats.Remove(oldSeat);
        if (occupant is not null) _seats.Remove(seat);

        _seats[seat] = moving;

        // 目标座位上原来有人、而且被移动的人本来也有座位：两人对调。
        if (occupant is not null && from is int back) _seats[back] = occupant;

        _selectedId = null;
        RefreshAll();
        MarkDirty();
    }

    private void Select(string id)
    {
        _selectedId = id;
        RefreshAll();
    }

    private void OnDeselect(object sender, RoutedEventArgs e)
    {
        _selectedId = null;
        RefreshAll();
    }

    private void OnTogglePin(object sender, RoutedEventArgs e)
    {
        if (_selectedId is not { } id) return;
        TogglePin(id);
    }

    private void TogglePin(string id)
    {
        if (!_pins.Add(id)) _pins.Remove(id);
        Services.Settings.SeatPinned = _pins;
        RefreshAll();
    }

    private void OnClearSeat(object sender, RoutedEventArgs e)
    {
        if (_selectedId is not { } id || SeatOf(id) is not int seat) return;
        ClearSeat(seat);
    }

    private void ClearSeat(int seat)
    {
        if (!_seats.ContainsKey(seat)) return;
        PushUndo();
        _seats.Remove(seat);
        _selectedId = null;
        RefreshAll();
        MarkDirty();
    }

    private void OnClearSeats(object sender, RoutedEventArgs e) => _ = ClearAllAsync();

    private async Task ClearAllAsync()
    {
        if (_seats.Count == 0) return;
        if (Shell is not { } shell) return;

        var ok = await shell.ConfirmAsync("清空座位",
            $"把 {_seats.Count} 个人从座位上撤下来，全部回到「未排座」。名单和积分不受影响，没保存之前可以撤销。",
            "清空", "取消");
        if (!ok) return;

        PushUndo();
        _seats.Clear();
        _selectedId = null;
        RefreshAll();
        MarkDirty();
    }

    // ================= 约束 =================

    private void OnEditRules(object sender, RoutedEventArgs e) => _ = EditRulesAsync();

    /// <summary>某个学生的区域要求与同桌关系。每人一条，换掉旧的而不是叠上去。</summary>
    private async Task EditRulesAsync()
    {
        if (_selectedId is not { } id || !_byId.TryGetValue(id, out var student)) return;

        var rules = _rules.Clone();

        var zone = new ComboBox
        {
            ItemsSource = new[] { "不限", "前排", "中排", "后排" },
            SelectedIndex = (int)rules.ZoneOf(id),
            Width = 160,
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        var others = _students.Where(s => s.Id != id).ToList();
        var relation = new ComboBox
        {
            ItemsSource = new[] { "不设置", "必须同桌", "不能同桌" },
            SelectedIndex = 0,
            Width = 160,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        var person = new ComboBox
        {
            ItemsSource = others.Select(Display).ToList(),
            SelectedIndex = -1,
            Width = 220,
            IsEnabled = false,
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        var existing = rules.LinkOf(id);
        if (existing is not null)
        {
            var otherId = existing.A == id ? existing.B : existing.A;
            var index = others.FindIndex(s => s.Id == otherId);
            if (index >= 0)
            {
                relation.SelectedIndex = existing.Kind == SeatLinkKind.Together ? 1 : 2;
                person.SelectedIndex = index;
                person.IsEnabled = true;
            }
        }

        relation.SelectionChanged += (_, _) => person.IsEnabled = relation.SelectedIndex > 0;

        var panel = new StackPanel { Spacing = 12, MinWidth = 360 };
        panel.Children.Add(Label("区域要求", "「前排」是第 1 到第 2 排（按排数三等分），中排、后排同理。"));
        panel.Children.Add(zone);
        panel.Children.Add(Label("同桌关系", "同桌 = 同一张桌子的左右两个座位；只影响自动排座，手动换位不会被拦。"));
        panel.Children.Add(relation);
        panel.Children.Add(Label("和谁", ""));
        panel.Children.Add(person);

        var dialog = new ContentDialog
        {
            Title = student.Name + " · 约束",
            Content = panel,
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        rules.SetZone(id, (SeatZone)System.Math.Max(0, zone.SelectedIndex));
        if (relation.SelectedIndex == 0)
            rules.SetLink(id, null, SeatLinkKind.Together);
        else if (person.SelectedIndex >= 0 && person.SelectedIndex < others.Count)
            rules.SetLink(id, others[person.SelectedIndex].Id,
                relation.SelectedIndex == 1 ? SeatLinkKind.Together : SeatLinkKind.Apart);

        _rules = rules;
        Services.Settings.SeatRules = rules;
        RefreshAll();
        Shell?.Toast("约束已保存，下次自动排座生效", 3);
    }

    private static UIElement Label(string title, string hint)
    {
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(new TextBlock { Text = title, Style = Styled("DeskColumnHeader") });
        if (!string.IsNullOrWhiteSpace(hint))
            panel.Children.Add(new TextBlock
            {
                Text = hint,
                Style = Styled("DeskSecondaryText"),
                TextWrapping = TextWrapping.Wrap,
            });
        return panel;
    }

    // ================= 自动排座 =================

    private async Task ApplyStrategyAsync(SeatStrategy strategy)
    {
        try
        {
            await ApplyStrategyCoreAsync(strategy);
        }
        catch (Exception ex)
        {
            Services.LogError("seat-arrange", ex);
            Shell?.Toast("排座失败：" + ex.Message, 5);
        }
    }

    private async Task ApplyStrategyCoreAsync(SeatStrategy strategy)
    {
        if (_students.Count == 0)
        {
            Shell?.Toast("名单是空的，先去「名单」把人加进来");
            return;
        }

        // 排座前先留一份底：这不只是"后悔药"，也是「尽量不重复上次区域」的判断依据。
        if (_seats.Count > 0) SaveSnapshot($"排座前 · {DateTime.Now:MM-dd HH:mm}", quiet: true);

        PushUndo();

        // 固定的人也占着座位：这些座位不参与分配，人也不进候选名单。
        var blocked = new List<int>();
        var candidates = new List<SeatCandidate>();
        foreach (var s in _students)
        {
            if (_pins.Contains(s.Id) && SeatOf(s.Id) is int seat)
            {
                blocked.Add(seat);
                continue;
            }
            candidates.Add(new SeatCandidate(
                s.Id,
                _points.GetValueOrDefault(s.Id),
                string.IsNullOrWhiteSpace(s.StudentNo) ? s.Name : s.StudentNo!));
        }

        // 只清非固定的：固定的座位和座位上的人原地不动。
        foreach (var seat in _seats.Where(kv => !_pins.Contains(kv.Value)).Select(kv => kv.Key).ToList())
            _seats.Remove(seat);

        var plan = SeatArranger.Plan(_layout, candidates, strategy, new SeatPlanOptions(
            blocked,
            _rules,
            LastZones(),
            Services.Settings.SeatPreferRotate));

        foreach (var (id, seat) in plan.Placements) _seats[seat] = id;

        _selectedId = null;
        RefreshAll();
        MarkDirty();

        var left = _students.Count(s => SeatOf(s.Id) is null);
        if (plan.Issues.Count > 0)
            await ShowIssuesAsync(plan, strategy, left);
        else
            Shell?.Toast(left > 0
                ? $"已按「{SeatArranger.Label(strategy)}」排座，{left} 人没有座位，记得点保存"
                : $"已按「{SeatArranger.Label(strategy)}」排座，记得点保存", 4);
    }

    /// <summary>最近一份快照里每个人所在的区域。没有快照就返回空 —— 偏好自然不生效。</summary>
    private Dictionary<string, SeatZone> LastZones()
    {
        var latest = Services.SeatSnapshots.Latest();
        if (latest is null) return [];

        var layout = latest.LayoutValue;
        var zones = new Dictionary<string, SeatZone>(StringComparer.Ordinal);
        foreach (var (seat, id) in latest.SeatMap())
        {
            if (!_byId.ContainsKey(id)) continue;
            zones[id] = SeatArranger.ZoneOf(layout, seat);
        }
        return zones;
    }

    private async Task ShowIssuesAsync(SeatPlan plan, SeatStrategy strategy, int left)
    {
        var lines = plan.Issues.Take(12).Select(Describe).ToList();
        if (plan.Issues.Count > lines.Count) lines.Add($"…另有 {plan.Issues.Count - lines.Count} 条");

        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(new TextBlock
        {
            Text = $"已按「{SeatArranger.Label(strategy)}」排完，{left} 人没有座位。下面这些要求这次没能满足：",
            Style = Styled("DeskBodyText"),
            TextWrapping = TextWrapping.Wrap,
        });
        foreach (var line in lines)
            panel.Children.Add(new TextBlock
            {
                Text = "· " + line,
                Style = Styled("DeskSecondaryText"),
                TextWrapping = TextWrapping.Wrap,
            });
        panel.Children.Add(new TextBlock
        {
            Text = "可以先把固定座位取消、把区域要求放宽，或换一个排座策略再试。",
            Style = Styled("DeskSecondaryText"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
        });

        var dialog = new ContentDialog
        {
            Title = "有几条要求没排上",
            Content = panel,
            PrimaryButtonText = "知道了",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        await dialog.ShowAsync();
    }

    private string Describe(SeatIssue issue)
    {
        var name = NameOf(issue.StudentId);
        return issue.Kind switch
        {
            SeatIssueKind.ZoneUnavailable => $"{name}：没有「{SeatRules.ZoneLabel(issue.Zone)}」的空位",
            SeatIssueKind.PairUnavailable => $"{name}：找不到能和 {NameOf(issue.PartnerId)} 同桌的位置",
            _ => $"{name}：没有空位",
        };
    }

    private string NameOf(string? id) =>
        id is not null && _byId.TryGetValue(id, out var student) ? student.Name : "（已删除的学生）";

    // ================= 布局 =================

    private async void OnEditLayout(object sender, RoutedEventArgs e)
    {
        var rows = new NumberBox
        {
            Header = "排数（2–10）",
            Minimum = SeatLayout.MinRows,
            Maximum = SeatLayout.MaxRows,
            Value = _layout.Rows,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            Width = 150,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        var groups = new NumberBox
        {
            Header = "组数（1–6）",
            Minimum = SeatLayout.MinGroups,
            Maximum = SeatLayout.MaxGroups,
            Value = _layout.Groups,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            Width = 150,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        var perGroup = new NumberBox
        {
            Header = "每组座位数（1–12）",
            Minimum = SeatLayout.MinPerGroup,
            Maximum = SeatLayout.MaxPerGroup,
            Value = _layout.PerGroup,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            Width = 190,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        var preview = new TextBlock
        {
            Style = Styled("DeskSecondaryText"),
            TextWrapping = TextWrapping.Wrap,
        };

        void UpdatePreview()
        {
            var next = SeatLayout.Create(Read(rows, _layout.Rows), Read(groups, _layout.Groups), Read(perGroup, _layout.PerGroup));
            preview.Text = $"一共 {next.Capacity} 个座位（{next.Rows} 排 × {next.Columns} 列），" +
                           (next.Groups > 1 ? $"{next.Aisles} 条组间走廊。" : "不分组，没有走廊。") +
                           "换布局时按「第几排第几列」保位，不是按旧座位号。";
        }

        rows.ValueChanged += (_, _) => UpdatePreview();
        groups.ValueChanged += (_, _) => UpdatePreview();
        perGroup.ValueChanged += (_, _) => UpdatePreview();
        UpdatePreview();

        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock
        {
            Text = "按教室实际摆法填。走廊由组数自动留出，不需要单独填。",
            Style = Styled("DeskSecondaryText"),
            TextWrapping = TextWrapping.Wrap,
        });
        var fields = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
        fields.Children.Add(rows);
        fields.Children.Add(groups);
        fields.Children.Add(perGroup);
        panel.Children.Add(fields);
        panel.Children.Add(preview);

        var dialog = new ContentDialog
        {
            Title = "教室布局",
            Content = panel,
            PrimaryButtonText = "应用",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var target = SeatLayout.Create(
            Read(rows, _layout.Rows), Read(groups, _layout.Groups), Read(perGroup, _layout.PerGroup));

        if (target == _layout) return;

        // 按"第几排第几列"重新编号：同一个位置的人还是同一个人。
        var remapped = new Dictionary<int, string>();
        foreach (var (seat, id) in _seats)
        {
            if (_layout.Remap(seat, target) is not int next) continue;
            remapped[next] = id;
        }

        var lost = _seats.Count - remapped.Count;

        PushUndo();
        _layout = target;
        Services.Settings.SeatLayout = target;
        Services.Audit.Add("seat.layout", null, null, target.ToSetting());
        _seats = remapped;
        _selectedId = null;

        BuildGrid();
        FitTiles();
        RefreshAll();
        MarkDirty();

        Shell?.Toast(lost > 0
            ? $"布局已改：{lost} 人新布局放不下，回到未排座"
            : "布局已改，座位按排/列保住了", 4);
    }

    private static int Read(NumberBox box, int fallback) =>
        double.IsNaN(box.Value) ? fallback : (int)System.Math.Round(box.Value);

    // ================= 挑人 =================

    private async Task PickStudentForAsync(int seat)
    {
        var unseated = _students.Where(s => SeatOf(s.Id) is null).ToList();
        if (unseated.Count == 0)
        {
            Shell?.Toast("没有未排座的学生：先点一个有人坐的座位，再点这里就能对调", 4);
            return;
        }

        var list = new ListView
        {
            SelectionMode = ListViewSelectionMode.Single,
            Height = 300,
            MinWidth = 320,
        };
        foreach (var s in unseated) list.Items.Add(Display(s));
        list.SelectedIndex = -1;

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock
        {
            Text = $"要放进「{_layout.SeatText(seat)}」的人：",
            Style = Styled("DeskBodyText"),
        });
        panel.Children.Add(list);

        var dialog = new ContentDialog
        {
            Title = "安排座位",
            Content = panel,
            PrimaryButtonText = "放到这里",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false,
            XamlRoot = XamlRoot,
        };
        list.SelectionChanged += (_, _) => dialog.IsPrimaryButtonEnabled = list.SelectedIndex >= 0;

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        if (list.SelectedIndex < 0) return;

        PushUndo();
        _seats[seat] = unseated[list.SelectedIndex].Id;
        _selectedId = null;
        RefreshAll();
        MarkDirty();
    }

    private static string Display(Student s) =>
        s.Name + (string.IsNullOrWhiteSpace(s.StudentNo) ? "" : $" · {s.StudentNo}");

    // ================= 快照 =================

    private void OnSnapshots(object sender, RoutedEventArgs e) => _ = ShowSnapshotsAsync();

    private async Task ShowSnapshotsAsync()
    {
        var nameBox = new TextBox
        {
            PlaceholderText = "快照名称（可留空）",
            Text = $"方案 {Services.SeatSnapshots.List().Count + 1}",
            Width = 260,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        var save = new Button { Content = "存为当前快照", Style = Styled("DeskButton") };

        var rows = new StackPanel { Spacing = 6 };
        var empty = new TextBlock
        {
            Text = "还没有快照。自动排座前会自动留一份，也可以在这里手动存。",
            Style = Styled("DeskSecondaryText"),
            TextWrapping = TextWrapping.Wrap,
        };

        void RenderSnapshots()
        {
            rows.Children.Clear();
            var snapshots = Services.SeatSnapshots.List();
            empty.Visibility = snapshots.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            foreach (var snapshot in snapshots)
            {
                var line = new Grid { ColumnSpacing = 8, Padding = new Thickness(0, 2, 0, 2) };
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var text = new StackPanel { Spacing = 1 };
                text.Children.Add(new TextBlock
                {
                    Text = snapshot.Name,
                    Style = Styled("DeskBodyText"),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });
                text.Children.Add(new TextBlock
                {
                    Text = $"{Clock.LocalDate(snapshot.CreatedAt):MM-dd HH:mm} · {snapshot.LayoutValue} · {snapshot.Count} 人",
                    Style = Styled("DeskSecondaryText"),
                });
                line.Children.Add(text);

                var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                var restore = new Button { Content = "恢复", Style = Styled("DeskButton"), Height = 28 };
                restore.Click += async (_, _) =>
                {
                    if (Shell is not { } shell) return;
                    var ok = await shell.ConfirmAsync("恢复快照",
                        $"把当前草稿换成「{snapshot.Name}」。没保存之前还能撤销。", "恢复", "取消");
                    if (!ok) return;
                    RestoreSnapshot(snapshot);
                };
                var delete = new Button { Content = "删除", Style = Styled("DeskButton"), Height = 28 };
                delete.Click += (_, _) =>
                {
                    Services.SeatSnapshots.Delete(snapshot.Id);
                    RenderSnapshots();
                };
                actions.Children.Add(restore);
                actions.Children.Add(delete);
                Grid.SetColumn(actions, 1);
                line.Children.Add(actions);
                rows.Children.Add(line);
            }
        }

        save.Click += (_, _) =>
        {
            SaveSnapshot(nameBox.Text, quiet: false);
            nameBox.Text = $"方案 {Services.SeatSnapshots.List().Count + 1}";
            RenderSnapshots();
        };

        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        header.Children.Add(nameBox);
        header.Children.Add(save);

        var listBox = new ScrollViewer { MaxHeight = 320, Content = rows };
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(header);
        panel.Children.Add(empty);
        panel.Children.Add(listBox);

        RenderSnapshots();

        var dialog = new ContentDialog
        {
            Title = "座位快照",
            Content = panel,
            CloseButtonText = "关闭",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        await dialog.ShowAsync();
    }

    private void SaveSnapshot(string name, bool quiet)
    {
        if (_seats.Count == 0)
        {
            if (!quiet) Shell?.Toast("现在一个人都没排，没什么可存的");
            return;
        }

        try
        {
            var snapshot = Services.SeatSnapshots.Save(name, _layout, _seats);
            if (!quiet) Shell?.Toast($"已存快照：{snapshot.Name}", 3);
        }
        catch (Exception ex)
        {
            Services.LogError("seat-snapshot", ex);
            if (!quiet) Shell?.Toast("存快照失败：" + ex.Message, 5);
        }
    }

    private void RestoreSnapshot(SeatSnapshot snapshot)
    {
        var layout = snapshot.LayoutValue;

        // 快照里的学生可能已经转走了：只还原还在册的。
        var seats = snapshot.SeatMap()
            .Where(kv => kv.Key <= layout.Capacity && _byId.ContainsKey(kv.Value))
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        PushUndo();
        _layout = layout;
        Services.Settings.SeatLayout = layout;
        _seats = seats;
        _selectedId = null;

        BuildGrid();
        FitTiles();
        RefreshAll();
        MarkDirty();
        Shell?.Toast($"已恢复「{snapshot.Name}」，没保存之前可以撤销", 4);
    }

    // ================= 导入 / 导出 =================

    private async Task ImportSheetAsync()
    {
        var path = FileDialogs.Open("选择座次表（按教室布局排列）", FileFilter.Sheet);
        if (string.IsNullOrWhiteSpace(path)) return;

        SheetGrid grid;
        try
        {
            grid = path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)
                ? CsvFile.Read(path)
                : ExcelReader.Read(path, 0);
        }
        catch (Exception ex)
        {
            Services.LogError("seat-import-read", ex);
            Shell?.Toast("读不了这个文件：" + ex.Message, 5);
            return;
        }

        if (grid.RowCount == 0)
        {
            Shell?.Toast("表格是空的");
            return;
        }

        // 猜一下"第 1 排第 1 座"在哪：找到第一个能对上名单姓名的单元格。
        var guessRow = 0;
        var guessCol = 0;
        var found = false;
        for (var r = 0; r < System.Math.Min(grid.RowCount, 30) && !found; r++)
        for (var c = 0; c < System.Math.Min(grid.ColumnCount, 20) && !found; c++)
        {
            if (MatchName(grid.Cell(r, c)) is not null)
            {
                guessRow = r;
                guessCol = c;
                found = true;
            }
        }

        var startRow = new NumberBox
        {
            Header = "第 1 排第 1 座所在的行",
            Minimum = 1,
            Maximum = System.Math.Max(1, grid.RowCount),
            Value = guessRow + 1,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            Width = 180,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        var startCol = new NumberBox
        {
            Header = "所在列（A = 1）",
            Minimum = 1,
            Maximum = System.Math.Max(1, grid.ColumnCount),
            Value = guessCol + 1,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            Width = 180,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        var preview = new TextBlock
        {
            Style = Styled("DeskSecondaryText"),
            TextWrapping = TextWrapping.Wrap,
        };

        void UpdatePreview()
        {
            var r = Read(startRow, 1);
            var c = Read(startCol, 1);
            var enough = r - 1 + _layout.Rows <= grid.RowCount && c - 1 + _layout.Columns <= grid.ColumnCount;
            preview.Text = $"{grid.Name}：{grid.RowCount} 行 × {grid.ColumnCount} 列；" +
                           $"按当前起点会读到第 {r}–{System.Math.Min(grid.RowCount, r - 1 + _layout.Rows)} 行、" +
                           $"第 {c}–{System.Math.Min(grid.ColumnCount, c - 1 + _layout.Columns)} 列，" +
                           $"正好对应当前教室的 {_layout.Rows} 排 × {_layout.Columns} 列。" +
                           (enough ? "" : " 表格比教室小，超出的座位会空着。");
        }

        startRow.ValueChanged += (_, _) => UpdatePreview();
        startCol.ValueChanged += (_, _) => UpdatePreview();
        UpdatePreview();

        var panel = new StackPanel { Spacing = 10, MinWidth = 420 };
        panel.Children.Add(new TextBlock
        {
            Text = "表格里每格放一个姓名，按教室的排/列排好；走廊那一列留空即可。" +
                   "导入会按姓名替换当前草稿里所有人的座位，表格里没有的人回到「未排座」。",
            Style = Styled("DeskSecondaryText"),
            TextWrapping = TextWrapping.Wrap,
        });
        var fields = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
        fields.Children.Add(startRow);
        fields.Children.Add(startCol);
        panel.Children.Add(fields);
        panel.Children.Add(preview);

        var dialog = new ContentDialog
        {
            Title = "导入座次表",
            Content = panel,
            PrimaryButtonText = "导入并替换",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var row0 = Read(startRow, 1) - 1;
        var col0 = Read(startCol, 1) - 1;

        var map = new Dictionary<int, string>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        var unknown = new List<string>();

        for (var i = 0; i < _layout.Rows; i++)
        {
            for (var j = 0; j < _layout.Columns; j++)
            {
                var raw = grid.Cell(row0 + i, col0 + j);
                var name = NameNormalizer.Clean(NameNormalizer.StripListIndex(raw));
                if (name.Length == 0) continue;

                var student = _students.FirstOrDefault(s =>
                    !used.Contains(s.Id) && NameNormalizer.Clean(s.Name) == name);

                if (student is not null && _layout.SeatNo(i + 1, j + 1) is int seat)
                {
                    map[seat] = student.Id;
                    used.Add(student.Id);
                }
                else
                {
                    unknown.Add(name);
                }
            }
        }

        if (map.Count == 0)
        {
            Shell?.Toast("一个名字都没对上：检查起始行/列，或名单里的姓名写法");
            return;
        }

        PushUndo();
        _seats = map;
        _selectedId = null;
        RefreshAll();
        MarkDirty();

        if (unknown.Count > 0)
        {
            var lines = string.Join("、", unknown.Distinct().Take(10));
            if (unknown.Distinct().Count() > 10) lines += "…";
            var warn = new ContentDialog
            {
                Title = "导入了，但有些名字没对上",
                Content = $"已排 {map.Count} 人。这些姓名不在名单里（或重名已用完），留空处理：{lines}",
                PrimaryButtonText = "知道了",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = XamlRoot,
            };
            await warn.ShowAsync();
        }
        else
        {
            Shell?.Toast($"已导入 {map.Count} 人的座位，记得点保存", 4);
        }
    }

    /// <summary>把一格文字按姓名对上名单里的一个人（重名时取还没被用的那个）。</summary>
    private Student? MatchName(string raw)
    {
        var name = NameNormalizer.Clean(NameNormalizer.StripListIndex(raw));
        if (name.Length == 0) return null;
        return _students.FirstOrDefault(s => NameNormalizer.Clean(s.Name) == name);
    }

    private SeatChart BuildChart()
    {
        var entries = new List<SeatChartEntry>();
        foreach (var (seat, id) in _seats.OrderBy(kv => kv.Key))
        {
            if (!_byId.TryGetValue(id, out var s)) continue;
            entries.Add(new SeatChartEntry(seat, s.Name, s.StudentNo,
                _points.GetValueOrDefault(id), _pins.Contains(id)));
        }

        var unseated = _students
            .Where(s => SeatOf(s.Id) is null)
            .Select(s => new SeatChartEntry(0, s.Name, s.StudentNo, _points.GetValueOrDefault(s.Id),
                _pins.Contains(s.Id)))
            .ToList();

        return new SeatChart(Services.ClassName, _layout, entries, unseated, DateTime.Now);
    }

    private void ExportExcel()
    {
        var path = FileDialogs.Save("导出座次表", $"座次表-{Services.ClassName}-{DateTime.Now:yyyyMMdd-HHmm}",
            FileFilter.Excel, "xlsx");
        if (string.IsNullOrWhiteSpace(path)) return;

        SeatSheetBuilder.SaveExcel(BuildChart(), path);
        Shell?.Toast("已导出：" + Path.GetFileName(path), 4);
    }

    private void ExportSvg()
    {
        var path = FileDialogs.Save("导出座次表", $"座次表-{Services.ClassName}-{DateTime.Now:yyyyMMdd-HHmm}",
            new FileFilter("SVG 图片", ["*.svg"]), "svg");
        if (string.IsNullOrWhiteSpace(path)) return;

        File.WriteAllText(path, SeatSheetBuilder.BuildSvg(BuildChart()), new UTF8Encoding(false));
        Shell?.Toast("已导出：" + Path.GetFileName(path), 4);
    }

    private void ExportCsv()
    {
        var path = FileDialogs.Save("导出座次表", $"座次表-{Services.ClassName}-{DateTime.Now:yyyyMMdd-HHmm}.csv",
            new FileFilter("CSV 表格", ["*.csv"]), "csv");
        if (string.IsNullOrWhiteSpace(path)) return;

        var csv = new StringBuilder();
        csv.AppendLine($"班级,{Field(Services.ClassName)}");
        csv.AppendLine($"教室,{Field(_layout.ToString())}");
        csv.AppendLine($"导出时间,{DateTime.Now:yyyy-MM-dd HH:mm}");
        csv.AppendLine();
        csv.AppendLine("排,列,座位号,姓名,学号,本周期积分,固定");

        foreach (var (seat, id) in _seats.OrderBy(kv => kv.Key))
        {
            if (_layout.Position(seat) is not { } pos || !_byId.TryGetValue(id, out var s)) continue;
            csv.AppendLine(string.Join(',',
                pos.Row, pos.Col, seat,
                Field(s.Name), Field(s.StudentNo ?? ""),
                _points.GetValueOrDefault(id),
                _pins.Contains(id) ? "是" : ""));
        }

        foreach (var s in _students.Where(s => SeatOf(s.Id) is null))
        {
            csv.AppendLine(string.Join(',', "", "", "未排座",
                Field(s.Name), Field(s.StudentNo ?? ""), _points.GetValueOrDefault(s.Id), ""));
        }

        File.WriteAllText(path, csv.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        Shell?.Toast("已导出：" + Path.GetFileName(path), 4);
    }

    private static string Field(string text) =>
        text.Contains(',') || text.Contains('"') || text.Contains('\n')
            ? "\"" + text.Replace("\"", "\"\"") + "\""
            : text;

    // ================= 保存 / 撤销 / 重做 =================

    private bool IsDirty => _seats.Count != _saved.Count ||
                            _seats.Any(kv => !_saved.TryGetValue(kv.Key, out var id) || id != kv.Value);

    private void MarkDirty()
    {
        var dirty = IsDirty;
        DirtyText.Visibility = dirty ? Visibility.Visible : Visibility.Collapsed;
        SaveButton.IsEnabled = dirty;
        UndoButton.IsEnabled = _undo.Count > 0;
        RedoButton.IsEnabled = _redo.Count > 0;
    }

    private void PushUndo()
    {
        _undo.Add(new Dictionary<int, string>(_seats));
        if (_undo.Count > 30) _undo.RemoveAt(0);
        _redo.Clear();
        UndoButton.IsEnabled = true;
        RedoButton.IsEnabled = false;
    }

    private void OnUndo(object sender, RoutedEventArgs e)
    {
        if (_undo.Count == 0) return;

        _redo.Add(new Dictionary<int, string>(_seats));
        _seats = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _selectedId = null;
        RefreshAll();
        MarkDirty();
    }

    private void OnRedo(object sender, RoutedEventArgs e)
    {
        if (_redo.Count == 0) return;

        _undo.Add(new Dictionary<int, string>(_seats));
        _seats = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        _selectedId = null;
        RefreshAll();
        MarkDirty();
    }

    private void OnSave(object sender, RoutedEventArgs e) => Save(quiet: false);

    /// <summary>
    /// 写回 students.seat_no。只写变了的人：没动的人 rev 不涨，云端也不会收到
    /// 一串"内容其实没变"的 upsert。
    /// </summary>
    private void Save(bool quiet)
    {
        if (!IsDirty) return;

        var changed = 0;
        try
        {
            foreach (var s in _students)
            {
                var seat = SeatOf(s.Id);
                if (seat == s.SeatNo) continue;

                // 座位号超出这一版布局的人：界面上是未排座，但不抹掉库里那个号 ——
                // 布局改回大教室、或这台机器只是临时用小教室，位置还能找回来。
                if (seat is null && s.SeatNo is int old && old > _layout.Capacity) continue;

                s.SeatNo = seat;
                Services.Students.Update(s);
                changed++;
            }

            _saved = new Dictionary<int, string>(_seats);
            MarkDirty();
            if (!quiet) Shell?.Toast(changed == 0 ? "座位没有变化" : $"已保存 {changed} 人的座位", 3);
        }
        catch (Exception ex)
        {
            Services.LogError("seat-save", ex);
            if (!quiet) Shell?.Toast("保存失败：" + ex.Message, 5);
        }
    }

    private void SaveQuietly()
    {
        try
        {
            if (IsDirty) Save(quiet: true);
        }
        catch (Exception ex)
        {
            Services.LogError("seat-autosave", ex);
        }
    }

    // ================= 界面刷新 =================

    private int? SeatOf(string id)
    {
        foreach (var (seat, occupant) in _seats)
            if (occupant == id) return seat;
        return null;
    }

    private void RefreshAll()
    {
        foreach (var seat in _visuals.Keys) PaintTile(seat);
        RefreshSelection();
        RefreshUnseated();
        RefreshHeader();
        MarkDirty();
    }

    private void PaintTile(int seat)
    {
        if (!_visuals.TryGetValue(seat, out var visual)) return;

        var id = _seats.GetValueOrDefault(seat);
        var pos = _layout.Position(seat);

        if (id is not null && _byId.TryGetValue(id, out var s))
        {
            var pinned = _pins.Contains(id);
            var zone = _rules.ZoneOf(id);

            visual.Name.Text = s.Name;
            visual.Name.FontSize = 15;
            visual.Name.Foreground = Br("TextPrimaryBrush");

            var parts = new List<string>();
            if (zone != SeatZone.Any)
                parts.Add(zone switch
                {
                    SeatZone.Front => "需前",
                    SeatZone.Mid => "需中",
                    _ => "需后",
                });
            if (pinned) parts.Add("固定");
            parts.Add($"{_points.GetValueOrDefault(id)} 分");
            visual.Sub.Text = string.Join(" · ", parts);

            visual.Pin.Visibility = pinned ? Visibility.Visible : Visibility.Collapsed;
            visual.Root.Background = pinned ? Br("AccentBgBrush") : Br("CardBgBrush");
        }
        else
        {
            visual.Name.Text = "空";
            visual.Name.FontSize = 13;
            visual.Name.Foreground = Br("TextSecondaryBrush");
            visual.Sub.Text = pos is { } p ? $"{p.Row} 排 {p.Col} 座" : "";
            visual.Pin.Visibility = Visibility.Collapsed;
            visual.Root.Background = Br("TileBgBrush");
        }

        var selected = id is not null && id == _selectedId;
        visual.Root.BorderThickness = new Thickness(selected ? 2.5 : 1);
        visual.Root.BorderBrush = selected ? Br("SelectStrokeBrush") : Br("CardStrokeBrush");
    }

    private void RefreshSelection()
    {
        if (_selectedId is not { } id || !_byId.TryGetValue(id, out var s))
        {
            _selectedId = null;
            SelectionBar.Visibility = Visibility.Collapsed;
            return;
        }

        var seat = SeatOf(id);
        var pinned = _pins.Contains(id);

        SelectionBar.Visibility = Visibility.Visible;

        var where = seat is int n ? _layout.SeatText(n) : "未排座";
        SelectionText.Text = $"已选：{s.Name}（{where}{ConstraintSummary(id)}）—— " +
                             (seat is null
                                 ? "点一个空位放进去，点有人的座位则替换，原座位的人回到未排座。"
                                 : "再点一个座位交换，或点「清空此座」。");

        PinButton.Content = pinned ? "取消固定" : "固定此座";
        PinButton.IsEnabled = seat is not null;
    }

    /// <summary>选中提示里那半句"要前排、和李四同桌"，不用再点开约束才知道。</summary>
    private string ConstraintSummary(string id)
    {
        var parts = new List<string>();
        var zone = _rules.ZoneOf(id);
        if (zone != SeatZone.Any) parts.Add("要" + SeatRules.ZoneLabel(zone));

        var link = _rules.LinkOf(id);
        if (link is not null)
        {
            var other = link.A == id ? link.B : link.A;
            parts.Add((link.Kind == SeatLinkKind.Together ? "要和" : "避开") + NameOf(other) + "同桌");
        }

        return parts.Count == 0 ? "" : "，" + string.Join("，", parts);
    }

    private void RefreshUnseated()
    {
        UnseatedList.Children.Clear();

        if (_students.Count == 0)
        {
            UnseatedTitle.Text = "名单是空的";
            UnseatedList.Children.Add(new TextBlock
            {
                Text = "先去「名单」把人加进来，再回来排座位。",
                Style = Styled("DeskSecondaryText"),
                VerticalAlignment = VerticalAlignment.Center,
            });
            return;
        }

        var unseated = _students.Where(s => SeatOf(s.Id) is null).ToList();

        UnseatedTitle.Text = unseated.Count == 0
            ? "未排座：全班都排上了"
            : $"未排座 {unseated.Count} 人 —— 点一下选中，再点空位放下";

        if (unseated.Count == 0)
        {
            UnseatedList.Children.Add(new TextBlock
            {
                Text = "没有未排座的学生。",
                Style = Styled("DeskSecondaryText"),
                VerticalAlignment = VerticalAlignment.Center,
            });
            return;
        }

        foreach (var s in unseated)
        {
            var selected = s.Id == _selectedId;
            var button = new Button
            {
                Content = Display(s),
                Style = Styled(selected ? "DeskPrimaryButton" : "DeskButton"),
                Height = 32,
                FontSize = 13,
                Tag = s.Id,
            };
            button.Click += (_, _) => Select(s.Id);
            UnseatedList.Children.Add(button);
        }
    }

    private void RefreshHeader()
    {
        var unseated = _students.Count - _seats.Count;
        SubText.Text = $"{Services.ClassName} · {_layout}（共 {_layout.Capacity} 座） · " +
                       $"已排 {_seats.Count} 人" + (unseated > 0 ? $"，{unseated} 人未排" : "");
        HintText.Text = "点一个座位选中，再点另一个交换；点空位把选中的学生放过去；右键（长按）可直接固定、约束或清空。";
    }

    private static Brush Br(string key) => (Brush)Application.Current.Resources[key];

    private static Style Styled(string key) => (Style)Application.Current.Resources[key];
}
