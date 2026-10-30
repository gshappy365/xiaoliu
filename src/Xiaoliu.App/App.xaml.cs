using System.Threading;
using System.Windows;
using Xiaoliu.Core.Configuration;

namespace Xiaoliu.App;

public partial class App : Application
{
    private Mutex? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 开发自检走单独分支：不进互斥锁、不建托盘、不装热键钩子，
        // 免得自检的合成输入被自己的钩子链看到（虽然合成输入本来就进不去）。
        var args = Environment.GetCommandLineArgs();
        var demoAt = Array.IndexOf(args, "--demo");
        var demoMode = demoAt >= 0 && demoAt + 1 < args.Length ? args[demoAt + 1] : "";

        if (string.Equals(demoMode, "typing", StringComparison.OrdinalIgnoreCase))
        {
            TypingSelfCheck.Run();
            return;
        }

        if (string.Equals(demoMode, "mic", StringComparison.OrdinalIgnoreCase))
        {
            var seconds = demoAt + 2 < args.Length && int.TryParse(args[demoAt + 2], out var parsed) ? parsed : 3;
            var deviceAt = Array.IndexOf(args, "--device");
            int? device = deviceAt >= 0 && deviceAt + 1 < args.Length && int.TryParse(args[deviceAt + 1], out var number)
                ? number
                : null;
            MicSelfCheck.Run(seconds, device);
            return;
        }

        // 命名互斥锁防多开：两个实例抢同一套热键，第二个必然失效且没人看得出原因。
        _singleInstance = new Mutex(true, @"Global\xiaoliu.exe.single-instance", out var isFirst);
        if (!isFirst)
        {
            MessageBox.Show("小六已经在运行了。", "小六", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("未处理异常", args.Exception);
            args.Handled = true;
        };

        var settings = AppSettings.Load();
        var host = new TrayHost(settings);
        host.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
