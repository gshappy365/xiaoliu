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
    private readonly OverlayWindow _overlay;
    private readonly Companion _companion;

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
        _tray.DoubleClick += (_, _) => ShowHistory();

        _subtitle = new SubtitleWindow();
        _overlay = new OverlayWindow();
        _companion = new Companion(settings, _subtitle, _overlay);

        _hotkeys = new HotkeyHook(settings.PushToTalkModifiers, settings.SelectRegionModifiers, settings.SelectRegionKey);
        _hotkeys.ChordPressed += () => _companion.OnChordPressed();
        _hotkeys.ChordReleased += () => _companion.OnChordReleased();
        _hotkeys.SelectRegionPressed += () => BeginSelection();
        _hotkeys.EscapePressed += () => _companion.OnEscape();
    }

    public void Start()
    {
        Log.Info("app", $"启动 版本 {typeof(TrayHost).Assembly.GetName().Version}");
        ApplyLaunchAtLogin(_settings.LaunchAtLogin);
        SweepOnStartup();

        // 托盘菜单之外没有任何 UI，所以不能靠「最后一个窗口关闭」来退出。
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        _subtitle.ShowTransient("小柳在托盘里。按住 Ctrl+Alt 说话，Ctrl+Alt+S 圈选一块区域给我看。", "小柳");

        if (Environment.GetCommandLineArgs().Contains("--demo"))
        {
            DrawDemoMarks();
        }
    }

    /// <summary>
    /// 开发用：把四种指点图形一次画出来看效果。真实链路要等一次语音往返，
    /// 调样式时没必要每次都说话。
    /// </summary>
    private void DrawDemoMarks()
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
        _overlay.Draw(new Core.Visual.GuidanceMark
        {
            Id = Guid.NewGuid(),
            Kind = Core.Visual.GuidanceKind.Point,
            ScreenNumber = 1,
            Label = "这是一个点",
            Bounds = new Core.Capture.CaptureRect(x + 460, y + 40, 1, 1),
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
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("历史记录", null, (_, _) => ShowHistory());
        menu.Items.Add("圈选一块区域（Ctrl+Alt+S）", null, (_, _) => BeginSelection());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("打开设置文件", null, (_, _) => OpenPath(AppSettings.DefaultPath));
        var autoStart = new ToolStripMenuItem("开机自启") { Checked = _settings.LaunchAtLogin, CheckOnClick = true };
        autoStart.CheckedChanged += (_, _) =>
        {
            _settings.LaunchAtLogin = autoStart.Checked;
            _settings.Save();
            ApplyLaunchAtLogin(autoStart.Checked);
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

    private void ShowHistory()
    {
        var window = new HistoryWindow(_settings);
        window.Show();
        window.Activate();
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

    private static void ApplyLaunchAtLogin(bool enabled)
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

    public void Dispose()
    {
        _hotkeys.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        _companion.Dispose();
    }
}
