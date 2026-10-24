using System.Windows;

namespace Dom2API;

/// <summary>
/// 管理 Dom2API 桌面应用程序的启动与资源。
/// </summary>
public partial class App : Application
{
    private Services.SingleInstanceService? singleInstance;

    /// <summary>
    /// 根据本次启动参数决定窗口是否显示，独立恢复已记住的服务状态。
    /// </summary>
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        bool launchedAtLogin = e.Args.Contains(Services.LoginStartupService.LaunchArgument,
            StringComparer.OrdinalIgnoreCase);
        singleInstance = new Services.SingleInstanceService(Environment.ProcessPath
            ?? throw new InvalidOperationException("无法获取程序的完整路径。"));
        if (!singleInstance.IsFirstInstance)
        {
            if (!launchedAtLogin) singleInstance.RequestActivation();
            Shutdown();
            return;
        }

        var window = new MainWindow();
        MainWindow = window;
        singleInstance.ListenForActivation(() => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!Dispatcher.HasShutdownStarted) window.ShowFromTray();
        })));
        await window.InitializeStartupAsync(launchedAtLogin);
    }

    /// <summary>退出时在 UI 线程释放实例锁，允许再次打开同路径的程序。</summary>
    protected override void OnExit(ExitEventArgs e)
    {
        singleInstance?.Dispose();
        base.OnExit(e);
    }
}

