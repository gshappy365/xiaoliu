using System.Windows;
using System.Windows.Controls;

namespace Xiaoliu.App;

/// <summary>
/// 一行设置：左侧「标题 + 描述」，右侧编辑器。
///
/// 数值照点点的 <c>Diandian.Windows.App.DesignSystem.SettingsRow</c>：
/// MinHeight 52、标题 13、描述 11（不透明度 0.72）、底边 0.5 分隔线。
/// 点点在宽度不足 420 时会把编辑器换到下一行；小柳主窗口最小宽度就大于这个数，
/// 所以不做那套自适应，少一条分支少一处能出错的地方。
///
/// 继承 ContentControl 而不是 Control：编辑器就是 Content，XAML 里直接写子元素即可。
/// </summary>
public sealed class SettingsRowControl : ContentControl
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(SettingsRowControl), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
        nameof(Description), typeof(string), typeof(SettingsRowControl), new PropertyMetadata(string.Empty));

    static SettingsRowControl()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(SettingsRowControl),
            new FrameworkPropertyMetadata(typeof(SettingsRowControl)));
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Description
    {
        get => (string)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }
}
