using System.IO;
using System.Security;
using System.Windows;
using Dom2API.Services;

namespace Dom2API;

public partial class MainWindow
{
    private readonly LoginStartupService loginStartup;
    private bool rememberedStartOnLogin;

    /// <summary>刷新登录启动项的实际状态，仅读取，不覆盖用户在 Windows 中的设置。</summary>
    private void RefreshLoginStartupState()
    {
        bool remember = RememberSettingsBox.IsChecked == true;
        StartOnLoginBox.IsEnabled = remember;
        UpdateStartupPathButton.Visibility = Visibility.Collapsed;
        try
        {
            var state = loginStartup.Read();
            StartOnLoginBox.IsChecked = remember && state.IsRegistered;
            if (!remember)
                viewModel.LoginStartupStatus = state.IsRegistered
                    ? "检测到已有登录启动项；勾选记住配置后可管理。" : "先勾选记住连接配置和账号。";
            else if (!state.IsRegistered)
                viewModel.LoginStartupStatus = rememberedStartOnLogin ? "启动项已被移除，重新勾选可登记。" : "";
            else
            {
                var messages = new List<string>();
                if (!state.IsCurrentExecutable)
                {
                    messages.Add("启动项路径或参数已变化，请更新启动项。");
                    UpdateStartupPathButton.Visibility = Visibility.Visible;
                }
                if (state.IsDisabledByWindows)
                    messages.Add("已在 Windows 中禁用，请在系统设置的“应用 → 启动”中启用。");
                else if (state.HasUnknownApproval)
                    messages.Add("请在 Windows 的“应用 → 启动”中确认启用状态。");
                if (messages.Count == 0) messages.Add("已设置为当前用户登录时运行。");
                viewModel.LoginStartupStatus = string.Join(" ", messages);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            StartOnLoginBox.IsChecked = false;
            StartOnLoginBox.IsEnabled = false;
            viewModel.LoginStartupStatus = "无法读取登录启动项：" + exception.Message;
        }
    }

    /// <summary>用户切回窗口时重新读取系统启动状态。</summary>
    private void Window_Activated(object? sender, EventArgs e) => RefreshLoginStartupState();

    /// <summary>用户显式选择登录自启动时同步配置与 Run 项，失败则按实际状态刷新。</summary>
    private void StartOnLogin_Click(object sender, RoutedEventArgs e)
        => SetStartOnLogin(StartOnLoginBox.IsChecked == true);

    /// <summary>用户点击更新时将登录启动项改为当前 EXE 路径及登录启动参数。</summary>
    private void UpdateStartupPath_Click(object sender, RoutedEventArgs e) => SetStartOnLogin(true);

    /// <summary>启用前先保存配置；禁用时先删除 Run 项，避免遗留有效自启动入口。</summary>
    private void SetStartOnLogin(bool enabled)
    {
        if (RememberSettingsBox.IsChecked != true) return;
        viewModel.LoginStartupError = "";
        bool previous = rememberedStartOnLogin;
        if (enabled)
        {
            rememberedStartOnLogin = true;
            if (!SaveRememberedSettings())
            {
                rememberedStartOnLogin = previous;
                RefreshLoginStartupState();
                return;
            }
        }
        try
        {
            if (enabled) loginStartup.Enable();
            else loginStartup.Disable();
            rememberedStartOnLogin = enabled;
            if (!enabled) SaveRememberedSettings();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            if (enabled)
            {
                rememberedStartOnLogin = previous;
                SaveRememberedSettings();
            }
            viewModel.LoginStartupError = "设置登录自启动失败：" + exception.Message;
        }
        RefreshLoginStartupState();
    }
}
