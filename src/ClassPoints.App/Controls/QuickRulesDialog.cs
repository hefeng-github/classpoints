using ClassPoints.App.Services;
using ClassPoints.App.Theme;
using ClassPoints.Core;
using ClassPoints.Data;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ClassPoints.App.Controls;

/// <summary>
/// 「快捷加分项」弹窗：在记分台上直接调按钮，不用绕到设置页去。
///
/// 两件事分清楚：
/// · 全班共用的项（OwnerId 为空）在这里只读 —— 改它等于改所有人的按钮，
///   入口留在「设置 → 加分与扣分项」。
/// · 自己新建的项属于当前这个人，换个人刷卡就看不见，互不干扰。
///
/// 弹窗内容自己换（列表 ⇄ 编辑表单），不开第二层 ContentDialog ——
/// WinUI 同时只能有一个 ContentDialog，套着开必炸。
/// </summary>
public sealed class QuickRulesDialog
{
    private readonly XamlRoot _root;
    private readonly string _memberId;
    private readonly ContentDialog _dialog;
    private readonly StackPanel _body = new() { Spacing = 10, Width = 470 };

    public QuickRulesDialog(XamlRoot root, string memberId)
    {
        _root = root;
        _memberId = memberId;
        _dialog = new ContentDialog
        {
            XamlRoot = _root,
            Title = "管理快捷项",
            CloseButtonText = "关闭",
            DefaultButton = ContentDialogButton.Close,
            // Content 只在这里设一次，之后列表 ⇄ 编辑只换 _body 的子元素。
            // 千万不能在按钮的 Click 里换 Content：正在处理点击的那个按钮就在被换掉的树里，
            // XAML 会抛 ArgumentException「Value does not fall within the expected range」，
            // 表现是"新建加分项不填东西点返回，整个程序没了"。
            Content = new ScrollViewer
            {
                Content = _body,
                MaxHeight = 520,
                // 顶对齐：不然 ScrollViewer 会撑满 ContentDialog 给的高度，
                // 三条记录下面拖出一大片空白，看着像加载失败
                VerticalAlignment = VerticalAlignment.Top,
            },
        };
    }

    /// <summary>返回 true 表示改过项目，调用方需要重画按钮。</summary>
    public bool Changed { get; private set; }

    public async Task<bool> ShowAsync()
    {
        ShowList();
        await Dialogs.ShowAsync(_dialog);
        return Changed;
    }

    // ---------------- 列表 ----------------

    private void ShowList()
    {
        _body.Children.Clear();
        _body.Children.Add(new TextBlock
        {
            Text = "只影响你自己刷卡时出现的按钮；「全班」那几项所有人共用，要改到设置里改。",
            Style = Styled("DeskSecondaryText"),
            TextWrapping = TextWrapping.Wrap,
        });

        var rules = Services.Rules.ListForMember(_memberId);
        if (rules.Count == 0)
            _body.Children.Add(new TextBlock
            {
                Text = "还没有项目。下面新建一个，它只出现在你的记分台上。",
                Style = Styled("DeskSecondaryText"),
            });

        foreach (var rule in rules)
            _body.Children.Add(BuildRow(rule));

        var footer = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 6, 0, 0) };
        footer.Children.Add(Small("新建加分项", () => ShowEditor(null, 10)));
        footer.Children.Add(Small("新建扣分项", () => ShowEditor(null, -10)));
        _body.Children.Add(footer);

        // 从编辑态返回时要复位，否则标题还挂着上一次的「编辑「xxx」」
        _dialog.Title = "管理快捷项";
    }

    private UIElement BuildRow(Rule rule)
    {
        var grid = new Grid { ColumnSpacing = 10, Padding = new Thickness(0, 6, 0, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var icon = new FontIcon
        {
            Glyph = rule.Glyph,
            FontFamily = new FontFamily(IconCatalog.FontFamily),
            FontSize = 16,
            Foreground = Pal.Secondary,
            VerticalAlignment = VerticalAlignment.Center,
        };
        grid.Children.Add(icon);

        var info = new StackPanel { Spacing = 1 };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        head.Children.Add(new TextBlock
        {
            Text = rule.Label,
            Style = Styled("DeskBodyText"),
            VerticalAlignment = VerticalAlignment.Center,
        });
        head.Children.Add(new TextBlock
        {
            Text = rule.DeltaText,
            FontFamily = (FontFamily)Application.Current.Resources["NumberFont"],
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = rule.Delta >= 0 ? Pal.Positive : Pal.Negative,
            VerticalAlignment = VerticalAlignment.Center,
        });
        if (rule.IsShared)
            head.Children.Add(Tag("全班"));
        info.Children.Add(head);
        info.Children.Add(new TextBlock
        {
            Text = rule.HasCap
                ? $"{(rule.CapScope == CapScope.Single ? "单次最多" : "每周期最多")} {rule.CapValue}"
                : "不封顶",
            Style = Styled("DeskSecondaryText"),
        });
        Grid.SetColumn(info, 1);
        grid.Children.Add(info);

        if (!rule.IsShared)
        {
            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                VerticalAlignment = VerticalAlignment.Center,
            };
            actions.Children.Add(Small("编辑", () => ShowEditor(rule, rule.Delta)));
            actions.Children.Add(Small("删除", () =>
            {
                // 归档而不是硬删：已经记过分的人还指着这条项名查账
                Services.Rules.Archive(rule.Id);
                Changed = true;
                ShowList();
            }));
            Grid.SetColumn(actions, 2);
            grid.Children.Add(actions);
        }

        return grid;
    }

    private static Border Tag(string text) => new()
    {
        Background = Pal.TileBg,
        CornerRadius = new CornerRadius(4),
        Padding = new Thickness(6, 1, 6, 1),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock
        {
            Text = text,
            FontSize = 11,
            Foreground = Pal.Secondary,
        },
    };

    // ---------------- 编辑 ----------------

    private void ShowEditor(Rule? existing, int delta)
    {
        var isNew = existing is null;
        var rule = existing ?? new Rule { Label = "", Delta = delta, Icon = IconCatalog.Default, OwnerId = _memberId };

        var labelBox = new TextBox { Header = "名称", Text = rule.Label, MaxWidth = 320, HorizontalAlignment = HorizontalAlignment.Left };
        // 用 NumberInput 而不是 NumberBox：后者要等焦点离开才写 Value，
        // 点"保存"那一刻读到的是旧值 —— 显示 50、存进去 10。
        var deltaBox = new NumberInput
        {
            Header = "分值（负数表示扣分）",
            Value = rule.Delta,
            Minimum = -500,
            Maximum = 500,
            Step = 5,
            BoxWidth = 120,
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        var icon = rule.Glyph;
        var iconPicker = IconChooser.Build(IconCatalog.RuleIcons, icon, g => icon = g);

        var capCombo = new ComboBox { Header = "封顶", Width = 200, HorizontalAlignment = HorizontalAlignment.Left };
        capCombo.Items.Add("不封顶");
        capCombo.Items.Add("单次最多");
        capCombo.Items.Add("每周期最多");
        capCombo.SelectedIndex = rule.HasCap ? (rule.CapScope == CapScope.Single ? 1 : 2) : 0;

        var capBox = new NumberInput
        {
            Header = "上限值",
            Value = rule.CapValue ?? 50,
            Minimum = 1,
            Maximum = 10000,
            Step = 5,
            BoxWidth = 120,
            IsEnabled = rule.HasCap,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        capCombo.SelectionChanged += (_, _) => capBox.IsEnabled = capCombo.SelectedIndex > 0;

        _body.Children.Clear();
        _body.Children.Add(new TextBlock
        {
            Text = "这一项只出现在你自己的记分台上。",
            Style = Styled("DeskSecondaryText"),
        });
        _body.Children.Add(labelBox);
        _body.Children.Add(deltaBox);
        _body.Children.Add(new TextBlock { Text = "图标", Style = Styled("DeskColumnHeader") });
        _body.Children.Add(iconPicker);
        _body.Children.Add(capCombo);
        _body.Children.Add(capBox);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 6, 0, 0) };
        actions.Children.Add(Small("保存", () =>
        {
            var label = NameNormalizer.Clean(labelBox.Text);
            if (label.Length == 0)
            {
                labelBox.Header = "名称（不能为空）";
                return;
            }

            rule.Label = label;
            deltaBox.Commit();
            rule.Delta = (int)deltaBox.Value;
            rule.Icon = IconCatalog.Normalize(icon);
            rule.OwnerId = _memberId;
            capBox.Commit();
            rule.CapValue = capCombo.SelectedIndex == 0 ? null : (int)capBox.Value;
            rule.CapScope = capCombo.SelectedIndex == 0 ? CapScope.None
                : capCombo.SelectedIndex == 1 ? CapScope.Single : CapScope.Period;

            Services.Rules.Save(rule);
            Changed = true;
            ShowList();
        }, primary: true));

        actions.Children.Add(Small("返回", ShowList));
        _body.Children.Add(actions);

        // 新建时给个默认标题，编辑时把名字写进去，免得不知道在改哪一条
        _dialog.Title = isNew ? (delta < 0 ? "新建扣分项" : "新建加分项") : $"编辑「{rule.Label}」";
    }

    // ---------------- 小工具 ----------------

    private static Style Styled(string key) => (Style)Application.Current.Resources[key];

    private static Button Small(string text, Action action, bool primary = false)
    {
        var button = new Button
        {
            Content = text,
            Style = Styled(primary ? "DeskPrimaryButton" : "DeskButton"),
            Height = 30,
            MinWidth = 62,
        };
        button.Click += (_, _) => action();
        return button;
    }

    private static AppServices Services => AppServices.Instance;
}
