using ClassPoints.App.Controls;
using ClassPoints.App.Services;
using ClassPoints.Core;
using ClassPoints.Data;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using ClassPoints.App.Theme;

namespace ClassPoints.App.Desk;

/// <summary>
/// 成绩页（仅电脑端）：导入 → 权重 → 排名预览 → 导出。
/// 这一页的所有滑块都要"当场看到后果"，所以任何一次调整都直接重算并重排预览，
/// 280 ms 防抖只是为了不让拖动过程把 CPU 打满。
/// </summary>
public sealed partial class DeskExamsPage : Page
{
    private Scheme _scheme = new();
    private List<Exam> _exams = [];
    private RankingResult? _ranking;
    private RankingResult? _baseline;
    private List<Student> _students = [];
    private Dictionary<string, int> _termPoints = [];

    /// <summary>本周期录入的场次。总评只用这些；<see cref="_exams"/> 仍列全部（旧周期要能删）。</summary>
    private List<Exam> _periodExams = [];

    private bool _loading;
    private bool _showAll;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _debounce;

    public DeskExamsPage()
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
            _loading = true;
            // 总评只看**当前周期**：平时分取本周期总分，考试也只取本周期录入的场次。
            // 以前是"累计积分 + 全部场次"，开过新周期之后旧分数还压在总评里；
            // 老师的口径是每个周期各评一次（云端在换周期时也会把旧周期结转清零，
            // 见 worker/src/carryover.ts —— 两边保持同一个口径）。
            //
            // 列表仍列全部场次：旧周期的考试要能删、能看，只是不参与这次总评。
            _exams = Services.Exams.ListAllExams();
            _periodExams = _exams.Where(e => e.PeriodId == Services.CurrentPeriod.Id).ToList();
            _termPoints = Services.PeriodTotals();
            _scheme = Services.Exams.ActiveScheme();
            _students = Services.Students.ListActive();

            // 滑块代表「平时分的占比」：填充段就是平时那一段，剩下的才是考试。
            // 以前这里存的是考试占比，于是"平时 20%"旁边却拉着一条 80% 长的填充 ——
            // 点跑到了 80% 的位置，看着就是平时和考试反了。
            WeightSlider.Value = _scheme.DailyPercent;

            _loading = false;

            RenderExams();
            RenderWeights();
            Recompute(resetBaseline: true);
        }
        catch (Exception ex)
        {
            _loading = false;
            Services.LogError("desk-exams", ex);
            Shell?.Toast("成绩页读取失败：" + ex.Message, 6);
        }
    }

    private static string PolicyLabel(AbsencePolicy p) => p switch
    {
        AbsencePolicy.Zero => "缺考记 0 分",
        AbsencePolicy.ClassAverage => "缺考按该场全班平均分替代",
        _ => "缺考那场不计，权重摊给其余场次",
    };

    // ---------------- 一、考试列表 ----------------

    private void RenderExams()
    {
        ExamList.Children.Clear();

        if (_exams.Count == 0)
        {
            ExamList.Children.Add(new Border
            {
                Padding = new Thickness(18, 4, 18, 18),
                Child = new TextBlock
                {
                    Text = "还没有成绩。点右上「选择文件导入成绩」，支持 xlsx / xls / csv。",
                    Style = Styled("DeskSecondaryText"),
                },
            });
            return;
        }

        foreach (var exam in _exams)
        {
            var scores = Services.Exams.Scores(exam.Id);
            var present = scores.Count(s => !s.Absent && s.Score is not null);
            var absent = scores.Count(s => s.Absent);
            var missing = System.Math.Max(0, _students.Count - present - absent);

            var grid = new Grid { ColumnSpacing = 12, Padding = new Thickness(18, 8, 18, 8), MinHeight = 44 };
            foreach (var w in new[] { 110d, 0d, 80d, 80d, 80d, 140d, 140d })
                grid.ColumnDefinitions.Add(new ColumnDefinition
                {
                    Width = w <= 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(w),
                });

            Put(grid, 0, new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(exam.ExamDate) ? "—" : exam.ExamDate!,
                Style = Styled("DeskSecondaryText"),
                VerticalAlignment = VerticalAlignment.Center,
            });

            Put(grid, 1, new TextBlock
            {
                Text = exam.Name,
                Style = Styled("DeskBodyText"),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });

            Put(grid, 2, Number(exam.FullScore.ToString("0.#")));
            Put(grid, 3, Number(present.ToString()));
            Put(grid, 4, Number(missing > 0 ? $"{absent} + {missing} 缺" : absent.ToString()));

            Put(grid, 5, new TextBlock
            {
                Text = Clock.ToLocal(exam.ImportedAt).ToString("yyyy-MM-dd HH:mm"),
                Style = Styled("DeskSecondaryText"),
                VerticalAlignment = VerticalAlignment.Center,
            });

            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            var full = new Button { Content = "改满分", Style = Styled("DeskButton"), Height = 28, MinWidth = 60 };
            full.Click += async (_, _) => await EditFullScoreAsync(exam);
            actions.Children.Add(full);

            var rename = new Button { Content = "改名", Style = Styled("DeskButton"), Height = 28, MinWidth = 54 };
            rename.Click += async (_, _) => await RenameAsync(exam);
            actions.Children.Add(rename);

            var del = new Button { Content = "删除", Style = Styled("DeskButton"), Height = 28, MinWidth = 54 };
            del.Click += async (_, _) => await DeleteExamAsync(exam);
            actions.Children.Add(del);

            Put(grid, 6, actions);

            ExamList.Children.Add(new Border
            {
                BorderBrush = Pal.Stroke,
                BorderThickness = new Thickness(0, 1, 0, 0),
                Child = grid,
            });
        }
    }

    private async Task EditFullScoreAsync(Exam exam)
    {
        var box = new NumberInput
        {
            Header = "满分",
            Value = exam.FullScore,
            Minimum = 1,
            Maximum = 100000,
            Step = 10,
            BoxWidth = 100,
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "修改满分",
            Content = new StackPanel
            {
                Spacing = 10,
                Width = 300,
                Children =
                {
                    box,
                    new TextBlock
                    {
                        Text = "改满分只影响「考试百分制」这一列。",
                        Style = Styled("DeskSecondaryText"),
                    },
                },
            },
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await Dialogs.ShowAsync(dialog) != ContentDialogResult.Primary) return;
        box.Commit();
        Services.Exams.UpdateFullScore(exam.Id, box.Value);
        Reload();
    }

    private async Task RenameAsync(Exam exam)
    {
        var box = new TextBox { Header = "考试名称", Text = exam.Name };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "重命名考试",
            Content = box,
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await Dialogs.ShowAsync(dialog) != ContentDialogResult.Primary) return;
        if (string.IsNullOrWhiteSpace(box.Text)) return;
        Services.Exams.Rename(exam.Id, box.Text.Trim());
        Reload();
    }

    private async Task DeleteExamAsync(Exam exam)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "删除这场考试？",
            Content = $"{exam.Name} 的 {Services.Exams.Scores(exam.Id).Count} 条成绩会被删除，" +
                      "总评里这场考试的权重也会清掉。积分不受影响。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await Dialogs.ShowAsync(dialog) != ContentDialogResult.Primary) return;
        Services.Exams.Delete(exam.Id);
        Services.Audit.Add("exam.delete", null, exam.Id, exam.Name);
        Reload();
    }

    // ---------------- 二、权重 ----------------

    private void RenderWeights()
    {
        _loading = true;

        var examPercent = _scheme.ExamPercent;
        var dailyPercent = 100 - examPercent;
        // 滑块的值是"平时占比"，和左边那个标签同一个数。
        if (System.Math.Abs(WeightSlider.Value - dailyPercent) > 0.01) WeightSlider.Value = dailyPercent;
        DailyWeightLabel.Text = $"平时 {dailyPercent}%";
        ExamWeightLabel.Text = $"考试 {examPercent}%";

        // 口径在设置页统一改；这里只留一句"现在是怎么算的"。
        SchemeHint.Text = PolicyLabel(Services.Settings.AbsencePolicy) +
                          "（在「设置 → 成绩口径」改）";

        // 考试那部分不再让人一场一场填百分比：只有一场就是 100%，
        // 多场自动平分 —— 这一行只负责把"现在怎么分"说出来，谁也改不了它。
        ExamShareHint.Text = _periodExams.Count switch
        {
            0 => "这个周期还没有考试。导入成绩后，考试成绩那部分才会算进总评。",
            1 => $"只有 1 场考试：考试部分 100% 计入「{_periodExams[0].Name}」。",
            _ => $"共 {_periodExams.Count} 场考试：考试部分按场次平均计入（每场 {100.0 / _periodExams.Count:0.#}%）。",
        };

        _loading = false;
        UpdateWeightState();
        UpdateImpact();
    }

    /// <summary>
    /// 权重合不合法这件事已经不存在了：考试部分恒由各场平分（BuildExamWeights 归一化到 100%），
    /// 所以导出键永远是可按的。这里保留成一处"能按"，免得哪天再加约束要满世界找。
    /// </summary>
    private void UpdateWeightState() => ExportButton.IsEnabled = true;

    private void UpdateImpact()
    {
        var examMean = _ranking is { Rows.Count: > 0 }
            ? _ranking.Rows.Average(r => r.ExamRaw)
            : 0;
        var pointsMean = _ranking is { Rows.Count: > 0 }
            ? _ranking.Rows.Average(r => (double)r.Points)
            : 0;

        ImpactText.Text = _periodExams.Count == 0
            ? ""
            : examMean <= 0
                ? "还没有成绩。"
                : SchemeMath.ImpactText(examMean, pointsMean, _scheme);

        FormulaText.Text = _scheme.FormulaText + " · 平时分 = 本周期积分";
    }

    private void OnWeightSliderChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_loading) return;

        // 滑块量的是平时分占比（填充段 = 平时那一段），剩下自动归考试。
        var daily = (int)System.Math.Round(WeightSlider.Value / 5) * 5;
        daily = System.Math.Clamp(daily, 0, 100);
        _scheme.DailyWeight = daily / 100.0;
        _scheme.ExamWeight = 1 - _scheme.DailyWeight;

        _loading = true;
        if (System.Math.Abs(WeightSlider.Value - daily) > 0.01) WeightSlider.Value = daily;
        DailyWeightLabel.Text = $"平时 {daily}%";
        ExamWeightLabel.Text = $"考试 {100 - daily}%";
        _loading = false;

        Services.Exams.SaveScheme(_scheme);
        DebouncedRecompute();
    }

    private void DebouncedRecompute()
    {
        _debounce ??= DispatcherQueue.CreateTimer();
        _debounce.Interval = TimeSpan.FromMilliseconds(280);
        _debounce.IsRepeating = false;
        _debounce.Tick -= OnDebounceTick;
        _debounce.Tick += OnDebounceTick;
        _debounce.Start();
    }

    private void OnDebounceTick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        Recompute(resetBaseline: false);
    }

    // ---------------- 三、排名预览 ----------------

    private void Recompute(bool resetBaseline)
    {
        try
        {
            // 只算本周期的场次：旧周期的考试是历史，不再参与这次总评
            _ranking = Services.Exams.BuildRanking(_students, _termPoints, _scheme,
                Services.Settings.AbsencePolicy, _periodExams);

            if (resetBaseline || _baseline is null) _baseline = _ranking;

            RenderRank();
            UpdateImpact();
        }
        catch (Exception ex)
        {
            Services.LogError("rank-preview", ex);
        }
    }

    private void RenderRank()
    {
        RankList.Children.Clear();
        if (_ranking is null)
        {
            RankEmpty.Visibility = Visibility.Visible;
            return;
        }

        RankEmpty.Visibility = _ranking.Rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var rows = _showAll ? _ranking.Rows : _ranking.Rows.Take(20).ToList();
        foreach (var r in rows)
        {
            var grid = new Grid { ColumnSpacing = 12, Padding = new Thickness(18, 7, 18, 7), MinHeight = 38 };
            foreach (var w in new[] { 60d, 0d, 100d, 110d, 110d, 110d })
                grid.ColumnDefinitions.Add(new ColumnDefinition
                {
                    Width = w <= 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(w),
                });

            Put(grid, 0, Number(r.Rank.ToString()));

            var who = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            who.Children.Add(new TextBlock { Text = r.Name, Style = Styled("DeskBodyText") });
            if (r.AbsentExams.Count > 0)
                who.Children.Add(new TextBlock
                {
                    Text = "缺考 " + string.Join("、", r.AbsentExams),
                    Style = Styled("DeskSecondaryText"),
                    Foreground = Pal.Warn,
                });
            Put(grid, 1, who);

            Put(grid, 2, Number(r.Points.ToString()));
            Put(grid, 3, Number(r.ExamText));
            Put(grid, 4, Number(r.ExamPercentText));

            var total = Number(r.TotalText);
            total.Foreground = Pal.Accent;
            total.FontWeight = FontWeights.SemiBold;
            Put(grid, 5, total);

            RankList.Children.Add(new Border
            {
                BorderBrush = Pal.Stroke,
                BorderThickness = new Thickness(0, 1, 0, 0),
                Child = grid,
            });
        }

        if (_ranking.Rows.Count > 20)
        {
            var more = new Button
            {
                Content = _showAll ? "只看前 20 名" : $"显示全部 {_ranking.Rows.Count} 人",
                Style = Styled("DeskButton"),
                Margin = new Thickness(18, 10, 18, 14),
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            more.Click += (_, _) =>
            {
                _showAll = !_showAll;
                RenderRank();
            };
            RankList.Children.Add(more);
        }

        ChangeText.Text = _ranking.HasExamData
            ? RankingCalculator.DescribeTopChanges(_baseline!, _ranking, 10)
            : "没有考试成绩时，总评就是平时分。";
    }

    // ---------------- 四、导入与导出 ----------------

    private async void OnImportScores(object sender, RoutedEventArgs e)
    {
        try
        {
            var picked = FileDialogs.Open("导入成绩", FileFilter.Sheet);
            if (picked is null) return;

            var grid = picked.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)
                ? CsvFile.Read(picked)
                : ExcelReader.Read(picked);

            if (grid.RowCount < 2)
            {
                Shell?.Toast("这个文件里没有成绩数据（至少要有表头 + 一行）");
                return;
            }

            // 全校大表：先挑班、再挑科，然后投影成「姓名/分数」两列，走同一条匹配管线
            GradeSheet.TryParse(grid, out var grade);
            await ShowImportDialogAsync(Path.GetFileName(picked), picked, grid, grade);
        }
        catch (Exception ex)
        {
            Services.LogError("import-scores", ex);
            Shell?.Toast("导入失败：" + ex.Message, 6);
        }
    }

    private async Task ShowImportDialogAsync(string fileName, string path, SheetGrid grid, GradeSheet? grade)
    {
        var parsed = ScoreSheetParser.Parse(
            grade is null ? grid : grade.Project(grade.Classes[0], grade.Subjects[0]), _students);
        var nameBox = new TextBox { Header = "考试名称", Text = GuessExamName(fileName), Width = 240 };

        // 满分默认值按"这张表里最大那个分"去猜常见的档位：95 → 100、150 → 150、750 → 750。
        // 以前写死 750，导一份百分制成绩进来，全班都显示成"13% 得分率"，
        // 还得手工改一次满分才看得懂。而直接取最大值向上取整也不行：
        // 班里最高 88 分的百分制卷子会变成"满分 88"，看起来更莫名其妙。
        static double PeakOf(ScoreParseResult p) => p.Rows
            .Where(r => r.Kind is not (ScoreMatchKind.Summary or ScoreMatchKind.Duplicate))
            .Select(r => ScoreSheetParser.TryParseScore(r.ScoreText, out var v) ? v : double.NaN)
            .Where(v => !double.IsNaN(v) && v > 0)
            .DefaultIfEmpty(double.NaN)
            .Max();

        var peak = PeakOf(parsed);

        var fullBox = new NumberInput
        {
            Header = "满分",
            Value = GuessFullScore(peak),
            Minimum = 1,
            Maximum = 100000,
            Step = 10,
            BoxWidth = 100,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 0),
        };
        var dateBox = new TextBox
        {
            Header = "考试日期",
            Text = DateTime.Today.ToString("yyyy-MM-dd"),
            Width = 160,
        };

        var summary = new TextBlock
        {
            Style = Styled("DeskSecondaryText"),
            TextWrapping = TextWrapping.Wrap,
            Text = parsed.SummaryText,
        };

        var list = new StackPanel { Spacing = 2 };
        void RenderRows()
        {
            list.Children.Clear();
            foreach (var row in parsed.Rows.Take(200))
            {
                var line = new Grid { ColumnSpacing = 10, Padding = new Thickness(0, 3, 0, 3) };
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                line.Children.Add(new TextBlock { Text = row.RawName, Style = Styled("DeskBodyText") });

                var score = Number(row.ScoreText);
                Grid.SetColumn(score, 1);
                line.Children.Add(score);

                var status = new TextBlock
                {
                    Text = row.Kind switch
                    {
                        ScoreMatchKind.Matched => row.Matched is null ? "匹配" : row.Matched.Name,
                        ScoreMatchKind.Absent => "缺考，按口径计入",
                        ScoreMatchKind.Summary => "非学生行，已忽略",
                        ScoreMatchKind.Duplicate => "两人同名，请选择",
                        ScoreMatchKind.Unmatched => "名单里没有这个人",
                        _ => "分数无法识别",
                    },
                    Style = Styled("DeskSecondaryText"),
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = row.Kind switch
                    {
                        ScoreMatchKind.Matched => Pal.Positive,
                        ScoreMatchKind.Absent => Pal.Warn,
                        ScoreMatchKind.Summary => Pal.Secondary,
                        _ => Pal.Negative,
                    },
                };
                Grid.SetColumn(status, 2);
                line.Children.Add(status);

                if (row.Kind == ScoreMatchKind.Unmatched)
                {
                    var add = new Button { Content = "加入名单", Style = Styled("DeskButton"), Height = 26, MinWidth = 70 };
                    add.Click += (_, _) =>
                    {
                        var student = Services.Students.Insert(new Student
                        {
                            Name = row.CleanName.Length > 0 ? row.CleanName : NameNormalizer.Clean(row.RawName),
                            SeatNo = (_students.Max(s => s.SeatNo) ?? 0) + 1,
                        });
                        _students.Add(student);
                        row.Matched = student;
                        row.Kind = ScoreMatchKind.Matched;
                        row.Include = true;
                        summary.Text = parsed.SummaryText;
                        RenderRows();
                    };
                    Grid.SetColumn(add, 3);
                    line.Children.Add(add);
                }

                list.Children.Add(line);
            }
        }
        RenderRows();

        // 两行排：挑班挑科一行、考试本身的信息一行。
        // 以前全塞一行横向 StackPanel，窗口一窄就把「考试日期」挤出对话框右边。
        var header = new StackPanel { Spacing = 10 };

        if (grade is not null)
        {
            // 全校大表：一个年级几百人、二十几科，不先挑就问"导哪个"等于没做
            var classBox = new ComboBox
            {
                Header = "班级",
                Width = 110,
                ItemsSource = grade.Classes,
                SelectedIndex = 0,
            };
            var subjectBox = new ComboBox
            {
                Header = "科目",
                Width = 150,
                ItemsSource = grade.Subjects.Select(x => x.Label).ToList(),
                SelectedIndex = 0,
            };

            void Reparse()
            {
                if (classBox.SelectedItem is not string className) return;
                if (subjectBox.SelectedIndex < 0 || subjectBox.SelectedIndex >= grade.Subjects.Count) return;

                parsed = ScoreSheetParser.Parse(grade.Project(className, grade.Subjects[subjectBox.SelectedIndex]), _students);
                summary.Text = parsed.SummaryText;
                // 满分跟着科目走：总分 750、语文 150，猜错了会导致得分率全错
                fullBox.Value = GuessFullScore(PeakOf(parsed));
                RenderRows();
            }

            classBox.SelectionChanged += (_, _) => Reparse();
            subjectBox.SelectionChanged += (_, _) => Reparse();

            var pickers = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            pickers.Children.Add(classBox);
            pickers.Children.Add(subjectBox);

            // 认出了好几门课时让老师自己确认一下认的是哪一列 —— 表头写成
            // "期末成绩(百分制)"这种，猜错一列就是整场分数白导
            var shown = new TextBlock
            {
                Style = Styled("DeskSecondaryText"),
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(4, 0, 0, 6),
                Text = $"{grade.Classes.Count} 个班 · {grade.Subjects.Count} 科",
            };
            pickers.Children.Add(shown);

            header.Children.Add(pickers);
        }
        else
        {
            // 普通成绩单（没有班级列）：列是猜出来的，猜错了要能当场纠正。
            // 以前只能重新做一份"姓名/分数"两列的表，或者干脆放弃导入。
            var layout = parsed.Layout;
            var nameColBox = new ComboBox
            {
                Header = "姓名列",
                Width = 190,
                ItemsSource = layout.Columns,
                DisplayMemberPath = nameof(ColumnOption.Display),
                SelectedIndex = System.Math.Max(0, layout.NameColumn),
            };
            var scoreColBox = new ComboBox
            {
                Header = "分数列",
                Width = 190,
                ItemsSource = layout.Columns,
                DisplayMemberPath = nameof(ColumnOption.Display),
                SelectedIndex = System.Math.Max(0, layout.ScoreColumn),
            };

            void Recolumn()
            {
                if (nameColBox.SelectedIndex < 0 || scoreColBox.SelectedIndex < 0) return;
                if (nameColBox.SelectedIndex == scoreColBox.SelectedIndex) return;

                parsed = ScoreSheetParser.Parse(grid, _students,
                    new ScoreSheetLayout(layout.HeaderRow, nameColBox.SelectedIndex, scoreColBox.SelectedIndex, layout.Columns));
                summary.Text = parsed.SummaryText;
                fullBox.Value = GuessFullScore(PeakOf(parsed));
                RenderRows();
            }

            nameColBox.SelectionChanged += (_, _) => Recolumn();
            scoreColBox.SelectionChanged += (_, _) => Recolumn();

            var pickers = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            pickers.Children.Add(nameColBox);
            pickers.Children.Add(scoreColBox);
            header.Children.Add(pickers);
        }

        // 考试名称单独一行：文件名常常很长，和满分、日期挤一行会把日期顶出对话框。
        header.Children.Add(nameBox);

        var basics = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        basics.Children.Add(fullBox);
        basics.Children.Add(dateBox);
        header.Children.Add(basics);

        var note = new TextBlock
        {
            Style = Styled("DeskSecondaryText"),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 460,
            Text = grade is null
                ? "按姓名匹配，同名与汇总行跳过。"
                : "这是一张全校成绩表：先选班级和科目，导入只取该班该科那一列分数（名次列不参与）。",
        };

        // 宽度卡在 460：ContentDialog 的内容区默认只有 548 左右，写 640 的话
        // 右边一截会被裁掉（"考试日期"只剩半个框，就是老师说的"出框"）。
        var panel = new StackPanel { Spacing = 12, Width = 460 };
        panel.Children.Add(header);
        panel.Children.Add(summary);
        panel.Children.Add(note);
        panel.Children.Add(new ScrollViewer { Content = list, MaxHeight = 320 });

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"导入成绩 · {fileName}",
            Content = new ScrollViewer { Content = panel, MaxHeight = 620 },
            PrimaryButtonText = "导入",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        var existing = _exams.FirstOrDefault(x =>
            string.Equals(x.Name, nameBox.Text.Trim(), StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            panel.Children.Insert(0, new TextBlock
            {
                Text = $"这场考试已有 {Services.Exams.Scores(existing.Id).Count} 条成绩，导入会整场覆盖。",
                Style = Styled("DeskSecondaryText"),
                Foreground = Pal.Warn,
            });
            dialog.DefaultButton = ContentDialogButton.Close;
        }

        if (await Dialogs.ShowAsync(dialog) != ContentDialogResult.Primary) return;

        var examName = nameBox.Text.Trim();
        if (examName.Length == 0)
        {
            Shell?.Toast("考试名称不能为空");
            return;
        }

        var rowsToWrite = parsed.Rows
            .Where(r => r.Include && r.Kind is ScoreMatchKind.Matched or ScoreMatchKind.Absent)
            .Select(r => (StudentId: r.Matched!.Id, Score: r.Score, Absent: r.Kind == ScoreMatchKind.Absent))
            .ToList();

        if (rowsToWrite.Count == 0)
        {
            Shell?.Toast("没有可导入的行");
            return;
        }

        fullBox.Commit();
        var result = Services.Exams.ImportScores(Services.CurrentPeriod.Id, examName,
            fullBox.Value <= 0 ? 100 : fullBox.Value,
            dateBox.Text.Trim(), rowsToWrite, Path.GetFileName(path));

        Services.Audit.Add("exam.import", null, result.ExamId,
            $"{examName}：写入 {result.Written}，缺考 {result.Absent}");

        Reload();

        RenderWeights();
        Recompute(resetBaseline: false);

        Shell?.Toast($"已导入 {examName}：{result.Written} 人" +
                     (result.Absent > 0 ? $"，缺考 {result.Absent} 人" : ""), 5);
    }

    /// <summary>
    /// 按最大分猜常见满分档。猜错也不要紧：改满分只影响"考试百分制"那一列，
    /// 而且导入页里就能改 —— 但默认值必须像个人写的，不能是 750 或者 88。
    /// </summary>
    private static double GuessFullScore(double peak)
    {
        if (double.IsNaN(peak) || peak <= 0) return 100;

        // 认错了列（把考号当成分数）时 peak 会是几亿 —— 那不是分数。
        // 高考总分再高也到不了 1000，超过 2000 一律当作没猜出来。
        if (peak > 2000) return 100;

        foreach (var common in new double[] { 100, 120, 150, 200, 300, 750 })
            if (peak <= common) return common;
        return System.Math.Ceiling(peak / 50) * 50;
    }

    private static string GuessExamName(string fileName)    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        foreach (var suffix in new[] { "成绩", "成绩单", "分数" })
            name = name.Replace(suffix, "");
        return name.Length == 0 ? "考试" : name;
    }

    private void OnPreflight(object sender, RoutedEventArgs e)
    {
        var issues = CollectIssues();
        var panel = new StackPanel { Spacing = 8, Width = 420 };
        panel.Children.Add(new TextBlock
        {
            Text = issues.Count == 0 ? "没有问题，可以导出。" : $"发现 {issues.Count} 个需要注意的地方：",
            Style = Styled("DeskBodyText"),
        });
        foreach (var issue in issues)
            panel.Children.Add(new TextBlock
            {
                Text = "· " + issue,
                Style = Styled("DeskSecondaryText"),
                Foreground = Pal.Warn,
            });
        if (issues.Count > 0)
            panel.Children.Add(new TextBlock
            {
                Text = "不影响导出，但会写进「参数说明」那张表。",
                Style = Styled("DeskSecondaryText"),
            });

        _ = Dialogs.ShowAsync(new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "导出前检查",
            Content = panel,
            CloseButtonText = "知道了",
        });
    }

    private List<string> CollectIssues()
    {
        var issues = new List<string>();

        if (_students.Count == 0) issues.Add("名单是空的，导出的表只有表头。");
        if (_periodExams.Count == 0) issues.Add("本周期没有导入考试成绩，总评等于平时分。");

        var missing = _periodExams.Sum(e =>
        {
            var scores = Services.Exams.Scores(e.Id);
            return System.Math.Max(0, _students.Count - scores.Count);
        });
        if (missing > 0) issues.Add($"{missing} 人次没有成绩记录（缺考或未导入）。");

        var zero = _ranking?.Rows.Count(r => r.Points == 0) ?? 0;
        if (zero > 0) issues.Add($"{zero} 人本周期积分为 0。");

        return issues;
    }

    private async void OnExport(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = FileDialogs.Save("导出 Excel",
                $"{Services.ClassName}-总评-{DateTime.Today:yyyyMMdd}",
                FileFilter.Excel, "xlsx");
            if (path is null) return;

            var bundle = Services.BuildExportBundle(Services.Settings.AbsencePolicy, CollectIssues());
            await File.WriteAllBytesAsync(path, WorkbookBuilder.Build(bundle));

            Services.Audit.Add("export.excel", null, Path.GetFileName(path), "");
            Shell?.Toast("已导出到 " + path, 6);
        }
        catch (Exception ex)
        {
            Services.LogError("export-excel", ex);
            Shell?.Toast("导出失败：" + ex.Message, 6);
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
