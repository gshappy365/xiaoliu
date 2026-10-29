using System.Windows;
using System.Windows.Input;

namespace Xiaoliu.App;

/// <summary>
/// 顶部常驻胶囊。只做三件事：表示小柳在、显示当前状态、点一下打开历史。
///
/// 为什么要有它：之前屏幕上什么都没有，只有托盘图标 —— 用户按完热键不知道该看哪里，
/// 也判断不出小柳到底有没有在听。点点的做法是常驻一个小胶囊，这里对齐这个做法。
/// </summary>
public partial class PillWindow : Window
{
    private const double TopMargin = 8;

    public PillWindow()
    {
        InitializeComponent();
    }

    /// <summary>点胶囊打开历史。默认什么都不做，由外壳挂上去。</summary>
    public event Action? Clicked;

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

    private void Place()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - ActualWidth) / 2;
        Top = area.Top + TopMargin;
    }

    /// <summary>右侧的状态文字，传空就是干净的「小柳」。</summary>
    public void SetStatus(string? status)
    {
        // "小柳" 是默认说话人，再显示一遍就成了「小柳 · 小柳」。
        var show = !string.IsNullOrWhiteSpace(status) && status != "小柳";
        Status.Text = show ? "· " + status : "";
    }

    private void OnPillClick(object sender, MouseButtonEventArgs e) => Clicked?.Invoke();
}
