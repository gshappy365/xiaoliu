using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Xiaoliu.Core.Interop;

public sealed record TargetWindow(string ProcessName, string Title, nint Handle, bool IsForeground);

/// <summary>
/// Win32 薄封装：列窗口、聚焦、键入、剪贴板。
///
/// 用 SendInput 逐字键入而不是 UIA ValuePattern，是因为 ValuePattern 只对一部分控件有效，
/// 而「往输入框里填字」的目标是任意应用；代价是必须先把窗口拿到前台，并且不发送回车。
/// </summary>
public static class NativeWindows
{
    private const int SW_RESTORE = 9;
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_UNICODE = 0x0004;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint Type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KEYBDINPUT Ki;
        [FieldOffset(0)] public MOUSEINPUT Mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort Vk;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    private delegate bool EnumWindowsProc(nint hWnd, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint hWnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hWnd, int command);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetFocus(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint hWnd, nint insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private static readonly nint HWND_TOP = 0;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, INPUT[] inputs, int size);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(nint hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetClipboardData(uint format, nint handle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalAlloc(uint flags, nuint bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalLock(nint handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(nint handle);

    private const uint GMEM_MOVEABLE = 0x0002;
    private const uint CF_UNICODETEXT = 13;

    /// <summary>当前所有可见的顶层窗口，按进程名去重（insert_text 的白名单就从这里来）。</summary>
    public static IReadOnlyList<TargetWindow> ListVisibleWindows()
    {
        var foreground = GetForegroundWindow();
        var result = new List<TargetWindow>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        EnumWindows((handle, _) =>
        {
            if (!IsWindowVisible(handle))
            {
                return true;
            }

            var title = new StringBuilder(512);
            if (GetWindowText(handle, title, title.Capacity) == 0 || title.Length == 0)
            {
                return true;
            }

            GetWindowThreadProcessId(handle, out var pid);
            if (pid == 0)
            {
                return true;
            }

            string processName;
            try
            {
                processName = Process.GetProcessById((int)pid).ProcessName + ".exe";
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                return true;
            }

            if (!seen.Add(processName))
            {
                return true;
            }

            result.Add(new TargetWindow(processName, title.ToString(), handle, handle == foreground));
            return true;
        }, 0);

        return result;
    }

    /// <summary>目标窗口此刻是不是系统眼里的前台窗口。</summary>
    public static bool IsForeground(nint handle) => handle != 0 && GetForegroundWindow() == handle;

    /// <summary>
    /// 把窗口切到前台。返回的是「真的切过去了没有」——以 GetForegroundWindow 为准。
    ///
    /// 直接调 SetForegroundWindow 在后台进程里会被前台锁挡掉，而且它只返回 false
    /// 不抛异常，调用方很容易以为成功了。实测（自检 --demo typing）：WPF 的 IsActive
    /// 会显示 true，但系统真实前台还是别的进程，接着键入的字符就全打进了那个进程里。
    /// 所以这里把本线程的输入队列临时挂到「当前前台线程」和「目标线程」上，
    /// 前台锁只拦非前台线程，挂上之后这次交接就是合法的。
    /// </summary>
    public static bool Focus(nint handle)
    {
        if (handle == 0)
        {
            return false;
        }

        if (IsIconic(handle))
        {
            ShowWindow(handle, SW_RESTORE);
        }

        if (GetForegroundWindow() == handle)
        {
            return true;
        }

        var currentThread = GetCurrentThreadId();
        var targetThread = GetWindowThreadProcessId(handle, out _);
        var foreground = GetForegroundWindow();
        var foregroundThread = foreground == 0 ? 0u : GetWindowThreadProcessId(foreground, out _);

        var attached = new List<uint>(2);
        try
        {
            if (foregroundThread != 0 && foregroundThread != currentThread &&
                AttachThreadInput(currentThread, foregroundThread, true))
            {
                attached.Add(foregroundThread);
            }

            if (targetThread != 0 && targetThread != currentThread && targetThread != foregroundThread &&
                AttachThreadInput(currentThread, targetThread, true))
            {
                attached.Add(targetThread);
            }

            BringWindowToTop(handle);
            SetWindowPos(handle, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
            SetForegroundWindow(handle);
            SetFocus(handle);
        }
        finally
        {
            foreach (var thread in attached)
            {
                AttachThreadInput(currentThread, thread, false);
            }
        }

        return GetForegroundWindow() == handle;
    }

    /// <summary>按 Unicode 逐字符键入。换行转成回车，其余原样。不发送回车结尾。</summary>
    public static int TypeText(string text)
    {
        var inputs = new List<INPUT>(text.Length * 2);
        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.Value == '\n')
            {
                inputs.Add(KeyInput(0x0D, 0, 0));
                inputs.Add(KeyInput(0x0D, 0, KEYEVENTF_KEYUP));
                continue;
            }

            if (rune.Value == '\r')
            {
                continue;
            }

            var scan = (ushort)rune.Value;
            inputs.Add(KeyInput(0, scan, KEYEVENTF_UNICODE));
            inputs.Add(KeyInput(0, scan, KEYEVENTF_UNICODE | KEYEVENTF_KEYUP));
        }

        if (inputs.Count == 0)
        {
            return 0;
        }

        var sent = SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>());
        return (int)sent / 2;
    }

    private static INPUT KeyInput(ushort vk, ushort scan, uint flags = 0) => new()
    {
        Type = INPUT_KEYBOARD,
        U = new InputUnion
        {
            Ki = new KEYBDINPUT { Vk = vk, Scan = scan, Flags = flags },
        },
    };

    /// <summary>写剪贴板。返回读回校验是否一致。</summary>
    public static bool SetClipboardText(string text)
    {
        if (!OpenClipboard(0))
        {
            return false;
        }

        try
        {
            EmptyClipboard();
            var bytes = Encoding.Unicode.GetBytes(text + "\0");
            var handle = GlobalAlloc(GMEM_MOVEABLE, (nuint)bytes.Length);
            if (handle == 0)
            {
                return false;
            }

            var target = GlobalLock(handle);
            if (target == 0)
            {
                return false;
            }

            Marshal.Copy(bytes, 0, target, bytes.Length);
            GlobalUnlock(handle);
            return SetClipboardData(CF_UNICODETEXT, handle) != 0;
        }
        finally
        {
            CloseClipboard();
        }
    }
}
