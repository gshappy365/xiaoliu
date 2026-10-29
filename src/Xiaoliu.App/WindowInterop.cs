using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Xiaoliu.App;

/// <summary>
/// 让窗口「看得见但点不到」：字幕条和指点图形都盖在用户正在操作的地方，
/// 一旦抢焦点或者吃掉鼠标，用户就没法干活了。
/// </summary>
internal static class WindowInterop
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_LAYERED = 0x00080000;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(nint hWnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(nint hWnd, int index, int newStyle);

    public static void MakeClickThrough(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var style = GetWindowLong(handle, GWL_EXSTYLE);
        SetWindowLong(handle, GWL_EXSTYLE,
            style | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED);
    }
}
