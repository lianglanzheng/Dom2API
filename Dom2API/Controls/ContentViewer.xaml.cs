using System.Windows;
using System.Windows.Controls;
using Dom2API.Services;
using Dom2API.Models;

namespace Dom2API.Controls;

/// <summary>
/// 自动识别 JSON、XML 并提供结构树与原始文本切换的内容详情控件。
/// </summary>
public partial class ContentViewer : UserControl
{
    /// <summary>标识原始内容的依赖属性。</summary>
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(ContentViewer), new PropertyMetadata("", Text_Changed));

    private bool hasStructure;

    /// <summary>当前消息关联的图片集合。</summary>
    public static readonly DependencyProperty ImagesProperty = DependencyProperty.Register(nameof(Images),
        typeof(IReadOnlyList<ImageAttachment>), typeof(ContentViewer),
        new PropertyMetadata(null, (sender, _) => ((ContentViewer)sender).UpdateDisplay()));

    /// <summary>在原始内容之外提供图片预览。</summary>
    public IReadOnlyList<ImageAttachment>? Images
    {
        get => (IReadOnlyList<ImageAttachment>?)GetValue(ImagesProperty);
        set => SetValue(ImagesProperty, value);
    }

    /// <summary>获取或设置原始内容，不对其格式或换行进行改写。</summary>
    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>
    /// 初始化内容详情控件。
    /// </summary>
    public ContentViewer()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 内容切换时重新识别格式，有结构的内容默认展示树。
    /// </summary>
    private static void Text_Changed(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var viewer = (ContentViewer)sender;
        string text = e.NewValue as string ?? "";
        viewer.PlainText.Text = text;
        viewer.hasStructure = ContentTreeBuilder.TryBuild(text, out string format, out var nodes);
        viewer.ContentTree.ItemsSource = nodes;
        viewer.FormatLabel.Text = format;
        viewer.PlainTextOption.Visibility = viewer.hasStructure ? Visibility.Visible : Visibility.Collapsed;
        viewer.PlainTextOption.IsChecked = false;
        viewer.UpdateDisplay();
    }

    /// <summary>
    /// 在树状显示与纯文本显示之间切换，保留树的展开状态。
    /// </summary>
    private void PlainTextOption_Click(object sender, RoutedEventArgs e) => UpdateDisplay();

    /// <summary>
    /// 根据解析结果与显示选项更新两个视图的可见性。
    /// </summary>
    private void UpdateDisplay()
    {
        bool hasImages = Images is { Count: > 0 };
        PlainTextOption.Visibility = hasStructure || hasImages ? Visibility.Visible : Visibility.Collapsed;
        PlainTextOption.Content = hasImages ? "原始内容" : "纯文本";
        bool showImages = hasImages && PlainTextOption.IsChecked != true;
        bool showTree = !showImages && hasStructure && PlainTextOption.IsChecked != true;
        ImageGallery.Visibility = showImages ? Visibility.Visible : Visibility.Collapsed;
        ContentTree.Visibility = showTree ? Visibility.Visible : Visibility.Collapsed;
        PlainText.Visibility = showTree || showImages ? Visibility.Collapsed : Visibility.Visible;
    }
}
