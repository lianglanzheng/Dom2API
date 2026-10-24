using System.Windows;

using Dom2API.Models;

namespace Dom2API;

/// <summary>确认在指定分组中创建的 API 密钥名称。</summary>
public partial class CreateApiKeyWindow : Window
{
    /// <summary>获取用户确认并去除首尾空白的密钥名称。</summary>
    public string KeyName { get; private set; } = "";

    /// <summary>显示目标站点和分组，默认使用分组名称。</summary>
    public CreateApiKeyWindow(string siteName, string groupName, SiteType type)
    {
        InitializeComponent();
        ContextText.Text = $"站点：{siteName}\n分组：{groupName}";
        if (type == SiteType.NewApi) ContextText.Text += "\n新密钥永不过期，不设额外额度上限，仍受账号余额限制。";
        NameBox.Text = groupName;
        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    /// <summary>验证名称，确认后才允许调用站点创建接口。</summary>
    private void Create_Click(object sender, RoutedEventArgs e)
    {
        string name = NameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            ErrorText.Text = "请输入 API 密钥名称。";
            ErrorPanel.Visibility = Visibility.Visible;
            NameBox.Focus();
            return;
        }

        KeyName = name;
        DialogResult = true;
    }
}
