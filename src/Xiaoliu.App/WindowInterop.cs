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

    /// <summary>
    /// 能点，但点击不激活、不进 Alt+Tab。给常驻胶囊用：
    /// 点它打开历史可以，把用户正在打字的窗口切走不行。
    /// </summary>
    public static void MakeNoActivate(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var style = GetWindowLong(handle, GWL_EXSTYLE);
        SetWindowLong(handle, GWL_EXSTYLE,
            style | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED);
    }

    /// <summary>
    /// 鼠标是否落在窗口矩形里（外扩 <paramref name="slack"/> 像素）。
    ///
    /// 为什么要自己算：面板要不要收起取决于「鼠标还在不在我身上」，
    /// 而 WPF 的 <c>IsMouseOver</c> 只在窗口自己收到鼠标消息时才更新 ——
    /// 鼠标移到别的程序上以后它就停在最后一次的值，靠它判断永远收不起来。
    /// </summary>
    public static bool IsCursorOver(Window window, int slack = 0)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == nint.Zero || !GetWindowRect(handle, out var rect) || !GetCursorPos(out var cursor))
        {
            return false;
        }

        return cursor.X >= rect.Left - slack && cursor.X <= rect.Right + slack
               && cursor.Y >= rect.Top - slack && cursor.Y <= rect.Bottom + slack;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint handle, out NativeRect rect);
}
