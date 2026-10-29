using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Xiaoliu.Core.Capture;
using Xiaoliu.Core.Visual;

namespace Xiaoliu.App;

/// <summary>
/// 指点图形层。全屏透明、点击穿透、不抢焦点 —— 用户正在被指的那个窗口必须照常可用。
///
/// 图形长存活：新图形叠加，不清旧的；下一轮对话或按 Esc 才整层清空
/// （交接文档的共识，见 docs/adr/0003）。
/// </summary>
public partial class OverlayWindow : Window
{
    private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0x4C, 0x8D, 0xFF));
    private static readonly Brush Act = new SolidColorBrush(Color.FromRgb(0xFF, 0xB0, 0x4C));
    private static readonly Brush LabelBackground = new SolidColorBrush(Color.FromArgb(0xE6, 0x14, 0x16, 0x1C));

    private CaptureRect _origin;

    public OverlayWindow()
    {
        InitializeComponent();
        Accent.Freeze();
        Act.Freeze();
        LabelBackground.Freeze();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowInterop.MakeClickThrough(this);
        Place();
    }

    /// <summary>铺满整个虚拟桌面（多屏时是并集，含负坐标）。</summary>
    public void Place()
    {
        _origin = ScreenCapture.VirtualScreenBounds();
        Left = _origin.X;
        Top = _origin.Y;
        Width = _origin.Width;
        Height = _origin.Height;
    }

    /// <summary>确保窗口存在且铺满当前桌面。名字不叫 Show 是避免遮蔽 Window.Show。</summary>
    public void ShowOverlay()
    {
        Place();
        if (!IsVisible)
        {
            base.Show();
        }
    }

    public void Draw(GuidanceMark mark)
    {
        ShowOverlay();

        var brush = mark.IsActIntent ? Act : Accent;
        switch (mark.Kind)
        {
            case GuidanceKind.Point:
                DrawPoint(mark, brush);
                break;
            case GuidanceKind.Highlight:
                DrawHighlight(mark, brush);
                break;
            case GuidanceKind.Connection:
                DrawConnection(mark, brush);
                break;
            case GuidanceKind.Drag:
                DrawDrag(mark, brush);
                break;
        }
    }

    public void Clear() => Surface.Children.Clear();

    // ---------------------------------------------------------------- 各类图形

    private void DrawPoint(GuidanceMark mark, Brush brush)
    {
        var x = mark.Bounds.X - _origin.X;
        var y = mark.Bounds.Y - _origin.Y;

        var halo = new Ellipse
        {
            Width = 44,
            Height = 44,
            Stroke = brush,
            StrokeThickness = 3,
            Opacity = 0.9,
        };
        Canvas.SetLeft(halo, x - 22);
        Canvas.SetTop(halo, y - 22);
        Surface.Children.Add(halo);

        var dot = new Ellipse { Width = 8, Height = 8, Fill = brush };
        Canvas.SetLeft(dot, x - 4);
        Canvas.SetTop(dot, y - 4);
        Surface.Children.Add(dot);

        Pulse(halo);
        AddLabel(mark.Label, x + 30, y - 18, brush);
    }

    private void DrawHighlight(GuidanceMark mark, Brush brush)
    {
        var x = mark.Bounds.X - _origin.X;
        var y = mark.Bounds.Y - _origin.Y;

        if (mark.Underline)
        {
            var line = new Line
            {
                X1 = x,
                Y1 = y + mark.Bounds.Height,
                X2 = x + mark.Bounds.Width,
                Y2 = y + mark.Bounds.Height,
                Stroke = brush,
                StrokeThickness = 3,
                StrokeEndLineCap = PenLineCap.Round,
            };
            Surface.Children.Add(line);
            AddLabel(mark.Label, x, y + mark.Bounds.Height + 8, brush);
            return;
        }

        var box = new Rectangle
        {
            Width = mark.Bounds.Width,
            Height = mark.Bounds.Height,
            Stroke = brush,
            StrokeThickness = 3,
            RadiusX = 6,
            RadiusY = 6,
            Fill = new SolidColorBrush(Color.FromArgb(0x1A, 0x4C, 0x8D, 0xFF)),
        };
        Canvas.SetLeft(box, x);
        Canvas.SetTop(box, y);
        Surface.Children.Add(box);

        Pulse(box);
        AddLabel(mark.Label, x, Math.Max(0, y - 30), brush);
    }

    private void DrawConnection(GuidanceMark mark, Brush brush)
    {
        DrawRegion(mark.Bounds, brush, alpha: 0x22);
        if (mark.Second is { } second)
        {
            DrawRegion(second, brush, alpha: 0x22);

            var from = Center(mark.Bounds);
            var to = Center(second);
            Surface.Children.Add(new Line
            {
                X1 = from.X,
                Y1 = from.Y,
                X2 = to.X,
                Y2 = to.Y,
                Stroke = brush,
                StrokeThickness = 2,
                StrokeDashArray = [6, 4],
            });
        }

        var label = Center(mark.Bounds);
        AddLabel(mark.Label, label.X - 20, label.Y - 34, brush);
    }

    private void DrawDrag(GuidanceMark mark, Brush brush)
    {
        var from = new Point(mark.Bounds.X - _origin.X, mark.Bounds.Y - _origin.Y);
        var to = mark.Second is { } end
            ? new Point(end.X - _origin.X, end.Y - _origin.Y)
            : new Point(from.X + mark.Bounds.Width, from.Y + mark.Bounds.Height);

        Surface.Children.Add(new Line
        {
            X1 = from.X,
            Y1 = from.Y,
            X2 = to.X,
            Y2 = to.Y,
            Stroke = brush,
            StrokeThickness = 3,
            StrokeDashArray = [4, 3],
            StrokeEndLineCap = PenLineCap.Round,
        });

        var head = new Ellipse { Width = 14, Height = 14, Fill = brush };
        Canvas.SetLeft(head, to.X - 7);
        Canvas.SetTop(head, to.Y - 7);
        Surface.Children.Add(head);

        var tail = new Ellipse
        {
            Width = 18,
            Height = 18,
            Stroke = brush,
            StrokeThickness = 2,
        };
        Canvas.SetLeft(tail, from.X - 9);
        Canvas.SetTop(tail, from.Y - 9);
        Surface.Children.Add(tail);

        AddLabel(mark.Label, (from.X + to.X) / 2, Math.Min(from.Y, to.Y) - 30, brush);
    }

    private void DrawRegion(CaptureRect rect, Brush brush, byte alpha)
    {
        var box = new Rectangle
        {
            Width = rect.Width,
            Height = rect.Height,
            Stroke = brush,
            StrokeThickness = 2,
            RadiusX = 6,
            RadiusY = 6,
            Fill = new SolidColorBrush(Color.FromArgb(alpha, 0x4C, 0x8D, 0xFF)),
        };
        Canvas.SetLeft(box, rect.X - _origin.X);
        Canvas.SetTop(box, rect.Y - _origin.Y);
        Surface.Children.Add(box);
    }

    private void AddLabel(string text, double x, double y, Brush brush)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var border = new Border
        {
            Background = LabelBackground,
            BorderBrush = brush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 4, 10, 5),
            Child = new TextBlock
            {
                Text = text,
                Foreground = Brushes.White,
                FontSize = 14,
                MaxWidth = 360,
                TextWrapping = TextWrapping.Wrap,
            },
        };

        Canvas.SetLeft(border, Math.Max(0, x));
        Canvas.SetTop(border, Math.Max(0, y));
        Surface.Children.Add(border);
    }

    private Point Center(CaptureRect rect) => new(
        rect.X - _origin.X + rect.Width / 2.0,
        rect.Y - _origin.Y + rect.Height / 2.0);

    private static void Pulse(UIElement element)
    {
        var animation = new DoubleAnimation
        {
            From = 1,
            To = 0.35,
            Duration = TimeSpan.FromMilliseconds(700),
            AutoReverse = true,
            RepeatBehavior = new RepeatBehavior(3),
        };
        element.BeginAnimation(OpacityProperty, animation);
    }
}
