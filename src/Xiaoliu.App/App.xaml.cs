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

        // 命名互斥锁防多开：两个实例抢同一套热键，第二个必然失效且没人看得出原因。
        _singleInstance = new Mutex(true, @"Global\xiaoliu.exe.single-instance", out var isFirst);
        if (!isFirst)
        {
            MessageBox.Show("小柳已经在运行了。", "小柳", MessageBoxButton.OK, MessageBoxImage.Information);
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
