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

    private void Reload()
    {
        try
        {
            SubText.Text = $"{Services.ClassName} · 班级码 {Services.Settings.ClassCode} · 数据在 {DataDir}";

            Sections.Children.Clear();
            BuildClassSection();
            BuildThisMachineSection();
            BuildAppearanceSection();
            BuildOnScreenSection();
            BuildSecuritySection();
            BuildMemberSection();
            BuildReaderSection();
            BuildRuleSection();
            BuildSchemeSection();
            BuildBackupSection();
            BuildLocalDataSection();
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
        var body = Section("班级信息", "班级名显示在大屏顶部。");

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
            "另一台设备配对时要用它。没连云时只是个标识。"));

        body.Children.Add(ValueRow("数据文件", Services.Db.DbPath,
            "名单、积分、成绩都在这一个文件里。备份用下面的「立即备份」，不要手动复制正在写的库。"));

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
        var body = Section("这台电脑", "一个程序两种排版，换形态不动数据。");

        // 当前是哪一种，看按钮就够了（选中的那个是实心蓝）。
        // 原来还单列一行「当前形态 · 触屏用…」，和下面的按钮说的是同一件事。
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var mode in new[] { AppMode.Board, AppMode.Desk })
        {
            var button = Small(mode.ToDisplay(), () => SwitchMode(mode));
            if (mode == App.Mode) button.Style = Styled("DeskPrimaryButton");
            row.Children.Add(button);
        }
        body.Children.Add(LabelledHint("界面形态",
            App.Mode.ToDisplay() + " · " + App.Mode.ToHint(), row));

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
        var body = Section("外观", "浅色为默认，投影更清楚。切换立即生效。");

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
        body.Children.Add(NumberField("看板轮播", "榜单与动态的切换间隔", 8, 120, 4,
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
    private ComboBox? _portCombo;
    private DispatcherTimer? _lampTimer;

    /// <summary>
    /// 读卡器自检。原来只在大屏设置里有 —— 而现在两端共用这一个设置页，
    /// 老师在哪台机器上插的读卡器就在哪台机器上配。
    /// </summary>
    private void BuildReaderSection()
    {
        var body = Section("读卡器", "PN532 串口读卡器。没有硬件可用模拟刷卡验证链接。");

        _lampGrid = new Grid { ColumnSpacing = 18, Margin = new Thickness(0, 4, 0, 0) };
        for (var i = 0; i < 3; i++)
            _lampGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _readerStatusText = new TextBlock { Style = Styled("DeskSecondaryText") };

        _portCombo = new ComboBox { MinWidth = 260, Height = 34 };

        // 第一项是"未连接"。以前列表直接选中第一个真实端口，而读卡器其实没启动，
        // 看起来像已经在用 COM1 了；而且办公室电脑的 COM1 往往是主板串口。
        _portCombo.Items.Add(new ComboBoxItem { Content = "未连接", Tag = "" });
        foreach (var port in Pn532Serial.ListPorts())
            _portCombo.Items.Add(new ComboBoxItem { Content = port.Display, Tag = port.PortName });

        if (_portCombo.Items.Count == 1)
            _portCombo.Items.Add(new ComboBoxItem
            {
                Content = "没有发现串口设备",
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
            if (!string.IsNullOrEmpty(port)) Services.Reader.Start(port);
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
            Services.Reader.Start(port);
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
        body.Children.Add(Note("设备（串口已打开）· 帧（握手通过）· 卡（读到过卡），三个都亮才可用。"));

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

        var printCard = Small("打印恢复卡", () => Shell?.Navigate(AppNav.Print));

        body.Children.Add(Labelled("恢复码", Wrap(codeText, generate, saveTxt, printCard)));
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
            result = await dialog.ShowAsync();
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
        // 上限值留空时 NumberBox 给的是 NaN，直接取整会变成 0 —— 那等于「一分都不许给」。
        rule.CapValue = capCombo.SelectedIndex == 0
            ? null
            : double.IsNaN(capBox.Value) ? 50 : (int)capBox.Value;
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
        var body = Section("备份与恢复", "每天首次启动自动备份，保留 30 天。");

        var dirBox = new TextBox { Text = Services.Backups.BackupRoot, Width = 460, Height = 32 };
        var dirActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
        };
        dirActions.Children.Add(Small("保存", () =>
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

        var dirLine = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        dirLine.Children.Add(dirBox);
        dirLine.Children.Add(dirActions);
        body.Children.Add(Labelled("备份目录", dirLine));

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
            Shell?.Toast("已清理（保留最近 30 天与每月 1 日）", 4);
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

        // 换成「文档」之后，老备份还在 %LOCALAPPDATA%，别让人以为它们没了
        if (Services.Backups.LegacyCount is > 0 and var legacy)
            body.Children.Add(Note($"另有 {legacy} 份在旧目录 {Database.BackupRoot}，上面同样可恢复。"));
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

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

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

            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

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
        var body = Section("预配置", "把云端地址、班级码、座位与名单导出成一个文件，带到学校导入。");

        var credentials = new SyncStore().Load();
        body.Children.Add(ValueRow("当前可下发的配置",
            string.IsNullOrWhiteSpace(Services.ClassName) ? "还没配班级信息" : Services.ClassName,
            credentials is null
                ? "这台还没连云端。导出的是纯本地配置（班级名、座位与名单）。"
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

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            buttons.Children.Add(Async("立即同步", async () =>
            {
                var report = await Services.Pump.RunAsync();
                Shell?.Toast(report is null ? "同步没有执行" : report.Ok
                    ? $"推送 {report.Pushed} 条，拉取 {report.Pulled} 条"
                    : $"同步失败：{report.Error}", 5);
                Reload();
            }));
            buttons.Children.Add(Small("解除配对…", () =>
            {
                sync.SignOut();
                Shell?.Toast("凭据已清除，可以重新配对", 5);
                Reload();
            }));
            body.Children.Add(buttons);
            return;
        }

        // 还没配对：两个入口。建班只该做一次，配对是每台设备做一次。
        var urlBox = new TextBox
        {
            PlaceholderText = "https://classpoints-api.你的账号.workers.dev",
            Height = 34,
            VerticalContentAlignment = VerticalAlignment.Center,
            Text = "http://127.0.0.1:8787",
        };
        var codeBox = new TextBox
        {
            PlaceholderText = "8 位班级码",
            Height = 34,
            VerticalContentAlignment = VerticalAlignment.Center,
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
                Services.Log($"同步配对成功 {baseUrl}");
                Services.Pump.Start();
                Shell?.Toast(report.Ok ? "配对成功，开始后台同步" : "配对失败：" + report.Error, 5);
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
