using System.ComponentModel;
using System.Windows;
using System.IO;
using System.Text.Json;
using System.Windows.Threading;
using Dom2API.Models;
using Dom2API.Services;
using Dom2API.ViewModels;
using Forms = System.Windows.Forms;

namespace Dom2API;

/// <summary>
/// 提供 Dom2API 服务、会话消息和网络请求的主窗口。
/// </summary>
public partial class MainWindow : Window
{
    private readonly CaptureStore store = new();
    private readonly SettingsStore settingsStore;
    private readonly ProxyService service;
    private readonly MainViewModel viewModel;
    private readonly DispatcherTimer refreshTimer;
    private readonly DispatcherTimer elapsedTimer;
    private readonly Forms.NotifyIcon trayIcon;
    private readonly System.Drawing.Icon trayImage;
    private readonly Forms.ContextMenuStrip trayMenu;
    private bool canClose;
    private bool closing;
    private bool closeRequested;
    private bool settingsReady;
    private bool rememberedServiceWasRunning;
    private Task? networkExportTask;
    private List<SiteConnection> rememberedSites = [];

    /// <summary>
    /// 初始化主窗口及其界面资源。
    /// </summary>
    public MainWindow() : this(new SettingsStore(AppContext.BaseDirectory), new LoginStartupService())
    {
    }

    /// <summary>使用指定的配置存储和登录启动管理器初始化窗口。</summary>
    internal MainWindow(SettingsStore settingsStore, LoginStartupService loginStartup)
    {
        this.settingsStore = settingsStore;
        this.loginStartup = loginStartup;
        InitializeComponent();
        service = new ProxyService(store);
        viewModel = new MainViewModel(store);
        DataContext = viewModel;
        RestoreRememberedSettings();
        RefreshLoginStartupState();
        Activated += Window_Activated;
        service.UpdateBillingSites(rememberedSites);
        viewModel.PropertyChanged += Settings_PropertyChanged;
        settingsReady = true;
        refreshTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(300), DispatcherPriority.Background,
            RefreshTimer_Tick, Dispatcher);
        elapsedTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background,
            ElapsedTimer_Tick, Dispatcher);
        viewModel.Refresh();
        trayMenu = new Forms.ContextMenuStrip();
        trayMenu.Items.Add("显示窗口", null, TrayShow_Click);
        trayMenu.Items.Add("退出", null, TrayExit_Click);
        // 从程序集资源读取并克隆图标，使单文件发布和输入流释放后仍可使用。
        using (var stream = Application.GetResourceStream(new Uri("pack://application:,,,/Dom2API;component/Assets/Dom2API.ico")).Stream)
        using (var icon = new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize))
        {
            trayImage = (System.Drawing.Icon)icon.Clone();
        }

        trayIcon = new Forms.NotifyIcon
        {
            Text = "Dom2API",
            Icon = trayImage,
            ContextMenuStrip = trayMenu,
            Visible = true
        };
        trayIcon.DoubleClick += TrayShow_Click;
    }

    /// <summary>
    /// 手动启动显示窗口，登录自启动保持托盘运行；服务按记住的启停状态恢复。
    /// </summary>
    public async Task InitializeStartupAsync(bool launchedAtLogin)
    {
        if (closeRequested)
        {
            return;
        }

        if (!launchedAtLogin)
        {
            Show();
        }

        if (rememberedServiceWasRunning && RememberSettingsBox.IsChecked == true && !closeRequested)
        {
            await StartServiceAsync();
        }
    }

    /// <summary>
    /// 从托盘恢复窗口，并将最小化窗口恢复到正常状态。
    /// </summary>
    private void TrayShow_Click(object? sender, EventArgs e) => ShowFromTray();

    /// <summary>通过托盘操作或重复手动启动恢复窗口。</summary>
    internal void ShowFromTray()
    {
        if (closeRequested || closing)
        {
            return;
        }

        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }

    /// <summary>
    /// 仅在用户选择托盘退出时发起真正的关闭，并防止重复退出。
    /// </summary>
    private void TrayExit_Click(object? sender, EventArgs e)
    {
        if (closeRequested)
        {
            return;
        }

        closeRequested = true;
        trayMenu.Enabled = false;
        Close();
    }

    /// <summary>
    /// 窗口最终关闭时释放托盘图标与菜单，避免通知区域残留图标。
    /// </summary>
    protected override void OnClosed(EventArgs e)
    {
        trayIcon.Visible = false;
        trayIcon.Dispose();
        trayImage.Dispose();
        trayMenu.Dispose();
        base.OnClosed(e);
    }

    /// <summary>
    /// 根据配置文件是否存在恢复勾选状态和输入内容，损坏文件保留以供用户取消记忆。
    /// </summary>
    private void RestoreRememberedSettings()
    {
        RememberSettingsBox.IsChecked = settingsStore.Exists;
        if (!settingsStore.Exists)
        {
            return;
        }

        try
        {
            var settings = settingsStore.Load();
            viewModel.Port = settings.Port;
            viewModel.TimeoutSeconds = settings.TimeoutSeconds;
            viewModel.CooldownMinutes = settings.CooldownMinutes;
            viewModel.Upstreams.Clear();
            foreach (var account in settings.Upstreams) viewModel.Upstreams.Add(new UpstreamAccountViewModel(account));
            rememberedSites = settings.Sites;
            viewModel.RenumberAccounts();
            rememberedServiceWasRunning = settings.IsRunning;
            rememberedStartOnLogin = settings.StartOnLogin;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            RememberSettingsBox.IsChecked = settingsStore.Exists;
            viewModel.SettingsError = "读取记忆配置失败：" + exception.Message;
        }
    }

    /// <summary>
    /// 勾选时立即保存配置；取消时先移除登录启动项，再删除配置文件。
    /// </summary>
    private void RememberSettings_Click(object sender, RoutedEventArgs e)
    {
        if (RememberSettingsBox.IsChecked == true)
        {
            SaveRememberedSettings();
            RefreshLoginStartupState();
            return;
        }

        try
        {
            loginStartup.Disable();
            rememberedStartOnLogin = false;
            settingsStore.Delete();
            viewModel.SettingsError = "";
            viewModel.LoginStartupError = "";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            RememberSettingsBox.IsChecked = settingsStore.Exists;
            viewModel.SettingsError = "取消配置记忆失败：" + exception.Message;
        }
        RefreshLoginStartupState();
    }

    /// <summary>
    /// 在端口或上游地址发生变化时更新已启用的记忆配置。
    /// </summary>
    private void Settings_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.Port)
            or nameof(MainViewModel.TimeoutSeconds) or nameof(MainViewModel.CooldownMinutes))
        {
            SaveRememberedSettings();
            if (!service.IsRunning && (e.PropertyName is nameof(MainViewModel.TimeoutSeconds) or nameof(MainViewModel.CooldownMinutes)))
            {
                try
                {
                    service.UpdateHealthSettings(viewModel.BuildProxySettings(requireEnabled: false));
                    viewModel.UpdateUpstreamStatuses(service);
                }
                // 输入过程中可能暂时为空或无效，仍由启动时的完整验证提示错误。
                catch (ArgumentException) { }
            }
        }
    }

    /// <summary>
    /// 清空内存记录，并让消息页和网络页恢复为空白初始状态。
    /// </summary>
    private void ClearRecords_Click(object sender, RoutedEventArgs e)
    {
        viewModel.ClearRecords();
    }

    /// <summary>账号编辑成功后应用一次完整配置快照并保存。</summary>
    private async Task ApplyAccountsAsync()
    {
        viewModel.RenumberAccounts();
        viewModel.ServiceError = "";
        viewModel.IsBusy = true;
        try
        {
            // 停止时允许先填写账号，再调整监听/超时输入；启动会验证全部输入。
            if (service.IsRunning) await service.UpdateAccountsAsync(viewModel.BuildProxySettings(requireEnabled: false));
            else await service.ForgetChangedAccountsAsync(viewModel.Upstreams.Select(row => row.Account));
            viewModel.UpdateUpstreamStatuses(service);
            SaveRememberedSettings();
        }
        catch (ArgumentException exception) { viewModel.ServiceError = exception.Message; }
        finally { viewModel.IsBusy = false; }
    }

    private async void AddUpstream_Click(object sender, RoutedEventArgs e)
    {
        if (!viewModel.CanManageAccounts) return;
        var dialog = new AccountEditorWindow(new UpstreamAccount(), int.TryParse(viewModel.Port, out int port) ? port : 0)
            { Owner = this, Title = "添加上游账号" };
        if (dialog.ShowDialog() != true) return;
        viewModel.Upstreams.Add(new UpstreamAccountViewModel(dialog.Account));
        await ApplyAccountsAsync();
    }

    private void SyncAccounts_Click(object sender, RoutedEventArgs e)
    {
        if (!viewModel.CanManageAccounts) return;
        var dialog = new SitesSyncWindow(
            rememberedSites,
            () => viewModel.Upstreams.Select(row => row.Account).ToArray(),
            AddSyncedAccountAsync,
            RemoveSyncedAccountAsync,
            sites =>
            {
                rememberedSites = sites;
                service.UpdateBillingSites(rememberedSites);
                SaveRememberedSettings();
            })
        { Owner = this };
        dialog.SetBinding(SitesSyncWindow.RememberSettingsEnabledProperty, new System.Windows.Data.Binding("IsChecked")
        {
            Source = RememberSettingsBox,
            Mode = System.Windows.Data.BindingMode.OneWay
        });
        dialog.ShowDialog();
    }

    private async Task<bool> AddSyncedAccountAsync(UpstreamAccount account)
    {
        if (!viewModel.CanManageAccounts) throw new SiteApiException("正在更新账号，请稍后重试。");
        var accounts = viewModel.Upstreams.Select(row => row.Account).ToArray();
        if (SiteAccountMapping.Find(accounts, account.BaseUrl, account.ApiKey) is not null) return true;
        var dialog = new AccountEditorWindow(account, int.TryParse(viewModel.Port, out int port) ? port : 0)
        {
            Owner = OwnedWindows.OfType<Window>().FirstOrDefault(window => window is SitesSyncWindow) ?? this,
            Title = "添加同步账号"
        };
        if (dialog.ShowDialog() != true) return false;
        if (SiteAccountMapping.Find(accounts, dialog.Account.BaseUrl, dialog.Account.ApiKey) is not null) return true;
        // 站点导入账号始终沿用站点代理，避免编辑账号时意外切换出口。
        var importedAccount = dialog.Account with { Proxy = account.Proxy };
        // 运行时先验证完整配置，避免无效连接被加入当前调度列表。
        if (service.IsRunning)
            _ = new ProxySettings(viewModel.Port, viewModel.TimeoutSeconds, viewModel.CooldownMinutes,
                accounts.Append(importedAccount), requireEnabled: false);
        viewModel.Upstreams.Add(new UpstreamAccountViewModel(importedAccount));
        await ApplyAccountsAsync();
        return true;
    }

    private async Task<bool> RemoveSyncedAccountAsync(string accountId)
    {
        if (!viewModel.CanManageAccounts) throw new SiteApiException("正在更新账号，请稍后重试。");
        var row = viewModel.Upstreams.FirstOrDefault(item => item.Account.Id == accountId);
        if (row is null) return false;
        viewModel.Upstreams.Remove(row);
        await ApplyAccountsAsync();
        return true;
    }

    private async void MoveUpstreamUp_Click(object sender, RoutedEventArgs e)
        => await MoveUpstreamAsync(sender, -1);

    private async void MoveUpstreamDown_Click(object sender, RoutedEventArgs e)
        => await MoveUpstreamAsync(sender, 1);

    private async Task MoveUpstreamAsync(object sender, int offset)
    {
        if (!viewModel.CanManageAccounts || sender is not FrameworkElement { DataContext: UpstreamAccountViewModel row }) return;
        int index = viewModel.Upstreams.IndexOf(row);
        int destination = index + offset;
        if (index < 0 || destination < 0 || destination >= viewModel.Upstreams.Count) return;
        viewModel.Upstreams.Move(index, destination);
        await ApplyAccountsAsync();
    }

    private async void DeleteUpstream_Click(object sender, RoutedEventArgs e)
    {
        if (!viewModel.CanManageAccounts || sender is not FrameworkElement { DataContext: UpstreamAccountViewModel row }) return;
        viewModel.Upstreams.Remove(row);
        await ApplyAccountsAsync();
    }

    private async void EditUpstream_Click(object sender, RoutedEventArgs e)
    {
        if (!viewModel.CanManageAccounts || sender is not FrameworkElement { DataContext: UpstreamAccountViewModel row }) return;
        var dialog = new AccountEditorWindow(row.Account, int.TryParse(viewModel.Port, out int port) ? port : 0) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        row.Replace(dialog.Account);
        await ApplyAccountsAsync();
    }

    private async void EnableUpstream_Click(object sender, RoutedEventArgs e)
    {
        if (!viewModel.CanManageAccounts || sender is not FrameworkElement { DataContext: UpstreamAccountViewModel row }) return;
        row.Replace(row.Account with { IsEnabled = !row.IsEnabled });
        await ApplyAccountsAsync();
    }

    private async void RefreshUpstream_Click(object sender, RoutedEventArgs e)
    {
        if (viewModel.IsBusy || sender is not FrameworkElement { DataContext: UpstreamAccountViewModel row }) return;
        try
        {
            var upstream = new UpstreamSettings(row.Account, row.Order, int.TryParse(viewModel.Port, out int port) ? port : 0);
            Task refresh = service.RefreshBillingAsync(upstream);
            viewModel.UpdateUpstreamStatuses(service);
            await refresh;
            viewModel.UpdateUpstreamStatuses(service);
        }
        catch (ArgumentException exception) { viewModel.ServiceError = exception.Message; }
    }

    /// <summary>
    /// 仅在初始化结束且勾选记住时写入配置，并显示写入失败原因。
    /// </summary>
    private bool SaveRememberedSettings()
    {
        if (!settingsReady || RememberSettingsBox.IsChecked != true)
        {
            return false;
        }

        try
        {
            settingsStore.Save(new RememberedSettings
            {
                Port = viewModel.Port,
                TimeoutSeconds = viewModel.TimeoutSeconds,
                CooldownMinutes = viewModel.CooldownMinutes,
                Upstreams = viewModel.Upstreams.Select(row => row.Account).ToList(),
                Sites = rememberedSites.Select(site => site with { }).ToList(),
                IsRunning = viewModel.IsRunning,
                StartOnLogin = rememberedStartOnLogin
            });
            viewModel.SettingsError = "";
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            RememberSettingsBox.IsChecked = settingsStore.Exists;
            viewModel.SettingsError = "保存记忆配置失败：" + exception.Message;
            return false;
        }
    }

    /// <summary>
    /// 验证界面配置并异步启动服务。
    /// </summary>
    private async void Start_Click(object sender, RoutedEventArgs e)
        => await StartServiceAsync();

    /// <summary>
    /// 复用手动和自动启动流程，验证配置并显示服务状态及失败原因。
    /// </summary>
    private async Task StartServiceAsync()
    {
        if (!viewModel.CanStart || closeRequested)
        {
            return;
        }

        viewModel.IsBusy = true;
        viewModel.ServiceError = "";
        viewModel.ServiceStatus = "正在启动";
        try
        {
            var settings = viewModel.BuildProxySettings();
            await service.StartAsync(settings);
            viewModel.IsRunning = true;
            viewModel.ServiceStatus = $"运行中 · 127.0.0.1:{settings.Port}";
        }
        catch (Exception exception)
        {
            viewModel.ServiceStatus = "启动失败";
            viewModel.ServiceError = exception.Message;
        }
        finally
        {
            viewModel.IsRunning = service.IsRunning;
            SaveRememberedSettings();
            viewModel.IsBusy = false;
            if (closeRequested)
            {
                Close();
            }
        }
    }

    /// <summary>
    /// 异步关闭服务，并向界面报告停止结果。
    /// </summary>
    private async void Stop_Click(object sender, RoutedEventArgs e)
    {
        viewModel.IsBusy = true;
        viewModel.ServiceStatus = "正在停止";
        viewModel.ServiceError = "";
        try
        {
            await service.StopAsync();
            viewModel.ServiceStatus = "已停止";
        }
        catch (Exception exception)
        {
            viewModel.ServiceStatus = "停止异常";
            viewModel.ServiceError = exception.Message;
        }
        finally
        {
            viewModel.IsRunning = service.IsRunning;
            SaveRememberedSettings();
            viewModel.IsBusy = false;
            viewModel.Refresh();
            if (closeRequested)
            {
                Close();
            }
        }
    }

    /// <summary>
    /// 在界面线程上合并后台捕获数据的更新。
    /// </summary>
    private void RefreshTimer_Tick(object? sender, EventArgs e) => viewModel.Refresh();

    /// <summary>选择保存位置，导出所有已捕获请求，不受当前网络页选择影响。</summary>
    private async void ExportNetworkRecords_Click(object sender, RoutedEventArgs e)
    {
        if (networkExportTask is { IsCompleted: false } || closeRequested) return;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出所有网络记录",
            Filter = "JSON 文件 (*.json)|*.json",
            DefaultExt = ".json",
            AddExtension = true,
            OverwritePrompt = true,
            FileName = $"Dom2API-network-{DateTime.Now:yyyyMMdd-HHmmss}.json"
        };
        if (dialog.ShowDialog(this) != true || closeRequested) return;
        networkExportTask = ExportNetworkRecordsAsync(dialog.FileName);
        await networkExportTask;
    }

    /// <summary>在后台写入固定快照，报告结果并避免重复导出。</summary>
    private async Task ExportNetworkRecordsAsync(string filePath)
    {
        ExportNetworkRecordsButton.IsEnabled = false;
        NetworkExportStatus.Foreground = System.Windows.Media.Brushes.SlateGray;
        NetworkExportStatus.Text = "正在导出网络记录…";
        try
        {
            var records = store.GetRecords();
            var exportedAt = DateTimeOffset.Now;
            await Task.Run(() => NetworkRecordExporter.ExportAsync(filePath, records, exportedAt));
            long messageCount = records.Sum(record => (long)record.NetworkMessages.Count);
            NetworkExportStatus.Text = $"已导出 {records.Count} 条请求、{messageCount} 条网络消息。\n{filePath}";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException or ArgumentException)
        {
            NetworkExportStatus.Foreground = System.Windows.Media.Brushes.Firebrick;
            NetworkExportStatus.Text = "导出失败：" + exception.Message;
        }
        finally { ExportNetworkRecordsButton.IsEnabled = true; }
    }

    /// <summary>
    /// 每 0.1 秒读取最新响应快照，并刷新仍在等待响应的请求。
    /// </summary>
    private void ElapsedTimer_Tick(object? sender, EventArgs e)
    {
        viewModel.Refresh();
        viewModel.UpdateUpstreamStatuses(service);
        foreach (var request in viewModel.Requests)
        {
            request.RefreshTimings();
        }
    }

    /// <summary>
    /// 普通关闭仅隐藏窗口；托盘退出时等待服务释放后完成关闭。
    /// </summary>
    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (canClose)
        {
            return;
        }

        e.Cancel = true;
        if (!closeRequested)
        {
            Hide();
            return;
        }

        if (closing)
        {
            return;
        }

        if (viewModel.IsBusy)
        {
            closeRequested = true;
            IsEnabled = false;
            return;
        }

        closing = true;
        IsEnabled = false;
        try
        {
            // 退出仅释放资源，保留最后一次服务启停状态供下次启动恢复。
            foreach (var syncWindow in OwnedWindows.OfType<SitesSyncWindow>().ToArray())
                await syncWindow.CloseWithLogoutAsync();
            if (networkExportTask is not null) await networkExportTask;
            await service.DisposeAsync();
        }
        catch (Exception exception)
        {
            viewModel.ServiceError = exception.Message;
        }
        finally
        {
            refreshTimer.Stop();
            elapsedTimer.Stop();
            canClose = true;
            // 排队关闭，确保首次 Closing 回调已返回，避免同步释放时重入关闭流程。
            _ = Dispatcher.InvokeAsync(Close);
        }
    }
}
