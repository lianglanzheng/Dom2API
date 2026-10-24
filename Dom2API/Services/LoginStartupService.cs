using System.IO;
using Microsoft.Win32;

namespace Dom2API.Services;

/// <summary>管理当前用户的登录启动项，只读取 Windows 对启动项的启用状态。</summary>
internal sealed class LoginStartupService
{
    public const string LaunchArgument = "--login-startup";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovalKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ValueName = "Dom2API";
    private readonly RegistryKey userKey;
    private readonly string? executablePath;

    /// <summary>使用当前用户与实际进程路径；允许指定独立注册表根以验证操作。</summary>
    public LoginStartupService(RegistryKey? userKey = null, string? executablePath = null)
    {
        this.userKey = userKey ?? Registry.CurrentUser;
        this.executablePath = executablePath ?? Environment.ProcessPath;
    }

    /// <summary>读取实际注册情况，不自动修复路径或重新启用被 Windows 禁用的项目。</summary>
    public LoginStartupState Read()
    {
        using var runKey = userKey.OpenSubKey(RunKeyPath);
        object? command = runKey?.GetValue(ValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        using var approvalKey = userKey.OpenSubKey(ApprovalKeyPath);
        object? approval = approvalKey?.GetValue(ValueName);
        uint? approvalState = approval is byte[] bytes && bytes.Length >= 12
            ? BitConverter.ToUInt32(bytes, 0) : null;
        return new LoginStartupState(command is not null,
            command is string text && string.Equals(text, $"\"{executablePath}\" {LaunchArgument}", StringComparison.OrdinalIgnoreCase),
            approvalState is 3 or 7,
            approval is not null && approvalState is not (2 or 3 or 6 or 7));
    }

    /// <summary>将带引号的 EXE 完整路径与登录启动参数登记为当前用户登录时运行的命令。</summary>
    public void Enable()
    {
        if (string.IsNullOrWhiteSpace(executablePath) || !Path.IsPathFullyQualified(executablePath)
            || !string.Equals(Path.GetExtension(executablePath), ".exe", StringComparison.OrdinalIgnoreCase)
            || string.Equals(Path.GetFileName(executablePath), "dotnet.exe", StringComparison.OrdinalIgnoreCase)
            || executablePath.Contains('"') || !File.Exists(executablePath))
            throw new IOException("请直接运行 Dom2API.exe 后设置登录自启动。");
        string command = $"\"{executablePath}\" {LaunchArgument}";
        if (command.Length > 260)
            throw new IOException("程序路径过长，请将程序移动到较短路径后设置登录自启动。");
        using var runKey = userKey.CreateSubKey(RunKeyPath, writable: true);
        runKey.SetValue(ValueName, command, RegistryValueKind.String);
    }

    /// <summary>仅删除本程序的 Run 项，保留 Windows 的启用或禁用记录。</summary>
    public void Disable()
    {
        using (var existingKey = userKey.OpenSubKey(RunKeyPath))
        {
            if (existingKey?.GetValue(ValueName) is null) return;
        }
        using var runKey = userKey.OpenSubKey(RunKeyPath, writable: true);
        runKey?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}

/// <summary>登录启动项的登记路径与 Windows 启用状态。</summary>
internal sealed record LoginStartupState(bool IsRegistered, bool IsCurrentExecutable,
    bool IsDisabledByWindows, bool HasUnknownApproval);
