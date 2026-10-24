using System.Windows;
using Dom2API.Models;

namespace Dom2API;

/// <summary>编辑账号副本，保存且验证成功后才应用。</summary>
public partial class AccountEditorWindow : Window
{
    private readonly int port;
    /// <summary>获取验证成功的编辑结果。</summary>
    public UpstreamAccount Account { get; private set; }

    /// <summary>创建独立账号编辑对话框。</summary>
    public AccountEditorWindow(UpstreamAccount account, int port)
    {
        InitializeComponent();
        Account = account with { };
        this.port = port;
        NameBox.Text = account.Name;
        BaseUrlBox.Text = account.BaseUrl;
        KeyBox.Password = account.ApiKey;
        UseProxyBox.IsChecked = !string.IsNullOrWhiteSpace(account.Proxy);
        ProxyBox.Text = account.Proxy ?? "socks5://127.0.0.1:1080";
        Loaded += (_, _) => NameBox.Focus();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var edited = Account with { Name = NameBox.Text.Trim(), BaseUrl = BaseUrlBox.Text.Trim(), ApiKey = KeyBox.Password.Trim() };
        try
        {
            edited.Proxy = Socks5Proxy.FromInput(UseProxyBox.IsChecked == true, ProxyBox.Text);
            _ = new UpstreamSettings(edited, 1, port);
            Account = edited;
            DialogResult = true;
        }
        catch (ArgumentException exception) { ErrorText.Text = exception.Message; }
    }
}
