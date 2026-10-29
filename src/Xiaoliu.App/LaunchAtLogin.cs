using Microsoft.Win32;

namespace Xiaoliu.App;

/// <summary>
/// 开机自启：只写 <c>HKCU\...\Run</c>，不碰计划任务、不碰服务。
///
/// 默认是关的 —— 静默写自启项是用户最反感的一类行为，必须由用户显式勾选。
/// 托盘菜单和主窗口设置页共用这一份实现，避免两处逻辑走岔。
/// </summary>
internal static class LaunchAtLogin
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "xiaoliu";

    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                return;
            }

            if (enabled)
            {
                var exe = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exe))
                {
                    key.SetValue(RunValueName, $"\"{exe}\"");
                }
            }
            else
            {
                key.DeleteValue(RunValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex)
        {
            Log.Error("app", ex);
        }
    }
}
