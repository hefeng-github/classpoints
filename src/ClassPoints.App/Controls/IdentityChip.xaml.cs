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

    /// <summary>不显示「切换」按钮（外壳自己的常驻状态条上用）。</summary>
    public bool ShowSwitch
    {
        get => SwitchButton.Visibility == Visibility.Visible;
        set => SwitchButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
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

        var who = session.Current;
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
}
