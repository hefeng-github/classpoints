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

    public DeskSettingsPage()
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
            SubText.Text = $"{Services.ClassName} · 班级码 {Services.Settings.ClassCode} · 数据在 {Database.RootDir}";

            Sections.Children.Clear();
            BuildClassSection();
            BuildThisMachineSection();
            BuildAppearanceSection();
            BuildOnScreenSection();
            BuildSecuritySection();
            BuildMemberSection();
            BuildReaderSection();
            BuildRuleSection();
            BuildBackupSection();
            BuildProvisionSection();
            BuildSyncSection();
            BuildUpdateSection();
            BuildAboutSection();
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
        var body = Section("班级信息", "只有班级名会显示在大屏上。");

        var nameBox = new TextBox { Text = Services.Settings.ClassName, Width = 300 };
        var save = Small("保存", () =>
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
            "电脑端配对要用它。没连云时只是个标识。"));

        body.Children.Add(ValueRow("数据文件", Database.DefaultDbPath,
            "积分、成绩、名单都在这一个文件里。备份请用「立即备份」，别手动复制正在写的库。"));

        var seatButton = Small("打开名单与座位", () => Shell?.Navigate(AppNav.Students));
        body.Children.Add(Labelled($"座位布局　{Services.Settings.SeatRows} 排 × {Services.Settings.SeatCols} 座",
            seatButton));

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
        var body = Section("这台电脑", "同一个程序三种用法。换形态只换界面，名单和积分一份都不动。");

        body.Children.Add(ValueRow("当前形态", App.Mode.ToDisplay() + " · " + App.Mode.ToHint(),
            "形态存在本机，不影响别的机器。"));

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var mode in new[] { AppMode.Board, AppMode.Desk })
        {
            var button = Small(mode.ToDisplay(), () => SwitchMode(mode));
            if (mode == App.Mode) button.Style = Styled("DeskPrimaryButton");
            row.Children.Add(button);
        }
        body.Children.Add(Labelled("界面形态", row));
        body.Children.Add(Note("两端功能一样，只是排版不同：大屏适合触屏，电脑适合键鼠。"));

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
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
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

    // ---------------- 外观 ----------------

    private void BuildAppearanceSection()
    {
        var body = Section("外观", "浅色是默认，投影出来更清楚。切换立即生效。");

        var current = Services.Settings.Theme;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };

        (AppTheme Theme, string Label)[ ] choices =
        [
            (AppTheme.Light, "浅色"),
            (AppTheme.Dark, "深色"),
            (AppTheme.System, "跟随系统"),
        ];

        foreach (var (theme, label) in choices)
        {
            var button = Small(label, () =>
            {
                App.ApplyTheme(theme);
                Reload();
            });
            if (theme == current) button.Style = Styled("DeskPrimaryButton");
            row.Children.Add(button);
        }

        body.Children.Add(Labelled("配色", row));
        body.Children.Add(Note("两端共用。这里切成深色，大屏下次同步也会跟着变。"));
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
        var body = Section("计时与音效", "只影响这台机器。大屏和电脑各存一份，不会互相覆盖。");

        body.Children.Add(NumberField("空闲回看板", "有人选中学生时不计时", 10, 600, 10,
            () => s.IdleTimeoutSec, v => s.IdleTimeoutSec = (int)v, " 秒"));
        body.Children.Add(NumberField("管理模式锁定", "超时只退管理态，不退身份", 60, 3600, 30,
            () => s.LockTimeoutSec, v => s.LockTimeoutSec = (int)v, " 秒"));
        body.Children.Add(NumberField("查分页超时", "到点回看板并清屏", 10, 300, 10,
            () => s.LookupTimeoutSec, v => s.LookupTimeoutSec = (int)v, " 秒"));
        body.Children.Add(NumberField("看板轮播", "榜单和动态的切换间隔", 8, 120, 4,
            () => s.BoardRotateSec, v => s.BoardRotateSec = (int)v, " 秒"));

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

    private UIElement NumberField(string label, string hint, double min, double max, double step,
        Func<double> get, Action<double> set, string unit)
    {
        var box = new NumberBox
        {
            Value = get(),
            Minimum = min,
            Maximum = max,
            SmallChange = step,
            LargeChange = step * 3,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            Width = 160,
            VerticalAlignment = VerticalAlignment.Center,
        };
        box.ValueChanged += (_, e) =>
        {
            if (double.IsNaN(e.NewValue)) return;
            set(e.NewValue);
        };

        var line = Wrap(box, ValueText(unit));
        return LabelledHint(label, hint, line);
    }

    private UIElement LabelledHint(string label, string hint, UIElement control)
    {
        var panel = new StackPanel { Spacing = 4, Padding = new Thickness(0, 6, 0, 6) };
        panel.Children.Add(new TextBlock { Text = label, Style = Styled("DeskColumnHeader") });
        panel.Children.Add(new TextBlock { Text = hint, Style = Styled("DeskSecondaryText") });
        panel.Children.Add(control);
        return panel;
    }

    // ---------------- 读卡器 ----------------

    private Grid? _lampGrid;
    private TextBlock? _readerStatusText;
    private ComboBox? _portCombo;
    private DispatcherTimer? _lampTimer;

    /// <summary>
    /// 读卡器自检。原来只在大屏设置里有 —— 而现在两端共用这一个设置页，
    /// 老师在哪台机器上插的读卡器就在哪台机器上配。
    /// </summary>
    private void BuildReaderSection()
    {
        var body = Section("读卡器", "PN532 转串口，插上后在设备管理器里会多一个 COM 口。没硬件也能用模拟通道验链路。");

        _lampGrid = new Grid { ColumnSpacing = 18, Margin = new Thickness(0, 4, 0, 0) };
        for (var i = 0; i < 3; i++)
            _lampGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _readerStatusText = new TextBlock { Style = Styled("DeskSecondaryText") };

        _portCombo = new ComboBox { MinWidth = 260, Height = 34 };
        foreach (var port in Pn532Serial.ListPorts())
            _portCombo.Items.Add(new ComboBoxItem { Content = port.Display, Tag = port.PortName });

        if (_portCombo.Items.Count == 0)
            _portCombo.Items.Add(new ComboBoxItem { Content = "没有发现串口设备", Tag = "" });

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
            Services.Reader.Start(string.IsNullOrEmpty(port) ? null : port);
            RefreshLamps();
        };

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Bottom };
        actions.Children.Add(Small("重新连接", () =>
        {
            Services.Reader.Stop();
            Services.Reader.Start(string.IsNullOrWhiteSpace(Services.Settings.ReaderPort)
                ? null
                : Services.Settings.ReaderPort);
            RefreshLamps();
        }));
        actions.Children.Add(Small("模拟刷一张卡", () =>
        {
            Services.Reader.SimulateSwipe("04A1B2C3D4E5F6");
            Shell?.Toast("已模拟一次刷卡（没有硬件时用来验链路）", 5);
        }));

        var portLine = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        portLine.Children.Add(_portCombo);
        portLine.Children.Add(actions);

        body.Children.Add(Labelled("端口", portLine));
        body.Children.Add(_lampGrid);
        body.Children.Add(_readerStatusText);
        body.Children.Add(Note("三个灯：设备（串口打开）· 帧（握手通过）· 卡（读到过卡）。三个都亮才真能用。"));

        Services.Reader.StatusChanged -= OnReaderStatus;
        Services.Reader.StatusChanged += OnReaderStatus;

        _lampTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _lampTimer.Tick -= OnLampTick;
        _lampTimer.Tick += OnLampTick;
        _lampTimer.Start();

        RefreshLamps();
    }

    private void OnLampTick(object? sender, object e) => RefreshLamps();

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
            _ => "已停止（点「重新连接」）",
        };
        _readerStatusText.Foreground = status.State switch
        {
            ReaderState.Running => Pal.Positive,
            ReaderState.Error => Pal.Negative,
            ReaderState.NoDevice => Pal.Warn,
            _ => Pal.Secondary,
        };
    }

    // ---------------- 密码与恢复码 ----------------

    private void BuildSecuritySection()
    {
        var body = Section("密码与恢复码",
            "谁输对了密码，就按谁的角色给权限：班主任和班长进管理，班委只记分。");

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

        // 恢复码：明文只出现一次，所以生成之后必须能立刻存下来 ——
        // 只让人"抄到纸上"是不够的，教室里找不到笔的那次就白生成了。
        var codeText = new TextBlock
        {
            Text = "还没有生成",
            FontFamily = (FontFamily)Application.Current.Resources["NumberFont"],
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Foreground = Pal.Warn,
            IsTextSelectionEnabled = true,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var generate = Small(Services.Settings.HasRecovery ? "重新生成" : "生成恢复码", async () =>
        {
            if (Shell is not { } shell) return;
            if (!await shell.AskPinAsync("生成恢复码", "需要管理密码", requireManager: true)) return;

            var code = RecoveryCode.Generate();
            Services.Settings.RecoveryCodeHash = PasswordHasher.Hash(RecoveryCode.Normalize(code));
            codeText.Text = code;
            Services.Audit.Add("recovery.generate", Services.Session.Current?.Id, null, null);
            shell.Toast("已生成。现在就存一份，关掉就看不到了。", 8);
        });

        var saveTxt = Small("保存为 txt", () =>
        {
            var code = codeText.Text;
            if (code.Length < 8 || code == "还没有生成")
            {
                Shell?.Toast("先生成恢复码");
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

        var printCard = Small("打印恢复卡", () => Shell?.Navigate(AppNav.Print));

        body.Children.Add(Labelled("恢复码", Wrap(codeText, generate, saveTxt, printCard)));
        body.Children.Add(Note("只存哈希，明文只出现这一次。忘了密码就靠它，丢了只能重置数据。"));
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
        var body = Section("成员与权限",
            "班主任和班长能进管理模式；班委只能记分。密码和卡都能用，输密码时按角色给权限。");

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

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

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

        var panel = new StackPanel { Spacing = 10, Width = 360 };
        panel.Children.Add(new TextBlock
        {
            Text = "两种方式：\n· 在大屏「设置 → 班委管理」点「发卡」再刷卡（推荐）。\n" +
                   "· 在这里手输 UID：手机 NFC 工具读，或抄卡面丝印。",
            Style = Styled("DeskSecondaryText"),
        });
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

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

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

        await new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"{m.DisplayName} 的卡",
            Content = panel,
            CloseButtonText = "关闭",
        }.ShowAsync();

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

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        Services.Members.Revoke(m.Id);
        Services.Audit.Add("member.revoke", null, m.Id, m.DisplayName);
        Reload();
    }

    // ---------------- 加分项 ----------------

    private bool _showArchivedRules;

    private void BuildRuleSection()
    {
        var body = Section("加分与扣分项",
            "分值是输入框，直接改数字就存。负数是扣分。名称、图标、封顶点「编辑」。");

        var rules = Services.Rules.ListAll(includeArchived: _showArchivedRules);
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
            head.Children.Add(new TextBlock
            {
                Text = $"{rule.Label}　{rule.DeltaText}",
                Style = Styled("DeskBodyText"),
                Opacity = rule.IsArchived ? 0.5 : 1,
            });
            info.Children.Add(head);
            info.Children.Add(new TextBlock
            {
                Text = rule.HasCap
                    ? $"封顶：{(rule.CapScope == CapScope.Single ? "单次最多" : "每周期最多")} {rule.CapValue}"
                    : "封顶：不封顶",
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
                var delta = new NumberBox
                {
                    Value = rule.Delta,
                    Minimum = -500,
                    Maximum = 500,
                    SmallChange = 5,
                    SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
                    Width = 132,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                var captured = rule;
                delta.ValueChanged += (_, e) =>
                {
                    if (double.IsNaN(e.NewValue)) return;
                    var next = (int)e.NewValue;
                    if (next == captured.Delta) return;
                    captured.Delta = next;
                    Services.Rules.Save(captured);
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
        var deltaBox = new NumberBox
        {
            Header = "默认分（负数表示扣分）",
            Value = rule.Delta,
            Minimum = -500,
            Maximum = 500,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            Width = 200,
        };

        var icon = rule.Glyph;
        var iconPicker = Controls.IconChooser.Build(IconCatalog.RuleIcons, icon, g => icon = g);

        var capCombo = new ComboBox { Header = "封顶", Width = 240 };
        capCombo.Items.Add("不封顶");
        capCombo.Items.Add("单次最多");
        capCombo.Items.Add("每周期最多");
        capCombo.SelectedIndex = rule.HasCap ? (rule.CapScope == CapScope.Single ? 1 : 2) : 0;

        var capBox = new NumberBox
        {
            Header = "上限值",
            Value = rule.CapValue ?? 50,
            Minimum = 1,
            Maximum = 10000,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
            Width = 200,
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
            Text = "「入账确认」不可关闭：分账总要对得上。",
            Style = Styled("DeskSecondaryText"),
        });

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = isNew ? "新建加分项" : "编辑加分项",
            Content = panel,
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var label = NameNormalizer.Clean(labelBox.Text);
        if (label.Length == 0)
        {
            Shell?.Toast("名称不能为空");
            return;
        }

        rule.Label = label;
        rule.Delta = double.IsNaN(deltaBox.Value) ? 10 : (int)deltaBox.Value;
        rule.Icon = IconCatalog.Normalize(icon);
        rule.CapValue = capCombo.SelectedIndex == 0 ? null : (int)capBox.Value;
        rule.CapScope = capCombo.SelectedIndex == 0 ? CapScope.None
            : capCombo.SelectedIndex == 1 ? CapScope.Single : CapScope.Period;

        Services.Rules.Save(rule);
        Reload();
        Shell?.Toast(isNew ? $"已新建「{label}」" : "已保存", 2);
    }

    // ---------------- 备份 ----------------

    private void BuildBackupSection()
    {
        var body = Section("备份与恢复",
            "整库快照 + 可选 Excel。每天首次启动自动备份，留 30 天。");

        body.Children.Add(ValueRow("备份目录", Services.Backups.BackupRoot,
            "建议改到 OneDrive 之类同步目录，换机器能直接拿到。"));

        body.Children.Add(ValueRow("上次备份", Services.Backups.LastBackupText,
            $"状态：{StalenessText()}"));

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(Small("立即备份", () =>
        {
            try
            {
                var path = Services.Backups.BackupNow("manual", Services.BuildExportBundle());
                Shell?.Toast("已备份：" + Path.GetFileName(path), 4);
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
            Shell?.Toast("已按规则清理（保留 30 天 + 每月 1 号）", 4);
            Reload();
        }));
        buttons.Children.Add(Small("整包导出（换机用）", ExportPack));
        buttons.Children.Add(Small("从整包导入…", ImportPack));
        body.Children.Add(buttons);

        var list = Services.Backups.List(8);
        if (list.Count == 0)
        {
            body.Children.Add(Note("还没有备份文件。"));
        }
        else
        {
            body.Children.Add(new TextBlock
            {
                Text = "最近的备份（恢复会覆盖当前库，先留档）",
                Style = Styled("DeskColumnHeader"),
                Margin = new Thickness(0, 8, 0, 2),
            });

            foreach (var file in list)
            {
                var row = new Grid { Padding = new Thickness(0, 4, 0, 4) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var info = new StackPanel { Spacing = 1 };
                info.Children.Add(new TextBlock { Text = file.Name, Style = Styled("DeskBodyText") });
                info.Children.Add(new TextBlock
                {
                    Text = $"{file.KindText} · {file.AtText} · {file.SizeText}",
                    Style = Styled("DeskSecondaryText"),
                });
                row.Children.Add(info);

                var restore = Small("恢复", () => RestoreFrom(file));
                Grid.SetColumn(restore, 1);
                row.Children.Add(restore);

                body.Children.Add(row);
            }
        }
    }

    private string StalenessText() => Services.Backups.StalenessLevel switch
    {
        0 => "今天备份过",
        1 => "两三天没备份了",
        _ => "一周没备份了，数据只有一份",
    };

    private async void RestoreFrom(BackupFile file)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "用这份备份覆盖当前数据？",
            Content = $"{file.Name}\n\n当前库会改名留档（不删），再用这份恢复。恢复完重启软件。",
            PrimaryButtonText = "恢复",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var check = Services.Backups.Restore(file.Path);
        Shell?.Toast(check.Message, check.Ok ? 8 : 8);
        Services.Audit.Add("backup.restore", null, file.Name, check.Message);
        if (check.Ok) Reload();
    }

    private async void ExportPack()
    {
        try
        {
            var path = Services.Backups.ExportPack();
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

            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

            var check = Services.Backups.ImportPack(path);
            Shell?.Toast(check.Message, 10);
            Services.Audit.Add("pack.import", null, Path.GetFileName(path), check.Message);
            if (check.Ok) Reload();
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
        var body = Section("预配置",
            "在家里配好云端地址、班级码、座位，连名单一起存成文件带到学校。" +
            "学校那台导入后只剩定分值和试一下。");

        var credentials = new SyncStore().Load();
        body.Children.Add(ValueRow("当前可下发的配置",
            string.IsNullOrWhiteSpace(Services.ClassName) ? "还没配班级信息" : Services.ClassName,
            credentials is null
                ? "这台还没连云端。导出的是纯本地配置（班级名 + 座位）。"
                : $"云端 {credentials.BaseUrl}，班级码 {credentials.ClassCode}。" +
                  "文件里带班级码和配对口令，到学校导入即自动配对。"));

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(Async("导出配置包…", ExportBundleAsync));
        buttons.Children.Add(Async("导入配置包…", ImportBundleAsync));
        body.Children.Add(buttons);

        body.Children.Add(Note("配对口令用配置包口令加密。U 盘丢了也看不到班主任密码。" +
                              "不设口令也能导出，只是文件里不带密码。"));
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

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

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

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
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

    private void BuildSyncSection()
    {
        var sync = Services.Sync;
        var configured = sync.IsConfigured;
        var creds = sync.IsConfigured ? new SyncStore().Load() : null;

        var body = Section("同步",
            configured
                ? $"已连接到 {creds?.BaseUrl}。两端各记各的，靠 UUID 幂等合并，断了网也照常用。"
                : "这台电脑目前是纯本地模式：它就是数据的唯一副本。「备份」就是你的同步。");

        body.Children.Add(ValueRow("同步状态", Services.Pump.StatusText,
            "本地写入永远先成功，同步只在后台跑。断网时界面只会多一个「待推送」角标。"));
        body.Children.Add(ValueRow("待推送", Services.Outbox.Count() + " 条",
            "每条写操作都有幂等键，重连后重复提交不会重复入账。"));

        if (configured)
        {
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            buttons.Children.Add(Async("立即同步", async () =>
            {
                var report = await Services.Pump.RunAsync();
                Shell?.Toast(report is null ? "同步没有执行" : report.Ok
                    ? $"推送 {report.Pushed} 条，拉取 {report.Pulled} 条"
                    : $"同步失败：{report.Error}", 5);
                Reload();
            }));
            buttons.Children.Add(Small("换一台设备配对…", () =>
            {
                sync.SignOut();
                Shell?.Toast("凭据已清除，下面可以重新配对", 5);
                Reload();
            }));
            body.Children.Add(buttons);
            return;
        }

        // 还没配对：两个入口。建班只该做一次，配对是每台设备做一次。
        var urlBox = new TextBox
        {
            PlaceholderText = "https://classpoints-api.你的账号.workers.dev",
            Width = 460,
            Height = 38,
            VerticalContentAlignment = VerticalAlignment.Center,
            Text = "http://127.0.0.1:8787",
        };
        var codeBox = new TextBox
        {
            PlaceholderText = "8 位，建班时给的",
            Width = 220,
            Height = 38,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        var pinBox = new PasswordBox
        {
            PasswordChar = "●",
            Width = 220,
            Height = 38,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        // 服务端配了 BOOTSTRAP_SECRET 才需要填。填了只在这一个请求里用，
        // 不存盘 —— 它是部署口令，不属于这台电脑该记得的东西。
        var bootstrapBox = new PasswordBox
        {
            PasswordChar = "●",
            Width = 220,
            Height = 38,
            VerticalContentAlignment = VerticalAlignment.Center,
            PlaceholderText = "没设就留空",
        };

        body.Children.Add(Labelled("服务器地址", urlBox));

        // 两个输入框并排。各自用 Labelled 把标签压在框上方，
        // 和上面「班级名称」用同一套写法，高度与基线才不会叉开。
        var pairRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        pairRow.Children.Add(Labelled("班级码", codeBox));
        pairRow.Children.Add(Labelled("班主任密码", pinBox));
        body.Children.Add(pairRow);

        body.Children.Add(Labelled("部署口令（建班用，服务端没设就留空）", bootstrapBox));

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
                Services.Log($"同步配对成功 {baseUrl}");
                Services.Pump.Start();
                Shell?.Toast(report.Ok ? "配对成功，开始后台同步" : "配对失败：" + report.Error, 5);
                Reload();
            }
            catch (Exception ex)
            {
                Services.LogError("sync-pair", ex);
                Shell?.Toast("配对失败：" + ex.Message, 6);
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

                var report = await sync.CreateClassAsync(
                    baseUrl, Services.ClassName, Services.DeskOperator.DisplayName, pin,
                    Crypto.RandomHex(4).ToUpperInvariant(), "desk", bootstrapBox.Password);
                Services.Log($"建班成功 {baseUrl}");
                Services.Pump.Start();
                Shell?.Toast($"建班成功，班级码 {report.CreatedCode}（大屏配对要用它）", 10);
                Reload();
            }
            catch (Exception ex)
            {
                Services.LogError("sync-create-class", ex);
                Shell?.Toast("建班失败：" + ex.Message, 6);
            }
        }));
        body.Children.Add(actions);

        body.Children.Add(Note("先把云端跑起来：worker/ 目录下 wrangler d1 migrations apply --remote，" +
            "再 wrangler deploy，然后把地址填到上面。没配也不影响用，只是换机器要靠整包导入。"));
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
        var body = Section("更新", "只接收正式版本。升级会先关掉本程序，装完自动重新打开。");

        body.Children.Add(ValueRow("当前版本", "班级积分 v" + AppServices.AppVersion,
            UpdateService.IsInstalled
                ? "安装版：可以直接在应用内升级到新版本。"
                : "便携版：只能下载新包自己解压覆盖，不会自动安装。数据在 AppData 里，覆盖程序不影响。"));

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
        feedRow.Children.Add(Small("保存", () =>
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
            ? "还没确定版本库地址。填一个公开版本库根地址（https://…/<用户>/<仓库>）就能接收更新。"
            : "实际使用：" + resolved));

        body.Children.Add(Note("直接连公开版本库下载，不经过同步服务，也不需要任何账号或令牌。" +
            "只认 v主.次.修 的正式版本，下载后先核对大小和 SHA-256，对不上就不装。"));
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

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
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

        body.Children.Add(ValueRow("版本", "班级积分 v" + AppServices.AppVersion,
            "数据库表结构版本 " + Sql.SchemaVersion + "。升级时只做向前迁移，不降级。"));
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
        var panel = new StackPanel { Spacing = 1, Padding = new Thickness(0, 6, 0, 6) };
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

    private UIElement Labelled(string label, UIElement control)
    {
        var panel = new StackPanel { Spacing = 6, Padding = new Thickness(0, 6, 0, 6) };
        panel.Children.Add(new TextBlock { Text = label, Style = Styled("DeskColumnHeader") });
        panel.Children.Add(control);
        return panel;
    }

    private UIElement Note(string text) => new TextBlock
    {
        Text = text,
        Style = Styled("DeskSecondaryText"),
        Margin = new Thickness(0, 4, 0, 4),
    };

    private Button Small(string text, Action action)
    {
        var button = new Button
        {
            Content = text,
            Style = Styled("DeskButton"),
            Height = 30,
            MinWidth = 64,
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
                Services.LogError("settings-action", ex);
                Shell?.Toast("操作失败：" + ex.Message, 5);
            }
        };
        return button;
    }

    private static Style Styled(string key) => (Style)Application.Current.Resources[key];
}
