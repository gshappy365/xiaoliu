using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Xiaoliu.App;

/// <summary>
/// 全局热键。用 WH_KEYBOARD_LL 低级钩子而不是 RegisterHotKey，理由与点点一致：
/// 我们要的是「纯修饰键按住」——Ctrl+Alt 单独按下没有任何窗口能注册成热键，
/// 而 RegisterHotKey 也不报告松手时刻。
///
/// 副作用：合成输入（keybd_event / SendInput）进不了钩子链，所以自动化脚本测不出热键。
/// 见 rea/reports/hotkey-diagnosis.md（路径记法见 docs/workspace-layout.md）。
/// </summary>
public sealed class HotkeyHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYUP = 0x0105;

    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12;
    private const int VK_SHIFT = 0x10;
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;
    private const int VK_ESCAPE = 0x1B;

    private delegate nint LowLevelKeyboardProc(int code, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint VkCode;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookEx(int hook, LowLevelKeyboardProc callback, nint module, uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(nint hook);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);

    [DllImport("kernel32.dll")]
    private static extern nint GetModuleHandle(string? moduleName);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    private readonly LowLevelKeyboardProc _callback;
    private readonly nint _hook;
    private readonly string[] _pushToTalkModifiers;
    private readonly string[] _selectModifiers;
    private readonly int _selectKey;
    private bool _chordDown;

    public HotkeyHook(string pushToTalkModifiers, string selectModifiers, string selectKey)
    {
        _pushToTalkModifiers = Split(pushToTalkModifiers);
        _selectModifiers = Split(selectModifiers);
        _selectKey = string.IsNullOrEmpty(selectKey) ? 0x53 : (int)char.ToUpperInvariant(selectKey[0]);

        _callback = Handle;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _callback, GetModuleHandle(null), 0);
        if (_hook == 0)
        {
            throw new InvalidOperationException($"安装键盘钩子失败（{Marshal.GetLastWin32Error()}）。");
        }
    }

    private static string[] Split(string value) =>
        value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Ctrl+Alt 这一类纯修饰键按住。</summary>
    public event Action? ChordPressed;

    public event Action? ChordReleased;

    /// <summary>圈选热键。</summary>
    public event Action? SelectRegionPressed;

    public event Action? EscapePressed;

    private nint Handle(int code, nint wParam, nint lParam)
    {
        if (code < 0)
        {
            return CallNextHookEx(_hook, code, wParam, lParam);
        }

        var message = (int)wParam;
        var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
        var vk = (int)data.VkCode;

        try
        {
            switch (message)
            {
                case WM_KEYDOWN:
                case WM_SYSKEYDOWN:
                    OnKeyDown(vk);
                    break;
                case WM_KEYUP:
                case WM_SYSKEYUP:
                    OnKeyUp(vk);
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error("hotkey", ex);
        }

        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    private void OnKeyDown(int vk)
    {
        if (vk == VK_ESCAPE)
        {
            EscapePressed?.Invoke();
            return;
        }

        if (IsModifier(vk))
        {
            // 纯修饰键组合：只有「该按的按了、别的键没按」才算按住说话。
            if (!_chordDown && MatchesModifiers(_pushToTalkModifiers) && !AnyOtherModifier(_pushToTalkModifiers))
            {
                _chordDown = true;
                ChordPressed?.Invoke();
            }

            return;
        }

        if (vk == _selectKey && MatchesModifiers(_selectModifiers))
        {
            SelectRegionPressed?.Invoke();
        }
    }

    private void OnKeyUp(int vk)
    {
        if (!_chordDown || !IsModifier(vk))
        {
            return;
        }

        // 松开任意一个必需修饰键就结束。
        if (!MatchesModifiers(_pushToTalkModifiers))
        {
            _chordDown = false;
            ChordReleased?.Invoke();
        }
    }

    private static bool IsModifier(int vk) =>
        vk is VK_CONTROL or VK_MENU or VK_SHIFT or VK_LWIN or VK_RWIN or 0xA2 or 0xA3 or 0xA0 or 0xA1 or 0xA4 or 0xA5;

    private static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    private bool MatchesModifiers(string[] modifiers)
    {
        foreach (var modifier in modifiers)
        {
            var down = modifier.ToLowerInvariant() switch
            {
                "control" or "ctrl" => IsDown(VK_CONTROL),
                "alt" => IsDown(VK_MENU),
                "shift" => IsDown(VK_SHIFT),
                "win" or "windows" => IsDown(VK_LWIN) || IsDown(VK_RWIN),
                _ => false,
            };

            if (!down)
            {
                return false;
            }
        }

        return true;
    }

    private bool AnyOtherModifier(string[] expected)
    {
        var has = expected.Select(m => m.ToLowerInvariant()).ToHashSet();
        if (!has.Contains("shift") && (IsDown(VK_SHIFT) || IsDown(0xA0) || IsDown(0xA1)))
        {
            return true;
        }

        if (!has.Contains("win") && !has.Contains("windows") && (IsDown(VK_LWIN) || IsDown(VK_RWIN)))
        {
            return true;
        }

        return false;
    }

    public void Dispose() => UnhookWindowsHookEx(_hook);
}
