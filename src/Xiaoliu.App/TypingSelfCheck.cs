using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Xiaoliu.Core.Interop;

namespace Xiaoliu.App;

/// <summary>
/// 开发用自检：验证 insert_text 的真实链路 —— 从后台把目标窗口抢到前台，再把文本送进去。
///
/// 为什么要这个：
/// 1. <see cref="NativeWindows.TypeText"/> 是唯一「我们生成输入、由别的窗口接收」的路径，
///    单测只能测到输入数组拼得对不对，测不到 SendInput 有没有被前台窗口收下。
/// 2. 更隐蔽的是 <see cref="NativeWindows.Focus"/>。生产场景里小柳在后台，用户当前焦点
///    在浏览器或别的程序上，这时 SetForegroundWindow 会被前台锁挡住并静默返回 false。
///    所以靶子窗口必须和一个「别的进程」抢前台，否则测了等于没测。
///
/// 用法：<c>xiaoliu.exe --demo typing</c>。退出码 0 表示抢前台成功且文本一致。
/// </summary>
internal static class TypingSelfCheck
{
    private const string Payload = "小柳自检：insert_text 通道正常 0123 ABC";

    public static void Run()
    {
        var box = new TextBox
        {
            FontSize = 18,
            Margin = new Thickness(16),
            AcceptsReturn = true,
            VerticalContentAlignment = VerticalAlignment.Top,
        };

        var target = new Window
        {
            Title = "小柳 insert_text 自检（自动关闭）",
            Content = box,
            Width = 560,
            Height = 180,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Topmost = true,
            ShowInTaskbar = true,
        };

        Process? decoy = null;
        target.Show();

        // ① 先让靶子窗口自己起来，确认 IsActive 能反映真实激活态。
        After(600, () =>
        {
            Log.Info("selftest", $"typing 阶段1 靶子窗口激活={target.IsActive}");

            // ② 起一个外部进程（notepad）把前台抢走 —— 这就是生产里「小柳在后台」的处境。
            try
            {
                decoy = Process.Start(new ProcessStartInfo("notepad.exe") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Log.Error("selftest", ex);
            }

            After(1200, () =>
            {
                var decoyUp = decoy is { HasExited: false };
                Log.Info("selftest", $"typing 阶段2 外部进程在前台={decoyUp && !target.IsActive} 靶子窗口激活={target.IsActive}");

                // ③ 关键一步：从后台把靶子窗口抢回前台。
                var handle = new WindowInteropHelper(target).Handle;
                var focused = NativeWindows.Focus(handle);
                Log.Info("selftest", $"typing 阶段3 Focus 返回={focused}（已带 AttachThreadInput 绕前台锁）");

                After(600, () =>
                {
                    var handle = new WindowInteropHelper(target).Handle;

                    // 判据只能是系统眼里的前台窗口。WPF 的 IsActive 会骗人：
                    // 实测抢前台失败时它照样是 true，照着它走就会把字打进别的程序。
                    var reclaimed = NativeWindows.IsForeground(handle);
                    var foreground = NativeWindows.ListVisibleWindows().FirstOrDefault(w => w.IsForeground);
                    Log.Info("selftest", $"typing 阶段3 抢回前台={reclaimed} 系统真实前台={foreground?.ProcessName ?? "未知"} WPF_IsActive={target.IsActive}");

                    if (!reclaimed)
                    {
                        // 没抢到就别送键了，否则字符会打进 notepad 里。
                        Finish(target, decoy, ok: false, "抢前台失败，未送键");
                        return;
                    }

                    // 真实目标程序在激活后会自己恢复内部焦点（记事本会回到正文区）。
                    // 这里显式补一次，把「抢前台」和「拿到输入焦点」两个变量分开，
                    // 否则测出来分不清是哪个环节的问题。
                    box.Focus();
                    Keyboard.Focus(box);
                    Log.Info("selftest", $"typing 阶段3 补焦点后={Keyboard.FocusedElement?.GetType().Name ?? "null"}");

                    var sent = NativeWindows.TypeText(Payload);

                    After(600, () =>
                    {
                        var got = box.Text;
                        var ok = string.Equals(got, Payload, StringComparison.Ordinal);
                        Log.Info("selftest", $"typing 阶段4 期望 {Payload.Length} 字 / 送出 {sent} 键 / 收到 {got.Length} 字 / ok={ok}");
                        if (!ok)
                        {
                            Log.Info("selftest", $"typing 实际内容：「{got}」");
                        }

                        Finish(target, decoy, ok, ok ? "全链路通过" : "文本不一致");
                    });
                });
            });
        });
    }

    private static void Finish(Window target, Process? decoy, bool ok, string note)
    {
        Log.Info("selftest", $"typing 结论：{note}（ok={ok}）");

        try
        {
            if (decoy is { HasExited: false })
            {
                decoy.Kill();
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // 自检的收尾动作失败不该掩盖自检结论。
        }

        target.Close();
        Application.Current.Shutdown(ok ? 0 : 1);
    }

    private static void After(int milliseconds, Action action)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            action();
        };
        timer.Start();
    }
}
