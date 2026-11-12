using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClassPoints.App.Controls;

/// <summary>
/// 一次性提示条（原生 InfoBar）。给「已保存」「已撤销」这类不需要人做决定的反馈用。
/// </summary>
public sealed partial class Toast : UserControl
{
    private DispatcherTimer? _timer;
    private (string Message, double Seconds)? _pending;

    public Toast()
    {
        InitializeComponent();
    }

    public void Show(string message, double seconds = 3)
    {
        // 后一条直接顶掉前一条：老师只关心刚刚发生的那件事
        _timer?.Stop();
        _pending = (message, seconds);

        // 不要在这里直接置 IsOpen。InfoBar 的模板部件要等它真正进视觉树才实例化，
        // 而在页面 Loaded 里第一次弹提示时（例如外壳初始化完立刻提示），
        // 直接开会拿到还没建好的模板，整个外壳构造就此中断、窗口一片空白。
        // 挪到下一个消息循环，等布局跑完再开。
        DispatcherQueue.TryEnqueue(Apply);
    }

    public void Dismiss()
    {
        _timer?.Stop();
        _pending = null;
        Bar.IsOpen = false;
    }

    private void Apply()
    {
        if (_pending is not { } p) return;
        _pending = null;

        Bar.Message = p.Message;
        Bar.IsOpen = true;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(p.Seconds) };
        _timer.Tick += (_, _) =>
        {
            _timer?.Stop();
            Bar.IsOpen = false;
        };
        _timer.Start();
    }
}
