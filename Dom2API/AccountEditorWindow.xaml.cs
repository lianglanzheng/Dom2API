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
        ModelFilterBox.Text = account.ModelFilter ?? "";
        ManualRateBox.Text = account.ManualRate.ToString("G29", System.Globalization.CultureInfo.CurrentCulture);
        AutoDetectRateBox.IsChecked = account.AutoDetectRate;
        UseProxyBox.IsChecked = !string.IsNullOrWhiteSpace(account.Proxy);
        ProxyBox.Text = account.Proxy ?? "socks5://127.0.0.1:1080";
        Loaded += (_, _) => NameBox.Focus();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var edited = Account with
        {
            Name = NameBox.Text.Trim(), BaseUrl = BaseUrlBox.Text.Trim(), ApiKey = KeyBox.Password.Trim(),
            ModelFilter = string.IsNullOrWhiteSpace(ModelFilterBox.Text) ? null : ModelFilterBox.Text
        };
        try
        {
            if (!decimal.TryParse(ManualRateBox.Text, System.Globalization.NumberStyles.AllowDecimalPoint
                | System.Globalization.NumberStyles.AllowLeadingSign | System.Globalization.NumberStyles.AllowLeadingWhite
                | System.Globalization.NumberStyles.AllowTrailingWhite, System.Globalization.CultureInfo.CurrentCulture, out decimal manualRate)
                || manualRate < 0)
                throw new ArgumentException("手动倍率必须是大于或等于零的数值。");
            edited.ManualRate = manualRate;
            edited.AutoDetectRate = AutoDetectRateBox.IsChecked == true;
            edited.Proxy = Socks5Proxy.FromInput(UseProxyBox.IsChecked == true, ProxyBox.Text);
            _ = new UpstreamSettings(edited, 1, port);
            Account = edited;
            DialogResult = true;
        }
        catch (ArgumentException exception) { ErrorText.Text = exception.Message; }
    }
}
