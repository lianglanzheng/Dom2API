using System.Windows;
using Dom2API.Models;
using Dom2API.Services;

namespace Dom2API;

/// <summary>编辑用于同步的站点登录信息。</summary>
public partial class SiteEditorWindow : Window
{
    private readonly CancellationTokenSource closing = new();
    private bool detecting;
    /// <summary>通过验证的站点配置。</summary>
    public SiteConnection Site { get; private set; } = new();

    /// <summary>使用配置副本初始化站点表单。</summary>
    public SiteEditorWindow(SiteConnection? site = null)
    {
        InitializeComponent();
        if (site is not null)
        {
            Site = site with { };
            NameBox.Text = site.Name;
            BaseUrlBox.Text = site.BaseUrl;
            UsernameBox.Text = site.Username;
            PasswordBox.Password = site.Password;
            UseProxyBox.IsChecked = !string.IsNullOrWhiteSpace(site.Proxy);
            ProxyBox.Text = site.Proxy ?? "socks5://127.0.0.1:1080";
            Title = "编辑站点";
        }
        else
        {
            BaseUrlBox.Text = "https://";
        }
        Loaded += (_, _) => (string.IsNullOrWhiteSpace(UsernameBox.Text) ? BaseUrlBox : UsernameBox).Focus();
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (detecting) return;
        string baseUrl = BaseUrlBox.Text.Trim().TrimEnd('/');
        string username = UsernameBox.Text.Trim();
        string password = PasswordBox.Password;
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            ErrorText.Text = "站点网址、账号和密码不能为空。";
            return;
        }

        CancellationToken token = closing.Token;
        try
        {
            Uri uri = SiteAccountMapping.ValidateBaseUrl(baseUrl);
            string normalized = uri.AbsoluteUri.TrimEnd('/');
            string? proxy = Socks5Proxy.FromInput(UseProxyBox.IsChecked == true, ProxyBox.Text);
            detecting = true;
            ProxyPanel.IsEnabled = false;
            SaveButton.IsEnabled = NameBox.IsEnabled = BaseUrlBox.IsEnabled = UsernameBox.IsEnabled = PasswordBox.IsEnabled = false;
            ErrorText.Text = "正在识别站点类型…";
            using var detector = new SiteTypeDetector(proxy: proxy);
            SiteType type = normalized == Site.BaseUrl && proxy == Site.Proxy && Enum.IsDefined(Site.Type)
                ? Site.Type : await detector.DetectAsync(normalized, token);
            token.ThrowIfCancellationRequested();
            Site = Site with { Name = NameBox.Text.Trim(), BaseUrl = normalized, Username = username, Password = password, Type = type, Proxy = proxy };
            DialogResult = true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (SiteApiException exception) { ErrorText.Text = exception.Message; }
        catch (ArgumentException exception) { ErrorText.Text = exception.Message; }
        finally
        {
            detecting = false;
            ProxyPanel.IsEnabled = true;
            if (!token.IsCancellationRequested)
                SaveButton.IsEnabled = NameBox.IsEnabled = BaseUrlBox.IsEnabled = UsernameBox.IsEnabled = PasswordBox.IsEnabled = true;
        }
    }

    /// <summary>关闭表单会取消识别，避免关闭后写入站点或发送登录请求。</summary>
    protected override void OnClosed(EventArgs e)
    {
        closing.Cancel();
        closing.Dispose();
        base.OnClosed(e);
    }
}
