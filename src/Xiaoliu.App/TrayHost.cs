using System.Diagnostics;
using System.IO;
using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using Microsoft.Win32;
using Xiaoliu.Core.Configuration;
using Xiaoliu.Core.Storage;
using Application = System.Windows.Application;

namespace Xiaoliu.App;

/// <summary>
/// 常驻外壳：托盘图标 + 全局热键 + 三个窗口（字幕条、指点层、圈选层）的生命周期。
/// 没有主窗口 —— 小柳是常驻助手，不该占一个窗口位置。
/// </summary>
public sealed class TrayHost : IDisposable
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "xiaoliu";

    private readonly AppSettings _settings;
    private readonly NotifyIcon _tray;
    private readonly HotkeyHook _hotkeys;
    private readonly SubtitleWindow _subtitle;
    private readonly PillWindow _pill;
    private readonly OverlayWindow _overlay;
    private readonly Companion _companion;
    private readonly Core.Capture.VisualEvidence _evidence = new();
    private WorkspaceWindow? _workspace;

    public TrayHost(AppSettings settings)
    {
        _settings = settings;

        _tray = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Visible = true,
            Text = "小柳（Ctrl+Alt 按住说话）",
            ContextMenuStrip = BuildMenu(),
        };
        _tray.DoubleClick += (_, _) => ShowWorkspace("home");

        _subtitle = new SubtitleWindow();
        _pill = new PillWindow();
        _pill.Clicked += () => ShowWorkspace("home");
        _subtitle.StatusChanged += status => _pill.SetStatus(status);
        _overlay = new OverlayWindow();
        _companion = new Companion(settings, _subtitle, _overlay, _evidence);

        _hotkeys = new HotkeyHook(settings.PushToTalkModifiers, settings.SelectRegionModifiers, settings.SelectRegionKey);
        _hotkeys.ChordPressed += () => _companion.OnChordPressed();
        _hotkeys.ChordReleased += () => _companion.OnChordReleased();
        _hotkeys.SelectRegionPressed += () => BeginSelection();
        _hotkeys.EscapePressed += () => _companion.OnEscape();
    }

    public void Start()
    {
        Log.Info("app", $"启动 版本 {typeof(TrayHost).Assembly.GetName().Version}");
        LaunchAtLogin.Apply(_settings.LaunchAtLogin);
        SweepOnStartup();

        // 托盘菜单之外没有主窗口，所以不能靠「最后一个窗口关闭」来退出。
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // 常驻胶囊：屏幕顶部一直有个「小柳」在那儿，按完热键知道该看哪里。
        if (_settings.ShowPill)
        {
            _pill.Show();
        }

        _subtitle.ShowTransient("按住 Ctrl+Alt 说话，Ctrl+Alt+S 圈选一块区域给我看。", "小柳");

        var args = Environment.GetCommandLineArgs();

        // 开发/截图用：启动后直接开主窗口，省得每次去点托盘。
        var windowAt = Array.IndexOf(args, "--window");
        if (windowAt >= 0)
        {
            var page = windowAt + 1 < args.Length && !args[windowAt + 1].StartsWith("--", StringComparison.Ordinal)
                ? args[windowAt + 1]
                : "home";
            ShowWorkspace(page);
        }

        var demoAt = Array.IndexOf(args, "--demo");
        if (demoAt >= 0)
        {
            var point = DrawDemoMarks();

            // --demo ui 顺带把气泡挂到那个点上，一眼就能看出「回答跟着指点走」。
            if (demoAt + 1 < args.Length && string.Equals(args[demoAt + 1], "ui", StringComparison.OrdinalIgnoreCase))
            {
                _subtitle.AnchorBounds = point;
                _subtitle.ShowText("这里是命令面板，点右上角的齿轮可以改主题。", "小柳");
            }
        }
    }

    /// <summary>
    /// 开发用：把四种指点图形一次画出来看效果。真实链路要等一次语音往返，
    /// 调样式时没必要每次都说话。
    /// </summary>
    private Core.Capture.CaptureRect DrawDemoMarks()
    {
        var area = Core.Capture.ScreenCapture.VirtualScreenBounds();
        var x = area.X + area.Width / 4;
        var y = area.Y + area.Height / 3;

        _overlay.Draw(new Core.Visual.GuidanceMark
        {
            Id = Guid.NewGuid(),
            Kind = Core.Visual.GuidanceKind.Highlight,
            ScreenNumber = 1,
            Label = "这是一个区域",
            Bounds = new Core.Capture.CaptureRect(x, y, 320, 120),
        });
        var point = new Core.Capture.CaptureRect(x + 460, y + 40, 1, 1);
        _overlay.Draw(new Core.Visual.GuidanceMark
        {
            Id = Guid.NewGuid(),
            Kind = Core.Visual.GuidanceKind.Point,
            ScreenNumber = 1,
            Label = "这是一个点",
            Bounds = point,
        });
        _overlay.Draw(new Core.Visual.GuidanceMark
        {
            Id = Guid.NewGuid(),
            Kind = Core.Visual.GuidanceKind.Connection,
            ScreenNumber = 1,
            Label = "这两处对应",
            Bounds = new Core.Capture.CaptureRect(x, y + 220, 200, 90),
            Second = new Core.Capture.CaptureRect(x + 560, y + 220, 200, 90),
        });
        _overlay.Draw(new Core.Visual.GuidanceMark
        {
            Id = Guid.NewGuid(),
            Kind = Core.Visual.GuidanceKind.Drag,
            ScreenNumber = 1,
            Label = "从这里拖到那里",
            Bounds = new Core.Capture.CaptureRect(x, y + 420, 1, 1),
            Second = new Core.Capture.CaptureRect(x + 420, y + 420, 1, 1),
        });

        return point;
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("打开小柳", null, (_, _) => ShowWorkspace("home"));
        menu.Items.Add("历史记录", null, (_, _) => ShowWorkspace("history"));
        menu.Items.Add("设置", null, (_, _) => ShowWorkspace("settings"));
        menu.Items.Add("圈选一块区域（Ctrl+Alt+S）", null, (_, _) => BeginSelection());
        menu.Items.Add(new ToolStripSeparator());
        var autoStart = new ToolStripMenuItem("开机自启") { Checked = _settings.LaunchAtLogin, CheckOnClick = true };
        autoStart.CheckedChanged += (_, _) =>
        {
            _settings.LaunchAtLogin = autoStart.Checked;
            _settings.Save();
            LaunchAtLogin.Apply(autoStart.Checked);
            _subtitle.ShowTransient(autoStart.Checked ? "下次登录会自动启动。" : "已取消开机自启。", "小柳");
        };
        menu.Items.Add(autoStart);
        menu.Items.Add("打开日志目录", null, (_, _) =>
            OpenPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "xiaoliu", "logs")));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => Application.Current.Shutdown());
        return menu;
    }

    private static void OpenPath(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            else if (Directory.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            }
            else
            {
                System.Windows.MessageBox.Show($"还没有这个文件：{path}", "小柳");
            }
        }
        catch (Exception ex)
        {
            Log.Error("app", ex);
        }
    }

    /// <summary>
    /// 主工作区只有一个实例：重复打开就切到对应页，不再堆窗口。
    /// 关闭时只是 Hide（窗口还在，状态不丢），进程退出走托盘菜单。
    /// </summary>
    private void ShowWorkspace(string page)
    {
        try
        {
            if (_workspace is null)
            {
                _workspace = new WorkspaceWindow(_settings, _companion, _evidence, BeginSelection, () => DrawDemoMarks());
                _workspace.Closed += (_, _) =>
                {
                    // 关掉窗口不等于退出小柳：托盘还在，热键还在。
                    _workspace = null;
                };
            }

            _workspace.ShowPage(page);
            Log.Info("workspace", $"主界面已打开 {page}，可见={_workspace.IsVisible} 句柄={new System.Windows.Interop.WindowInteropHelper(_workspace).Handle}");
        }
        catch (Exception ex)
        {
            // 主窗口是 XAML 拼出来的，构造期出错会把整个进程带走 —— 托盘还活着的时候
            // 用户只看到「程序闪一下就没了」，所以这里必须兜住并留下证据。
            Log.Error("workspace", ex);
            System.Windows.MessageBox.Show("主界面打不开：" + ex.Message, "小柳");
        }
    }

    private void BeginSelection()
    {
        var selection = new SelectionWindow();
        selection.RegionSelected += rect =>
        {
            try
            {
                var image = Core.Capture.ScreenCapture.Capture(rect);
                var shot = _companion.AddSelection(rect, image);
                _subtitle.ShowTransient(
                    $"已圈选 {rect.Width}×{rect.Height}（{shot.CaptureCallId}）。按住 Ctrl+Alt 说你的问题。",
                    "小柳");
            }
            catch (Exception ex)
            {
                Log.Error("capture", ex);
                _subtitle.ShowTransient("取图失败：" + ex.Message, "小柳");
            }
        };
        selection.Cancelled += () => _subtitle.ShowTransient("已取消圈选。", "小柳");
        selection.Begin();
    }

    private void SweepOnStartup()
    {
        try
        {
            using var store = HistoryStore.OpenDefault(_settings);
            var report = store.Sweep(_settings);
            Log.Info("retention", $"启动清理：图片 {report.DeletedImages} / 文本 {report.DeletedTexts} / "
                                  + $"超限淘汰 图 {report.EvictedImages} 文 {report.EvictedTexts} / "
                                  + $"剩余 {report.RemainingBytes / 1024.0 / 1024.0:F1}MB");
        }
        catch (Exception ex)
        {
            Log.Error("retention", ex);
        }
    }

    public void Dispose()
    {
        _hotkeys.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        _companion.Dispose();
    }
}
