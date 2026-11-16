using ClassPoints.Core;
using ClassPoints.App.Theme;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace ClassPoints.App.Controls;

/// <summary>
/// 图标选择：一组 ToggleButton，每个里面一个 Segoe MDL2 字形。
///
/// 不用 ComboBox 是因为字形要在下拉里以图标字体渲染，得挂 ItemTemplate，
/// 而这几处都是代码里临时拼的对话框 —— 一排按钮更短，也更适合手指点。
///
/// **必须换行**：以前是一整条横向 StackPanel，10 个 44px 按钮加起来 494px，
/// 而对话框内容区只有 400~470 —— 最右边那个图标直接被裁掉一半，
/// 看着像"图标显示不全"，而且被裁的那个偏偏点不到。
/// 现在固定每行 7 个、按钮 42px（7×42 + 6×6 = 330），
/// 最窄的那个宿主是设置页里的加分项编辑面板（固定 340），也放得下。
/// </summary>
public static class IconChooser
{
    /// <summary>每行放几个。7 个刚好能放进最窄的那个面板（340px），屏幕小时也不会溢出。</summary>
    private const int PerRow = 7;

    /// <summary>按钮边长。42 是"手指点得中"和"7 个放得下"的交点。</summary>
    private const int ButtonSize = 42;

    public static FrameworkElement Build(IReadOnlyList<string> icons, string? current, Action<string> onPick)
    {
        var selected = IconCatalog.Normalize(current);
        var buttons = new List<ToggleButton>();

        var grid = new Grid
        {
            ColumnSpacing = 6,
            RowSpacing = 6,
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        for (var c = 0; c < System.Math.Min(PerRow, System.Math.Max(1, icons.Count)); c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var rows = (icons.Count + PerRow - 1) / PerRow;
        for (var r = 0; r < rows; r++)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        for (var i = 0; i < icons.Count; i++)
        {
            var glyph = icons[i];
            var button = new ToggleButton
            {
                Width = ButtonSize,
                Height = ButtonSize,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(10),
                IsChecked = glyph == selected,
                Content = new FontIcon
                {
                    Glyph = glyph,
                    FontFamily = new FontFamily(IconCatalog.FontFamily),
                    FontSize = 19,
                },
                Tag = glyph,
            };

            button.Click += (_, _) =>
            {
                // 手写单选：ToggleButton 自己不会互斥
                foreach (var other in buttons)
                    if (!ReferenceEquals(other, button)) other.IsChecked = false;

                button.IsChecked = true;
                onPick(glyph);
            };

            var tip = IconCatalog.Describe(glyph) ?? "图标";
            ToolTipService.SetToolTip(button, tip);
            // 自绘内容（FontIcon）在 UIA 里没有名字，读屏只会念"按钮"
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, tip);

            Grid.SetRow(button, i / PerRow);
            Grid.SetColumn(button, i % PerRow);
            buttons.Add(button);
            grid.Children.Add(button);
        }

        foreach (var b in buttons) b.Foreground = Pal.Primary;
        return grid;
    }
}
