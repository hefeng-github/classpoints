using ClassPoints.App.Services;
using ClassPoints.Core;
using ClassPoints.Data;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using ClassPoints.App.Theme;

namespace ClassPoints.App.Desk;

/// <summary>
/// 名单：改名单是"坐下来的活"，所以完整功能都在这一页（大屏端只读 + 向导里粘贴）。
/// 行内不给"编辑"二级页，双击一行直接改，改完点保存 —— 名单一辈子改不到十次，别为它做路由。
/// </summary>
public sealed partial class DeskStudentsPage : Page
{
    private List<Student> _all = [];
    private Dictionary<string, int> _period = [];
    private Dictionary<string, int> _term = [];

    public DeskStudentsPage()
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
            _period = Services.Entries.PeriodTotals(period.Id);
            _term = Services.Entries.CumulativeTotals();

            // 必须带上已删除的：回收站就是从这个列表里筛 IsDeleted 的。
            // 以前这里用默认参数（只取未删除），于是「停用」把学生置了 deleted_at 之后
            // 既不在名单里、也不在回收站里 —— 界面上就是"人没了，也恢复不了"。
            _all = Services.Students.ListAll(includeDeleted: true);

            var active = _all.Count(s => !s.IsDeleted);
            var deleted = _all.Count(s => s.IsDeleted);
            SubText.Text = $"{active} 人在册" + (deleted > 0 ? $"，{deleted} 人在回收站（30 天内可恢复）" : "");

            // 「座位表」入口只在可选功能打开时出现。关着的时候这一页一个字都不提座位。
            SeatButton.Visibility = Services.Settings.SeatEnabled ? Visibility.Visible : Visibility.Collapsed;

            Render();
        }
        catch (Exception ex)
        {
            Services.LogError("desk-students", ex);
            Shell?.Toast("名单读取失败：" + ex.Message, 6);
        }
    }

    private void Render()
    {
        var keyword = SearchBox.Text.Trim();
        var rows = _all
            .Where(s => !s.IsDeleted)
            .Where(s => keyword.Length == 0
                        || s.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase)
                        || (s.StudentNo ?? "").Contains(keyword, StringComparison.OrdinalIgnoreCase))
            .OrderBy(s => s.SeatNo ?? int.MaxValue)
            .ThenBy(s => s.Name, StringComparer.CurrentCulture)
            .ToList();

        RowList.Items.Clear();
        EmptyPanel.Visibility = _all.All(s => s.IsDeleted) ? Visibility.Visible : Visibility.Collapsed;

        foreach (var s in rows) RowList.Items.Add(BuildRow(s));
    }

    private UIElement BuildRow(Student s)
    {
        var grid = new Grid { ColumnSpacing = 12, MinHeight = 44, Padding = new Thickness(12, 0, 12, 0) };
        // 座位列已隐藏（v1.3）：列宽表与表头 XAML 必须一一对应，
        // 改这里就要一起改 DeskStudentsPage.xaml 的 ColumnDefinitions。
        foreach (var w in new[] { 90d, 240d, 100d, 100d, 90d, 210d, 0d })
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = w <= 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(w),
            });

        Add(grid, 0, new TextBlock
        {
            Text = s.StudentNo ?? "—",
            Style = Styled("DeskSecondaryText"),
            VerticalAlignment = VerticalAlignment.Center,
        });

        var name = new TextBlock
        {
            Text = s.Name,
            Style = Styled("DeskBodyText"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Add(grid, 1, name);

        Add(grid, 2, PointsText(_period.GetValueOrDefault(s.Id)));

        Add(grid, 3, PointsText(_term.GetValueOrDefault(s.Id)));

        var state = new TextBlock
        {
            Text = s.Active ? "在册" : "已停用",
            Style = Styled("DeskSecondaryText"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Add(grid, 4, state);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var records = new Button { Content = "记录", Style = Styled("DeskButton"), Height = 28, MinWidth = 54 };
        records.Click += (_, _) => ShowStudentRecords(s);
        actions.Children.Add(records);

        var edit = new Button { Content = "编辑", Style = Styled("DeskButton"), Height = 28, MinWidth = 54 };
        edit.Click += async (_, _) => await EditStudentAsync(s, isNew: false);
        actions.Children.Add(edit);

        var toggle = new Button
        {
            Content = s.Active ? "停用" : "恢复",
            Style = Styled("DeskButton"),
            Height = 28,
            MinWidth = 54,
        };
        toggle.Click += (_, _) => ToggleStudent(s);
        actions.Children.Add(toggle);

        Add(grid, 5, actions);

        return new Border
        {
            Child = grid,
            BorderBrush = Pal.Stroke,
            BorderThickness = new Thickness(0, 0, 0, 1),
        };
    }

    private static TextBlock PointsText(int value)
    {
        var block = new TextBlock
        {
            Text = value.ToString(),
            FontFamily = (FontFamily)Application.Current.Resources["NumberFont"],
            FontSize = 15,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = value < 0
                ? Pal.Negative
                : Pal.Primary,
        };
        return block;
    }

    private static void Add(Grid grid, int column, FrameworkElement element)
    {
        Grid.SetColumn(element, column);
        grid.Children.Add(element);
    }

    private static Style Styled(string key) => (Style)Application.Current.Resources[key];

    private void OnSearchChanged(object sender, TextChangedEventArgs e) => Render();

    /// <summary>名单页上的座位入口：只在可选功能打开时可见。排完座位回名单，名单照旧按座位排序。</summary>
    private void OnOpenSeats(object sender, RoutedEventArgs e) => Shell?.Navigate(AppNav.Seats);

    // ---------------- 添加 / 编辑 / 停用 ----------------

    private async Task<bool> EditStudentAsync(Student s, bool isNew)
    {
        // 连点两下「新增学生」不该弹两个框，也不该把第二个请求压成"等前一个关掉"。
        // 真正的崩溃在对话框层已经兜住了（Dialogs 排队 + 重试），这一层是顺手治好手感。
        if (_editing) return false;
        _editing = true;
        try
        {
            return await EditStudentCoreAsync(s, isNew);
        }
        finally
        {
            _editing = false;
        }
    }

    private bool _editing;

    private async Task<bool> EditStudentCoreAsync(Student s, bool isNew)
    {
        var nameBox = new TextBox { Header = "姓名", Text = s.Name, Width = 320 };
        var noBox = new TextBox { Header = "学号（可空）", Text = s.StudentNo ?? "", Width = 320 };

        // 座位功能已隐藏（v1.3）：界面不再让人指定排/座，
        // 但库里已有的 seat_no 必须原样保留 —— 它是名单和记分台的排序依据。
        // 所以这里既不读也不写 SeatNo，编辑姓名/学号不会把座位冲掉。
        var panel = new StackPanel { Spacing = 12, Width = 360 };
        panel.Children.Add(nameBox);
        panel.Children.Add(noBox);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = isNew ? "新增学生" : "编辑学生",
            Content = panel,
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await Dialogs.ShowAsync(dialog) != ContentDialogResult.Primary) return false;

        var name = NameNormalizer.Clean(nameBox.Text);
        if (name.Length == 0)
        {
            Shell?.Toast("姓名不能为空");
            return false;
        }

        s.Name = name;
        s.StudentNo = string.IsNullOrWhiteSpace(noBox.Text) ? null : noBox.Text.Trim();
        s.Active = true;

        if (isNew) Services.Students.Insert(s); else Services.Students.Update(s);
        Reload();
        Shell?.Toast(isNew ? $"已添加 {s.Name}" : $"已保存 {s.Name}", 2);
        return true;
    }

    private void ToggleStudent(Student s)
    {
        if (s.Active)
        {
            Services.Students.SoftDelete(s.Id);
            Shell?.Toast($"{s.Name} 已停用，可在「回收站」恢复", 3);
        }
        else
        {
            Services.Students.Restore(s.Id);
            Shell?.Toast($"{s.Name} 已恢复", 3);
        }
        Reload();
    }

    private async void OnAddStudent(object sender, RoutedEventArgs e)
    {
        var nextSeat = (_all.Where(x => !x.IsDeleted).Max(x => x.SeatNo) ?? 0) + 1;
        var draft = new Student { Name = "", SeatNo = nextSeat };
        await EditStudentAsync(draft, isNew: true);
    }

    // ---------------- 粘贴一列姓名 ----------------

    private async void OnPasteNames(object sender, RoutedEventArgs e)
    {
        var box = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 260,
            PlaceholderText = "一行一个姓名，例如：\n王小明\n李思远\n张伟",
            FontFamily = (FontFamily)Application.Current.Resources["TextFont"],
        };
        var panel = new StackPanel { Spacing = 12, Width = 420 };
        panel.Children.Add(box);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "粘贴一列姓名",
            Content = panel,
            PrimaryButtonText = "导入",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await Dialogs.ShowAsync(dialog) != ContentDialogResult.Primary) return;

        var lines = TextLines.Split(box.Text);
        if (lines.Length == 0)
        {
            Shell?.Toast("没有可导入的姓名");
            return;
        }

        // 起始座位不再让用户填：座位不显示了，但内部仍接着往下编号，
        // 保证新加的人排在上一次导入的后面，而不是全落到"未排座"里乱序。
        var added = Services.Students.ImportNames(lines, null);
        Reload();
        Services.Audit.Add("students.import_paste", null, null, $"{added} 人");
        Shell?.Toast(added == 0 ? "全部重名，没有新增" : $"已导入 {added} 人", 3);
    }

    // ---------------- Excel 导入 ----------------

    private async void OnImportExcel(object sender, RoutedEventArgs e)
    {
        try
        {
            var picked = FileDialogs.Open("导入名单", FileFilter.Sheet);
            if (picked is null) return;

            var grid = picked.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)
                ? CsvFile.Read(picked)
                : ExcelReader.Read(picked);

            if (grid.RowCount == 0)
            {
                Shell?.Toast("这个文件里没有读到数据");
                return;
            }

            // 全校成绩大表（两行表头 + 班级列）：先挑班再导入，别把整个年级塞进一个人的班
            if (GradeSheet.TryParse(grid, out var grade) && grade is not null)
            {
                await ImportFromGradeSheetAsync(Path.GetFileName(picked), grade);
                return;
            }

            // 名单只认「姓名」一列，其余列尽量识别；识别不准就让用户自己选
            var options = Enumerable.Range(0, grid.ColumnCount)
                .Select(c => new ColumnOption(c, grid.Cell(0, c), grid.RowCount > 1 ? grid.Cell(1, c) : ""))
                .ToList();

            // ColumnOption 是 record，不设 DisplayMemberPath 时下拉框显示的是
            // "ColumnOption { Index = 0, Header = 姓名, Sample = 王小明 }"，三行都这样。
            var nameCombo = new ComboBox
            {
                Header = "姓名列", ItemsSource = options, Width = 320,
                DisplayMemberPath = nameof(ColumnOption.Display),
            };
            nameCombo.SelectedIndex = options.FindIndex(o => o.Header.Contains("姓名") || o.Header.Contains("学生"));
            if (nameCombo.SelectedIndex < 0) nameCombo.SelectedIndex = 0;

            var noCombo = new ComboBox
            {
                Header = "学号列（可选）", ItemsSource = options, Width = 320,
                DisplayMemberPath = nameof(ColumnOption.Display),
            };
            noCombo.SelectedIndex = options.FindIndex(o => o.Header.Contains("学号") || o.Header.Contains("编号"));

            var preview = new TextBlock { Style = Styled("DeskSecondaryText"), TextWrapping = TextWrapping.Wrap };
            void UpdatePreview()
            {
                var lines = new List<string>();
                for (var r = 1; r < System.Math.Min(grid.RowCount, 6); r++)
                {
                    var n = nameCombo.SelectedItem is ColumnOption c ? grid.Cell(r, c.Index) : "";
                    lines.Add($"· {n}");
                }
                preview.Text = $"共 {grid.RowCount - 1} 行数据。前几行：\n" + string.Join("\n", lines);
            }
            nameCombo.SelectionChanged += (_, _) => UpdatePreview();
            UpdatePreview();

            var hint = new TextBlock
            {
                Style = Styled("DeskSecondaryText"),
                Text = "只按姓名匹配：重名跳过，没有的新增。学号按列自动识别。",
            };

            var panel = new StackPanel { Spacing = 12, Width = 360 };
            panel.Children.Add(new TextBlock { Text = Path.GetFileName(picked), Style = Styled("DeskBodyText") });
            panel.Children.Add(nameCombo);
            panel.Children.Add(noCombo);
            panel.Children.Add(preview);
            panel.Children.Add(hint);

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "导入名单",
                Content = new ScrollViewer { Content = panel, MaxHeight = 520 },
                PrimaryButtonText = "导入",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
            };

            if (await Dialogs.ShowAsync(dialog) != ContentDialogResult.Primary) return;
            if (nameCombo.SelectedItem is not ColumnOption nameCol) return;

            var added = 0;
            var skipped = 0;
            var seat = (_all.Where(x => !x.IsDeleted).Max(x => x.SeatNo) ?? 0);

            for (var r = 1; r < grid.RowCount; r++)
            {
                var name = NameNormalizer.StripListIndex(grid.Cell(r, nameCol.Index));
                if (name.Length == 0) continue;
                if (NameNormalizer.IsLikelySummaryRow(name)) { skipped++; continue; }

                var exists = _all.Any(x => !x.IsDeleted && NameNormalizer.NormalizeKey(x.Name) == NameNormalizer.NormalizeKey(name));
                if (exists) { skipped++; continue; }

                var student = new Student
                {
                    Name = name,
                    StudentNo = noCombo.SelectedItem is ColumnOption noCol && grid.Cell(r, noCol.Index).Length > 0
                        ? grid.Cell(r, noCol.Index).Trim()
                        : null,
                    // 座位功能隐藏后不再从表格里认座位列，只按导入顺序往下编号
                    SeatNo = ++seat,
                };
                Services.Students.Insert(student);
                _all.Add(student);
                added++;
            }

            Reload();
            Services.Audit.Add("students.import_excel", null, Path.GetFileName(picked), $"新增 {added}，跳过 {skipped}");
            Shell?.Toast($"已新增 {added} 人" + (skipped > 0 ? $"，跳过 {skipped} 行（重名/表头）" : ""), 5);
        }
        catch (Exception ex)
        {
            Services.LogError("import-students", ex);
            Shell?.Toast("导入失败：" + ex.Message, 6);
        }
    }

    /// <summary>
    /// 从全校大表里导入一个班：先选班，再预览会新增多少人；另外给一个姓名搜索，
    /// 让老师能把**别的班**的某个学生单独加进来（转班、借读、临时合班都用得上）。
    /// </summary>
    private async Task ImportFromGradeSheetAsync(string fileName, GradeSheet sheet)
    {
        var classBox = new ComboBox
        {
            Header = "班级",
            Width = 200,
            ItemsSource = sheet.Classes,
            SelectedIndex = 0,
        };

        var preview = new TextBlock
        {
            Style = Styled("DeskSecondaryText"),
            TextWrapping = TextWrapping.Wrap,
        };

        var seat = _all.Where(x => !x.IsDeleted).Max(x => x.SeatNo) ?? 0;
        var added = 0;

        void RefreshPreview()
        {
            if (classBox.SelectedItem is not string className) return;
            var rows = sheet.DataRows(className).ToList();
            var have = rows.Count(r =>
                _all.Any(x => !x.IsDeleted &&
                    NameNormalizer.NormalizeKey(x.Name) == NameNormalizer.NormalizeKey(sheet.NameOf(r))));
            preview.Text = $"{className} 班：表里 {rows.Count} 人，其中 {have} 人已在名单里，"
                         + $"导入会新增 {rows.Count - have} 人。本次已另外添加 {added} 人。";
        }

        // ---- 跨班搜索：把「不是这个班」的某个学生单独加进来 ----
        var searchBox = new TextBox
        {
            Header = "加一个人（可以不是上面这个班）",
            PlaceholderText = "输入姓名的一部分，例如：朱",
            Width = 320,
        };
        var hits = new StackPanel { Spacing = 4 };

        void RenderHits()
        {
            hits.Children.Clear();
            var keyword = searchBox.Text.Trim();
            if (keyword.Length == 0) return;

            var found = sheet.Search(keyword, 8).ToList();
            if (found.Count == 0)
            {
                hits.Children.Add(new TextBlock
                {
                    Text = "表里没有这个人",
                    Style = Styled("DeskSecondaryText"),
                });
                return;
            }

            foreach (var row in found)
            {
                var name = sheet.NameOf(row);
                var line = new Grid { ColumnSpacing = 10 };
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                line.Children.Add(new TextBlock { Text = name, Style = Styled("DeskBodyText") });

                var cls = new TextBlock
                {
                    Text = sheet.ClassOf(row) + " 班",
                    Style = Styled("DeskSecondaryText"),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(cls, 1);
                line.Children.Add(cls);

                var add = new Button
                {
                    Content = "加入名单",
                    Style = Styled("DeskButton"),
                    Height = 26,
                    MinWidth = 84,
                    HorizontalAlignment = HorizontalAlignment.Right,
                };
                add.Click += (_, _) =>
                {
                    if (_all.Any(x => !x.IsDeleted &&
                            NameNormalizer.NormalizeKey(x.Name) == NameNormalizer.NormalizeKey(name)))
                    {
                        Shell?.Toast($"{name} 已经在名单里了", 4);
                        return;
                    }
                    var student = Services.Students.Insert(new Student
                    {
                        Name = name,
                        StudentNo = sheet.NoOf(row).Length > 0 ? sheet.NoOf(row) : null,
                        SeatNo = ++seat,
                    });
                    _all.Add(student);
                    added++;
                    RefreshPreview();
                    RenderHits();
                    Shell?.Toast($"已加入 {name}（{sheet.ClassOf(row)} 班）", 4);
                };
                Grid.SetColumn(add, 2);
                line.Children.Add(add);

                hits.Children.Add(line);
            }
        }

        searchBox.TextChanged += (_, _) => RenderHits();
        classBox.SelectionChanged += (_, _) => RefreshPreview();
        RefreshPreview();

        var panel = new StackPanel { Spacing = 12, Width = 460 };
        panel.Children.Add(new TextBlock
        {
            Text = fileName + " · 全校成绩表",
            Style = Styled("DeskBodyText"),
        });
        panel.Children.Add(classBox);
        panel.Children.Add(preview);
        panel.Children.Add(searchBox);
        panel.Children.Add(hits);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "导入名单",
            Content = new ScrollViewer { Content = panel, MaxHeight = 560 },
            PrimaryButtonText = "导入这个班",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await Dialogs.ShowAsync(dialog) != ContentDialogResult.Primary)
        {
            Reload();
            return;
        }
        if (classBox.SelectedItem is not string chosen) return;

        var imported = 0;
        var skipped = 0;
        foreach (var row in sheet.DataRows(chosen))
        {
            var name = NameNormalizer.StripListIndex(sheet.NameOf(row));
            if (name.Length == 0) continue;

            if (_all.Any(x => !x.IsDeleted &&
                    NameNormalizer.NormalizeKey(x.Name) == NameNormalizer.NormalizeKey(name)))
            {
                skipped++;
                continue;
            }

            var student = Services.Students.Insert(new Student
            {
                Name = name,
                StudentNo = sheet.NoOf(row).Length > 0 ? sheet.NoOf(row) : null,
                SeatNo = ++seat,
            });
            _all.Add(student);
            imported++;
        }

        Reload();
        Services.Audit.Add("students.import_grade", null, fileName,
            $"{chosen} 班：新增 {imported}，跳过 {skipped}");
        Shell?.Toast($"已从 {chosen} 班导入 {imported} 人"
                     + (added > 0 ? $"，另外手动加入 {added} 人" : "")
                     + (skipped > 0 ? $"，跳过 {skipped} 人（已在名单里）" : ""), 6);
    }

    // ---------------- 回收站 ----------------

    /// <summary>
    /// 一个学生的全部积分记录（跨周期，最新的在前）。
    ///
    /// 名单页原来是"只有总分"：家长问「这 30 分怎么来的」，班主任得翻明细页
    /// 一条条找。这里直接按人列出来，权限够的话就地能撤。
    /// </summary>
    private async void ShowStudentRecords(Student s)
    {
        var body = new StackPanel { Spacing = 6, Width = 560 };

        void Render()
        {
            body.Children.Clear();

            var term = Services.Entries.CumulativeTotals().GetValueOrDefault(s.Id);
            var periodTotal = Services.Entries.PeriodTotals(Services.CurrentPeriod.Id).GetValueOrDefault(s.Id);
            body.Children.Add(new TextBlock
            {
                Text = $"累计 {term} 分　·　{Services.PeriodName} {periodTotal} 分",
                Style = Styled("DeskBodyText"),
                Margin = new Thickness(0, 0, 0, 4),
            });

            var entries = Services.Entries.EntriesForStudent(s.Id, 500);
            if (entries.Count == 0)
            {
                body.Children.Add(new TextBlock
                {
                    Text = "还没有记录。",
                    Style = Styled("DeskSecondaryText"),
                });
                return;
            }

            foreach (var entry in entries)
                body.Children.Add(BuildRecordRow(entry, Render));
        }

        Render();

        await Dialogs.ShowAsync(new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = s.Name + " · 积分记录",
            Content = new ScrollViewer { Content = body, MaxHeight = 460 },
            CloseButtonText = "关闭",
        });

        Reload();
    }

    private UIElement BuildRecordRow(Entry entry, Action refresh)
    {
        var grid = new Grid { ColumnSpacing = 10, Padding = new Thickness(0, 5, 0, 5) };
        foreach (var w in new[] { 96d, 0d, 96d, 120d, 72d })
            grid.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = w <= 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(w),
            });

        var time = new TextBlock
        {
            Text = Clock.ToLocal(entry.OccurredAt).ToString("MM-dd HH:mm"),
            Style = Styled("DeskSecondaryText"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        grid.Children.Add(time);

        var label = new TextBlock
        {
            Text = entry.RuleLabel ?? "—",
            Style = Styled("DeskBodyText"),
            Opacity = entry.IsReversed ? 0.5 : 1,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);

        var op = new TextBlock
        {
            Text = entry.OperatorName,
            Style = Styled("DeskSecondaryText"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(op, 2);
        grid.Children.Add(op);

        var delta = new TextBlock
        {
            Text = entry.IsReversed ? "已撤销" : entry.DeltaText,
            FontFamily = (FontFamily)Application.Current.Resources["NumberFont"],
            FontSize = 14,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = entry.IsReversed ? Pal.Secondary : entry.Delta >= 0 ? Pal.Positive : Pal.Negative,
        };
        Grid.SetColumn(delta, 3);
        grid.Children.Add(delta);

        if (!entry.IsReversed && !entry.IsReversal)
        {
            var undo = new Button
            {
                Content = "撤销",
                Style = Styled("DeskButton"),
                Height = 26,
                MinWidth = 54,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            undo.Click += async (_, _) =>
            {
                if (await TryReverseAsync(entry)) refresh();
            };
            Grid.SetColumn(undo, 4);
            grid.Children.Add(undo);
        }

        return new Border
        {
            Child = grid,
            BorderBrush = Pal.Stroke,
            BorderThickness = new Thickness(0, 0, 0, 1),
        };
    }

    /// <summary>
    /// 按权限撤销一条：自己刚记的、或管理态/班主任，直接过；
    /// 别人的旧记录要管理密码。门禁统一在 EntryService.CheckReversePermission 里，
    /// 这里只负责把"要去问密码"翻译成一次弹窗。
    /// </summary>
    private async Task<bool> TryReverseAsync(Entry entry)
    {
        var rows = Services.Entries.BatchRows(entry.BatchId);
        var permission = EntryService.CheckReversePermission(
            rows, Services.Operator, Services.Session.IsAdmin, Clock.NowMs());

        if (permission == ReversePermission.Denied)
        {
            Shell?.Toast("没有权限撤销这一条");
            return false;
        }

        if (permission == ReversePermission.NeedsPassword)
        {
            if (Shell is not { } shell) return false;
            if (!await shell.AskPinAsync("撤销记录", $"{entry.StudentName} · {entry.RuleLabel}，需要管理密码", requireManager: true)) return false;
            Services.Session.EnterAdmin();
        }

        try
        {
            Services.Entries.ReverseSingle(entry.Id, Services.Operator, "desk.students");
            Shell?.Toast($"已撤销 {entry.StudentName} 的 {entry.RuleLabel} {entry.DeltaText}", 3);
            return true;
        }
        catch (Exception ex)
        {
            Services.LogError("undo-student-entry", ex);
            Shell?.Toast("撤销失败：" + ex.Message, 5);
            return false;
        }
    }

    private async void OnRecycle(object sender, RoutedEventArgs e)
    {
        var deleted = _all.Where(s => s.IsDeleted).ToList();
        var panel = new StackPanel { Spacing = 8, Width = 460 };

        if (deleted.Count == 0)
        {
            panel.Children.Add(new TextBlock { Text = "回收站是空的。", Style = Styled("DeskSecondaryText") });
        }
        else
        {
            panel.Children.Add(new TextBlock
            {
                Text = "停用的不进榜单、不计入人数，数据保留，随时恢复。",
                Style = Styled("DeskSecondaryText"),
            });

            foreach (var s in deleted)
            {
                var row = new Grid();
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var text = new TextBlock
                {
                    Text = $"{s.Name} · 累计 {_term.GetValueOrDefault(s.Id)}",
                    Style = Styled("DeskBodyText"),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                row.Children.Add(text);

                var restore = new Button { Content = "恢复", Style = Styled("DeskButton"), Height = 28 };
                restore.Click += (_, _) =>
                {
                    Services.Students.Restore(s.Id);

                    // 弹窗是模态的，页面刷了也看不见 —— 直接把这一行从弹窗里去掉，
                    // 否则点完还挂在"回收站"列表里，像是没恢复成功。
                    panel.Children.Remove(row);
                    Reload();
                    Shell?.Toast($"{s.Name} 已恢复", 2);
                };
                Grid.SetColumn(restore, 1);
                row.Children.Add(restore);

                panel.Children.Add(row);
            }
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "回收站",
            Content = new ScrollViewer { Content = panel, MaxHeight = 460 },
            CloseButtonText = "关闭",
        };
        await Dialogs.ShowAsync(dialog);
        Reload();
    }
}
