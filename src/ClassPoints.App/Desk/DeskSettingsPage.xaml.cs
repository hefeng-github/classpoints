using ClassPoints.App.Controls;
using ClassPoints.App.Services;
using ClassPoints.Core;
using ClassPoints.Data;
using ClassPoints.Core.Config;
using ClassPoints.Data.Config;
using ClassPoints.Data.Sync;
using ClassPoints.Data.Update;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using ClassPoints.App.Theme;

namespace ClassPoints.App.Desk;

/// <summary>
/// 电脑端设置：管理类的东西全在这边（大屏上只放本机项）。
/// 这里的每个动作都要求"点一下才生效"，不做自动保存 —— 办公室里改设置的人，
/// 需要的是一个明确的保存动作，而不是"我改了没改"的怀疑。
/// </summary>
public sealed partial class DeskSettingsPage : Page
{

    /// <summary>验收截图时用来压住 SelectorBar 自然触发的那一次重建。</summary>
    private bool _suppressTab;

    public DeskSettingsPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            ApplySnapshotTab();
            Reload();
        };

        // 读卡器那两个订阅和 2 秒一次的灯是挂在服务上的：页面离开后不停，
        // 旧页面就一直被 Reader 的事件钉在内存里（每进一次设置多一份）。
        Unloaded += (_, _) =>
        {
            _lampTimer?.Stop();
            Services.Reader.StatusChanged -= OnReaderStatus;
        };
    }

    private static AppServices Services => App.Services;

    private static IShellHost? Shell => App.Host;

    /// <summary>数据库实际所在的目录。用 --db 指到别处时，它和 Database.RootDir 不是一个地方。</summary>
    private static string DataDir =>
        Path.GetDirectoryName(Services.Db.DbPath) ?? Database.RootDir;

    /// <summary>当前选中的分类。XAML 里给每项写了 Tag，这里只认那个。</summary>
    private string CurrentGroup =>
        (Tabs.SelectedItem as SelectorBarItem)?.Tag as string ?? "common";

    /// <summary>
    /// 换分类：整页重建 + 回到顶部。
    ///
    /// 不缓存各组的控件是有意的 —— 这一页到处是"存下来再回读"的控件（密码、路径、开关），
    /// 缓存起来就得处理"另一个页面改了、这里还是旧值"。重建一次几十毫秒，换来的是
    /// 每次看到的都是库里的真值。
    /// </summary>
    private void OnTabChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        // InitializeComponent 里 IsSelected="True" 会先触发一次，那时页面还没 Loaded
        if (!IsLoaded || _suppressTab) return;

        Reload();
        ContentScroll.ChangeView(null, 0, null);
    }

    /// <summary>
    /// 验收用：CP_SNAP_TAB=scoring / people / sync / data 时默认选中那一组，配合 CP_SNAP 逐组截图。
    /// 和 CP_MODE / CP_FORCE_READONLY 一样只影响这一次运行，正常使用不会读它。
    /// </summary>
    private void ApplySnapshotTab()
    {
        var tag = Environment.GetEnvironmentVariable("CP_SNAP_TAB");
        if (string.IsNullOrWhiteSpace(tag)) return;

        foreach (var item in Tabs.Items.OfType<SelectorBarItem>())
        {
            if (!string.Equals(item.Tag as string, tag.Trim(), StringComparison.OrdinalIgnoreCase)) continue;

            _suppressTab = true;
            Tabs.SelectedItem = item;
            _suppressTab = false;
            return;
        }
    }

    private void Reload()
    {
        try
        {
            SubText.Text = $"{Services.ClassName} · 班级码 {Services.Settings.ClassCode} · 数据在 {DataDir}";

            Sections.Children.Clear();

            // 十六节分五组。每组几百像素，一屏多一点 —— 以前是一整页十几屏，
            // 找「改数据目录」得从最上面一直滚到最下面。
            switch (CurrentGroup)
            {
                case "scoring":
                    BuildFeatureSection();    // 可选功能（座位等）
                    BuildRuleSection();       // 加分与扣分项
                    BuildReaderSection();     // 读卡器
                    break;

                case "people":
                    BuildSecuritySection();   // 密码与恢复码
                    BuildMemberSection();     // 成员与权限
                    BuildSchemeSection();     // 成绩口径
                    break;

                case "sync":
                    BuildSyncSection();
                    BuildUpdateSection();
                    BuildProvisionSection();  // 预配置（云端地址与名单打包）
                    break;

                case "data":
                    BuildBackupSection();
                    BuildLocalDataSection();
                    BuildAboutSection();      // 其它：日志、库结构
                    break;

                default:
                    BuildClassSection();
                    BuildThisMachineSection();
                    BuildAppearanceSection();
                    BuildOnScreenSection();
                    break;
            }
        }
        catch (Exception ex)
        {
            Services.LogError("desk-settings", ex);
            Shell?.Toast("设置页读取失败：" + ex.Message, 6);
        }
    }

    // ---------------- 班级 ----------------

    private void BuildClassSection()
    {
        var body = Section("班级信息", "班级名显示在大屏顶部。");

        var nameBox = new TextBox { Text = Services.Settings.ClassName, Width = 300 };
        var save = Small("保存班级名称", () =>
        {
            var name = NameNormalizer.Clean(nameBox.Text);
            if (name.Length == 0)
            {
                Shell?.Toast("班级名不能空");
                return;
            }
            Services.Settings.ClassName = name;
            Services.Audit.Add("class.rename", null, null, name);
            Shell?.Toast("已保存", 2);
            Reload();
        });

        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        line.Children.Add(nameBox);
        line.Children.Add(save);
        body.Children.Add(Labelled("班级名称", line));

        body.Children.Add(ValueRow("班级码", Services.Settings.ClassCode,
            Services.Sync.IsConfigured
                ? "云端认的就是这个码，另一台设备用它配对。"
                : "另一台设备配对时要用它。还没连云端，所以它只是本机的标识 —— 建班或配对之后会换成云端发的那个。"));

        body.Children.Add(ValueRow("数据文件", Services.Db.DbPath,
            "名单、积分、成绩都在这一个文件里。备份用下面的「立即备份」，不要手动复制正在写的库。"));

        // 座位布局不在这里：打开「可选功能 → 座位表」之后，布局在座位页自己的
        // 「教室布局」里改。设置页放两个数字输入框，只会让人以为座位是必填项。
        // seat_rows / seat_cols 两个键仍然保留 —— 记分台一屏放几列读的就是它们。

        // 示例数据是向导里明确勾选才写进来的，所以也要能一键清掉。
        if (Services.Settings.DemoData)
        {
            var demos = Services.Students.ListActive().Count(s => s.Name.StartsWith("学生 ", StringComparison.Ordinal));
            var clear = Small($"清除示例数据（{demos} 人）", async () =>
            {
                if (Shell is not { } shell) return;
                var ok = await shell.ConfirmAsync("清除示例数据",
                    $"会停用 {demos} 个以「学生 XX」命名的演示条目。真实名单不受影响，记录也不会被删。",
                    "清除", "算了");
                if (!ok) return;

                foreach (var s in Services.Students.ListActive()
                             .Where(s => s.Name.StartsWith("学生 ", StringComparison.Ordinal)).ToList())
                    Services.Students.SoftDelete(s.Id);

                Services.Settings.DemoData = false;
                shell.Toast("示例数据已清除");
                Reload();
            });
            body.Children.Add(Labelled("示例数据", Wrap(clear)));
        }
    }

    // ---------------- 这台电脑 ----------------

    private void BuildThisMachineSection()
    {
        var body = Section("这台电脑", "一个程序两种排版，换形态不动数据。");

        // 当前是哪一种，看选中的那一段就够了。
        var modes = new[] { AppMode.Board, AppMode.Desk };
        var modeBar = Selector(
            modes.Select(m => m.ToDisplay()).ToArray(),
            Array.IndexOf(modes, App.Mode),
            i => SwitchMode(modes[i]));
        body.Children.Add(LabelledHint("界面形态", App.Mode.ToHint(), modeBar));

        var footer = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        footer.Children.Add(Small("重新运行首次配置…", async () =>
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "重新运行首次配置",
                Content = "会重新问形态、班级名、配置包。名单和积分不受影响，但当前页面会切回向导。",
                PrimaryButtonText = "开始",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await Dialogs.ShowAsync(dialog) != ContentDialogResult.Primary) return;
            App.Window?.RunWizard();
        }));
        body.Children.Add(footer);
    }

    private void SwitchMode(AppMode mode)
    {
        if (mode == App.Mode) return;

        Services.Audit.Add("app.mode", null, null, App.Mode.ToDb() + " → " + mode.ToDb());
        App.Window?.SwitchToMode(mode);
    }

    // ---------------- 可选功能 ----------------

    /// <summary>
    /// 可选功能：默认全关。
    ///
    /// 关着的时候这一节里只有一行开关，没有说明、没有"去看看"按钮，程序别处也
    /// 不会出现任何相关的字样（导航里没有那一项、名单没有那一列）。打开之后才
    /// 长出使用说明和入口 —— 用不上的人不该被一个功能的名词反复打扰。
    /// </summary>
    private void BuildFeatureSection()
    {
        var body = Section("可选功能", "默认关闭，打开后才会出现在导航里。");

        var seats = new ToggleSwitch
        {
            IsOn = Services.Settings.SeatEnabled,
            OnContent = "开",
            OffContent = "关",
            MinWidth = 100,
            VerticalAlignment = VerticalAlignment.Center,
        };

        seats.Toggled += (_, _) =>
        {
            var on = seats.IsOn;
            if (Services.Settings.SeatEnabled == on) return;

            Services.Settings.SeatEnabled = on;
            Services.Audit.Add("seat.toggle", null, null, on ? "on" : "off");

            // 导航项要跟着变：打开后侧栏立刻多一格，关掉后整格消失。
            Shell?.RebuildNavigation();
            Shell?.Toast(on ? "座位表已打开：侧栏多了一项「座位」" : "座位表已关闭：界面不再显示它，座位数据保留", 4);

            // 重建这一节，让「去排座位」和说明跟着开关出现 / 消失。
            DispatcherQueue.TryEnqueue(Reload);
        };

        body.Children.Add(Labelled("座位表", seats));

        if (!Services.Settings.SeatEnabled) return;

        var layout = Services.Settings.SeatLayout;
        body.Children.Add(Note($"打开后侧栏多一页「座位」：按教室布局（当前 {layout.Rows} 排 × {layout.Groups} 组 × 每组 {layout.PerGroup} 座）" +
                               "排座，点两个座位交换；自动排座可设前排/后排、谁和谁同桌，排完能存快照、导出 Excel/SVG。" +
                               "关闭后数据保留，不会被清掉。"));

        var go = Small("去排座位", () => Shell?.Navigate(AppNav.Seats));
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 4, 0, 4) };
        actions.Children.Add(go);
        body.Children.Add(actions);
    }

    // ---------------- 外观 ----------------

    private void BuildAppearanceSection()
    {
        var body = Section("外观", "浅色为默认，投影更清楚。切换立即生效。");

        (AppTheme Theme, string Label)[] choices =
        [
            (AppTheme.Light, "浅色"),
            (AppTheme.Dark, "深色"),
            (AppTheme.System, "跟随系统"),
        ];

        var bar = Selector(
            choices.Select(c => c.Label).ToArray(),
            Array.FindIndex(choices, c => c.Theme == Services.Settings.Theme),
            i =>
            {
                App.ApplyTheme(choices[i].Theme);

                // 主题一换，整页的 ThemeResource 都变；重画放到下一个消息循环，
                // 别在 SelectorBar 自己的事件里把它的父节点换掉。
                DispatcherQueue.TryEnqueue(Reload);
            });

        body.Children.Add(Labelled("配色", bar));
    }

    // ---------------- 计时与音效 ----------------

    /// <summary>
    /// 这些是"这台机器怎么表现"，不是"班级怎么算"。
    ///
    /// 以前它们只在大屏设置里，电脑端看不到 —— 而两端功能一样是这一版的前提，
    /// 于是搬到这里：一台机器改了不影响别的机器，大屏和电脑各有一份。
    /// </summary>
    private void BuildOnScreenSection()
    {
        var s = Services.Settings;
        var body = Section("计时与音效", "只影响这台电脑。");

        body.Children.Add(NumberField("空闲回看板", "有选中学生时不计时", 10, 600, 10,
            () => s.IdleTimeoutSec, v => s.IdleTimeoutSec = (int)v, " 秒"));
        body.Children.Add(NumberField("管理模式锁定", "超时只退出管理态", 60, 3600, 30,
            () => s.LockTimeoutSec, v => s.LockTimeoutSec = (int)v, " 秒"));
        body.Children.Add(NumberField("查分页超时", "到时回看板并清屏", 10, 300, 10,
            () => s.LookupTimeoutSec, v => s.LookupTimeoutSec = (int)v, " 秒"));

        var sound = new ToggleSwitch
        {
            IsOn = s.SoundOn,
            OnContent = "开",
            OffContent = "关",
            MinWidth = 100,
            VerticalAlignment = VerticalAlignment.Center,
        };
        sound.Toggled += (_, _) =>
        {
            s.SoundOn = sound.IsOn;
            Sounds.Enabled = sound.IsOn;
            if (sound.IsOn) Sounds.Play(Cue.Ding);
        };
        body.Children.Add(Labelled("音效", sound));
    }

    /// <summary>
    /// 数字输入框：TextBox + 加减两个按钮。
    ///
    /// 为什么不用原生 NumberBox：它要等"焦点离开且校验通过"才把文本写进 Value，
    /// 而这里的数字没有保存按钮 —— 老师把 30 改成 600 之后直接去点别的区块，
    /// 界面显示 600、库里还是 30，重启又弹回 30。看起来像"改了不生效"。
    ///
    /// 这里改成"离开输入框或按回车就当场落库"，并且把落库后的真实值写回框里：
    /// 越界（比如把 600 打到 6000）会立刻看到它被夹回上限，而不是表面接受了、实际没用。
    /// </summary>
    private UIElement NumberField(string label, string hint, double min, double max, double step,
        Func<double> get, Action<double> set, string unit)
    {
        var box = new TextBox
        {
            Text = ((int)get()).ToString(),
            Width = 96,
            Height = 34,
            MinWidth = 96,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var committed = get();

        void Commit()
        {
            var raw = box.Text.Trim();
            if (!double.TryParse(raw, out var v) || double.IsNaN(v))
            {
                // 乱输入（空、字母、"6OO"）不写库，框里退回真正生效的值
                box.Text = ((int)committed).ToString();
                return;
            }

            v = System.Math.Clamp(System.Math.Round(v), min, max);
            set(v);
            committed = v;
            var text = ((int)v).ToString();
            if (box.Text != text) box.Text = text;
        }

        box.LostFocus += (_, _) => Commit();
        box.KeyDown += (_, e) =>
        {
            if (e.Key != Windows.System.VirtualKey.Enter) return;
            Commit();
            e.Handled = true;
        };

        var minus = Small("\u2212", () =>
        {
            Commit();
            var next = System.Math.Clamp(committed - step, min, max);
            set(next);
            committed = next;
            box.Text = ((int)next).ToString();
        }, width: 38);
        var plus = Small("+", () =>
        {
            Commit();
            var next = System.Math.Clamp(committed + step, min, max);
            set(next);
            committed = next;
            box.Text = ((int)next).ToString();
        }, width: 38);

        var line = Wrap(minus, box, plus, ValueText(unit));
        return LabelledHint(label, hint, line);
    }

    private UIElement LabelledHint(string label, string hint, UIElement control)
    {
        var panel = new StackPanel
        {
            Spacing = 4,
            Padding = new Thickness(0, 6, 0, 6),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        panel.Children.Add(new TextBlock { Text = label, Style = Styled("DeskColumnHeader") });
        panel.Children.Add(new TextBlock { Text = hint, Style = Styled("DeskSecondaryText") });
        panel.Children.Add(control);
        return panel;
    }

    // ---------------- 读卡器 ----------------

    private Grid? _lampGrid;
    private TextBlock? _readerStatusText;
    private TextBlock? _readerRawText;
    private ComboBox? _portCombo;
    private DispatcherTimer? _lampTimer;

    /// <summary>
    /// 读卡器自检。原来只在大屏设置里有 —— 而现在两端共用这一个设置页，
    /// 老师在哪台机器上插的读卡器就在哪台机器上配。
    /// </summary>
    private void BuildReaderSection()
    {
        var body = Section("读卡器", "串口读卡器。PN532 模块和「刷卡自己吐卡号」的一体式读卡器都支持，类型选错就一句不回。");

        _lampGrid = new Grid { ColumnSpacing = 18, Margin = new Thickness(0, 4, 0, 0) };
        for (var i = 0; i < 3; i++)
            _lampGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // 握手失败时的说明会说出具体该查哪一根线，一行放不下 —— 必须允许折行
        _readerStatusText = new TextBlock
        {
            Style = Styled("DeskSecondaryText"),
            TextWrapping = TextWrapping.Wrap,
        };

        // 原始字节。只在握手没成功过时才出现 ——
        // 老师报"读卡器连不上"时，这一行能直接说明是波特率跑了、帧头差一位、还是根本没数据，
        // 比"收到 38 个字节"有用得多，也不用再让对方开命令行写探针。
        _readerRawText = new TextBlock
        {
            Style = Styled("DeskSecondaryText"),
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            FontSize = 12,
            Visibility = Visibility.Collapsed,
        };

        _portCombo = new ComboBox { MinWidth = 260, Height = 34 };

        // 第一项是"未连接"。以前列表直接选中第一个真实端口，而读卡器其实没启动，
        // 看起来像已经在用 COM1 了；而且办公室电脑的 COM1 往往是主板串口。
        _portCombo.Items.Add(new ComboBoxItem { Content = "未连接", Tag = "" });
        foreach (var port in Pn532Serial.ListPorts())
            _portCombo.Items.Add(new ComboBoxItem { Content = port.Display, Tag = port.PortName });

        if (_portCombo.Items.Count == 1)
            _portCombo.Items.Add(new ComboBoxItem
            {
                // "没有发现"和"发现了但驱动没装"是两种病，前者等插设备，
                // 后者得装驱动 —— 下拉里就把话说准，别让老师对着空列表猜。
                Content = ReaderDriver.NeedsInstall ? "发现 CH340 设备，但驱动未安装" : "没有发现串口设备",
                Tag = "",
                IsEnabled = false,
            });

        var saved = Services.Settings.ReaderPort;
        for (var i = 0; i < _portCombo.Items.Count; i++)
            if (_portCombo.Items[i] is ComboBoxItem item && (string?)item.Tag == saved)
                _portCombo.SelectedIndex = i;
        if (_portCombo.SelectedIndex < 0) _portCombo.SelectedIndex = 0;

        _portCombo.SelectionChanged += (_, _) =>
        {
            if (_portCombo?.SelectedItem is not ComboBoxItem item) return;
            var port = (string?)item.Tag ?? "";
            Services.Settings.ReaderPort = port;
            Services.Reader.Stop();

            // 选"未连接"就只是停下，不再去探一遍 COM1
            if (!string.IsNullOrEmpty(port)) Services.Reader.Start(port, Services.Settings.ReaderBaud, Services.Settings.ReaderProtocol);
            RefreshLamps();
        };

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Bottom };
        actions.Children.Add(Small("重新连接", () =>
        {
            var port = Services.Settings.ReaderPort;
            if (string.IsNullOrWhiteSpace(port))
            {
                Shell?.Toast("先在左边选一个端口，再点重新连接");
                return;
            }

            Services.Reader.Stop();
            Services.Reader.Start(port, Services.Settings.ReaderBaud, Services.Settings.ReaderProtocol);
            RefreshLamps();
        }));
        actions.Children.Add(Small("连不上怎么办", () => ShowReaderHelp()));

        // 读卡器类型：市面上两种设备外观几乎一样、协议完全不同，选错的表现
        // 跟"读卡器坏了"一模一样（串口通了、一个字节都不回），所以这一项必须能手动切。
        var kindCombo = new ComboBox { MinWidth = 300, Height = 34 };
        kindCombo.Items.Add(new ComboBoxItem
        {
            Content = "PN532 模块（要发命令才有回应）",
            Tag = ReaderProtocol.Pn532,
        });
        kindCombo.Items.Add(new ComboBoxItem
        {
            Content = "自动上报型（刷卡时自己吐卡号）",
            Tag = ReaderProtocol.AutoUid,
        });
        kindCombo.SelectedIndex = Services.Settings.ReaderProtocol == ReaderProtocol.AutoUid ? 1 : 0;
        kindCombo.SelectionChanged += (_, _) =>
        {
            if (kindCombo.SelectedItem is not ComboBoxItem item || item.Tag is not ReaderProtocol protocol) return;

            Services.Settings.ReaderProtocol = protocol;
            var port = Services.Settings.ReaderPort;
            Services.Reader.Stop();
            if (!string.IsNullOrEmpty(port)) Services.Reader.Start(port, Services.Settings.ReaderBaud, protocol);
            RefreshLamps();
        };

        var portLine = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        portLine.Children.Add(_portCombo);
        portLine.Children.Add(actions);

        body.Children.Add(Labelled("读卡器类型", kindCombo));
        body.Children.Add(Labelled("端口", portLine));

        // 驱动没装的机器：设备插着、串口列表却是空的，点多少次"重新连接"都没用。
        // 把修复按钮放在问题出现的地方，比让老师去设备管理器里认黄色感叹号近得多。
        var realPorts = _portCombo.Items.OfType<ComboBoxItem>()
            .Count(i => i.IsEnabled && (string?)i.Tag is { Length: > 0 });
        if (realPorts == 0 && ReaderDriver.NeedsInstall)
        {
            var fix = ReaderDriver.Bundled
                ? Small("安装 CH340 驱动…", () => _ = InstallDriverAsync())
                : null;
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            if (fix is not null) row.Children.Add(fix);
            body.Children.Add(Labelled("USB 驱动", row));
            body.Children.Add(Note(ReaderDriver.Bundled
                ? "发现 CH340 串口设备，但驱动没有安装。点上面的按钮，在弹出的授权窗口里点「是」。"
                : "发现 CH340 串口设备，但驱动没有安装（这个包里没带驱动文件）。到 wch.cn 下载 CH341SER 装一次即可。"));
        }

        body.Children.Add(_lampGrid);
        body.Children.Add(_readerStatusText);
        body.Children.Add(_readerRawText);
        body.Children.Add(Note("设备（串口已打开）· 帧（通信正常）· 卡（读到过卡），三个都亮才可用。"));

        Services.Reader.StatusChanged -= OnReaderStatus;
        Services.Reader.StatusChanged += OnReaderStatus;

        _lampTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _lampTimer.Tick -= OnLampTick;
        _lampTimer.Tick += OnLampTick;
        _lampTimer.Start();

        RefreshLamps();
    }

    private void OnLampTick(object? sender, object e) => RefreshLamps();

    private async Task InstallDriverAsync()
    {
        Shell?.Toast("正在安装驱动，弹出授权窗口时请点「是」", 8);
        var ok = await ReaderDriver.InstallAsync();
        Shell?.Toast(ok
            ? "驱动装好了。下面选上新出现的端口，点「重新连接」。"
            : "没有装成：授权窗口被取消，或系统拒绝了安装。可以再试一次。", 8);
        Reload();
    }

    /// <summary>
    /// 连不上时的检查清单。
    ///
    /// 写在对话框里而不是堆在设置页：真正需要看它的人一天只有一次，
    /// 而把它铺在页面上，每天都要看的人（其实没事的）会多读六行。
    /// </summary>
    private void ShowReaderHelp()
    {
        var lines = new[]
        {
            ("模块上的拨码/跳线", "要拨到 UART（有的板子印 HSU）。停在 I2C / SPI 档时，串口一个字节都收不到。"),
            ("TX / RX 要交叉", "转接板的 TX 接模块的 RX，RX 接模块的 TX。接成 TX-TX 是最常见的一种错。"),
            ("GND 必须共地", "转接板和模块之间只接两根数据线、不接地，偶尔能通，但很快就不稳定了。"),
            ("供电电压", "PN532 模块是 3.3V。5V 会烧；有些板子标了 VCC 但内部没做降压。"),
            ("波特率不用管", "程序会自己从 115200 一路试到 9600，模块被 Arduino 例程改过速率也能连上。"),
            ("串口别被占用", "Arduino IDE 的串口监视器、厂商工具、另一个本程序实例都会把 COM 口锁住。"),
            ("换一根 USB 线", "有些线只有电源没有数据 —— 设备管理器里能看到串口，但它连的是别的口。"),
        };

        var panel = new StackPanel { Spacing = 10, Width = 520 };
        foreach (var (title, detail) in lines)
        {
            var item = new StackPanel { Spacing = 2 };
            item.Children.Add(new TextBlock { Text = title, Style = Styled("DeskBodyText") });
            item.Children.Add(new TextBlock
            {
                Text = detail,
                Style = Styled("DeskSecondaryText"),
                TextWrapping = TextWrapping.Wrap,
            });
            panel.Children.Add(item);
        }

        panel.Children.Add(new TextBlock
        {
            Text = "三个灯的含义：设备 = 串口打开了；帧 = 模块回了话（握手成功）；卡 = 读到过卡。",
            Style = Styled("DeskSecondaryText"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
        });

        _ = Dialogs.ShowAsync(new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "读卡器连不上时按顺序看",
            Content = new ScrollViewer { Content = panel, MaxHeight = 520 },
            CloseButtonText = "知道了",
        });
    }

    private void OnReaderStatus(ReaderStatus status) => DispatcherQueue.TryEnqueue(RefreshLamps);

    private void RefreshLamps()
    {
        if (_lampGrid is null) return;

        var status = Services.Reader.Status;
        var lamps = status.Lamps;
        _lampGrid.Children.Clear();

        (string Label, bool On)[] data =
        [
            ("设备", lamps.Device),
            ("帧", lamps.Frame),
            ("卡", lamps.Card),
        ];

        for (var i = 0; i < data.Length; i++)
        {
            var stack = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center };
            stack.Children.Add(new Border
            {
                Width = 16,
                Height = 16,
                CornerRadius = new CornerRadius(8),
                Background = data[i].On ? Pal.Positive : Pal.StrokeStrong,
                HorizontalAlignment = HorizontalAlignment.Center,
            });
            stack.Children.Add(new TextBlock
            {
                Text = data[i].Label,
                Style = Styled("DeskSecondaryText"),
                HorizontalAlignment = HorizontalAlignment.Center,
            });
            Grid.SetColumn(stack, i);
            _lampGrid.Children.Add(stack);
        }

        if (_readerStatusText is null) return;

        _readerStatusText.Text = status.State switch
        {
            ReaderState.Running => $"运行中 · {status.Port} · {status.Message}",
            ReaderState.NoDevice => "没找到读卡器。" + status.Message,
            ReaderState.Error => "出错了：" + status.Message,
            ReaderState.Opening => "正在打开端口…",
            _ => string.IsNullOrWhiteSpace(Services.Settings.ReaderPort)
                ? "未连接。选一个端口后点「重新连接」。"
                : "已停止。点「重新连接」继续。",
        };
        _readerStatusText.Foreground = status.State switch
        {
            ReaderState.Running => Pal.Positive,
            ReaderState.Error => Pal.Negative,
            ReaderState.NoDevice => Pal.Warn,
            _ => Pal.Secondary,
        };

        // 通了就不再显示这行乱码 —— 它只在"没通、需要人判断"的时候有价值
        if (_readerRawText is not null)
        {
            var show = status.Raw.Length > 0 && status.State != ReaderState.Running;
            _readerRawText.Text = show ? "最近收到：" + status.Raw : "";
            _readerRawText.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    // ---------------- 密码与恢复码 ----------------

    private void BuildSecuritySection()
    {
        var body = Section("密码与恢复码", "输谁的密码，就按谁的角色给权限。");

        var owner = Services.Members.Owner();

        var setPin = Small(owner?.HasPin == true ? "改密码" : "设置密码", async () =>
        {
            if (Shell is not { } shell) return;
            var first = await shell.AskDigitsAsync("设置管理密码", "6 位数字，班主任和班长共用这一道");
            if (first is null) return;
            var second = await shell.AskDigitsAsync("再输一次", "两次要一样");
            if (second is null) return;
            if (first != second)
            {
                shell.Toast("两次输入不一样，没改");
                return;
            }

            var member = Services.Members.Owner() ?? new Member { DisplayName = "班主任", Role = MemberRole.Owner };
            Services.Members.Save(member, first);
            Services.Audit.Add("member.pin", member.Id, null, member.DisplayName);
            shell.Toast("管理密码已更新");
            Reload();
        });

        body.Children.Add(Labelled("管理模式密码",
            Wrap(setPin, ValueText(owner?.HasPin == true ? "已设置" : "未设置"))));
        body.Children.Add(Note("电脑端已经认出班主任/班长就不再问；大屏每次进管理模式都要输。"));

        // 恢复码的明文只在"生成的那一次"存在，所以：
        // ① 生成后必须能立刻存下来（教室里找不到笔的那次就白生成了）；
        // ② 重开设置页时不能假装还有一串码 —— 以前固定写"还没有生成"，
        //    已经生成过的机器看上去像从来没设过。
        var codeText = new TextBlock
        {
            Text = Services.Settings.HasRecovery ? "已生成，明文不再显示" : "还没有生成",
            FontFamily = (FontFamily)Application.Current.Resources["NumberFont"],
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Foreground = Services.Settings.HasRecovery ? Pal.Secondary : Pal.Warn,
            IsTextSelectionEnabled = true,
            VerticalAlignment = VerticalAlignment.Center,
        };

        // 这一次会话里生成出来的明文。为空就表示"看不到"，不能拿界面上的提示文字去存文件。
        string? plainCode = null;

        var generate = Small(Services.Settings.HasRecovery ? "重新生成" : "生成恢复码", async () =>
        {
            if (Shell is not { } shell) return;

            // 生成恢复码要先证明自己是班主任。还没设密码时，校验只会回一句"密码不对"，
            // 这里直接说清楚缺的是哪一步。
            if (Services.Members.Owner()?.HasPin != true)
            {
                shell.Toast("先在上面设置管理密码，再生成恢复码");
                return;
            }

            if (!await shell.AskPinAsync("生成恢复码", "需要管理密码", requireManager: true)) return;

            var code = RecoveryCode.Generate();
            Services.Settings.RecoveryCodeHash = PasswordHasher.Hash(RecoveryCode.Normalize(code));
            plainCode = code;
            codeText.Text = code;
            codeText.Foreground = Pal.Primary;
            Services.Audit.Add("recovery.generate", Services.Session.Current?.Id, null, null);
            shell.Toast("已生成。现在就存一份，关掉就看不到了。", 8);
        });

        var saveTxt = Small("保存为 txt", () =>
        {
            if (plainCode is not { } code)
            {
                Shell?.Toast("明文只在生成时可见。要存文件，先重新生成一张");
                return;
            }

            try
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "班级积分");
                Directory.CreateDirectory(dir);
                var file = Path.Combine(dir, $"恢复码-{DateTime.Now:yyyyMMdd-HHmm}.txt");
                File.WriteAllText(file, $"""
                    班级积分 · 恢复码
                    生成时间：{DateTime.Now:yyyy-MM-dd HH:mm}
                    班级：{Services.ClassName}

                    {code}

                    用途：忘记管理密码、或换一台电脑接管这个班时，用它重置。
                    请抄一份纸质留存，并把这份文件放在不会被学生看到的地方。
                    """);
                Shell?.Toast("已保存到 " + file, 8);
            }
            catch (Exception ex)
            {
                Services.LogError("save-recovery", ex);
                Shell?.Toast("保存失败：" + ex.Message, 6);
            }
        });

        body.Children.Add(Labelled("恢复码", Wrap(codeText, generate, saveTxt)));
        body.Children.Add(Note("明文只显示一次，库里只存哈希。忘了密码只能靠它或本机重置。"));
    }

    /// <summary>几个控件一个标签摆一行，省得每次手写 StackPanel。</summary>
    private static StackPanel Wrap(params UIElement[] children)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };
        foreach (var child in children) panel.Children.Add(child);
        return panel;
    }

    private static TextBlock ValueText(string text) => new()
    {
        Text = text,
        Style = (Style)Application.Current.Resources["DeskBodyText"],
        VerticalAlignment = VerticalAlignment.Center,
        MinWidth = 64,
    };

    // ---------------- 班委 ----------------

    private void BuildMemberSection()
    {
        var body = Section("成员与权限", "班主任与班长可管理，班委只能记分。密码与卡都可用于认人。");

        var members = Services.Members.ListAll(includeRevoked: false);
        if (members.Count == 0)
            body.Children.Add(Note("还没有班委。加一位再发卡。"));

        foreach (var m in members)
        {
            var cards = Services.Members.Identities(m.Id);
            var row = new Grid { ColumnSpacing = 10, Padding = new Thickness(0, 6, 0, 6) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var info = new StackPanel { Spacing = 1 };
            info.Children.Add(new TextBlock
            {
                Text = m.DisplayName + "　" + RoleName(m.Role),
                Style = Styled("DeskBodyText"),
            });
            info.Children.Add(new TextBlock
            {
                Text = cards.Count == 0
                    ? "没有卡 · 只能用密码"
                    : $"{cards.Count} 张卡 · " + string.Join("、", cards.Select(c => c.Label ?? "未命名卡")),
                Style = Styled("DeskSecondaryText"),
            });
            row.Children.Add(info);

            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                VerticalAlignment = VerticalAlignment.Center,
            };
            actions.Children.Add(Small("发卡", () => IssueCard(m)));
            actions.Children.Add(Small("设密码", async () => await SetMemberPin(m)));
            if (cards.Count > 0)
                actions.Children.Add(Small("注销卡", () => RevokeCards(m)));
            actions.Children.Add(Small("停用", () => RevokeMember(m)));

            Grid.SetColumn(actions, 1);
            row.Children.Add(actions);

            body.Children.Add(row);
        }

        body.Children.Add(Small("新增班委", AddMember));
    }

    private static string RoleName(MemberRole role) => role.ToDisplay();

    private async void AddMember()
    {
        var nameBox = new TextBox { Header = "姓名", Width = 300 };
        var roleCombo = new ComboBox { Header = "角色", Width = 300 };
        roleCombo.Items.Add("班委");
        roleCombo.Items.Add("班长");
        roleCombo.SelectedIndex = 0;

        var pinBox = new TextBox { Header = "6 位数字密码（可空：只刷卡）", Width = 300, MaxLength = 6 };

        var panel = new StackPanel { Spacing = 10, Width = 320 };
        panel.Children.Add(nameBox);
        panel.Children.Add(roleCombo);
        panel.Children.Add(pinBox);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "新增班委",
            Content = panel,
            PrimaryButtonText = "添加",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await Dialogs.ShowAsync(dialog) != ContentDialogResult.Primary) return;

        var name = NameNormalizer.Clean(nameBox.Text);
        if (name.Length == 0)
        {
            Shell?.Toast("姓名不能为空");
            return;
        }

        var pin = pinBox.Text.Trim();
        if (pin.Length > 0 && (pin.Length != 6 || !pin.All(char.IsDigit)))
        {
            Shell?.Toast("密码必须是 6 位数字");
            return;
        }

        Services.Members.Save(new Member
        {
            DisplayName = name,
            Role = roleCombo.SelectedIndex == 1 ? MemberRole.CoOwner : MemberRole.Officer,
        }, pin.Length == 0 ? null : pin);

        Services.Audit.Add("member.add", null, null, name);
        Shell?.Toast($"已添加 {name}", 2);
        Reload();
    }

    /// <summary>给某个人单独设 6 位密码。输谁的密码，就按谁的角色给权限。</summary>
    private async Task SetMemberPin(Member m)
    {
        if (Shell is not { } shell) return;

        var first = await shell.AskDigitsAsync($"给 {m.DisplayName} 设密码", "6 位数字，只在这一台和配对过的机器上有效");
        if (first is null) return;
        var second = await shell.AskDigitsAsync("再输一次", "两次要一样");
        if (second is null) return;

        if (first != second)
        {
            shell.Toast("两次输入不一样，没改");
            return;
        }

        Services.Members.Save(m, first);
        Services.Audit.Add("member.pin", m.Id, null, m.DisplayName);
        shell.Toast($"{m.DisplayName} 的密码已更新");
        Reload();
    }

    private async void IssueCard(Member m)
    {
        var uidBox = new TextBox
        {
            Header = "卡号（UID）",
            PlaceholderText = "10~14 位十六进制，例如 04A2B3C4D5E680",
            Width = 340,
        };
        var labelBox = new TextBox { Header = "备注（可空）", Text = "校园卡", Width = 340 };

        var status = new TextBlock
        {
            Text = "把卡放到读卡器上，卡号会自动填入。",
            Style = Styled("DeskSecondaryText"),
            Foreground = Pal.Accent,
        };

        var panel = new StackPanel { Spacing = 10, Width = 360 };
        panel.Children.Add(new TextBlock
        {
            Text = "· 把卡放到读卡器上，卡号自动填入（读卡器需已连接）。\n" +
                   "· 没有读卡器就手输 UID：手机 NFC 工具读，或抄卡面丝印。",
            Style = Styled("DeskSecondaryText"),
        });
        panel.Children.Add(status);
        panel.Children.Add(uidBox);
        panel.Children.Add(labelBox);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"给 {m.DisplayName} 发卡",
            Content = panel,
            PrimaryButtonText = "登记",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        // 对话框开着的时候监听刷卡：这是「发卡」最顺手的姿势 ——
        // 班主任手里拿着学生那张卡，点一下、放上去，卡号就填好了，不用手抄十六进制。
        void OnCard(CardRead read) => DispatcherQueue.TryEnqueue(() =>
        {
            uidBox.Text = read.Uid;
            status.Text = $"已读到卡：{read.ShortUid}";
        });

        Services.Reader.CardDetected += OnCard;
        ContentDialogResult result;
        try
        {
            result = await Dialogs.ShowAsync(dialog);
        }
        finally
        {
            Services.Reader.CardDetected -= OnCard;
        }

        if (result != ContentDialogResult.Primary) return;

        try
        {
            var id = Services.Members.AddCard(m.Id, uidBox.Text.Trim(), labelBox.Text.Trim());
            Services.Audit.Add("member.card_add", null, id, m.DisplayName);
            var uid = Crypto.NormalizeUid(uidBox.Text);
            var tail = uid.Length >= 4 ? uid[^4..] : uid;
            Shell?.Toast($"已登记，卡号尾号 {tail}", 4);
            Reload();
        }
        catch (Exception ex)
        {
            Services.LogError("issue-card", ex);
            Shell?.Toast("登记失败：" + ex.Message, 5);
        }
    }

    private async void RevokeCards(Member m)
    {
        var cards = Services.Members.Identities(m.Id);
        var panel = new StackPanel { Spacing = 8, Width = 380 };
        panel.Children.Add(new TextBlock
        {
            Text = "注销后这张卡立刻失效。数据不受影响。",
            Style = Styled("DeskSecondaryText"),
        });

        foreach (var card in cards)
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            row.Children.Add(new TextBlock
            {
                Text = $"{card.Label ?? "未命名卡"} · 登记于 {Clock.LocalDate(card.CreatedAt):MM-dd}",
                Style = Styled("DeskBodyText"),
                VerticalAlignment = VerticalAlignment.Center,
            });

            var button = Small("注销", () =>
            {
                Services.Members.RevokeCard(card.Id);
                Services.Audit.Add("member.card_revoke", null, card.Id, m.DisplayName);
                Shell?.Toast("已注销这张卡", 3);
            });
            Grid.SetColumn(button, 1);
            row.Children.Add(button);

            panel.Children.Add(row);
        }

        await Dialogs.ShowAsync(new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"{m.DisplayName} 的卡",
            Content = panel,
            CloseButtonText = "关闭",
        });

        Reload();
    }

    private async void RevokeMember(Member m)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"停用 {m.DisplayName}？",
            Content = "停用后这个人的卡和密码都不能用。他记的积分一条不动，" +
                      "流水里仍显示是他记的。",
            PrimaryButtonText = "停用",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await Dialogs.ShowAsync(dialog) != ContentDialogResult.Primary) return;
        Services.Members.Revoke(m.Id);
        Services.Audit.Add("member.revoke", null, m.Id, m.DisplayName);
        Reload();
    }

    // ---------------- 加分项 ----------------

    /// <summary>
    /// 默认只看启用中的；点「查看已归档」翻过来。
    /// 验收用 CP_SNAP_ARCHIVED=1 时直接展开 —— 归档项平时藏在一次点击后面，截不到。
    /// </summary>
    private bool _showArchivedRules =
        Environment.GetEnvironmentVariable("CP_SNAP_ARCHIVED") == "1";

    private void BuildRuleSection()
    {
        var body = Section("加分与扣分项",
            "负数是扣分。「全班」的项目所有人在记分台都能看到；写着人名的只在他刷卡时出现。");

        var rules = Services.Rules.ListAll(includeArchived: _showArchivedRules);
        var members = Services.Members.ListAll(includeRevoked: true).ToDictionary(m => m.Id, m => m.DisplayName);
        foreach (var rule in rules)
        {
            var row = new Grid { ColumnSpacing = 10, Padding = new Thickness(0, 6, 0, 6) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var info = new StackPanel { Spacing = 1 };
            var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            head.Children.Add(new FontIcon
            {
                Glyph = rule.Glyph,
                FontFamily = new FontFamily(IconCatalog.FontFamily),
                FontSize = 15,
                Foreground = Pal.Secondary,
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = rule.IsArchived ? 0.5 : 1,
            });
            var title = new TextBlock
            {
                Text = $"{rule.Label}　{rule.DeltaText}",
                Style = Styled("DeskBodyText"),
                Opacity = rule.IsArchived ? 0.5 : 1,
            };
            head.Children.Add(title);

            // 归属：不标出来，班主任会以为「怎么多出几项自己没建过的」
            var owner = rule.OwnerId is { } ownerId && members.TryGetValue(ownerId, out var name)
                ? name
                : (rule.IsShared ? "全班" : "已停用成员");
            head.Children.Add(new Border
            {
                Background = Pal.TileBg,
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 1, 6, 1),
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = rule.IsArchived ? 0.5 : 1,
                Child = new TextBlock { Text = owner, FontSize = 11, Foreground = Pal.Secondary },
            });
            info.Children.Add(head);
            info.Children.Add(new TextBlock
            {
                Text = rule.HasCap
                    ? $"{(rule.CapScope == CapScope.Single ? "单次最多" : "每周期最多")} {rule.CapValue}"
                    : "不封顶",
                Style = Styled("DeskSecondaryText"),
            });
            row.Children.Add(info);

            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                VerticalAlignment = VerticalAlignment.Center,
            };

            if (!rule.IsArchived)
            {
                // 分值就地改：以前是「−5 / +5」两枚按钮，改一次 5 分要连点，还得数点了几下。
                // 就地改分值。NumberInput 每改一次就给一个"已定下来"的值，
                // 不像 NumberBox 那样要等焦点离开才肯写 —— 改完直接去点别的区块不会丢。
                var delta = new NumberInput
                {
                    Value = rule.Delta,
                    Minimum = -500,
                    Maximum = 500,
                    Step = 5,
                    BoxWidth = 76,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                var captured = rule;
                delta.ValueChanged += next0 =>
                {
                    var next = (int)next0;
                    if (next == captured.Delta) return;
                    captured.Delta = next;
                    Services.Rules.Save(captured);

                    // 左边那行字里也印着分值，不同步就会一行两个数
                    title.Text = $"{captured.Label}　{captured.DeltaText}";
                };
                actions.Children.Add(delta);

                actions.Children.Add(Small("编辑", () => EditRule(rule)));
                actions.Children.Add(Small("归档", () =>
                {
                    Services.Rules.Archive(rule.Id);
                    Shell?.Toast($"「{rule.Label}」已归档，记分台不再出现", 4);
                    Reload();
                }));
            }
            else
            {
                actions.Children.Add(Small("恢复", () =>
                {
                    Services.Rules.Restore(rule.Id);
                    Reload();
                }));

                // 归档只是"不用了"，一年下来能攒十几条只能看不能清的项。
                // 彻底删除要两道门：先确认后果（历史记分不受影响），再验管理密码 ——
                // 它删的不是一行配置，是"这条以后再也改不回来了"。
                actions.Children.Add(Async("彻底删除", async () =>
                {
                    if (Shell is not { } shell) return;
                    if (!await shell.ConfirmAsync($"彻底删除「{rule.Label}」？",
                            "这条加分项会从所有设备上一起消失，不能恢复。\n" +
                            "已经记过的分数不受影响：历史记录里仍然看得到这条的名字和分值。",
                            "彻底删除", "取消")) return;
                    if (!await shell.AskPinAsync("彻底删除", "删掉之后无法恢复，需要管理密码", requireManager: true)) return;

                    var removed = Services.Purge.Purge("rule", rule.Id);
                    Services.Log($"彻底删除加分项 {rule.Label}（{rule.Id}）removed={removed}");
                    Shell?.Toast(removed > 0 ? $"「{rule.Label}」已彻底删除" : "这条已经不在了", 4);
                    Reload();
                }));
            }

            Grid.SetColumn(actions, 1);
            row.Children.Add(actions);

            body.Children.Add(row);
        }

        var footer = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        footer.Children.Add(Small("新建加分项", () => EditRule(null)));
        footer.Children.Add(Small("新建扣分项", () => EditRule(null, delta: -10)));
        footer.Children.Add(Small(_showArchivedRules ? "只看启用中的" : "查看已归档", () =>
        {
            _showArchivedRules = !_showArchivedRules;
            Reload();
        }));
        body.Children.Add(footer);
    }

    private async void EditRule(Rule? existing, int delta = 10)
    {
        var isNew = existing is null;
        var rule = existing ?? new Rule { Label = "", Delta = delta, Icon = IconCatalog.Default, Sort = 100 };

        var labelBox = new TextBox { Header = "名称", Text = rule.Label, Width = 320 };
        var deltaBox = new NumberInput
        {
            Header = "默认分（负数表示扣分）",
            Value = rule.Delta,
            Minimum = -500,
            Maximum = 500,
            Step = 5,
            BoxWidth = 120,
        };

        var icon = rule.Glyph;
        var iconPicker = Controls.IconChooser.Build(IconCatalog.RuleIcons, icon, g => icon = g);

        var capCombo = new ComboBox { Header = "封顶", Width = 240 };
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
        };
        capCombo.SelectionChanged += (_, _) => capBox.IsEnabled = capCombo.SelectedIndex > 0;

        var panel = new StackPanel { Spacing = 10, Width = 340 };
        panel.Children.Add(labelBox);
        panel.Children.Add(deltaBox);
        panel.Children.Add(new TextBlock { Text = "图标", Style = Styled("DeskColumnHeader") });
        panel.Children.Add(iconPicker);
        panel.Children.Add(capCombo);
        panel.Children.Add(capBox);
        panel.Children.Add(new TextBlock
        {
            Text = "封顶按单次或每个周期计；不封顶就一直累加。",
            Style = Styled("DeskSecondaryText"),
        });

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = isNew
                ? (rule.Delta < 0 ? "新建扣分项" : "新建加分项")
                : (rule.Delta < 0 ? "编辑扣分项" : "编辑加分项"),
            Content = panel,
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await Dialogs.ShowAsync(dialog) != ContentDialogResult.Primary) return;

        var label = NameNormalizer.Clean(labelBox.Text);
        if (label.Length == 0)
        {
            Shell?.Toast("名称不能为空");
            return;
        }

        rule.Label = label;
        deltaBox.Commit();
        capBox.Commit();
        rule.Delta = (int)deltaBox.Value;
        rule.Icon = IconCatalog.Normalize(icon);
        rule.CapValue = capCombo.SelectedIndex == 0 ? null : (int)capBox.Value;
        rule.CapScope = capCombo.SelectedIndex == 0 ? CapScope.None
            : capCombo.SelectedIndex == 1 ? CapScope.Single : CapScope.Period;

        Services.Rules.Save(rule);
        Reload();
        Shell?.Toast(isNew ? $"已新建「{label}」" : "已保存", 2);
    }

    // ---------------- 成绩口径 ----------------

    /// <summary>顺序和下拉框的项一一对应，改顺序要同时改下面两个 switch。</summary>
    private static readonly AbsencePolicy[] PolicyOrder =
    [
        AbsencePolicy.Redistribute,
        AbsencePolicy.Zero,
        AbsencePolicy.ClassAverage,
    ];

    /// <summary>
    /// 口径原来在「成绩与总评」页里，和权重滑块挤在一张卡上 ——
    /// 一个是每节课都会动的数，一个是开学定一次就再也不碰的口径，放在一起只会被误拖。
    /// </summary>
    private void BuildSchemeSection()
    {
        var body = Section("成绩口径", "总评怎么算。改完立即影响排名与导出。");

        var scheme = Services.Exams.ActiveScheme();

        var normalize = new CheckBox
        {
            Content = "总评折算成百分制",
            IsChecked = scheme.Normalize,
            MinWidth = 0,
        };
        normalize.Checked += (_, _) => SaveNormalize(true);
        normalize.Unchecked += (_, _) => SaveNormalize(false);
        body.Children.Add(Labelled("总评形式", normalize));
        body.Children.Add(new TextBlock
        {
            Text = "开启：各场考试按满分折算成百分制后加权，总评是 0–100 的分数。" +
                   "关闭：按考试原始分与积分加权，只用于本班内排名。",
            Style = Styled("DeskSecondaryText"),
            MaxWidth = 620,
            HorizontalAlignment = HorizontalAlignment.Left,
        });

        var policy = new ComboBox { Width = 360 };
        policy.Items.Add("缺考那场不计，权重摊给其余场次");
        policy.Items.Add("缺考记 0 分");
        policy.Items.Add("缺考按该场全班平均分替代");
        var index = Array.IndexOf(PolicyOrder, Services.Settings.AbsencePolicy);
        policy.SelectedIndex = index < 0 ? 0 : index;
        policy.SelectionChanged += (_, _) =>
        {
            if (policy.SelectedIndex < 0) return;
            Services.Settings.AbsencePolicy = PolicyOrder[policy.SelectedIndex];
            Services.Audit.Add("scheme.policy", null, null,
                Services.Settings.AbsencePolicy.ToString());
        };
        body.Children.Add(Labelled("缺考处理", policy));
        body.Children.Add(new TextBlock
        {
            Text = "缺考与考低分是两件事，默认不按 0 分计入。",
            Style = Styled("DeskSecondaryText"),
            HorizontalAlignment = HorizontalAlignment.Left,
        });
    }

    private void SaveNormalize(bool on)
    {
        var scheme = Services.Exams.ActiveScheme();
        if (scheme.Normalize == on) return;
        scheme.Normalize = on;
        Services.Exams.SaveScheme(scheme);
        Services.Audit.Add("scheme.normalize", null, null, on ? "百分制" : "原值加权");
    }

    // ---------------- 备份 ----------------

    private void BuildBackupSection()
    {
        var body = Section("备份与恢复", "每天最多自动备份一次（启动时、或当天记满 50 人次时补一份）；重置或恢复前会另存一份。保留最近 7 天。");

        // 路径可能很长（数据在安装目录下时尤其长），所以给整行宽度，按钮另起一行；
        // 挤在一行时后半截会被截掉，正好截掉盘符之后最要紧的那段。
        var dirBox = new TextBox
        {
            Text = Services.Backups.BackupRoot,
            Height = 32,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var dirActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };
        dirActions.Children.Add(Small("保存备份目录", () =>
        {
            var path = dirBox.Text.Trim();
            if (!Services.Backups.SetBackupRoot(path))
            {
                // 盘符不存在、路径里带了非法字符，都会走到这里。把失败说出来，
                // 否则用户以为改成功了，之后每次备份都悄悄失败。
                Shell?.Toast("这个目录建不了，请换一个（例如 D:\\班级积分备份）", 6);
                return;
            }
            Shell?.Toast("备份目录已改为 " + Services.Backups.BackupRoot, 5);
            Reload();
        }));
        dirActions.Children.Add(Small("恢复默认", () =>
        {
            if (!Services.Backups.SetBackupRoot(null))
            {
                Shell?.Toast("默认目录建不了：" + Database.DefaultBackupDir, 6);
                return;
            }
            Shell?.Toast("已改回 " + Services.Backups.BackupRoot, 5);
            Reload();
        }));
        dirActions.Children.Add(Small("打开文件夹", () =>
        {
            try
            {
                Directory.CreateDirectory(Services.Backups.BackupRoot);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = Services.Backups.BackupRoot,
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                Shell?.Toast("打不开：" + ex.Message, 4);
            }
        }));

        body.Children.Add(new TextBlock { Text = "备份目录", Style = Styled("DeskColumnHeader") });
        body.Children.Add(dirBox);
        body.Children.Add(dirActions);

        body.Children.Add(ValueRow("上次备份", Services.Backups.LastBackupText, StalenessText()));

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(Small("立即备份", () =>
        {
            try
            {
                var path = Services.Backups.BackupNow("manual", Services.BuildExportBundle());
                Shell?.Toast("已备份到 " + path, 6);
                Reload();
            }
            catch (Exception ex)
            {
                Shell?.Toast("备份失败：" + ex.Message, 6);
            }
        }));
        buttons.Children.Add(Small("清理旧备份", () =>
        {
            Services.Backups.Cleanup();
            Shell?.Toast("已清理（保留最近 7 天，至少留一份）", 4);
            Reload();
        }));
        buttons.Children.Add(Async("整包导出（换机用）", ExportPackAsync));
        buttons.Children.Add(Small("从整包导入…", ImportPack));
        body.Children.Add(buttons);

        var list = Services.Backups.List(8);
        body.Children.Add(new TextBlock
        {
            Text = list.Count == 0 ? "还没有备份。" : "最近的备份（恢复会覆盖当前库，先自动留档）",
            Style = Styled("DeskColumnHeader"),
            Margin = new Thickness(0, 10, 0, 2),
        });

        foreach (var file in list)
        {
            var row = new Grid { Padding = new Thickness(0, 5, 0, 5) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var info = new StackPanel { Spacing = 1 };
            // 时间在上：人找备份时先想的是"昨天那份"，而不是"哪个文件"
            info.Children.Add(new TextBlock { Text = file.AtText, Style = Styled("DeskBodyText") });
            info.Children.Add(new TextBlock { Text = file.DetailText, Style = Styled("DeskSecondaryText") });
            row.Children.Add(info);

            var restore = Small("恢复", () => RestoreFrom(file));
            Grid.SetColumn(restore, 1);
            row.Children.Add(restore);

            body.Children.Add(row);
        }

        // 换过默认目录、搬过数据目录，老备份都还在原来的地方，别让人以为它们没了
        if (Services.Backups is { LegacyCount: > 0 } backups)
            body.Children.Add(Note(
                $"另有 {backups.LegacyCount} 份在旧目录 {string.Join("、", backups.LegacyRoots)}，上面同样可恢复。"));
    }

    private string StalenessText()
    {
        // 主行已经写着「还没有备份过」了，这里不能再补一句「一周没备份了」——
        // 两句话叠在一起既自相矛盾，也把一个从没备份过的机器说成"很久没备份"。
        if (Services.Backups.LastBackupAt <= 0)
            return "目前没有备份。点「立即备份」做一份，之后每天首次启动会自动备份。";

        return Services.Backups.StalenessLevel switch
        {
            0 => "今天备份过",
            1 => "两三天没备份了",
            _ => "一周没备份了，数据只有一份",
        };
    }

    // ---------------- 本机数据 ----------------

    /// <summary>
    /// 这台电脑上的数据怎么处置。
    ///
    /// 「本机重置」清的是本机，不是班级：先自动备份，再删库重启，回到刚装好的向导。
    /// 不提供"只删一部分"的选项 —— 半个班的数据比没有数据更难解释。
    /// </summary>
    private void BuildLocalDataSection()
    {
        var body = Section("本机数据", "数据只在这一台电脑上，换机器用整包导出。");

        body.Children.Add(ValueRow("数据库", Services.Db.DbPath));

        // 位置放在这里而不是「备份与恢复」里：它决定的是库、日志、同步凭据、默认备份
        // 这一整串东西，不只是备份。
        body.Children.Add(ValueRow("数据目录", Database.RootDir,
            "库、日志、同步凭据、更新包都在这里。装在 C 盘时搬到别的盘能省系统盘空间，" +
            "换机时整个目录拷走也最省事。"));

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(Small("打开数据目录", () =>
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = DataDir,
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                Shell?.Toast("打不开：" + ex.Message, 4);
            }
        }));
        row.Children.Add(Async("更改位置…", MoveDataDir));
        body.Children.Add(row);

        body.Children.Add(new TextBlock
        {
            Text = "本机重置",
            Style = Styled("DeskColumnHeader"),
            Margin = new Thickness(0, 12, 0, 2),
        });
        body.Children.Add(new TextBlock
        {
            Text = "清空这台电脑上的名单、积分、成绩与密码，重新进入首次配置。清空前自动备份一份。",
            Style = Styled("DeskSecondaryText"),
            MaxWidth = 560,
            HorizontalAlignment = HorizontalAlignment.Left,
        });

        var reset = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 6, 0, 0),
        };
        reset.Children.Add(Small("本机重置…", ResetLocal));
        body.Children.Add(reset);
    }

    /// <summary>
    /// 换数据目录。搬完必须重启：库文件换了地方，内存里的会话、周期、形态都还连着旧的那份。
    /// 这里只做"复制 + 记下位置"，不删原目录里的任何东西 —— 搬错了还能退回去。
    /// </summary>
    private async Task MoveDataDir()
    {
        if (Shell is not { } shell) return;

        // 用 --db 启动时库的位置由参数说了算，搬完重启还会被参数指回原处 ——
        // 与其让人搬半天没效果，不如当场说清楚。
        if (!string.IsNullOrWhiteSpace(App.Options.DbPath))
        {
            shell.Toast("这台机器是用 --db 指定库启动的，位置由那个参数决定；去掉参数后才能在这里搬", 8);
            return;
        }

        if (!await shell.AskPinAsync("更改数据位置", "需要管理密码", requireManager: true)) return;

        var picked = FileDialogs.PickFolder("选择新的数据目录（建议放在非系统盘）", DataDir);
        if (picked is null) return;

        // 选到盘符根目录就补一层子目录：把库、logs、backups 直接摊在 D:\ 上，
        // 过两个月谁都不敢动那个盘。
        var root = Path.GetPathRoot(picked) ?? "";
        var target = string.Equals(picked.TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(picked, "ClassPoints")
            : picked;

        if (DataPaths.SameDir(target, Database.RootDir))
        {
            shell.Toast("数据已经在这个目录里了", 4);
            return;
        }

        var ok = await shell.ConfirmAsync("把数据搬到这个目录？",
            $"新位置：{target}\n\n" +
            "会复制数据库（一致性副本，不是简单拷贝）、同步凭据和恢复码，然后自动重启。" +
            "原来的目录一个文件都不删，确认新位置没问题后可以自己清理。",
            "搬过去并重启", "取消");
        if (!ok) return;

        // 复制几百 KB 到几十 MB，放后台线程，别把界面卡住
        var result = await Task.Run(() => Services.Backups.MoveDataRoot(target));
        if (!result.Ok)
        {
            shell.Toast(result.Message, 8);
            return;
        }

        Services.Audit.Add("data.move", null, null, result.Message);
        shell.Toast("数据已搬到 " + result.Message + "，正在重启…", 8);
        App.Window?.RestartApp();
    }

    private async void ResetLocal()
    {
        if (Shell is not { } shell) return;
        if (!await shell.AskPinAsync("本机重置", "需要管理密码", requireManager: true)) return;

        var ok = await shell.ConfirmAsync("确定清空这台电脑上的数据？",
            "名单、积分、成绩、周期和管理密码都会删除，之后重新进入首次配置。" +
            "清空前会自动做一份备份，撤销只能在备份里找回。",
            "清空并重启", "取消");
        if (!ok) return;

        try
        {
            var backup = Services.Backups.BackupNow("before-reset", null);
            Services.Audit.Add("local.reset", null, null, backup);
            shell.Toast("已备份到 " + backup + "，正在重启…", 5);
            App.Window?.ResetLocalAndRestart();
        }
        catch (Exception ex)
        {
            Services.LogError("local-reset", ex);
            shell.Toast("重置失败：" + ex.Message, 8);
        }
    }

    private async void RestoreFrom(BackupFile file)
    {
        // 列表里同时有快照（文件夹里的库文件）和整包（.zip）。两者恢复方式不同：
        // 直接拿 zip 去走 Restore 会得到"这不是一个 SQLite 数据库文件"。
        var pack = file.IsPack;

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = pack ? "用这个整包替换当前数据？" : "用这份备份覆盖当前数据？",
            Content = $"{file.AtText}\n{file.DetailText}\n\n" +
                      (pack
                          ? "会先校验校验码、结构版本与记录条数，全部对上才替换；"
                          : "当前库会改名留档（不删），再用这份恢复；") +
                      "程序会自动重新打开。",
            PrimaryButtonText = pack ? "校验并恢复" : "恢复",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await Dialogs.ShowAsync(dialog) != ContentDialogResult.Primary) return;

        var check = pack
            ? Services.Backups.ImportPack(file.Path)
            : Services.Backups.Restore(file.Path);
        Services.Audit.Add(pack ? "pack.import" : "backup.restore", null, file.Name, check.Message);

        if (!check.Ok)
        {
            Shell?.Toast(check.Message, 8);
            return;
        }

        // 库文件已经换掉了，内存里还是旧会话 —— 必须重开程序，不能只是 Reload。
        Shell?.Toast(check.Message + "，正在重新打开…", 6);
        App.Window?.RestartApp();
    }

    private async Task ExportPackAsync()
    {
        try
        {
            // 打包要拷库再压缩，放后台：教室电脑上这一步可能一两秒，别让界面卡住。
            var path = await Task.Run(() => Services.Backups.ExportPack());
            Shell?.Toast("整包已导出：" + path, 8);
        }
        catch (Exception ex)
        {
            Services.LogError("export-pack", ex);
            Shell?.Toast("整包导出失败：" + ex.Message, 6);
        }
    }

    private async void ImportPack()
    {
        try
        {
            var path = FileDialogs.Open("选择整包备份", FileFilter.Zip);
            if (path is null) return;

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "从整包恢复？",
                Content = $"{Path.GetFileName(path)}\n\n会先校验清单（校验码、表结构、条数、原库留档），" +
                          "四项都对上才替换。",
                PrimaryButtonText = "校验并恢复",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
            };

            if (await Dialogs.ShowAsync(dialog) != ContentDialogResult.Primary) return;

            var check = Services.Backups.ImportPack(path);
            Services.Audit.Add("pack.import", null, Path.GetFileName(path), check.Message);

            if (!check.Ok)
            {
                Shell?.Toast(check.Message, 10);
                return;
            }

            // 同「恢复」：整包会把库整个替换，重开程序才能确保读的是新库。
            Shell?.Toast(check.Message + "，正在重新打开…", 6);
            App.Window?.RestartApp();
        }
        catch (Exception ex)
        {
            Services.LogError("import-pack", ex);
            Shell?.Toast("整包导入失败：" + ex.Message, 6);
        }
    }

    // ---------------- 预配置 ----------------

    private void BuildProvisionSection()
    {
        var body = Section("预配置", "把云端地址、班级码与名单导出成一个文件，带到学校导入。");

        var credentials = new SyncStore().Load();
        body.Children.Add(ValueRow("当前可下发的配置",
            string.IsNullOrWhiteSpace(Services.ClassName) ? "还没配班级信息" : Services.ClassName,
            credentials is null
                ? "这台还没连云端。导出的是纯本地配置（班级名与名单）。"
                : $"云端 {credentials.BaseUrl}，班级码 {credentials.ClassCode}。" +
                  "文件里带班级码和配对口令，到学校导入即自动配对。"));

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(Async("导出配置包…", ExportBundleAsync));
        buttons.Children.Add(Async("导入配置包…", ImportBundleAsync));
        body.Children.Add(buttons);

        body.Children.Add(Note("配对口令用配置包口令加密，未设口令的包里不含任何密码。"));
    }

    private async Task ExportBundleAsync()
    {
        var note = new TextBox { Header = "备注（给文件起个名）", PlaceholderText = "例：育才中学 初二（3）班" };
        var pairingPin = new PasswordBox { Header = "班主任密码（配对用）", PasswordChar = "●" };
        var adminPin = new PasswordBox { Header = "预置管理密码（可空）", PasswordChar = "●", MaxLength = 6 };
        var passphrase = new PasswordBox { Header = "配置包口令（至少 6 位）", PasswordChar = "●" };
        var confirm = new PasswordBox { Header = "再输一次口令", PasswordChar = "●" };

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "导出配置包",
            Content = new StackPanel
            {
                Spacing = 10,
                Width = 420,
                Children =
                {
                    new TextBlock
                    {
                        Text = "名单会一起打包（" + Services.Students.ListActive().Count + " 人）。" +
                               "带上班主任密码，学校那台导入时就能自动配对；预置管理密码则省掉到校现设。",
                        Style = Styled("DeskSecondaryText"),
                        TextWrapping = TextWrapping.Wrap,
                    },
                    note, pairingPin, adminPin, passphrase, confirm,
                },
            },
            PrimaryButtonText = "选择保存位置",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await Dialogs.ShowAsync(dialog) != ContentDialogResult.Primary) return;

        var pin = pairingPin.Password.Trim();
        var admin = adminPin.Password.Trim();
        var pass = passphrase.Password;

        if (admin.Length > 0 && (admin.Length != 6 || !admin.All(char.IsDigit)))
        {
            Shell?.Toast("预置管理密码得是 6 位数字");
            return;
        }

        if (pin.Length > 0 || admin.Length > 0)
        {
            if (pass.Length < 6)
            {
                Shell?.Toast("要带密码，就得设一个至少 6 位的配置包口令", 5);
                return;
            }
            if (pass != confirm.Password)
            {
                Shell?.Toast("两次输入的口令不一样", 4);
                return;
            }
        }

        var target = FileDialogs.Save("导出配置包",
            string.IsNullOrWhiteSpace(note.Text) ? "classpoints" : "classpoints-" + Services.ClassName,
            FileFilter.Config, "cpcfg");
        if (target is null) return;

        try
        {
            var bundle = Services.Config.Build(pin, admin, pass, (note.Text ?? "").Trim());
            ConfigService.SaveAndKeep(bundle, target);
            Services.Audit.Add("config.export", null, null, $"{bundle.ClassName} → {target}");
            Shell?.Toast($"已导出到 {target}" +
                         (bundle.NeedsPassphrase ? "（含加密的配对口令）" : "（不含配对口令）"), 8);
        }
        catch (Exception ex)
        {
            Services.LogError("config-export", ex);
            Shell?.Toast("导出失败：" + ex.Message, 6);
        }
    }

    private async Task ImportBundleAsync()
    {
        var picked = FileDialogs.Open("导入配置包", FileFilter.Config);
        if (picked is null) return;

        ConfigBundle bundle;
        try
        {
            bundle = ConfigBundleFile.Load(picked);
        }
        catch (Exception ex)
        {
            Shell?.Toast(ex.Message, 8);
            return;
        }

        var passphrase = "";
        var passBox = new PasswordBox { PlaceholderText = "配置包口令", PasswordChar = "●" };

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "导入配置包",
            Content = new StackPanel
            {
                Spacing = 10,
                Width = 420,
                Children =
                {
                    new TextBlock
                    {
                        Text = Services.Config.Describe(bundle),
                        Style = Styled("DeskBodyText"),
                        TextWrapping = TextWrapping.Wrap,
                    },
                    new TextBlock
                    {
                        Text = bundle.NeedsPassphrase
                            ? "这份配置带加密的配对密码，请输入导出时设的口令。"
                            : "这份配置没有加密的配对密码，导入后到「同步」里手动配对即可。",
                        Style = Styled("DeskSecondaryText"),
                        TextWrapping = TextWrapping.Wrap,
                    },
                    passBox,
                },
            },
            PrimaryButtonText = "导入",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await Dialogs.ShowAsync(dialog) != ContentDialogResult.Primary) return;
        passphrase = passBox.Password;

        try
        {
            var result = await Services.Config.ApplyAsync(bundle, passphrase, "desk");
            ConfigBundleFile.KeepCopy(bundle, picked);
            Services.Audit.Add("config.import", null, null, $"{bundle.ClassName} ← {picked}");

            Shell?.Toast(result.Message + (result.Pulled is null ? "" : "，" + result.Pulled), 8);
            Reload();
        }
        catch (ConfigException ex)
        {
            // 口令错、文件被改、字段不符 —— 都是能说清楚的话，原样告诉老师
            Shell?.Toast(ex.Message, 8);
        }
        catch (Exception ex)
        {
            Services.LogError("config-import", ex);
            Shell?.Toast("导入失败：" + ex.Message, 8);
        }
    }

    // ---------------- 同步 ----------------

    /// <summary>
    /// 重新配对时替老师填好的地址和班级码。
    ///
    /// 存在字段里是因为 SignOut 之后凭据就没了，而这一页要重建一次才能变成
    /// "还没配对"的样子 —— 重建之后从哪里拿回那两串？只能先记下来。
    /// </summary>
    private string _repairUrl = "";
    private string _repairCode = "";

    private void BuildSyncSection()
    {
        var sync = Services.Sync;
        var configured = sync.IsConfigured;
        var creds = sync.IsConfigured ? new SyncStore().Load() : null;
        var body = Section("同步",
            configured
                ? $"已连接 {creds?.BaseUrl}。断网照常用，写操作在后台补推。"
                : "纯本地模式：这台电脑就是数据的唯一副本，备份即同步。");

        body.Children.Add(ValueRow("同步状态", Services.Pump.StatusText));

        // 特征码放在这里而不是只放在「关于」：班级一旦开了设备审核，
        // 老师要做的第一件事就是把这串抄给班主任 —— 或者反过来，对着网页核对它。
        body.Children.Add(ValueRow("本机特征码", DeviceFingerprint.Display,
            "班主任在「班级管理台」里凭这一串认这台机器。班级没开设备审核时用不到。"));

        if (configured)
        {
            // 纯本地模式不显示这一行：那台机器压根没接云端，
            // 「待推送 136 条」只会让人以为有东西卡住了。
            body.Children.Add(ValueRow("待推送", Services.Outbox.Count() + " 条"));

            // 冲突从来不挡同步（挡了就等于永远同步不上），但"某个人的学号被让出去了"
            // 会影响下次按学号导入名单，所以留一行让人能看见。
            if (Services.Pump.Conflicts > 0)
            {
                body.Children.Add(ValueRow("已合并", $"{Services.Pump.Conflicts} 处重名或学号冲突",
                    "两台设备各自用了同一个学号（或者同一句常用理由），同步时按云端为准合并了：人都在，" +
                    "只是其中一条的学号或那条重复理由没有重复记。下次按学号导入名单时留意一眼。"));
            }

            // 自动重配这件事必须写明：它意味着配对时输的那个密码留在了本机。
            // 不说清楚，等于偷偷存口令 —— 而这台机器是要摆在教室里的。
            body.Children.Add(Note(creds?.CanSelfRepair == true
                ? "配对时用的口令随 Windows 账号加密留在本机（换 Windows 用户就读不出）。凭据被别处换掉时会先用它自动重配，接不上才请你重新配对。"
                : "这台机器的凭据里没有留口令，凭据一旦失效只能手动重新配对。到「设置 → 同步」点「重新配对」补一次密码即可。"));

            // 凭据失效是"只能人来救"的那一类：云端把设备踢了、重置过班级、
            // 或者这台机器重装过。以前只写一句"请重新配对"，可界面上没有那个入口，
            // 老师得自己摸到"解除配对"再走一遍表单 —— 所以这里直接把按钮摆出来。
            if (Services.Pump.NeedsPairing)
            {
                body.Children.Add(ValueRow("凭据", Services.Pump.PairingReason.Title(),
                    Services.Pump.PairingReason.Advice()));
            }

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            buttons.Children.Add(Async("立即同步", async () =>
            {
                var report = await Services.Pump.RunAsync();
                Shell?.Toast(report is null ? "同步没有执行" : report.Ok
                    ? $"推送 {report.Pushed} 条，拉取 {report.Pulled} 条"
                    : $"同步失败：{report.Error}", 5);
                Reload();
            }));

            if (Services.Pump.NeedsPairing)
            {
                buttons.Children.Add(PrimaryButton("重新配对…", () =>
                {
                    // 地址和班级码先记下来：SignOut 之后就读不到了，而让老师手抄
                    // 一串 https://… 是最容易输错的一步。
                    _repairUrl = creds?.BaseUrl ?? "";
                    _repairCode = creds?.ClassCode ?? "";
                    sync.SignOut();
                    Shell?.Toast("地址和班级码已经填好，补一次班主任密码就能重新配对", 8);
                    Reload();
                }));
            }
            else
            {
                buttons.Children.Add(Small("解除配对…", () =>
                {
                    // 先把码记到表单上：SignOut 之后 sync.json 就没了，而"配回原来那个班"
                    // 正是解除配对之后最常见的一步。
                    _repairCode = sync.LastClassCode;
                    sync.SignOut();
                    Shell?.Toast("凭据已清除，可以重新配对", 5);
                    Reload();
                }));
            }
            body.Children.Add(buttons);
            return;
        }

        // 还没配对：两个入口。建班只该做一次，配对是每台设备做一次。
        var urlBox = new TextBox
        {
            PlaceholderText = "https://classpoints-api.你的账号.workers.dev",
            Height = 34,
            VerticalContentAlignment = VerticalAlignment.Center,
            Text = _repairUrl.Length > 0 ? _repairUrl : "http://127.0.0.1:8787",
        };
        var codeBox = new TextBox
        {
            PlaceholderText = "8 位班级码",
            Height = 34,
            VerticalContentAlignment = VerticalAlignment.Center,
            // 上次配对/建班用过的码：解除配对之后最常见的一步就是"配回去"
            Text = _repairCode.Length > 0 ? _repairCode : sync.LastClassCode,
        };
        var pinBox = new PasswordBox
        {
            PasswordChar = "●",
            Height = 34,
            VerticalContentAlignment = VerticalAlignment.Center,
            PlaceholderText = "6 位数字",
        };
        // 服务端配了 BOOTSTRAP_SECRET 才需要填。填了只在这一个请求里用，
        // 不存盘 —— 它是部署口令，不属于这台电脑该记得的东西。
        var bootstrapBox = new PasswordBox
        {
            PasswordChar = "●",
            Height = 34,
            VerticalContentAlignment = VerticalAlignment.Center,
            PlaceholderText = "没设就留空",
        };

        body.Children.Add(Labelled("服务器地址", urlBox, 460));

        // 三个字段同一行、同一宽度、同一基线：分别在三个 StackPanel 里各写一遍宽度，
        // 迟早会出现"标签在最左、输入框飘在中间"的错位。
        body.Children.Add(FieldRow(200,
            ("班级码", codeBox),
            ("班主任密码", pinBox),
            ("部署口令", bootstrapBox)));

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(Async("配对到已有班级", async () =>
        {
            try
            {
                var baseUrl = (urlBox.Text ?? "").Trim();
                var code = (codeBox.Text ?? "").Trim();
                var pin = pinBox.Password;
                if (baseUrl.Length == 0 || code.Length == 0 || pin.Length == 0)
                {
                    Shell?.Toast("地址、班级码、密码都要填", 4);
                    return;
                }

                var report = await sync.PairAsync(baseUrl, code, pin, "desk");
                // 输进去的这个码才是云端认的：本机设置里那份可能还是建班之前懒生成的随机码。
                Services.AdoptClassCode(code);
                Services.Log($"同步配对成功 {baseUrl} readOnly={report.ReadOnly}");
                Services.Pump.Start();
                // 待审不算失败：连上了、能看，只是暂时写不了。话术里带特征码，
                // 老师要拿它去找班主任点通过，6 秒读不完一串十六进制。
                Shell?.Toast(
                    report.ReadOnly
                        ? report.PendingMessage ?? "配对成功，但设备还在等班主任通过，暂时只能查看。"
                        : "配对成功，开始后台同步",
                    report.ReadOnly ? 16 : 5);
                Reload();
            }
            catch (Exception ex)
            {
                Services.LogError("sync-pair", ex);
                // 待审和被拒的话术里带着特征码，老师要照着去网页上点通过，
                // 6 秒读不完一串十六进制，给足时间。
                var needsAction = ex is SyncException { Code: "device_pending" or "device_blocked" };
                Shell?.Toast("配对失败：" + ex.Message, needsAction ? 16 : 6);
            }
        }));
        actions.Children.Add(Async("在这台电脑上建班", async () =>
        {
            try
            {
                var baseUrl = (urlBox.Text ?? "").Trim();
                var pin = pinBox.Password;
                if (baseUrl.Length == 0 || pin.Length < 6)
                {
                    Shell?.Toast("地址要填，密码至少 6 位", 4);
                    return;
                }

                // 建班 = 在云端新建一个**空**班。而本机这些数据早就属于上一个班了：
                // 同一个学生在云端只能属于一个班（主键全局唯一，写不进第二个班）。
                // 所以这一步必须先说清楚，并把上一个班的码摆出来 —— 现场就是这里连点了
                // 两次建班，第二个班一个人都没有，教室大屏配对上去只看到"还没有学生"。
                if (Services.StudentCount > 0 && Shell is { } shell)
                {
                    var last = Services.Sync.LastClassCode;
                    var ok = await shell.ConfirmAsync("建一个新班？本机数据不会跟着过去",
                        $"这台电脑上已经有 {Services.StudentCount} 人的名单和他们的积分记录，它们属于" +
                        (last.Length > 0 ? $"班级码 {last} 的那个班" : "之前那个班") + "。\n\n" +
                        "同一个人在云端只能属于一个班，所以新班是空的、这些数据推不过去。" +
                        "如果你只是想重新连上原来的班：先取消，点「配对到已有班级」，填那个班级码。",
                        "仍然建一个新班", "取消");
                    if (!ok) return;
                }

                var report = await sync.CreateClassAsync(
                    baseUrl, Services.ClassName, Services.DeskOperator.DisplayName, pin,
                    Crypto.RandomHex(4).ToUpperInvariant(), "desk", bootstrapBox.Password);

                // 云端发的码要落到本机设置里：否则右下角提示一个码、上面「班级信息」显示另一个，
                // 老师照着设置页念给大屏，配对必然 class_not_found。
                Services.AdoptClassCode(report.CreatedCode);
                Services.Log($"建班成功 {baseUrl} 班级码 {report.CreatedCode}");
                Services.Pump.Start();

                Shell?.Toast($"建班成功，班级码 {report.CreatedCode}（大屏配对要用它）", 12);
                Reload();
            }
            catch (Exception ex)
            {
                Services.LogError("sync-create-class", ex);
                Shell?.Toast("建班失败：" + ex.Message, 6);
            }
        }));
        body.Children.Add(actions);

        body.Children.Add(Note("云端未部署时不用填：换机器改用「整包导出 / 导入」。"));
    }

    private Button Async(string text, Func<Task> work)
    {
        var button = new Button
        {
            Content = text,
            Style = Styled("DeskButton"),
            Height = 30,
            MinWidth = 64,
            FontSize = 13,
        };
        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            try
            {
                await work();
            }
            catch (Exception ex)
            {
                Services.LogError("settings-async", ex);
                Shell?.Toast("操作失败：" + ex.Message, 5);
            }
            finally
            {
                button.IsEnabled = true;
            }
        };
        return button;
    }

    // ---------------- 更新 ----------------

    /// <summary>
    /// 更新那一节。这里最容易被做成"一个按钮"，但那样子老师永远不知道
    /// 自己这台机器到底能不能自动升级 —— 所以先把"安装版 / 便携版"写清楚。
    /// </summary>
    private void BuildUpdateSection()
    {
        var center = UpdateCenter.Current;
        var body = Section("更新", "只接收正式版本。升级时程序会自动关闭并重新打开。");

        body.Children.Add(ValueRow("当前版本", "班级积分 v" + AppServices.AppVersion,
            UpdateService.IsInstalled ? "安装版：可在应用内升级。" : "便携版：只能下载新包手动覆盖。"));

        var statusText = new TextBlock
        {
            Text = center.Status,
            Style = Styled("DeskBodyText"),
            TextWrapping = TextWrapping.Wrap,
        };
        body.Children.Add(statusText);

        var bar = new ProgressBar
        {
            Minimum = 0,
            Maximum = 1,
            Value = 0,
            Height = 4,
            Margin = new Thickness(0, 6, 0, 0),
            Visibility = Visibility.Collapsed,
        };
        body.Children.Add(bar);

        if (center.Available is { } manifest)
        {
            body.Children.Add(ValueRow($"新版本 {manifest.Version}",
                string.IsNullOrWhiteSpace(manifest.Notes) ? "这一版没有写说明。" : manifest.Notes,
                $"发布标签 {manifest.Tag}，安装包 {manifest.Setup.Size / 1048576.0:F1} MB。"));
        }

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        actions.Children.Add(Async("检查更新", async () =>
        {
            await center.CheckAsync(manual: true);
            Reload();
        }));

        if (center.Available is { } target)
        {
            var label = UpdateService.IsInstalled ? $"下载并安装 {target.Version}" : $"下载 {target.Version}";
            actions.Children.Add(Async(label, async () => await InstallAsync(target, statusText, bar)));

            if (UpdateService.IsInstalled)
                actions.Children.Add(Small($"跳过 {target.Version}", () =>
                {
                    center.Skip(target.Version);
                    Reload();
                }));
        }

        body.Children.Add(actions);

        var auto = new ToggleSwitch
        {
            IsOn = Services.Settings.UpdateAutoCheck,
            OnContent = "启动时自动检查",
            OffContent = "启动时自动检查",
            MinWidth = 0,
            Margin = new Thickness(0, 4, 0, 0),
        };
        auto.Toggled += (_, _) => Services.Settings.UpdateAutoCheck = auto.IsOn;
        body.Children.Add(auto);

        // 地址默认不让人操心：留空就用发布时编译进去的公开版本库地址。
        var feedBox = new TextBox
        {
            Text = Services.Settings.UpdateFeed,
            Width = 420,
            Height = 32,
            PlaceholderText = "留空就用发布时内置的地址",
        };
        var feedRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        feedRow.Children.Add(feedBox);
        feedRow.Children.Add(Small("保存更新地址", () =>
        {
            try
            {
                Services.Settings.UpdateFeed = feedBox.Text;
                Shell?.Toast("已保存", 2);
                Reload();
            }
            catch (Exception ex)
            {
                Shell?.Toast("地址不对：" + ex.Message, 5);
            }
        }));
        body.Children.Add(Labelled("版本库地址", feedRow));

        var resolved = UpdateCenter.ResolveFeed();
        body.Children.Add(Note(string.IsNullOrWhiteSpace(resolved)
            ? "还没确定版本库地址。填一个公开版本库根地址（https://…/<用户>/<仓库>）。"
            : "实际使用：" + resolved));
    }

    /// <summary>下载 → 校验 → 拉起安装程序。便携版到"下载完"为止。</summary>
    private async Task InstallAsync(UpdateManifest manifest, TextBlock status, ProgressBar bar)
    {
        bar.Visibility = Visibility.Visible;
        bar.Value = 0;
        status.Text = "准备下载…";

        var progress = new Progress<UpdateProgress>(p =>
        {
            bar.Value = p.Fraction;
            status.Text = $"正在下载 {p.Received / 1048576.0:F1} / {p.Total / 1048576.0:F1} MB";
        });

        var path = await UpdateCenter.Current.DownloadAsync(progress);
        bar.Visibility = Visibility.Collapsed;

        if (!UpdateService.IsInstalled)
        {
            // 便携版：不替用户做覆盖，但把文件直接选中给他，省得在资源管理器里翻
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{path}\"",
                UseShellExecute = true,
            });
            status.Text = $"已下载到 {System.IO.Path.GetDirectoryName(path)}，解压后覆盖旧目录即可";
            Shell?.Toast("新版本已下载，覆盖旧目录即可", 8);
            return;
        }

        var dialog = new ContentDialog
        {
            Title = $"升级到 {manifest.Version}",
            Content = string.IsNullOrWhiteSpace(manifest.Notes)
                ? "程序会关闭并立即安装，装完自动打开。班级数据不受影响。"
                : manifest.Notes,
            PrimaryButtonText = "现在升级",
            CloseButtonText = "稍后",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };

        if (await Dialogs.ShowAsync(dialog) != ContentDialogResult.Primary)
        {
            status.Text = $"已下载 {manifest.Version}，随时可以再点上面那个按钮";
            return;
        }

        Services.Log($"拉起安装程序 {path}");
        try
        {
            UpdateService.LaunchInstaller(path);
        }
        catch (Exception ex)
        {
            Services.LogError("update-launch", ex);
            status.Text = "启动安装程序失败：" + ex.Message;
            Shell?.Toast("启动安装程序失败，文件在 " + System.IO.Path.GetDirectoryName(path), 8);
            return;
        }

        // 必须立刻退：安装程序要用 Restart Manager 关掉本进程才能覆盖 ClassPoints.exe，
        // 而"关掉"依赖我们自己放锁。Application.Exit 在 WinUI 里不保证马上结束，
        // 这里要的是确定性，所以直接 Environment.Exit。
        Environment.Exit(0);
    }

    // ---------------- 关于 ----------------

    private void BuildAboutSection()
    {
        var body = Section("其它", "");

        // 重跑向导的入口只在上面「这台电脑」那一节留一个 —— 同一件事放两个按钮，
        // 一个"下次启动生效"、一个"立刻就换"，老师点错就要骂人。
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(Small("打开日志目录", () =>
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = Database.LogDir,
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                Shell?.Toast("打不开：" + ex.Message, 4);
            }
        }));
        body.Children.Add(buttons);

        // 「当前版本」在「更新」那一节已经有一行了，这里再写一遍只是重复。
        // 这一行要回答的是"库结构是哪一代、能不能降级"。
        body.Children.Add(ValueRow("数据库结构", "v" + Sql.SchemaVersion,
            "升级时只做向前迁移，不降级。"));
    }

    // ---------------- 构件 ----------------

    private StackPanel Section(string title, string hint)
    {
        var border = new Border { Style = Styled("DeskCardBorder") };

        var outer = new StackPanel { Spacing = 10 };
        var head = new StackPanel { Spacing = 2 };
        head.Children.Add(new TextBlock { Text = title, Style = Styled("DeskH2Text") });
        if (!string.IsNullOrWhiteSpace(hint))
            head.Children.Add(new TextBlock { Text = hint, Style = Styled("DeskSecondaryText") });
        outer.Children.Add(head);

        var body = new StackPanel { Spacing = 4 };
        outer.Children.Add(body);

        border.Child = outer;
        Sections.Children.Add(border);
        return body;
    }

    private UIElement ValueRow(string label, string value, string? hint = null)
    {
        var panel = new StackPanel
        {
            Spacing = 1,
            Padding = new Thickness(0, 6, 0, 6),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        panel.Children.Add(new TextBlock { Text = label, Style = Styled("DeskColumnHeader") });
        panel.Children.Add(new TextBlock
        {
            Text = value,
            Style = Styled("DeskBodyText"),
            TextWrapping = TextWrapping.Wrap,
        });
        if (!string.IsNullOrWhiteSpace(hint))
            panel.Children.Add(new TextBlock
            {
                Text = hint,
                Style = Styled("DeskSecondaryText"),
                Margin = new Thickness(0, 2, 0, 0),
            });
        return panel;
    }

    /// <summary>
    /// 标签在上、输入框在下。
    ///
    /// 输入框必须显式左对齐：StackPanel 里的子元素默认 HorizontalAlignment=Stretch，
    /// 而 Stretch 遇上显式 Width 时会被摆到容器正中 —— 表现就是「标签在最左边、
    /// 输入框飘到中间」，几行之间还各飘各的。所有表单都只能从这里出去。
    /// </summary>
    private UIElement Labelled(string label, FrameworkElement control, double width = 0)
    {
        if (width > 0) control.Width = width;
        control.HorizontalAlignment = HorizontalAlignment.Left;
        control.VerticalAlignment = VerticalAlignment.Center;

        var panel = new StackPanel
        {
            Spacing = 6,
            Padding = new Thickness(0, 6, 0, 6),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        panel.Children.Add(new TextBlock { Text = label, Style = Styled("DeskColumnHeader") });
        panel.Children.Add(control);
        return panel;
    }

    /// <summary>一行并排几个输入框：等宽、基线一致，宽度由调用方给。</summary>
    private UIElement FieldRow(double fieldWidth, params (string Label, FrameworkElement Control)[] fields)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
        foreach (var (label, control) in fields) row.Children.Add(Labelled(label, control, fieldWidth));
        return row;
    }

    private UIElement Note(string text) => new TextBlock
    {
        Text = text,
        Style = Styled("DeskSecondaryText"),
        Margin = new Thickness(0, 4, 0, 4),
    };

    /// <summary>
    /// "现在就要做这件事"的那个按钮：主色、比 Small 大一号。
    /// 同步凭据失效时用它 —— 那一步不做，积压的记录永远上不去云。
    /// </summary>
    private Button PrimaryButton(string text, Action action)
    {
        var button = new Button
        {
            Content = text,
            Style = Styled("DeskPrimaryButton"),
            Height = 30,
            MinWidth = 88,
            FontSize = 13,
        };
        button.Click += (_, _) =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Services.LogError("settings-primary", ex);
                Shell?.Toast("操作失败：" + ex.Message, 5);
            }
        };
        return button;
    }

    private Button Small(string text, Action action, double width = 0)
    {        var button = new Button
        {
            Content = text,
            Style = Styled("DeskButton"),
            Height = 30,
            MinWidth = width > 0 ? width : 64,
            FontSize = 13,
        };
        if (width > 0)
        {
            button.Width = width;
            button.Padding = new Thickness(0);
        }
        button.Click += (_, _) =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Services.LogError("settings-action", ex);
                Shell?.Toast("操作失败：" + ex.Message, 5);
            }
        };
        return button;
    }

    private static Style Styled(string key) => (Style)Application.Current.Resources[key];

    /// <summary>
    /// 原生 SelectorBar：一排互斥选项。选中态、指示条、键盘左右切换都由系统控件负责，
    /// 以前这里是「一排 Button + 给选中那个换主按钮样式」—— 那等于自己实现了一遍单选。
    /// </summary>
    private static SelectorBar Selector(string[] labels, int selected, Action<int> onPick)
    {
        var bar = new SelectorBar { HorizontalAlignment = HorizontalAlignment.Left };
        var items = new List<SelectorBarItem>();

        for (var i = 0; i < labels.Length; i++)
        {
            var item = new SelectorBarItem { Text = labels[i], Tag = i };
            bar.Items.Add(item);
            items.Add(item);
        }

        if (selected >= 0 && selected < items.Count) bar.SelectedItem = items[selected];

        bar.SelectionChanged += (s, _) =>
        {
            if (s.SelectedItem?.Tag is int index) onPick(index);
        };
        return bar;
    }
}
