using ClassPoints.App.Services;
using ClassPoints.App.Theme;
using ClassPoints.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ClassPoints.App.Desk;

/// <summary>
/// 周期与归档。这里的按钮会让"数据看起来消失了"，
/// 所以每一步都先归档、再切换，并且在界面上把"数据还在哪"写清楚。
///
/// 周期按序号走（第 1 周期、第 2 周期…）。以前叫"学期编号"，还要区分
/// 「重置积分 / 重置学期」两种重置 —— 那是两个心智模型，老师记不住也不想记。
/// </summary>
public sealed partial class DeskPeriodsPage : Page
{
    public DeskPeriodsPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Reload();
    }

    private static AppServices Services => App.Services;

    private static IShellHost? Shell => App.Host;

    private void Reload()
    {
        try
        {
            var period = Services.CurrentPeriod;
            var entryCount = Services.Periods.EntryCount(period.Id);
            var periodPoints = Services.Entries.PeriodPointsAwarded(period.Id);
            var totalPoints = Services.Entries.CumulativeTotals().Values.Sum();
            var archived = Services.Periods.Archived();

            PeriodNameText.Text = period.TitleText;
            PeriodRangeText.Text = "起止 " + period.RangeText;
            EntryCountText.Text = entryCount.ToString();
            PointsText.Text = periodPoints.ToString();
            TotalPointsText.Text = totalPoints.ToString();
            PeriodIndexText.Text = archived.Count.ToString();

            ResetHint.Text = $"归档包会写到：{Services.Backups.BackupRoot}";

            RenderArchives();
        }
        catch (Exception ex)
        {
            Services.LogError("desk-periods", ex);
            Shell?.Toast("周期页读取失败：" + ex.Message, 6);
        }
    }

    private void RenderArchives()
    {
        var archived = Services.Periods.Archived();
        ArchiveList.Children.Clear();
        ArchiveEmpty.Visibility = archived.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ArchiveHint.Text = archived.Count == 0 ? "" : $"共 {archived.Count} 个已归档周期";

        foreach (var p in archived)
        {
            var count = Services.Periods.EntryCount(p.Id);
            var total = Services.Entries.PeriodTotals(p.Id).Values.Sum();

            var grid = new Grid { ColumnSpacing = 12, Padding = new Thickness(18, 8, 18, 8), MinHeight = 42 };
            foreach (var w in new[] { 0d, 180d, 90d, 90d, 150d })
                grid.ColumnDefinitions.Add(new ColumnDefinition
                {
                    Width = w <= 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(w),
                });

            var title = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            title.Children.Add(new TextBlock { Text = p.TitleText, Style = Styled("DeskBodyText") });
            title.Children.Add(new TextBlock
            {
                Text = p.Name == p.TitleText ? "已归档" : p.Name,
                Style = Styled("DeskSecondaryText"),
            });
            grid.Children.Add(title);

            Put(grid, 1, new TextBlock
            {
                Text = p.RangeText,
                Style = Styled("DeskSecondaryText"),
                VerticalAlignment = VerticalAlignment.Center,
            });
            Put(grid, 2, Number(count.ToString()));
            Put(grid, 3, Number(total.ToString()));

            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var view = new Button { Content = "看榜单", Style = Styled("DeskButton"), Height = 28, MinWidth = 62 };
            view.Click += async (_, _) => await ShowRankingAsync(p);
            actions.Children.Add(view);

            var export = new Button { Content = "导出", Style = Styled("DeskButton"), Height = 28, MinWidth = 54 };
            export.Click += async (_, _) => await ExportAsync(p);
            actions.Children.Add(export);

            Put(grid, 4, actions);

            ArchiveList.Children.Add(new Border
            {
                BorderBrush = Pal.Stroke,
                BorderThickness = new Thickness(0, 1, 0, 0),
                Child = grid,
            });
        }
    }

    /// <summary>
    /// 历史周期该按「当时在班里的学生」统计：既包括现在仍在册的，
    /// 也包括已经转走、或后来停用删掉、但在这个周期记过分的人。
    /// 直接拿 ListActive() 会让转走的学生从旧榜上整条消失 —— 老师回头查账会以为分丢了。
    /// </summary>
    private static List<Student> StudentsForArchive(IReadOnlyDictionary<string, int> periodTotals)
    {
        var list = new List<Student>();
        foreach (var s in Services.Students.ListAll())
        {
            if (s.Active && !s.IsDeleted)
            {
                list.Add(s);
                continue;
            }

            // 没在这个周期记过分的人，没必要出现在旧榜上
            if (!periodTotals.ContainsKey(s.Id)) continue;

            // RankingCalculator 只算 Active 的学生，这里给一份「当年在册」的副本，
            // 不去改库里的真实记录。
            list.Add(new Student
            {
                Id = s.Id,
                Name = s.Name,
                StudentNo = s.StudentNo,
                SeatNo = s.SeatNo,
                Active = true,
                Rev = s.Rev,
                UpdatedAt = s.UpdatedAt,
                CreatedAt = s.CreatedAt,
            });
        }
        return list;
    }

    private async Task ShowRankingAsync(Period period)
    {
        var totals = Services.Entries.PeriodTotals(period.Id);
        var students = StudentsForArchive(totals)
            .OrderByDescending(s => totals.GetValueOrDefault(s.Id))
            .ThenBy(s => s.Name, StringComparer.CurrentCulture)
            .ToList();

        var panel = new StackPanel { Spacing = 4, Width = 420 };
        var rank = 0;
        var last = int.MinValue;
        for (var i = 0; i < students.Count; i++)
        {
            var points = totals.GetValueOrDefault(students[i].Id);
            if (points != last) rank = i + 1;
            last = points;

            var line = new Grid();
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            line.Children.Add(new TextBlock
            {
                Text = rank.ToString(),
                Style = Styled("DeskSecondaryText"),
            });

            var name = new TextBlock { Text = students[i].Name, Style = Styled("DeskBodyText") };
            Grid.SetColumn(name, 1);
            line.Children.Add(name);

            var value = Number(points.ToString());
            Grid.SetColumn(value, 2);
            line.Children.Add(value);

            panel.Children.Add(line);
        }

        await Dialogs.ShowAsync(new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = period.TitleText + " · 排行榜",
            Content = new ScrollViewer { Content = panel, MaxHeight = 480 },
            CloseButtonText = "关闭",
        });
    }

    private async Task ExportAsync(Period period)
    {
        try
        {
            var path = FileDialogs.Save("导出周期流水",
                $"{Services.ClassName}-{period.TitleText}-流水",
                FileFilter.Excel, "xlsx");
            if (path is null) return;

            var scheme = Services.Exams.ActiveScheme();
            var exams = Services.Exams.ListExams(period.Id);

            // 历史周期的"看榜单/导出"必须用那个周期的积分与场次：
            // 用 CumulativeTotals 会把今天的分写进过去的榜，用全部考试会把之后才考的算进去。
            var periodPoints = Services.Entries.PeriodTotals(period.Id);
            var students = StudentsForArchive(periodPoints);
            var ranking = Services.Exams.BuildRanking(students, periodPoints, scheme,
                Services.Settings.AbsencePolicy, exams);

            var bundle = new ClassPoints.Data.ExportBundle(
                ranking, exams,
                Services.Exams.ScoresForTerm(null, exams.Select(e => e.Id).ToList()),
                Services.Entries.Entries(period.Id, 100_000),
                scheme, period, Services.Settings.AbsencePolicy, period.TermKey,
                students, new Dictionary<string, double>(),
                Services.DeskOperator.DisplayName, DateTime.Now,
                new[] { "这是历史周期「" + period.TitleText + "」的导出，不含当前周期的数据。" });

            await File.WriteAllBytesAsync(path, ClassPoints.Data.WorkbookBuilder.Build(bundle));
            Shell?.Toast("已导出 " + path, 5);
        }
        catch (Exception ex)
        {
            Services.LogError("export-archive", ex);
            Shell?.Toast("导出失败：" + ex.Message, 6);
        }
    }

    private async void OnRename(object sender, RoutedEventArgs e)
    {
        var period = Services.CurrentPeriod;
        var box = new TextBox { Header = "周期名称", Text = period.Name };

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "重命名周期",
            Content = box,
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await Dialogs.ShowAsync(dialog) != ContentDialogResult.Primary) return;
        if (string.IsNullOrWhiteSpace(box.Text)) return;
        Services.Periods.Rename(period.Id, box.Text.Trim());
        Reload();
    }

    // ---------------- 开新周期 ----------------

    private async void OnResetPeriod(object sender, RoutedEventArgs e)
    {
        var period = Services.CurrentPeriod;
        var count = Services.Periods.EntryCount(period.Id);
        var nextName = PeriodNames.Format(Services.Periods.NextIndex());

        var nameBox = new TextBox { Header = "新周期名称", Text = nextName };
        var panel = new StackPanel { Spacing = 12, Width = 420 };
        panel.Children.Add(new TextBlock
        {
            Text = $"「{period.TitleText}」会归档，{count} 条记录一条不删，随时能在下面的历史里查看和导出。" +
                   "累计积分不清零，总评照常连续。",
            Style = Styled("DeskSecondaryText"),
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(nameBox);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "开始新周期？",
            Content = panel,
            PrimaryButtonText = "归档并开始",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await Dialogs.ShowAsync(dialog) != ContentDialogResult.Primary) return;
        if (string.IsNullOrWhiteSpace(nameBox.Text)) return;

        Rollover(nameBox.Text.Trim());
    }

    private void Rollover(string name)
    {
        try
        {
            var period = Services.CurrentPeriod;

            // 先归档（Excel 四表 + 数据库副本），再做任何切换。
            // 归档失败就不重置 —— 顺序反了的话，一旦导出报错，数据就"看不见了"。
            var archivePath = Services.Backups.BackupNow("period-reset", Services.BuildExportBundle());

            var next = Services.Periods.Rollover(name);
            Services.Settings.LastBackupAt = Clock.NowMs();
            Services.Audit.Add("period.rollover", null, next.Id,
                $"{period.Name} → {next.Name}，归档 {Path.GetFileName(archivePath)}");

            // 清派生状态：会话里的身份、选中、周期封顶计数都不该跨周期存在。
            Services.Session.SignOut();

            Reload();
            Shell?.Toast($"已归档「{period.TitleText}」，新周期「{next.TitleText}」开始。", 6);
            Shell?.Navigate(AppNav.Periods);
        }
        catch (Exception ex)
        {
            Services.LogError("rollover", ex);
            Shell?.Toast("归档失败，已中止重置：" + ex.Message, 8);
        }
    }

    // ---------------- 小工具 ----------------

    private static Style Styled(string key) => (Style)Application.Current.Resources[key];

    private static void Put(Grid grid, int column, FrameworkElement element)
    {
        Grid.SetColumn(element, column);
        grid.Children.Add(element);
    }

    private static TextBlock Number(string text) => new()
    {
        Text = text,
        FontFamily = (FontFamily)Application.Current.Resources["NumberFont"],
        FontSize = 15,
        HorizontalAlignment = HorizontalAlignment.Right,
        VerticalAlignment = VerticalAlignment.Center,
    };
}
