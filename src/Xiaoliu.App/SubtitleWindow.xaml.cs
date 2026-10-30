using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Xiaoliu.Core.Capture;

namespace Xiaoliu.App;

/// <summary>
/// 回答气泡：320×96，跟着指点目标浮动；没有指点目标时贴在顶部胶囊下方。
/// 只读、不可点、不抢焦点（见 WindowInterop）。
///
/// 尺寸与锚定方式对齐点点的 AnswerBubbleWindow：目标下方 52 DIP、水平居中于目标，
/// 越界就夹回屏幕工作区内。区别是点点有角色形象，这里只有文字。
/// </summary>
public partial class SubtitleWindow : Window
{
    private const double TopOffset = 46;
    private const double TargetOffset = 52;

    private readonly DispatcherTimer _hideTimer = new() { Interval = TimeSpan.FromSeconds(6) };

    public SubtitleWindow()
    {
        InitializeComponent();
        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer.Stop();
            Hide();
        };
    }

    /// <summary>
    /// 当前这轮指到的位置（物理像素）。设了它就贴着目标浮动，清掉就回到顶部。
    /// 由 Companion 在派发指点图形时设置，新一轮对话开始时清空。
    /// </summary>
    public CaptureRect? AnchorBounds { get; set; }

    /// <summary>状态行变了（在听 / 在想 / 小六）。常驻胶囊跟着它显示同一句话。</summary>
    public event Action<string?>? StatusChanged;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowInterop.MakeClickThrough(this);
        AnchorTo(AnchorBounds);
    }

    private void AnchorTo(CaptureRect? target)
    {
        // CaptureRect 是物理像素，Window.Left/Top 是 DIP，要按当前 DPI 折算。
        var dpi = VisualTreeHelper.GetDpi(this);
        var area = SystemParameters.WorkArea;

        double left;
        double top;

        if (target is { } bounds)
        {
            var centerX = (bounds.X + bounds.Width / 2.0) / dpi.DpiScaleX;
            var bottom = (bounds.Y + bounds.Height) / dpi.DpiScaleY;
            left = centerX - Width / 2;
            top = bottom + TargetOffset;
        }
        else
        {
            left = area.Left + (area.Width - Width) / 2;
            top = area.Top + TopOffset;
        }

        var height = ActualHeight > 0 ? ActualHeight : MinHeight;
        Left = Math.Clamp(left, area.Left, Math.Max(area.Left, area.Left + area.Width - Width));
        Top = Math.Clamp(top, area.Top, Math.Max(area.Top, area.Top + area.Height - height));
    }

    private void EnsureVisible()
    {
        // 每次都要重新定位：窗口可能已经因为上一条消息停在别处了，
        // 只判断 IsVisible 会让新回答留在旧位置（实测气泡没跟着指点走就是这么来的）。
        AnchorTo(AnchorBounds);

        if (!IsVisible)
        {
            Show();
        }
    }

    public void ShowStatus(string status)
    {
        _hideTimer.Stop();
        StatusText.Text = status;
        StatusChanged?.Invoke(status);
        EnsureVisible();
    }

    public void ShowText(string text, string? status = null)
    {
        _hideTimer.Stop();
        if (status is not null)
        {
            StatusText.Text = status;
            StatusChanged?.Invoke(status);
        }

        BodyText.Text = text;
        EnsureVisible();
    }

    public void ShowTransient(string text, string status = "小六")
    {
        ShowText(text, status);
        _hideTimer.Stop();
        _hideTimer.Interval = TimeSpan.FromSeconds(5);
        _hideTimer.Start();
    }

    public void HideSoon(int seconds = 4)
    {
        _hideTimer.Stop();
        _hideTimer.Interval = TimeSpan.FromSeconds(seconds);
        _hideTimer.Start();
    }

    public void HideNow()
    {
        _hideTimer.Stop();
        Hide();
        StatusChanged?.Invoke(null);
    }
}
