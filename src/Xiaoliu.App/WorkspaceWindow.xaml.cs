using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Xiaoliu.Core.Capture;
using Xiaoliu.Core.Configuration;
using Xiaoliu.Core.Interop;
using Xiaoliu.Core.Storage;
using Xiaoliu.App.Audio;

namespace Xiaoliu.App;

/// <summary>
/// 主工作区：左侧导航 + 右侧页面，设置页带二级分类。
///
/// 结构与点点 <c>MainWorkspaceWindow</c> 同构（1000×680、232 宽的导航栏、
/// 设置是窗口里的一页而不是独立窗口），但不搬它没有对应能力的东西：
/// 账号、课程库、技能包、积分这几个分类在小六里是空的，放上去只会是死入口。
///
/// 它不是独立窗口，而是**从顶部胶囊下面拉出来的一块面板**：鼠标移到胶囊上就展开，
/// 移开就收回（点点的做法）。从托盘菜单打开时是「钉住」的，不会自己消失。
/// </summary>
public partial class WorkspaceWindow : Window
{
    private readonly AppSettings _settings;
    private readonly Companion _companion;
    private readonly VisualEvidence _evidence;
    private readonly Action? _beginSelection;
    private readonly Action? _previewMarks;

    /// <summary>顶部胶囊：面板贴着它下沿展开，鼠标还压在它上面时也不收。</summary>
    private readonly PillWindow? _pill;

    /// <summary>悬停展开的面板靠轮询光标位置来收起 —— WPF 自己不知道鼠标跑哪去了。</summary>
    private readonly DispatcherTimer _hoverWatch;

    /// <summary>钉住：从托盘菜单/双击托盘图标打开时不再自动收起。</summary>
    private bool _pinned = true;

    /// <summary>连续多少次轮询发现鼠标不在面板上。攒够几次再收，避免划过缝隙就没了。</summary>
    private int _outsideTicks;

    /// <summary>正在播收起动画，别在这时候又 Show 回来。</summary>
    private bool _hiding;

    /// <summary>初始化期间不要触发「改了就保存」，否则会把默认值写回去。</summary>
    private bool _loading = true;

    public WorkspaceWindow(
        AppSettings settings,
        Companion companion,
        VisualEvidence evidence,
        Action? beginSelection = null,
        Action? previewMarks = null,
        PillWindow? pill = null)
    {
        _settings = settings;
        _companion = companion;
        _evidence = evidence;
        _beginSelection = beginSelection;
        _previewMarks = previewMarks;
        _pill = pill;

        InitializeComponent();

        var version = typeof(WorkspaceWindow).Assembly.GetName().Version;
        var versionText = $"v{version?.ToString(3) ?? "0.0.0"}";
        VersionText.Text = $"小六 {versionText}";
        AboutVersionText.Text = versionText;
        EndpointText.Text = _settings.BaseUrl;

        _companion.TurnFinished += OnTurnFinished;

        FillPickers();
        ApplySettingsToControls();
        _loading = false;

        Nav.SelectedIndex = 0;
        SettingsNav.SelectedIndex = 0;

        Log.Info("settings", $"模型下拉 {ModelPicker.Items.Count} 项，当前值 {_settings.Model}，"
                             + $"选中 {(ModelPicker.SelectedItem as ComboBoxItem)?.Content ?? "（无）"}");

        _hoverWatch = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(180),
        };
        _hoverWatch.Tick += OnHoverWatch;

        Closed += (_, _) =>
        {
            _hoverWatch.Stop();
            _companion.TurnFinished -= OnTurnFinished;
        };
    }

    /// <summary>面板展开/收起时通知外壳，胶囊据此亮/灭。</summary>
    public event Action<bool>? PanelVisibilityChanged;

    /// <summary>面板是不是钉住的（不会因为鼠标移开而收起）。</summary>
    public bool IsPinned => _pinned;

    /// <summary>
    /// 把悬停展开的面板钉住：鼠标可以离开，面板留着。
    /// 用户悬停展开后想在里面翻历史/改设置，就靠这个 —— 否则鼠标一滑走就没了。
    /// </summary>
    public void Pin()
    {
        _pinned = true;
        _outsideTicks = 0;
        _hoverWatch.Stop();

        Activate();
        Focus();
        Dispatcher.InvokeAsync(() => AskBox.Focus(), DispatcherPriority.Loaded);
    }

    /// <summary>外部（托盘、胶囊）打开时指定落在哪一页。支持 <c>settings:voice</c> 这种二级定位。</summary>
    /// <param name="pin">
    /// true = 钉住，鼠标移开也不收（托盘菜单进来就是这种）；
    /// false = 悬停态，鼠标一离开就收回（胶囊移上去展开就是这种）。
    /// </param>
    public void ShowPage(string tag, bool pin = true)
    {
        _pinned = pin;
        _outsideTicks = 0;
        _hiding = false;

        SelectPage(tag);
        PlaceUnderPill();

        if (!IsVisible)
        {
            Show();
        }

        PlayReveal();
        PanelVisibilityChanged?.Invoke(true);

        if (pin)
        {
            _hoverWatch.Stop();

            // 钉住打开是用户主动叫出来的，可以抢焦点 —— 他要立刻打字。
            Activate();
            Focus();

            // Show() 刚回来时模板还没铺开，AskBox 立刻 Focus() 会落空，等一拍再给。
            Dispatcher.InvokeAsync(() => AskBox.Focus(), DispatcherPriority.Loaded);
        }
        else
        {
            // 悬停展开不能抢焦点：用户可能正在别的窗口打字，面板一冒出来就把光标抢走很讨厌。
            _hoverWatch.Start();
        }
    }

    /// <summary>鼠标离开面板且面板没被激活时，淡出收回。</summary>
    public void HidePanel()
    {
        if (!IsVisible || _hiding)
        {
            return;
        }

        _hiding = true;
        _pinned = false;
        _hoverWatch.Stop();

        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(140))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
        };
        fade.Completed += (_, _) =>
        {
            if (_hiding)
            {
                Hide();

                // 动画会把 Opacity 钉在 0 上。不摘掉的话，下次不走 PlayReveal 的 Show()
                // （比如「预览指点图形」先藏后显那条路）会显示成一个全透明的窗口。
                BeginAnimation(OpacityProperty, null);
                Opacity = 1;

                _hiding = false;
                PanelVisibilityChanged?.Invoke(false);
            }
        };

        BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>贴着胶囊下沿、水平居中，从上面滑进来。</summary>
    private void PlaceUnderPill()
    {
        var area = SystemParameters.WorkArea;

        // 胶囊底边 = 工作区顶 + 胶囊外边距 + 胶囊高度。拿不到就退回到「顶 + 52」。
        var top = area.Top + 52;
        if (_pill is not null)
        {
            top = _pill.BottomEdge + 6;
        }

        Left = area.Left + (area.Width - Width) / 2;
        Top = top;

        // 屏幕矮的时候别让面板尾巴掉出去。
        var maxHeight = area.Bottom - top - 12;
        if (maxHeight > MinHeight && Height > maxHeight)
        {
            Height = maxHeight;
        }
    }

    private void PlayReveal()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(170))
        {
            EasingFunction = ease,
        });

        Slide.BeginAnimation(
            System.Windows.Media.TranslateTransform.YProperty,
            new DoubleAnimation(-12, 0, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
    }

    private void OnHoverWatch(object? sender, EventArgs e)
    {
        if (_pinned || !IsVisible || _hiding)
        {
            _hoverWatch.Stop();
            return;
        }

        // 鼠标还在面板上（含 8px 容差）或者还在胶囊上，都算「没走」。
        if (WindowInterop.IsCursorOver(this, 8) || _pill?.IsCursorOver == true)
        {
            _outsideTicks = 0;
            return;
        }

        // 用户点进面板里了（激活状态），说明他在用，不是路过 —— 不收。
        if (IsActive)
        {
            _outsideTicks = 0;
            return;
        }

        // 180ms 一轮，攒够 4 轮 ≈ 720ms 才收：鼠标从胶囊滑到面板要穿过 6px 缝隙，
        // 中途会短暂「哪都不在」，太快收起会闪。
        if (++_outsideTicks >= 4)
        {
            HidePanel();
        }
    }

    /// <summary>切到某一页（含 <c>settings:voice</c> 这种二级定位）。</summary>
    private void SelectPage(string tag)
    {
        var parts = tag.Split(':', 2);
        var page = parts[0];

        foreach (var item in Nav.Items.OfType<ListBoxItem>())
        {
            if (string.Equals(item.Tag as string, page, StringComparison.Ordinal))
            {
                Nav.SelectedItem = item;
                break;
            }
        }

        if (parts.Length > 1)
        {
            foreach (var item in SettingsNav.Items.OfType<ListBoxItem>())
            {
                if (string.Equals(item.Tag as string, parts[1], StringComparison.Ordinal))
                {
                    SettingsNav.SelectedItem = item;
                    break;
                }
            }
        }
    }

    // ------------------------------------------------------------------ 导航

    private void OnNavSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Nav.SelectedItem is not ListBoxItem item)
        {
            return;
        }

        var tag = item.Tag as string;
        HomePage.Visibility = tag == "home" ? Visibility.Visible : Visibility.Collapsed;
        HistoryPage.Visibility = tag == "history" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = tag == "settings" ? Visibility.Visible : Visibility.Collapsed;

        PageTitle.Text = tag switch
        {
            "history" => "历史",
            "settings" => "设置",
            _ => "对话",
        };

        if (tag == "history")
        {
            ReloadHistory();
        }
        else if (tag == "home")
        {
            RefreshHome();
        }
    }

    private void OnSettingsNavSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SettingsNav.SelectedItem is not ListBoxItem item)
        {
            return;
        }

        var tag = item.Tag as string;
        GeneralPanel.Visibility = tag == "general" ? Visibility.Visible : Visibility.Collapsed;
        VoicePanel.Visibility = tag == "voice" ? Visibility.Visible : Visibility.Collapsed;
        ModelPanel.Visibility = tag == "model" ? Visibility.Visible : Visibility.Collapsed;
        AppearancePanel.Visibility = tag == "appearance" ? Visibility.Visible : Visibility.Collapsed;
        DataPanel.Visibility = tag == "data" ? Visibility.Visible : Visibility.Collapsed;
        ConnectionPanel.Visibility = tag == "connection" ? Visibility.Visible : Visibility.Collapsed;
        AboutPanel.Visibility = tag == "about" ? Visibility.Visible : Visibility.Collapsed;

        if (tag == "data")
        {
            RefreshUsage();
        }
    }

    // ------------------------------------------------------------------ 填充

    private static ComboBoxItem Item(string text, object tag, string? tip = null) =>
        new() { Content = text, Tag = tag, ToolTip = tip };

    /// <summary>下拉里的分组标题：不可选，只是把三组分开。</summary>
    private static ComboBoxItem Header(string text) => new()
    {
        Content = text,
        IsEnabled = false,
        FontSize = 11,
        Padding = new Thickness(10, 9, 10, 3),

        // 用 TryFindResource 而不是 FindResource：主题字典挂在窗口上而不是 Application 上，
        // 查不到会抛异常，把整个 FillModelPicker 打断 —— 表现出来就是下拉里只剩几个协议名。
        Foreground = Application.Current?.TryFindResource("MutedBrush") as System.Windows.Media.Brush
                     ?? System.Windows.Media.Brushes.Gray,
    };

    /// <summary>
    /// 模型下拉：先放能看图的（截屏理解要靠它），再放纯文本的。
    /// 清单来自 arkcli 的一次快照，见 <see cref="ModelCatalog"/>。
    /// </summary>
    private void FillModelPicker()
    {
        ModelPicker.Items.Clear();

        ModelPicker.Items.Add(Header("能看懂截屏"));
        foreach (var model in ModelCatalog.Vision)
        {
            ModelPicker.Items.Add(Item(
                model.Display, model.Id, $"{model.Id}\n{model.Vendor} · {model.Note}"));
        }

        ModelPicker.Items.Add(Header("只看文本（圈选/截图类提问会失效）"));
        foreach (var model in ModelCatalog.TextOnly)
        {
            ModelPicker.Items.Add(Item(
                model.Display, model.Id, $"{model.Id}\n{model.Vendor} · {model.Note}"));
        }
    }

    /// <summary>按标识选中；清单里没有就临时插一项，免得下拉框显示空白。</summary>
    private void SelectModel(string id)
    {
        foreach (var item in ModelPicker.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag as string, id, StringComparison.OrdinalIgnoreCase))
            {
                ModelPicker.SelectedItem = item;
                return;
            }
        }

        var extra = Item($"{id}（当前配置）", id, $"{id}\n清单里没有这个名字，是手输进去的");
        ModelPicker.Items.Insert(0, extra);
        ModelPicker.SelectedItem = extra;
    }

    private void FillPickers()
    {
        HoldPicker.Items.Clear();
        HoldPicker.Items.Add(Item("150 毫秒 · 灵敏", 150));
        HoldPicker.Items.Add(Item("250 毫秒 · 标准", 250));
        HoldPicker.Items.Add(Item("400 毫秒 · 稳一点", 400));

        RequestBudgetPicker.Items.Clear();
        foreach (var value in new[] { 3, 4, 6, 8, 10 })
        {
            RequestBudgetPicker.Items.Add(Item($"{value} 次", value));
        }

        TextRetentionPicker.Items.Clear();
        foreach (var value in new[] { 30, 90, 180, 365 })
        {
            TextRetentionPicker.Items.Add(Item($"{value} 天", value));
        }

        ImageRetentionPicker.Items.Clear();
        foreach (var value in new[] { 1, 3, 7, 30 })
        {
            ImageRetentionPicker.Items.Add(Item($"{value} 天", value));
        }

        QuotaPicker.Items.Clear();
        foreach (var value in new[] { 100, 200, 500, 1024 })
        {
            QuotaPicker.Items.Add(Item($"{value} MB", value));
        }

        FillModelPicker();

        RefreshDevices();
    }

    private void RefreshDevices()
    {
        var keep = _settings.CaptureDeviceNumber;
        CaptureDevicePicker.Items.Clear();
        CaptureDevicePicker.Items.Add(Item("跟随系统默认", -1));

        try
        {
            var devices = AudioEngine.ListCaptureDevices();
            foreach (var name in devices)
            {
                // "0: 名称" → 序号与名称分开
                var split = name.IndexOf(':');
                if (split > 0 && int.TryParse(name[..split], out var index))
                {
                    CaptureDevicePicker.Items.Add(Item(name[(split + 1)..].Trim(), index));
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("settings", ex);
            CaptureDevicePicker.Items.Add(Item("枚举设备失败：" + ex.Message, -1));
        }

        foreach (var item in CaptureDevicePicker.Items.OfType<ComboBoxItem>())
        {
            if (item.Tag is int index && index == keep)
            {
                CaptureDevicePicker.SelectedItem = item;
                return;
            }
        }

        CaptureDevicePicker.SelectedIndex = 0;
    }

    private void ApplySettingsToControls()
    {
        LaunchAtLoginSwitch.IsChecked = _settings.LaunchAtLogin;
        PushToTalkBox.Text = _settings.PushToTalkModifiers.Replace(",", " + ");
        SelectRegionBox.Text = $"{_settings.SelectRegionModifiers.Replace(",", " + ")} + {_settings.SelectRegionKey}";

        SelectByTag(HoldPicker, _settings.PushToTalkHoldMilliseconds);
        SelectByTag(RequestBudgetPicker, _settings.MaxModelRequestsPerTurn);
        SelectByTag(TextRetentionPicker, _settings.TextRetentionDays);
        SelectByTag(ImageRetentionPicker, _settings.ImageRetentionDays);
        SelectByTag(QuotaPicker, _settings.MaxTotalMegabytes);
        SelectByTag(CaptureDevicePicker, _settings.CaptureDeviceNumber);

        AsrPartialSwitch.IsChecked = _settings.ShowAsrPartial;
        ShowCursorSwitch.IsChecked = _settings.ShowCursor;
        ShowPillSwitch.IsChecked = _settings.ShowPill;
        BubbleFollowSwitch.IsChecked = _settings.BubbleFollowsTarget;

        ReasoningSlider.Value = _settings.ReasoningEffort switch
        {
            "low" => 0,
            "high" => 2,
            _ => 1,
        };
        UpdateReasoningText();

        SelectModel(_settings.Model);
        VoicePicker.Text = _settings.TtsVoiceId;

        BaseUrlBox.Text = _settings.BaseUrl;
        TokenBox.Text = _settings.AccessToken;
        UntrustedTlsSwitch.IsChecked = _settings.AllowUntrustedTls;

        RefreshHome();
    }

    private static void SelectByTag(ComboBox picker, object tag)
    {
        foreach (var item in picker.Items.OfType<ComboBoxItem>())
        {
            if (Equals(item.Tag, tag))
            {
                picker.SelectedItem = item;
                return;
            }
        }
    }

    private static object? SelectedTag(ComboBox picker) =>
        (picker.SelectedItem as ComboBoxItem)?.Tag;

    private void Save()
    {
        if (_loading)
        {
            return;
        }

        _settings.Save();
    }

    // ------------------------------------------------------------------ 通用

    private void OnLaunchAtLoginChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _settings.LaunchAtLogin = LaunchAtLoginSwitch.IsChecked == true;
        Save();
        LaunchAtLogin.Apply(_settings.LaunchAtLogin);
        CaptionStatus.Text = _settings.LaunchAtLogin ? "已设为开机自启" : "已取消开机自启";
    }

    private void OnHoldChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || SelectedTag(HoldPicker) is not int ms)
        {
            return;
        }

        _settings.PushToTalkHoldMilliseconds = ms;
        Save();
    }

    private void OnTypingSelfCheckClick(object sender, RoutedEventArgs e)
    {
        // 自检要抢前台，主窗口留着会干扰它；先藏起来，跑完再回来。
        Hide();
        Task.Run(() =>
        {
            try
            {
                TypingSelfCheck.Run();
            }
            finally
            {
                Dispatcher.Invoke(() =>
                {
                    Show();
                    Activate();
                    MessageBox.Show(this, "自检已跑完，结论在日志里（关键词 selftest）。", "小六");
                });
            }
        });
    }

    private void OnOpenLogsClick(object sender, RoutedEventArgs e) =>
        OpenFolder(Path.Combine(AppSettings.DefaultDirectory, "logs"));

    private void OnOpenSettingsFileClick(object sender, RoutedEventArgs e) =>
        OpenFolder(AppSettings.DefaultDirectory);

    private void OnOpenDataDirectoryClick(object sender, RoutedEventArgs e) =>
        OpenFolder(AppSettings.DefaultDirectory);

    private void OnOpenInstallDirectoryClick(object sender, RoutedEventArgs e) =>
        OpenFolder(AppContext.BaseDirectory);

    private static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{path}\"")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Log.Error("settings", ex);
        }
    }

    // ------------------------------------------------------------------ 语音

    private void OnCaptureDeviceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || SelectedTag(CaptureDevicePicker) is not int index)
        {
            return;
        }

        _settings.CaptureDeviceNumber = index;
        Save();
        CaptionStatus.Text = index < 0 ? "麦克风跟随系统默认" : $"麦克风已指定设备 {index}";
    }

    private void OnRefreshDevicesClick(object sender, RoutedEventArgs e)
    {
        _loading = true;
        RefreshDevices();
        _loading = false;
        MicLevelText.Text = "已刷新";
    }

    private async void OnTestMicClick(object sender, RoutedEventArgs e)
    {
        MicLevelText.Text = "录 3 秒…";
        var seconds = 3;
        var (peak, device, error) = await Task.Run(() => MeasureMic(seconds));

        MicLevelText.Text = error is not null
            ? error
            : $"peak {peak:F4}（{device}）" + (peak < 0.005 ? " · 没收到声音" : " · 有声音");
    }

    private (double Peak, string Device, string? Error) MeasureMic(int seconds)
    {
        try
        {
            var engine = new AudioEngine { CaptureDeviceNumber = _settings.CaptureDeviceNumber };
            var peak = 0;
            engine.AudioCaptured += chunk =>
            {
                for (var i = 0; i + 1 < chunk.Length; i += 2)
                {
                    var sample = Math.Abs(BitConverter.ToInt16(chunk, i));
                    if (sample > peak)
                    {
                        peak = sample;
                    }
                }
            };

            if (!engine.StartRecording())
            {
                return (0, "未知", engine.LastError ?? "麦克风打不开");
            }

            Thread.Sleep(seconds * 1000);
            engine.StopRecording();
            engine.Dispose();

            return (peak / 32768.0, engine.LastCaptureDeviceName ?? "未知", null);
        }
        catch (Exception ex)
        {
            Log.Error("settings", ex);
            return (0, "未知", "测试失败：" + ex.Message);
        }
    }

    private void OnVoiceCommitted(object sender, RoutedEventArgs e) => ApplyVoice();

    private void OnVoiceKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ApplyVoice();
        }
    }

    private void ApplyVoice()
    {
        var value = VoicePicker.Text.Trim();
        if (_loading || value.Length == 0 || value == _settings.TtsVoiceId)
        {
            return;
        }

        _settings.TtsVoiceId = value;
        Save();
        CaptionStatus.Text = "音色已改";
    }

    private void OnAsrPartialChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _settings.ShowAsrPartial = AsrPartialSwitch.IsChecked == true;
        Save();
    }

    // ------------------------------------------------------------------ 模型

    private void OnModelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ModelPicker.SelectedItem is ComboBoxItem picked)
        {
            ModelPicker.ToolTip = picked.ToolTip;
        }

        if (_loading || ModelPicker.SelectedItem is not ComboBoxItem item || item.Tag is not string id)
        {
            return;
        }

        if (string.Equals(id, _settings.Model, StringComparison.Ordinal))
        {
            return;
        }

        _settings.Model = id;
        Save();
        CaptionStatus.Text = "模型已改，下一轮生效";
    }

    private void OnReasoningChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading)
        {
            return;
        }

        _settings.ReasoningEffort = (int)Math.Round(ReasoningSlider.Value) switch
        {
            0 => "low",
            2 => "high",
            _ => "medium",
        };
        UpdateReasoningText();
        Save();
    }

    private void UpdateReasoningText()
    {
        ReasoningText.Text = _settings.ReasoningEffort switch
        {
            "low" => "迅速",
            "high" => "深度",
            _ => "标准",
        };
    }

    private void OnRequestBudgetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || SelectedTag(RequestBudgetPicker) is not int budget)
        {
            return;
        }

        _settings.MaxModelRequestsPerTurn = budget;
        Save();
    }

    // ------------------------------------------------------------------ 外观

    private void OnShowPillChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _settings.ShowPill = ShowPillSwitch.IsChecked == true;
        Save();
        CaptionStatus.Text = _settings.ShowPill ? "常驻胶囊已打开（重开生效）" : "常驻胶囊已关掉（重开生效）";
    }

    private void OnBubbleFollowChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _settings.BubbleFollowsTarget = BubbleFollowSwitch.IsChecked == true;
        Save();
    }

    private void OnShowCursorChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _settings.ShowCursor = ShowCursorSwitch.IsChecked == true;
        Save();
    }

    private void OnPreviewMarksClick(object sender, RoutedEventArgs e)
    {
        Hide();
        _previewMarks?.Invoke();
        Dispatcher.InvokeAsync(async () =>
        {
            await Task.Delay(600);
            Show();
            Activate();
        });
    }

    // ------------------------------------------------------------------ 数据

    private void OnTextRetentionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || SelectedTag(TextRetentionPicker) is not int days)
        {
            return;
        }

        _settings.TextRetentionDays = days;
        Save();
    }

    private void OnImageRetentionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || SelectedTag(ImageRetentionPicker) is not int days)
        {
            return;
        }

        _settings.ImageRetentionDays = days;
        Save();
    }

    private void OnQuotaChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || SelectedTag(QuotaPicker) is not int megabytes)
        {
            return;
        }

        _settings.MaxTotalMegabytes = megabytes;
        Save();
    }

    private void RefreshUsage()
    {
        try
        {
            using var store = HistoryStore.OpenDefault(_settings);
            var entries = store.Recent(1);
            UsageText.Text = $"{store.TotalBytes() / 1024.0 / 1024.0:F1} MB";
            _ = entries;
        }
        catch (Exception ex)
        {
            UsageText.Text = "读不到：" + ex.Message;
        }
    }

    private void OnClearHistoryClick(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this, "清空本机全部对话历史与截图？这个操作不可撤销。",
            "小六", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.OK)
        {
            return;
        }

        try
        {
            using var store = HistoryStore.OpenDefault(_settings);
            store.ClearAll();
            ReloadHistory();
            RefreshUsage();
            CaptionStatus.Text = "历史已清空";
        }
        catch (Exception ex)
        {
            Log.Error("history", ex);
            MessageBox.Show(this, "清空失败：" + ex.Message, "小六");
        }
    }

    // ------------------------------------------------------------------ 连接

    private void OnUntrustedTlsChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        _settings.AllowUntrustedTls = UntrustedTlsSwitch.IsChecked == true;
        Save();
    }

    private async void OnSaveConnectionClick(object sender, RoutedEventArgs e)
    {
        _settings.BaseUrl = BaseUrlBox.Text.Trim();
        _settings.AccessToken = TokenBox.Text.Trim();
        _settings.Normalize();
        Save();

        EndpointText.Text = _settings.BaseUrl;
        ConnectionResultText.Text = "正在测试…";

        try
        {
            using var client = new HttpClient();
            var uri = new Uri(_settings.BaseUri, "healthz");
            var response = await client.GetAsync(uri);
            ConnectionResultText.Text = response.IsSuccessStatusCode
                ? $"通（{(int)response.StatusCode}）"
                : $"返回 {(int)response.StatusCode}";
        }
        catch (Exception ex)
        {
            ConnectionResultText.Text = "连不上：" + ex.Message;
        }
    }

    // ------------------------------------------------------------------ 对话页

    private async void OnSendClick(object sender, RoutedEventArgs e) => await SendAsync();

    /// <summary>
    /// 必须挂在 PreviewKeyDown 上：TextBox 自己的类处理器会把 Enter 处理掉并标记 Handled，
    /// 挂在 KeyDown 上永远收不到（实测：窗口能收到 Return，这个方法不执行）。
    /// </summary>
    private async void OnAskBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            await SendAsync();
        }
    }

    private async Task SendAsync()
    {
        var question = AskBox.Text.Trim();
        if (question.Length == 0)
        {
            return;
        }

        AskBox.Text = string.Empty;
        SendButton.IsEnabled = false;
        CaptionStatus.Text = "在想…";
        LastQuestionText.Text = question;
        LastAnswerText.Text = "…";

        try
        {
            await _companion.AskTextAsync(question);
        }
        catch (Exception ex)
        {
            Log.Error("turn", ex);
            LastAnswerText.Text = "这一轮出错了：" + ex.Message;
        }
        finally
        {
            SendButton.IsEnabled = true;
            CaptionStatus.Text = string.Empty;
            RefreshHome();
        }
    }

    private void OnSelectRegionClick(object sender, RoutedEventArgs e)
    {
        _beginSelection?.Invoke();
        CaptionStatus.Text = "拖出一块区域";
    }

    private void OnPickImageClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择一张图片给小六看",
            Filter = "图片|*.png;*.jpg;*.jpeg;*.bmp",
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var image = ScreenCapture.FromFile(dialog.FileName);
            _companion.AddImage(image);
            RefreshHome();
            CaptionStatus.Text = "已加入一张图片";
        }
        catch (Exception ex)
        {
            Log.Error("capture", ex);
            MessageBox.Show(this, "读不到这张图：" + ex.Message, "小六");
        }
    }

    private void OnClearEvidenceClick(object sender, RoutedEventArgs e)
    {
        _evidence.Clear();
        RefreshHome();
        CaptionStatus.Text = "画面已清空";
    }

    private void OnTurnFinished(string question, string answer)
    {
        Dispatcher.Invoke(() =>
        {
            LastQuestionText.Text = question;
            LastAnswerText.Text = answer;
            RefreshHome();
        });
    }

    private void RefreshHome()
    {
        var shots = _evidence.Shots;
        EvidenceSummary.Text = shots.Count == 0
            ? "还没有圈选任何区域。"
            : $"{shots.Count} 张画面，指纹都还对得上（画面变了就会被判为过时）。";

        EvidenceList.Children.Clear();
        foreach (var shot in shots)
        {
            var line = new TextBlock
            {
                Style = (Style)FindResource("RowDescription"),
                Margin = new Thickness(0, 4, 0, 0),
                Text = $"· {shot.Image.Width}×{shot.Image.Height}  {shot.CaptureCallId}  {shot.CapturedAt:HH:mm:ss}",
            };
            EvidenceList.Children.Add(line);
        }

        if (HistorySummary.Text.Length == 0)
        {
            HistorySummary.Text = "还没读过历史。";
        }
    }

    // ------------------------------------------------------------------ 历史页

    private void ReloadHistory()
    {
        try
        {
            using var store = HistoryStore.OpenDefault(_settings);
            var entries = store.Recent(500);
            HistoryGrid.ItemsSource = entries;
            HistorySummary.Text = $"{entries.Count} 条 · 共 {store.TotalBytes() / 1024.0 / 1024.0:F1} MB · "
                                  + $"文本留 {_settings.TextRetentionDays} 天 / 截图留 {_settings.ImageRetentionDays} 天";
        }
        catch (Exception ex)
        {
            Log.Error("history", ex);
            HistorySummary.Text = "读历史失败：" + ex.Message;
        }
    }

    private void OnHistoryRefreshClick(object sender, RoutedEventArgs e) => ReloadHistory();

    private void OnHistorySearchClick(object sender, RoutedEventArgs e) => RunHistorySearch();

    private void OnHistorySearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            RunHistorySearch();
        }
    }

    private void RunHistorySearch()
    {
        var query = HistorySearchBox.Text.Trim();
        if (query.Length == 0)
        {
            ReloadHistory();
            return;
        }

        try
        {
            using var store = HistoryStore.OpenDefault(_settings);
            var hits = store.Search(query, 200);
            HistoryGrid.ItemsSource = hits;
            HistorySummary.Text = $"「{query}」命中 {hits.Count} 条";
        }
        catch (Exception ex)
        {
            Log.Error("history", ex);
            HistorySummary.Text = "搜索失败：" + ex.Message;
        }
    }

    private void OnHistoryClearClick(object sender, RoutedEventArgs e) => OnClearHistoryClick(sender, e);

    // ------------------------------------------------------------------ 窗口

    /// <summary>Esc 收起面板。挂着问一句的输入框按 Esc 也只是收起，不丢草稿。</summary>
    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        _pinned = false;
        HidePanel();
        e.Handled = true;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        _pinned = false;
        HidePanel();
    }
}
