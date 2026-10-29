using System.Windows;
using Xiaoliu.Core.Configuration;
using Xiaoliu.Core.Storage;

namespace Xiaoliu.App;

/// <summary>
/// 只读历史窗。搜索历史由 <c>search_history</c> 工具供模型使用；
/// 这个窗口是给人看的 —— ADR 0003 说得很清楚：库的价值取决于用户真的会去翻。
/// </summary>
public partial class HistoryWindow : Window
{
    private readonly AppSettings _settings;

    public HistoryWindow(AppSettings settings)
    {
        _settings = settings;
        InitializeComponent();
        Loaded += (_, _) => Reload();
    }

    private void Reload()
    {
        try
        {
            using var store = HistoryStore.OpenDefault(_settings);
            var entries = store.Recent(500);
            Grid.ItemsSource = entries;
            SummaryText.Text = $"{entries.Count} 条 · 共 {store.TotalBytes() / 1024.0 / 1024.0:F1} MB · "
                               + $"文本留 {_settings.TextRetentionDays} 天 / 图片留 {_settings.ImageRetentionDays} 天";
        }
        catch (Exception ex)
        {
            Log.Error("history", ex);
            SummaryText.Text = "读历史失败：" + ex.Message;
        }
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => Reload();

    private void OnClear(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this, "清空本机全部对话历史与截图？这个操作不可撤销。",
            "小柳", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.OK)
        {
            return;
        }

        try
        {
            using var store = HistoryStore.OpenDefault(_settings);
            store.ClearAll();
            Reload();
        }
        catch (Exception ex)
        {
            Log.Error("history", ex);
            MessageBox.Show(this, "清空失败：" + ex.Message, "小柳");
        }
    }
}
