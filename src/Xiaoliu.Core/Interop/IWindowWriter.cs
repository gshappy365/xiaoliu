namespace Xiaoliu.Core.Interop;

/// <summary>
/// 把字送进「别人的窗口」所需的三个动作。
///
/// 抽成接口只有一个目的：让「抢不到前台就不许打字」这条约束能被测试。
/// 真要靠 P/Invoke 去测，只能靠人肉开两个窗口对着看，而且必然漏掉失败分支 ——
/// 而失败分支恰恰是危险的那一半：SendInput 发给的是当前前台窗口，不是句柄，
/// 抢前台失败还照打，字就进了用户当时正在用的那个程序。
/// </summary>
public interface IWindowWriter
{
    /// <summary>把窗口切到前台，返回是否真的切过去了。</summary>
    bool Focus(nint handle);

    /// <summary>目标窗口此刻是不是前台。</summary>
    bool IsForeground(nint handle);

    /// <summary>向当前前台窗口逐字键入，返回键入的字符数。</summary>
    int Type(string text);
}

/// <summary>生产实现，直接落到 Win32。</summary>
public sealed class NativeWindowWriter : IWindowWriter
{
    public static readonly NativeWindowWriter Instance = new();

    public bool Focus(nint handle) => NativeWindows.Focus(handle);

    public bool IsForeground(nint handle) => NativeWindows.IsForeground(handle);

    public int Type(string text) => NativeWindows.TypeText(text);
}
