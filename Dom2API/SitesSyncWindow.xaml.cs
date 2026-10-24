using System.Windows;
using Dom2API.Models;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Navigation;
using Dom2API.Services;
using Dom2API.ViewModels;

namespace Dom2API;

/// <summary>管理站点连接并选择同步 API 密钥。</summary>
public partial class SitesSyncWindow : Window
{
    /// <summary>主窗口是否启用连接配置记忆。</summary>
    public static readonly DependencyProperty RememberSettingsEnabledProperty = DependencyProperty.Register(
        nameof(RememberSettingsEnabled), typeof(bool), typeof(SitesSyncWindow), new PropertyMetadata(false));

    /// <summary>用于显示明文密码保存提醒，随主窗口选项同步更新。</summary>
    public bool RememberSettingsEnabled
    {
        get => (bool)GetValue(RememberSettingsEnabledProperty);
        set => SetValue(RememberSettingsEnabledProperty, value);
    }

    private readonly SitesClient client = new();
    private readonly CancellationTokenSource shutdown = new();
    private readonly CancellationToken stopToken;
    private readonly SitesSyncViewModel viewModel = new();
    private readonly Func<IReadOnlyList<UpstreamAccount>> getAccounts;
    private readonly Func<UpstreamAccount, Task<bool>> addAccount;
    private readonly Func<string, Task<bool>> removeAccount;
    private readonly Action<List<SiteConnection>> saveSites;
    private Task? closeTask;
    private bool canClose;

    /// <summary>站点修改即时写回内存，磁盘记忆由主窗口控制。</summary>
    public SitesSyncWindow(IEnumerable<SiteConnection> sites,
        Func<IReadOnlyList<UpstreamAccount>> getAccounts, Func<UpstreamAccount, Task<bool>> addAccount,
        Func<string, Task<bool>> removeAccount, Action<List<SiteConnection>> saveSites)
    {
        InitializeComponent();
        stopToken = shutdown.Token;
        this.getAccounts = getAccounts;
        this.addAccount = addAccount;
        this.removeAccount = removeAccount;
        this.saveSites = saveSites;
        DataContext = viewModel;
        foreach (var site in sites) viewModel.Sites.Add(new(site, client, getAccounts));
        Loaded += InitialRefreshAsync;
    }

    private async void InitialRefreshAsync(object sender, RoutedEventArgs e)
    {
        Loaded -= InitialRefreshAsync;
        foreach (var site in viewModel.Sites.ToArray())
        {
            if (stopToken.IsCancellationRequested) break;
            if (viewModel.Sites.Contains(site)) await site.RefreshAsync(stopToken);
        }
    }

    private async void AddSite_Click(object sender, RoutedEventArgs e)
    {
        var editor = new SiteEditorWindow { Owner = this };
        if (editor.ShowDialog() != true) return;
        if (viewModel.Sites.Any(item => new Uri(item.Site.BaseUrl).AbsoluteUri.TrimEnd('/') == new Uri(editor.Site.BaseUrl).AbsoluteUri.TrimEnd('/')
            && string.Equals(item.Site.Username, editor.Site.Username, StringComparison.OrdinalIgnoreCase)))
        {
            viewModel.WindowStatus = "此站点账号已添加，可编辑或刷新已有连接。";
            return;
        }
        var site = new SiteViewModel(editor.Site, client, getAccounts);
        viewModel.Sites.Add(site);
        SaveSites();
        await site.RefreshAsync(stopToken);
    }

    private async void EditSite_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SiteViewModel site } || site.IsBusy) return;
        var editor = new SiteEditorWindow(site.Site) { Owner = this };
        if (editor.ShowDialog() != true) return;
        if (viewModel.Sites.Any(item => item != site
            && new Uri(item.Site.BaseUrl).AbsoluteUri.TrimEnd('/') == new Uri(editor.Site.BaseUrl).AbsoluteUri.TrimEnd('/')
            && string.Equals(item.Site.Username, editor.Site.Username, StringComparison.OrdinalIgnoreCase)))
        {
            viewModel.WindowStatus = "此站点账号已添加，不能保存重复连接。";
            return;
        }
        var edited = new SiteViewModel(editor.Site, client, getAccounts);
        viewModel.Sites[viewModel.Sites.IndexOf(site)] = edited;
        SaveSites();
        await edited.RefreshAsync(stopToken);
    }

    private async void RefreshSite_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: SiteViewModel site }) await site.RefreshAsync(stopToken);
    }

    private void MoveSiteUp_Click(object sender, RoutedEventArgs e) => MoveSite(sender, -1);

    private void MoveSiteDown_Click(object sender, RoutedEventArgs e) => MoveSite(sender, 1);

    private void MoveSite(object sender, int offset)
    {
        if (sender is FrameworkElement { DataContext: SiteViewModel site } && viewModel.MoveSite(site, offset))
            SaveSites();
    }

    private void SiteUrl_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        e.Handled = true;
        try
        {
            var uri = SiteAccountMapping.ValidateBaseUrl(e.Uri.AbsoluteUri);
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            viewModel.WindowStatus = "";
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or SiteApiException)
        {
            viewModel.WindowStatus = "无法打开站点网址：" + exception.Message;
        }
    }

    private async void RemoveSite_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SiteViewModel site } || site.IsBusy) return;
        viewModel.Sites.Remove(site);
        SaveSites();
        await client.LogoutSiteAsync(site.Site.Id);
    }

    private void SaveSites()
    {
        saveSites(viewModel.Sites.Select(item => item.Site with { }).ToList());
        viewModel.WindowStatus = "";
    }

    private async void AddApiKey_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SiteKeyViewModel key } || !key.Parent.CanUseSnapshot) return;
        await key.Parent.ImportAsync(key, addAccount, stopToken);
        foreach (var site in viewModel.Sites) site.RefreshMatches();
    }

    private async void RemoveApiKey_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SiteKeyViewModel key } || !key.Parent.CanUseSnapshot) return;
        await key.Parent.RemoveLocalAsync(key, removeAccount, stopToken);
        foreach (var site in viewModel.Sites) site.RefreshMatches();
    }

    /// <summary>点击后直接调用站点接口删除 API 密钥。</summary>
    private async void DeleteRemoteApiKey_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SiteKeyViewModel key }
            || !key.Parent.CanUseSnapshot || stopToken.IsCancellationRequested) return;
        await key.Parent.DeleteRemoteAsync(key, stopToken);
    }

    private async void CreateGroup_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SiteGroupViewModel group } || !group.Parent.CanUseSnapshot) return;
        var dialog = new CreateApiKeyWindow(group.Parent.DisplayName, group.Name, group.Parent.Site.Type) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        await group.Parent.CreateKeyAsync(group, dialog.KeyName, stopToken);
    }

    /// <summary>先取消业务请求，再等待各站点退出登录，最后关闭窗口。</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!canClose)
        {
            e.Cancel = true;
            _ = CloseWithLogoutAsync();
        }
        base.OnClosing(e);
    }

    /// <summary>供窗口关闭及主窗口退出共同等待会话清理。</summary>
    public Task CloseWithLogoutAsync() => closeTask ??= CloseSessionsAsync();

    private async Task CloseSessionsAsync()
    {
        IsEnabled = false;
        viewModel.WindowStatus = "正在退出站点登录…";
        // 避免无会话时同步完成，在 OnClosing 返回前再次调用 Close。
        await System.Windows.Threading.Dispatcher.Yield();
        shutdown.Cancel();
        await client.DisposeAsync();
        canClose = true;
        Close();
    }

    /// <summary>释放窗口取消源；非标准关闭路径也启动会话清理。</summary>
    protected override void OnClosed(EventArgs e)
    {
        shutdown.Cancel();
        _ = client.DisposeAsync();
        shutdown.Dispose();
        base.OnClosed(e);
    }
}
