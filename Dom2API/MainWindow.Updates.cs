using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using Dom2API.Services;

namespace Dom2API;

public partial class MainWindow
{
    private readonly CancellationTokenSource updateCheckCancellation = new();
    private bool updateCheckStarted;

    private async Task CleanupUpdateDirectoriesAsync()
    {
        var cancellationToken = updateCheckCancellation.Token;
        try
        {
            // 给上一版更新程序留出退出时间，再回收其工作目录锁释放后的空目录。
            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
            await Task.Run(() => ApplicationUpdater.CleanupEmptyUpdateDirectories(AppContext.BaseDirectory), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private async void InstallUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (!viewModel.CanInstallUpdate || closeRequested) return;
        viewModel.IsUpdating = true;
        viewModel.UpdateInstallStatus = "正在获取最新正式版…";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(updateCheckCancellation.Token);
        timeout.CancelAfter(TimeSpan.FromMinutes(15));
        try
        {
            // 开发环境的 apphost 依赖旁边的程序集，不能安全地按单文件发布包替换。
#pragma warning disable IL3000
            if (!string.IsNullOrEmpty(typeof(App).Assembly.Location))
                throw new InvalidOperationException("自动更新仅支持发布版单文件程序，请前往发布页下载。");
#pragma warning restore IL3000
            string target = Environment.ProcessPath ?? throw new InvalidOperationException("无法获取当前程序路径。");
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(15), MaxResponseContentBufferSize = 1024 * 1024 };
            var release = await new ReleaseUpdateChecker(client).CheckAsync(viewModel.ApplicationVersion, timeout.Token);
            if (release is null)
            {
                viewModel.UpdateInstallStatus = "未发现更新的正式版。";
                return;
            }
            viewModel.AvailableUpdate = release;
            var progress = new Progress<double>(percent =>
            {
                if (!closeRequested && viewModel.IsUpdating) viewModel.UpdateInstallStatus = $"正在下载更新… {percent:0}%";
            });
            using var prepared = await new ApplicationUpdater(client).DownloadAsync(release, target, RuntimeInformation.OSArchitecture, progress, timeout.Token);
            if (closeRequested) return;
            viewModel.UpdateInstallStatus = prepared.RequiresElevation
                ? "下载校验完成，请在 Windows 权限提示中允许更新…" : "下载校验完成，正在准备重启…";
            // 配置也由安装进程写入，避免在受保护目录下被提前保存失败阻断。
            if (RememberSettingsBox.IsChecked == true) prepared.SaveSettings(BuildRememberedSettings());
            await prepared.StartInstallerAsync(timeout.Token);
            if (closeRequested) return;
            prepared.Commit();
            viewModel.UpdateInstallStatus = "正在退出并安装更新…";
            closeRequested = true;
            trayMenu.Enabled = false;
            Close();
        }
        catch (OperationCanceledException)
        {
            if (!closeRequested) viewModel.UpdateInstallStatus = "更新超时，当前程序未替换，请重试。";
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or UnauthorizedAccessException
            or InvalidOperationException or ArgumentException or JsonException or NotSupportedException or Win32Exception or System.Security.SecurityException)
        {
            if (!closeRequested) viewModel.UpdateInstallStatus = "更新失败：" + exception.Message;
        }
        finally { viewModel.IsUpdating = false; }
    }

    /// <summary>启动后仅检查一次；网络错误仅显示在关于板块，不阻塞启动或恢复服务。</summary>
    private async Task CheckForUpdatesAsync()
    {
        var cancellationToken = updateCheckCancellation.Token;
        viewModel.UpdateCheckStatus = "正在检查新版本…";
        using var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15),
            MaxResponseContentBufferSize = 1024 * 1024
        };
        try
        {
            var update = await new ReleaseUpdateChecker(client).CheckAsync(viewModel.ApplicationVersion, cancellationToken);
            if (closeRequested || cancellationToken.IsCancellationRequested) return;
            viewModel.AvailableUpdate = update;
            viewModel.UpdateCheckStatus = "";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException
            or JsonException or ArgumentException or IOException)
        {
            if (!closeRequested && !cancellationToken.IsCancellationRequested)
                viewModel.UpdateCheckStatus = "暂时无法检查新版本，下次启动时将重试。";
        }
    }
}
