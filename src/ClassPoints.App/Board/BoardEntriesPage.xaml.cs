using ClassPoints.App.Controls;
using ClassPoints.App.Services;
using ClassPoints.Core;
using ClassPoints.Data;
using Windows.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using ClassPoints.App.Theme;

namespace ClassPoints.App.Board;

/// <summary>
/// 明细（大屏）：只看当天；未认人时隐藏操作人列（教室里公开屏幕，谁扣的分不能随便露）。
/// 撤销走左滑，且不做"全滑即撤销" —— 触屏误滑概率太高。
/// </summary>
public sealed partial class BoardEntriesPage : Page
{
    private static readonly SolidColorBrush Primary = Pal.Primary;
    private static readonly SolidColorBrush Secondary = Pal.Secondary;
    private static readonly SolidColorBrush Positive = Pal.Positive;
    private static readonly SolidColorBrush Negative = Pal.Negative;
    private static readonly SolidColorBrush Danger = Pal.Danger;

    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromSeconds(20) };

    public BoardEntriesPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private AppServices Services => AppServices.Instance;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 这页两端共用：大屏靠左滑，电脑上鼠标拖一下也能滑，但"完整流水在电脑端"
        // 这句话是说给站在大屏前的人听的，在电脑上读起来像走错了地方。
        FootNote.Text = App.Mode == AppMode.Board
            ? "点「撤销」或左滑可撤销 · 只显示今天；完整流水在「周期」页导出。"
            : "点「撤销」即可撤回（也支持按住向右拖）· 只显示今天；完整流水在「周期」页导出。";

        Render();

        // Loaded 可能不止一次（页面被重新挂上视觉树）：先退订再挂，避免每次刷新跑两遍
        _refresh.Tick -= OnRefreshTick;
        _refresh.Tick += OnRefreshTick;
        _refresh.Start();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => _refresh.Stop();

    private void OnRefreshTick(object? sender, object e) => Render();

    private void OnRefreshClick(object sender, RoutedEventArgs e) => Render();

    private void Render()
    {
        var period = Services.CurrentPeriod;

        // 办公室里这台不是公开屏幕：操作人一直显示 —— 不然为了看一眼"谁扣的分"，
        // 得先认一遍人（电脑端虽然免 PIN，但那一步本身就是门槛）。
        // 大屏仍然是认人才显示：那一列挂在教室墙上，谁路过都能看见。
        var showOperator = Services.Session.CanWrite || App.Mode == AppMode.Desk;

        // 教室里是公开屏幕：谁扣的分不能随便露。未认人（看板/查分状态）时，
        // 操作人列连宽度一起收掉，不是只把文字变透明。
        OperatorColumn.Width = showOperator ? new GridLength(130) : new GridLength(0);
        OperatorHeader.Visibility = showOperator ? Visibility.Visible : Visibility.Collapsed;

        var from = Clock.StartOfDay(DateTime.Today);
        var all = Services.Entries.Entries(period.Id, limit: 400)
            .Where(e => e.OccurredAt >= from)
            .ToList();

        EntryList.Items.Clear();
        CountText.Text = all.Count == 0 ? "" : $"共 {all.Count} 条";

        foreach (var entry in all)
        {
            var row = BuildRow(entry, showOperator);
            var swipe = new SwipeControl { Content = row };
            var items = new SwipeItems { Mode = SwipeMode.Execute };
            var undo = new SwipeItem
            {
                Text = "撤销",
                IconSource = new SymbolIconSource { Symbol = Symbol.Undo },
                Background = Danger,
            };
            undo.Invoked += async (_, _) => await UndoEntryAsync(entry);
            items.Add(undo);
            swipe.RightItems = items;
            EntryList.Items.Add(swipe);
        }

        var empty = all.Count == 0;
        Empty.Show(empty);
        if (empty)
        {
            // 这页只看当天，空状态就别说"这个周期"了 —— 标题写着"今天的记录"，正文换个口径会让人以为筛选出错
            Empty.Configure("\uE8A5",
                "今天还没有记录。加的第一条分会出现在这里。",
                "去记分台", null);

            // Render 每 20 秒跑一次：不先退订就会挂上十几份，点一下跳十几次
            Empty.PrimaryInvoked -= GotoScore;
            Empty.PrimaryInvoked += GotoScore;
        }
    }

    private void GotoScore() => App.Host?.Navigate(AppNav.Score);

    private FrameworkElement BuildRow(Entry entry, bool showOperator)
    {
        var grid = new Grid
        {
            Padding = new Thickness(0, 10, 0, 10),
            Background = Pal.Card,
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });

        // 姓名列定宽：留着当星号列的话，操作人一收掉，多出来的宽度全灌进姓名列，
        // 名字缩在左边、右边空一大片。多出来的宽度应该给「项目 / 备注」。
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition
        {
            Width = showOperator ? new GridLength(130) : new GridLength(0),
        });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });

        var time = new TextBlock
        {
            Text = Clock.ToLocal(entry.OccurredAt).ToString("HH:mm"),
            FontFamily = new FontFamily("Consolas"),
            FontSize = 18,
            Foreground = Secondary,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 0, 0),
        };
        Grid.SetColumn(time, 0);

        var name = new TextBlock
        {
            Text = string.IsNullOrEmpty(entry.StudentName) ? "—" : entry.StudentName,
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Foreground = Primary,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(name, 1);

        var detail = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        detail.Children.Add(new TextBlock
        {
            Text = entry.RuleLabel ?? "",
            FontSize = 18,
            Foreground = Primary,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        var suffix = entry.DetailSuffix;
        if (!string.IsNullOrEmpty(suffix) || !string.IsNullOrEmpty(entry.Note))
            detail.Children.Add(new TextBlock
            {
                Text = string.IsNullOrEmpty(entry.Note) ? suffix : $"{entry.Note}　{suffix}".Trim(),
                FontSize = 13,
                Foreground = entry.IsReversed ? Danger : Secondary,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
        Grid.SetColumn(detail, 2);

        var op = new TextBlock
        {
            Text = string.IsNullOrEmpty(entry.OperatorName) ? "—" : entry.OperatorName,
            FontSize = 15,
            Foreground = Secondary,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Visibility = showOperator ? Visibility.Visible : Visibility.Collapsed,
        };
        Grid.SetColumn(op, 3);

        var delta = new TextBlock
        {
            Text = entry.IsReversed ? "已撤销" : entry.DeltaText,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 22,
            FontWeight = FontWeights.SemiBold,
            Foreground = entry.IsReversed ? Secondary : entry.Delta >= 0 ? Positive : Negative,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };
        Grid.SetColumn(delta, 4);

        // 左滑是触屏手势，鼠标用户十个有八个不知道有这回事。
        // 认得出来是谁在操作时，给一枚显式的撤销，门禁仍然是同一套。
        if (Services.Session.CanWrite && !entry.IsReversed && !entry.IsReversal)
        {
            var undo = new Button
            {
                Content = "撤销",
                Style = (Style)Application.Current.Resources["BtnSm"],
                MinWidth = 80,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
            };
            undo.Click += async (_, _) => await UndoEntryAsync(entry);
            Grid.SetColumn(undo, 5);
            grid.Children.Add(undo);
        }

        grid.Children.Add(time);
        grid.Children.Add(name);
        grid.Children.Add(detail);
        grid.Children.Add(op);
        grid.Children.Add(delta);
        return grid;
    }

    private async Task UndoEntryAsync(Entry entry)
    {
        if (entry.IsReversed || entry.IsReversal) return;

        var session = Services.Session;
        if (!session.CanWrite)
        {
            Toast(session.WriteBlockedText);
            return;
        }

        var rows = Services.Entries.BatchRows(entry.BatchId);
        var permission = EntryService.CheckReversePermission(rows, Services.Operator, session.IsAdmin, Clock.NowMs());

        if (permission == ReversePermission.Denied)
        {
            Toast("没有权限撤销这一批");
            return;
        }

        if (permission == ReversePermission.NeedsPassword)
        {
            var shell = App.Host;
            if (shell is null) return;
            var ok = await shell.AskPinAsync("撤销记录", $"{entry.StudentName} · {entry.RuleLabel}，需要管理密码", requireManager: true);
            if (!ok) return;
            session.EnterAdmin();
        }

        try
        {
            Services.Entries.ReverseSingle(entry.Id, Services.Operator, "board.entries");
            Sounds.Play(Cue.Undo);
            Toast($"已撤销 {entry.StudentName} 的 {entry.RuleLabel} {entry.DeltaText}");
            Render();
        }
        catch (Exception ex)
        {
            Services.LogError("undo-entry", ex);
            Toast("撤销失败：" + ex.Message);
        }
    }

    private void Toast(string message) => App.Host?.Toast(message);
}
