using System.Windows;
using System.Windows.Threading;

namespace Xiaoliu.App;

/// <summary>
/// 字幕悬浮条：屏幕上方居中，显示「正在听 / 听到的字 / 回复正文」。
/// 只读、不可点、不抢焦点（见 WindowInterop）。
/// </summary>
public partial class SubtitleWindow : Window
{
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

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowInterop.MakeClickThrough(this);
        PlaceTopCenter();
    }

    private void PlaceTopCenter()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Top + 24;
    }

    public void ShowStatus(string status)
    {
        _hideTimer.Stop();
        StatusText.Text = status;
        if (!IsVisible)
        {
            Show();
        }
    }

    public void ShowText(string text, string? status = null)
    {
        _hideTimer.Stop();
        if (status is not null)
        {
            StatusText.Text = status;
        }

        BodyText.Text = text;
        if (!IsVisible)
        {
            Show();
        }
    }

    public void ShowTransient(string text, string status = "小柳")
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
    }
}
