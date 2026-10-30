using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Xiaoliu.Core.Capture;

namespace Xiaoliu.App;

/// <summary>
/// 圈选层：用户拖一个框，松手就取这块区域。
///
/// 这是小六唯一的「看图」入口 —— 模型不能自己抓屏（ADR 0002），
/// 所以这个窗口的可用性等于「小六能不能看屏幕」。
/// </summary>
public partial class SelectionWindow : Window
{
    private CaptureRect _origin;
    private Point? _start;

    public SelectionWindow()
    {
        InitializeComponent();
    }

    /// <summary>用户拖完了一块区域（屏幕绝对坐标）。</summary>
    public event Action<CaptureRect>? RegionSelected;

    public event Action? Cancelled;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _origin = ScreenCapture.VirtualScreenBounds();
        Left = _origin.X;
        Top = _origin.Y;
        Width = _origin.Width;
        Height = _origin.Height;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        _start = e.GetPosition(Surface);
        Selection.Visibility = Visibility.Visible;
        Canvas.SetLeft(Selection, _start.Value.X);
        Canvas.SetTop(Selection, _start.Value.Y);
        Selection.Width = 0;
        Selection.Height = 0;
        Hint.Visibility = Visibility.Collapsed;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_start is null)
        {
            return;
        }

        var current = e.GetPosition(Surface);
        var x = Math.Min(_start.Value.X, current.X);
        var y = Math.Min(_start.Value.Y, current.Y);
        Canvas.SetLeft(Selection, x);
        Canvas.SetTop(Selection, y);
        Selection.Width = Math.Abs(current.X - _start.Value.X);
        Selection.Height = Math.Abs(current.Y - _start.Value.Y);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_start is null)
        {
            return;
        }

        _start = null;

        // 太小的框当成误触：拖了 3 像素就想看图，多半是手抖。
        if (Selection.Width < 12 || Selection.Height < 12)
        {
            Cancel();
            return;
        }

        var screenRect = new CaptureRect(
            _origin.X + (int)Math.Round(Canvas.GetLeft(Selection)),
            _origin.Y + (int)Math.Round(Canvas.GetTop(Selection)),
            (int)Math.Round(Selection.Width),
            (int)Math.Round(Selection.Height));

        Close();
        RegionSelected?.Invoke(screenRect);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Cancel();
            return;
        }

        base.OnKeyDown(e);
    }

    private void Cancel()
    {
        Close();
        Cancelled?.Invoke();
    }

    public void Begin()
    {
        Show();
        Activate();
        Focus();
    }
}
