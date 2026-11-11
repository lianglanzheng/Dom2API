using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Dom2API.Models;
using Dom2API.Services;
using Microsoft.Win32;

namespace Dom2API.Controls;

/// <summary>一张图片的按需预览、原尺寸查看、复制和保存。</summary>
public partial class ImagePreview : UserControl
{
    /// <summary>图片来源依赖属性。</summary>
    public static readonly DependencyProperty AttachmentProperty = DependencyProperty.Register(nameof(Attachment),
        typeof(ImageAttachment), typeof(ImagePreview), new PropertyMetadata(null, Attachment_Changed));

    /// <summary>当前图片来源。</summary>
    public ImageAttachment? Attachment
    {
        get => (ImageAttachment?)GetValue(AttachmentProperty);
        set => SetValue(AttachmentProperty, value);
    }

    private byte[]? bytes;
    private CancellationTokenSource? loading;

    /// <summary>创建图片预览控件。</summary>
    public ImagePreview() => InitializeComponent();

    private static void Attachment_Changed(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var preview = (ImagePreview)sender;
        preview.loading?.Cancel();
        preview.bytes = null;
        preview.Thumbnail.Source = null;
        preview.ImageName.Text = preview.Attachment?.Name ?? "图片";
        preview.StatusText.Text = "";
        preview.LoadButton.IsEnabled = true;
        preview.SaveButton.IsEnabled = preview.CopyButton.IsEnabled = preview.ZoomButton.IsEnabled = false;
        if (preview.IsLoaded) preview.LoadEmbedded();
    }

    private void Preview_Loaded(object sender, RoutedEventArgs e) => LoadEmbedded();
    private void Preview_Unloaded(object sender, RoutedEventArgs e) => loading?.Cancel();
    private async void LoadEmbedded()
    {
        if (bytes is null && Attachment?.Source.StartsWith("data:", StringComparison.OrdinalIgnoreCase) == true)
            await LoadAsync();
    }
    private async void Load_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    private async Task LoadAsync()
    {
        if (Attachment is not { } attachment) return;
        loading?.Cancel();
        using var cancellation = new CancellationTokenSource();
        loading = cancellation;
        LoadButton.IsEnabled = false;
        StatusText.Text = "正在加载图片…";
        try
        {
            byte[] data = await ImagePreviewLoader.ReadAsync(attachment, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            bytes = data;
            SaveButton.IsEnabled = true;
            var bitmap = await Task.Run(() => ImagePreviewLoader.Decode(data, 480), cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            Thumbnail.Source = bitmap;
            CopyButton.IsEnabled = ZoomButton.IsEnabled = true;
            StatusText.Text = $"{data.Length:N0} 字节";
        }
        catch (OperationCanceledException)
        {
            if (loading == cancellation) StatusText.Text = "加载已取消，可重新加载。";
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            if (loading == cancellation) StatusText.Text = "预览失败：" + exception.Message
                + (bytes is not null ? " 可保存原始图片。" : "");
        }
        finally
        {
            if (loading == cancellation)
            {
                loading = null;
                LoadButton.IsEnabled = true;
            }
        }
    }

    private void Zoom_Click(object sender, RoutedEventArgs e)
    {
        if (bytes is null) return;
        TryImageAction(() =>
        {
            var bitmap = ImagePreviewLoader.Decode(bytes);
            var image = new Image { Source = bitmap, Stretch = Stretch.Uniform, Width = bitmap.PixelWidth, Height = bitmap.PixelHeight };
            var slider = new Slider { Minimum = 10, Maximum = 200, Value = 100, Width = 220, Margin = new Thickness(12) };
            slider.ValueChanged += (_, _) => { image.Width = bitmap.PixelWidth * slider.Value / 100; image.Height = bitmap.PixelHeight * slider.Value / 100; };
            var label = new TextBlock { Text = $"{bitmap.PixelWidth} × {bitmap.PixelHeight} · 缩放", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12) };
            var toolbar = new StackPanel { Orientation = Orientation.Horizontal };
            toolbar.Children.Add(label);
            toolbar.Children.Add(slider);
            DockPanel.SetDock(toolbar, Dock.Top);
            var panel = new DockPanel();
            panel.Children.Add(toolbar);
            panel.Children.Add(new ScrollViewer { Content = image, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            new Window { Title = Attachment?.Name ?? "图片", Owner = Window.GetWindow(this), Width = 900, Height = 700, Content = panel }.Show();
        });
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (bytes is not null) TryImageAction(() => { Clipboard.SetImage(ImagePreviewLoader.Decode(bytes)); StatusText.Text = "图片已复制。"; });
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (bytes is not { } data) return;
        string extension = ImagePreviewLoader.Extension(data);
        var dialog = new SaveFileDialog { FileName = "image" + extension, DefaultExt = extension,
            Filter = $"原始图片 (*{extension})|*{extension}|所有文件 (*.*)|*.*" };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            TryImageAction(() => { File.WriteAllBytes(dialog.FileName, data); StatusText.Text = "图片已保存。"; });
    }

    private void TryImageAction(Action action)
    {
        try { action(); }
        catch (Exception exception) when (exception is not OutOfMemoryException) { StatusText.Text = "操作失败：" + exception.Message; }
    }
}
