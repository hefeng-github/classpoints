using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ClassPoints.App.Controls;

/// <summary>
/// 数字输入：一个输入框 + 减 / 加两个按钮。
///
/// 为什么不用自带的 NumberBox：它要等"焦点离开并且校验通过"才把文本写进 Value。
/// 而这个应用里的数字输入全是**没有保存按钮**的那种 —— 老师把 10 改成 50，
/// 直接去点"确定"，读到的还是 10。界面上明明显示 50，落库却是 10，
/// 这种错在测试里很难复现（自动化都是直接 setValue），在教室里却天天发生。
///
/// 这里的规则只有一条：离开输入框、按回车、点加减，都当场把值定下来，
/// 并且把定下来之后的真实值写回框里 —— 输超范围会立刻看到它被夹回来。
/// </summary>
public sealed class NumberInput : StackPanel
{
    private readonly TextBox _box = new();
    private readonly TextBlock _header = new();
    private double _value;

    private readonly StackPanel _row = new() { Orientation = Orientation.Horizontal, Spacing = 4 };
    private readonly Button _minus;
    private readonly Button _plus;
    private bool _enabled = true;
    private double _minimum = double.MinValue;
    private double _maximum = double.MaxValue;

    public NumberInput()
    {
        Orientation = Orientation.Vertical;
        Spacing = 4;

        _header.Visibility = Visibility.Collapsed;
        Children.Add(_header);

        _minus = StepButton("\u2212", -1);
        _plus = StepButton("+", 1);

        _box.Width = 72;
        _box.Height = 34;
        _box.TextAlignment = TextAlignment.Right;
        _box.VerticalAlignment = VerticalAlignment.Center;
        _box.LostFocus += (_, _) => Commit();
        _box.KeyDown += (_, e) =>
        {
            if (e.Key != Windows.System.VirtualKey.Enter) return;
            Commit();
            e.Handled = true;
        };

        _row.Children.Add(_minus);
        _row.Children.Add(_box);
        _row.Children.Add(_plus);
        Children.Add(_row);
    }

    /// <summary>
    /// 能不能改。StackPanel 本身没有 IsEnabled（那是 Control 的），
    /// 所以这里自己管三件事：输入框、两个按钮、以及"看得出来的灰"。
    /// </summary>
    public bool IsEnabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            _box.IsEnabled = value;
            _minus.IsEnabled = value;
            _plus.IsEnabled = value;
            Opacity = value ? 1 : 0.5;
        }
    }


    /// <summary>框上方的标签（对应 NumberBox 的 Header）。设了才显示。</summary>
    public string Header
    {
        get => _header.Text;
        set
        {
            _header.Text = value;
            _header.Visibility = string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    public double Minimum
    {
        get => _minimum;
        set
        {
            _minimum = value;
            // 初始化器里 "Value = x, Maximum = y" 是按书写顺序执行的：
            // 先赋值、后设上限时，超范围的值会留在框里（现场见过"满分 2147483647"）。
            // 设完范围重新夹一次，把框里的显示也刷新。
            Value = _value;
        }
    }

    public double Maximum
    {
        get => _maximum;
        set
        {
            _maximum = value;
            Value = _value;
        }
    }

    /// <summary>加减一次的幅度。</summary>
    public double Step { get; set; } = 1;

    /// <summary>小数位数。默认 0（分数都是整数）。</summary>
    public int Decimals { get; set; }

    /// <summary>值。设置时不触发 <see cref="ValueChanged"/> —— 那是用户操作的回调。</summary>
    public double Value
    {
        get => _value;
        set
        {
            _value = Clamp(value);
            _box.Text = Format(_value);
        }
    }

    /// <summary>用户改完、已经夹到合法范围之后的值。</summary>
    public event Action<double>? ValueChanged;

    /// <summary>输入框本身的宽度（不含两侧加减按钮）。</summary>
    public double BoxWidth
    {
        set => _box.Width = System.Math.Max(56, value);
    }

    private Button StepButton(string glyph, int direction)
    {
        var button = new Button
        {
            Content = glyph,
            Width = 30,
            Height = 34,
            Padding = new Thickness(0),
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
            // 加减也是"改值"，要能被辅助工具念出名字（UIA 里一排无名 button 是 v1.2 的老问题）
            IsTabStop = true,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            button, direction < 0 ? "减" : "加");
        button.Click += (_, _) =>
        {
            // 先落定框里手输的内容，再加一步：否则"输一半直接点 +"会把它覆盖掉
            Commit(silent: true);
            Apply(_value + Step * direction, notify: true);
        };
        return button;
    }

    /// <summary>把输入框里的文本定下来。解析不了就退回真正生效的值。</summary>
    public void Commit() => Commit(silent: false);

    private void Commit(bool silent)
    {
        var raw = (_box.Text ?? "").Trim().Replace("−", "-");
        if (!double.TryParse(raw, out var parsed) || double.IsNaN(parsed))
        {
            _box.Text = Format(_value);
            return;
        }

        Apply(parsed, notify: !silent);
    }

    private void Apply(double wanted, bool notify)
    {
        var next = Clamp(wanted);
        var changed = System.Math.Abs(next - _value) > 1e-9;
        _value = next;
        _box.Text = Format(next);
        if (notify && changed) ValueChanged?.Invoke(next);
    }

    private double Clamp(double v)
    {
        // 无穷大（"1e999"、除以 0 之类）不能留：强转整数会饱和成 2147483647
        if (!double.IsFinite(v)) return _value;
        v = System.Math.Clamp(v, _minimum, _maximum);
        return Decimals <= 0 ? System.Math.Round(v) : System.Math.Round(v, Decimals);
    }

    private string Format(double v) =>
        Decimals <= 0
            ? v.ToString("0", System.Globalization.CultureInfo.InvariantCulture)
            : v.ToString("0." + new string('#', Decimals), System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>焦点回到输入框（校验失败时把光标放回去）。</summary>
    public void FocusBox() => _box.Focus(FocusState.Programmatic);
}
