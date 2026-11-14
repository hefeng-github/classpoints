using ClassPoints.App.Services;
using ClassPoints.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClassPoints.App.Controls;

/// <summary>
/// 「当前是谁」的小胶囊。页面挂上它就不用各自去拼一段文字 ——
/// 身份文案只在 <see cref="BoardSession.IdentityText"/> 和这里各出现一次。
/// </summary>
public sealed partial class IdentityChip : UserControl
{
    private bool _hooked;

    public IdentityChip()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private static BoardSession? Session => App.Services?.Session;

    private bool _showSwitch = true;

    /// <summary>不显示「切换」按钮（外壳自己的常驻状态条上用）。</summary>
    public bool ShowSwitch
    {
        get => _showSwitch;
        set
        {
            _showSwitch = value;
            Refresh();
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_hooked && Session is { } session)
        {
            session.Changed += Refresh;
            _hooked = true;
        }
        Refresh();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_hooked && Session is { } session)
        {
            session.Changed -= Refresh;
            _hooked = false;
        }
    }

    public void Refresh()
    {
        var session = Session;
        if (session is null) return;

        // 待审设备：这里不是"未认人"，是"这台机器还不许写"。
        // 两者长得像（都不能记分），但下一步动作完全相反：一个是去刷卡，
        // 一个是等班主任在网页上点通过 —— 文案必须分开。
        if (session.ReadOnly)
        {
            NameText.Text = "待审核 · 只读";
            RoleText.Text = "等班主任点通过";
            AvatarText.Text = "待";
            Avatar.Background = Theme.Pal.Warn;
            ExitButton.Visibility = Visibility.Collapsed;
            // 只读时「切换」也是个死按钮（点了只会弹"等班主任通过"），干脆收掉，
            // 顺便把宽度让给"待审核 · 只读"这行字 —— 侧栏只有 248 宽。
            SwitchButton.Visibility = Visibility.Collapsed;
            return;
        }

        SwitchButton.Visibility = _showSwitch ? Visibility.Visible : Visibility.Collapsed;

        var who = session.Current;
        ExitButton.Visibility = who is null ? Visibility.Collapsed : Visibility.Visible;
        if (who is null)
        {
            NameText.Text = "未认人";
            RoleText.Text = "只能查看";
            AvatarText.Text = "?";
            Avatar.Background = Theme.Pal.StrokeStrong;
            return;
        }

        NameText.Text = who.DisplayName;
        RoleText.Text = session.IsAdmin ? who.Role.ToDisplay() + " · 管理中" : who.Role.ToDisplay();
        AvatarText.Text = who.DisplayName.Length > 0 ? who.DisplayName[..1] : "?";
        Avatar.Background = who.IsManager ? Theme.Pal.Accent : Theme.Pal.Secondary;
    }

    private async void OnSwitchClick(object sender, RoutedEventArgs e)
    {
        if (App.Host is not { } host) return;
        await host.SwitchIdentityAsync();
        Refresh();
    }

    private void OnExitClick(object sender, RoutedEventArgs e)
    {
        if (Session?.Current is null) return;
        if (App.Host is not { } host) return;
        host.ExitIdentity();
        Refresh();
    }
}
