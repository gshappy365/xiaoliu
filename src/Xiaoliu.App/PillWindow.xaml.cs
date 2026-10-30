using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Xiaoliu.App;

/// <summary>
/// 顶部常驻胶囊。它是小六在屏幕上的唯一常驻标记，也是主面板的入口：
/// 鼠标移上去就把面板从它下面拉出来，移开就收回去（对齐点点的交互）。
///
/// 为什么要有它：之前屏幕上什么都没有，只有托盘图标 —— 用户按完热键不知道该看哪里，
/// 也判断不出小六到底有没有在听。
/// </summary>
public partial class PillWindow : Window
{
    private const double TopMargin = 8;

    /// <summary>面板展开时胶囊往下缩的距离，视觉上像被面板顶了一下。</summary>
    private bool _expanded;

    public PillWindow()
    {
        InitializeComponent();
    }

    /// <summary>鼠标移到胶囊上。外壳收到后把主面板拉出来。</summary>
    public event Action? Hovered;

    /// <summary>点一下胶囊：面板开着就收起，没开就钉住打开。</summary>
    public event Action? Clicked;

    /// <summary>胶囊下边缘在屏幕上的 Y 坐标（物理像素），面板从这个位置往下长。</summary>
    public double BottomEdge => Top + ActualHeight;

    /// <summary>鼠标是否还压在胶囊上。面板靠它判断「该不该收起」。</summary>
    public bool IsCursorOver => WindowInterop.IsCursorOver(this, 2);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // 不吃焦点：点它不应该把用户正在打字的窗口切走。
        WindowInterop.MakeNoActivate(this);
        Place();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        Place();
    }

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);

        // 面板已经开着的时候鼠标从面板滑回胶囊，不该再触发一次展开。
        if (!_expanded)
        {
            Hovered?.Invoke();
        }
    }

    private void Place()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - ActualWidth) / 2;
        Top = area.Top + TopMargin;
    }

    /// <summary>右侧的状态文字，传空就是干净的「小六」。</summary>
    public void SetStatus(string? status)
    {
        // "小六" 是默认说话人，再显示一遍就成了「小六 · 小六」。
        var show = !string.IsNullOrWhiteSpace(status) && status != "小六";
        Status.Text = show ? "· " + status : "";
    }

    /// <summary>
    /// 面板开着时胶囊保持高亮，鼠标不在上面也不熄 —— 否则用户会以为「入口没了」。
    /// </summary>
    public void SetExpanded(bool expanded)
    {
        if (_expanded == expanded)
        {
            return;
        }

        _expanded = expanded;

        var animation = new DoubleAnimation(expanded ? 1.0 : 0.0, TimeSpan.FromMilliseconds(140))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };

        Glow.BeginAnimation(OpacityProperty, animation);
    }

    private void OnPillClick(object sender, MouseButtonEventArgs e) => Clicked?.Invoke();
}
