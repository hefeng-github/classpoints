using ClassPoints.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using ClassPoints.App.Theme;

namespace ClassPoints.App.Controls;

/// <summary>
/// 自绘分数键盘（右侧抽屉）。系统的数字键盘有三个毛病：弹出慢、遮半屏、样式脱节，
/// 而一体机常常根本没配键盘 —— 所以分数与密码都走这一套。
/// </summary>
public sealed partial class NumberPad : UserControl
{
    private static readonly SolidColorBrush Warn = Pal.Warn;
    private static readonly SolidColorBrush TextSecondary = Pal.Secondary;

    private Rule? _rule;
    private int _peopleCount;
    private int _raw;
    private bool _negative;
    private int _alreadyInPeriod;

    /// <summary>
    /// 面板打开时预填的是这一项的默认分。第一个数字键应当替换它，而不是接在后面：
    /// 默认 +10，想给 8 却得到 108，是这套键盘之前最容易被当成"按键失灵"的一处。
    /// </summary>
    private bool _freshEntry;

    public NumberPad()
    {
        InitializeComponent();
    }

    /// <summary>确定：delta 是最终计入值，note 是输入的理由。</summary>
    public event Action<int, string?>? Confirmed;

    /// <summary>超出单次上限、用户要求按原值计入 —— 上层先要管理密码，再调 <see cref="ConfirmForce"/>。</summary>
    public event Action<int, string?>? OverCapRequested;

    public event Action? Cancelled;

    public bool IsOpen => Visibility == Visibility.Visible;

    private int EffectiveValue
    {
        get
        {
            if (_rule is null) return _raw;
            if (!_rule.HasCap || _rule.CapScope != CapScope.Single || _rule.CapValue is not int cap) return _raw;
            if (_raw > 0 && _raw > cap) return cap;
            return _raw;
        }
    }

    public int SignedValue => _negative ? -EffectiveValue : EffectiveValue;

    public int RawSignedValue => _negative ? -_raw : _raw;

    /// <summary>打开键盘：预填该类型默认分（想给 8 直接按 8 就会覆盖）。</summary>
    public void Show(Rule rule, int peopleCount, int alreadyInPeriod = 0)
    {
        _rule = rule;
        _peopleCount = peopleCount;
        _alreadyInPeriod = alreadyInPeriod;
        _negative = rule.Delta < 0;
        _raw = System.Math.Abs(rule.Delta);
        _freshEntry = true;

        TitleText.Text = rule.Label;
        ReasonBox.Text = "";
        ValueText.Text = "";
        Refresh();
        Visibility = Visibility.Visible;
        // 不抢焦点：一体机上 AutoFocus 会把系统软键盘顶上来，正好盖住刚打开的面板。
        // 要写理由就自己点一下输入框。
    }

    public void Hide()
    {
        Visibility = Visibility.Collapsed;
    }

    private string? Reason()
    {
        var text = (ReasonBox.Text ?? "").Trim();
        return text.Length == 0 ? null : text;
    }

    private void OnDigitClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string digit) return;
        if (!int.TryParse(digit, out var d)) return;

        if (_freshEntry)
        {
            _raw = d;
            _freshEntry = false;
            Refresh();
            return;
        }

        // 四位数封顶：这个系统里没有需要五位数的分值，多按一位只可能是手滑
        if (_raw > 999) return;
        _raw = _raw * 10 + d;
        if (_raw > 9999) _raw = d;
        Refresh();
    }

    private void OnBackspaceClick(object sender, RoutedEventArgs e)
    {
        _freshEntry = false;
        _raw /= 10;
        Refresh();
    }

    private void OnSignClick(object sender, RoutedEventArgs e)
    {
        _negative = !_negative;
        Refresh();
    }

    private void Refresh()
    {
        var effective = SignedValue;
        ValueText.Text = (effective >= 0 ? "+" : "") + effective;
        ValueText.Foreground = effective >= 0 ? Pal.Positive : Pal.Negative;

        var overCap = _rule is not null && _rule.HasCap && _rule.CapScope == CapScope.Single
                      && _rule.CapValue is int cap && _raw > cap && !_negative;

        if (overCap && _rule!.CapValue is int capValue)
        {
            HintText.Text = $"单次上限 {capValue}，已压到 {capValue}";
            HintText.Foreground = Warn;
            OverCapButton.Visibility = Visibility.Visible;
            OverCapButton.Content = $"要按 {_raw} 计入";
        }
        else
        {
            HintText.Text = effective == 0
                ? "分值为 0 不会入账"
                : _rule is null ? "" : DescribeScope();
            HintText.Foreground = effective == 0 ? Warn : TextSecondary;
            OverCapButton.Visibility = Visibility.Collapsed;
        }

        ScopeText.Text = _peopleCount <= 1 ? "" : $"将给 {_peopleCount} 人各记一次";

        // 0 分不是一次操作：入账层会写出一条 delta=0 的记录，榜单没变化却多了一条流水。
        ConfirmButton.IsEnabled = effective != 0;
        ConfirmButton.Content = _peopleCount <= 1
            ? effective == 0 ? "确定" : $"确定 {(effective >= 0 ? "+" : "")}{effective}"
            : effective == 0 ? "确定" : $"确定 · {_peopleCount} 人 各 {(effective >= 0 ? "+" : "")}{effective}";
    }

    private string DescribeScope()
    {
        if (_rule is null) return "";
        if (!_rule.HasCap) return "不封顶";
        return _rule.CapScope == CapScope.Single
            ? $"单次上限 {_rule.CapValue}"
            : $"每周期上限 {_rule.CapValue}（本周期已得 {_alreadyInPeriod}）";
    }

    private void OnConfirmClick(object sender, RoutedEventArgs e)
    {
        if (SignedValue == 0) return;
        Confirmed?.Invoke(SignedValue, Reason());
    }

    private void OnOverCapClick(object sender, RoutedEventArgs e)
    {
        OverCapRequested?.Invoke(RawSignedValue, Reason());
    }

    /// <summary>密码校验通过后按原值入账。</summary>
    public void ConfirmForce() => Confirmed?.Invoke(RawSignedValue, Reason());

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        Hide();
        Cancelled?.Invoke();
    }

    private void OnScrimTapped(object sender, TappedRoutedEventArgs e)
    {
        // 点空白处关掉：代价只是重来一次，不值得做成模态
        Hide();
        Cancelled?.Invoke();
    }
}
