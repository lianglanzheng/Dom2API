using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dom2API.Models;

namespace Dom2API.Services;

/// <summary>下载并校验单文件发布包，再交给独立进程在当前程序退出后替换。</summary>
internal sealed class ApplicationUpdater(HttpClient client)
{
    /// <summary>只清理程序目录中符合命名规则的空更新目录，保留备份及错误现场。</summary>
    internal static void CleanupEmptyUpdateDirectories(string applicationDirectory)
    {
        try
        {
            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(applicationDirectory));
            const string prefix = ".Dom2API-update-";
            foreach (string directory in Directory.EnumerateDirectories(root, prefix + "*", SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileName(directory);
                if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    || !Guid.TryParseExact(name[prefix.Length..], "N", out _)) continue;
                try
                {
                    if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(directory)), root, StringComparison.OrdinalIgnoreCase)
                        || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
                    // 不枚举或删除内部文件；目录非空或正在使用时由系统拒绝删除。
                    Directory.Delete(directory, recursive: false);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
    }

    internal static ReleaseAsset SelectAsset(ReleaseUpdate release, Architecture architecture)
    {
        string suffix = architecture switch
        {
            Architecture.X64 => "x64",
            Architecture.X86 => "x86",
            Architecture.Arm64 => "arm64",
            _ => throw new NotSupportedException("当前 Windows 架构不支持自动更新。")
        };
        string version = release.Version.Trim();
        if (version.StartsWith('v') || version.StartsWith('V')) version = version[1..];
        string name = $"Dom2API-{version}-{suffix}.exe";
        var matches = release.Assets.Where(asset => string.Equals(asset.Name, name, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1) throw new InvalidDataException($"发布页未提供唯一的 {suffix} 更新文件，请前往发布页下载。");
        var asset = matches[0];
        var expectedUrl = new Uri("https://github.com/lianglanzheng/Dom2API/releases/download/"
            + Uri.EscapeDataString(release.Version) + "/" + Uri.EscapeDataString(asset.Name));
        if (asset.DownloadUrl != expectedUrl || asset.Size is <= 0 or > 512L * 1024 * 1024)
            throw new InvalidDataException("更新文件的地址或大小无效。");
        if (asset.Digest is not { } digest || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
            || digest.Length != 71 || !digest[7..].All(Uri.IsHexDigit))
            throw new InvalidDataException("发布页未提供有效的 SHA-256 校验信息，请前往发布页下载。");
        return asset;
    }

    public async Task<PreparedUpdate> DownloadAsync(ReleaseUpdate release, string executablePath, Architecture architecture,
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var asset = SelectAsset(release, architecture);
        string target = Path.GetFullPath(executablePath);
        if (!File.Exists(target) || !string.Equals(Path.GetExtension(target), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("无法确定当前程序路径。");
        // 下载及握手文件放在用户目录，不能在请求 UAC 前写入 Program Files。
        bool requiresElevation = RequiresElevation(target);
        string directory = Path.Combine(Path.GetTempPath(), ".Dom2API-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var prepared = new PreparedUpdate(directory, requiresElevation);
        try
        {
            string originalHash;
            await using (var original = File.OpenRead(target))
                originalHash = Convert.ToHexString(await SHA256.HashDataAsync(original, cancellationToken));

            using var request = new HttpRequestMessage(HttpMethod.Get, asset.DownloadUrl);
            request.Headers.UserAgent.ParseAdd("Dom2API-Updater");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is { } length && length != asset.Size)
                throw new InvalidDataException("下载文件大小与发布信息不符。");
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var output = new FileStream(prepared.PayloadPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                byte[] buffer = new byte[81920];
                long received = 0;
                int lastPercent = -1;
                int count;
                while ((count = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    received += count;
                    if (received > asset.Size) throw new InvalidDataException("下载文件超过预期大小。");
                    hash.AppendData(buffer, 0, count);
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                    int percent = (int)(received * 100 / asset.Size);
                    if (percent != lastPercent) { lastPercent = percent; progress?.Report(percent); }
                }
                if (received != asset.Size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(asset.Digest![7..], StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("下载文件不完整或 SHA-256 校验失败，请重试。");
            }
            ValidateExecutable(prepared.PayloadPath, architecture);
            using var process = Process.GetCurrentProcess();
            var plan = new
            {
                ExecutablePath = target, ProcessId = process.Id,
                ProcessStartTimeUtcTicks = process.StartTime.ToUniversalTime().Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Size = asset.Size, Sha256 = asset.Digest![7..], OriginalSha256 = originalHash,
                UseBroker = true
            };
            await File.WriteAllTextAsync(Path.Combine(directory, "plan.json"), JsonSerializer.Serialize(plan), new UTF8Encoding(true), cancellationToken);
            using var script = typeof(ApplicationUpdater).Assembly.GetManifestResourceStream("Dom2API.Services.ApplyUpdate.ps1")
                ?? throw new InvalidOperationException("缺少更新程序资源。");
            await using (var output = File.Create(Path.Combine(directory, "apply.ps1")))
                await script.CopyToAsync(output, cancellationToken);
            return prepared;
        }
        catch
        {
            prepared.Dispose();
            throw;
        }
    }

    internal static bool RequiresElevation(string target)
    {
        try
        {
            string probe = Path.Combine(Path.GetDirectoryName(target)!, ".Dom2API-write-" + Guid.NewGuid().ToString("N"));
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
            // 目录可写并不代表已有 EXE 可替换；申请 DELETE 权限但不删除文件。
            using var handle = OpenForDelete(target, 0x00010000, 7, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                if (error == 5) return true;
                throw new System.ComponentModel.Win32Exception(error);
            }
            string settings = Path.Combine(Path.GetDirectoryName(target)!, "Dom2API.settings.json");
            if (File.Exists(settings))
            {
                using var settingsHandle = OpenForDelete(settings, 0x00010000, 7, IntPtr.Zero, 3, 0, IntPtr.Zero);
                if (settingsHandle.IsInvalid && Marshal.GetLastWin32Error() == 5) return true;
            }
            return false;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or System.Security.SecurityException) { return true; }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle OpenForDelete(string path, uint access, uint share,
        IntPtr security, uint creation, uint flags, IntPtr template);

    internal static void ValidateExecutable(string path, Architecture architecture)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        if (stream.Length < 64 || reader.ReadUInt16() != 0x5A4D) throw new InvalidDataException("更新文件不是 Windows 程序。");
        stream.Position = 0x3C;
        int offset = reader.ReadInt32();
        if (offset < 64 || offset > stream.Length - 6) throw new InvalidDataException("更新文件的 PE 头无效。");
        stream.Position = offset;
        if (reader.ReadUInt32() != 0x00004550) throw new InvalidDataException("更新文件的 PE 签名无效。");
        ushort machine = reader.ReadUInt16();
        ushort expected = architecture switch { Architecture.X64 => 0x8664, Architecture.X86 => 0x014C, Architecture.Arm64 => 0xAA64, _ => 0 };
        if (machine != expected) throw new InvalidDataException("更新文件架构与当前 Windows 不匹配。");
    }
}

/// <summary>控制后台替换程序的就绪与提交握手；未提交时取消更新。</summary>
internal sealed class PreparedUpdate(string directory, bool requiresElevation = false) : IDisposable
{
    private Process? installer;
    private bool committed;
    public string PayloadPath => Path.Combine(directory, "update.exe");
    public bool RequiresElevation => requiresElevation;

    public void SaveSettings(RememberedSettings settings) => new SettingsStore(directory).Save(settings);

    public async Task StartInstallerAsync(CancellationToken cancellationToken)
    {
        string powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        var start = new ProcessStartInfo(powershell)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            // Windows 会锁定进程的工作目录，不能使用待清理的更新目录。
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(directory))!
        };
        foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File",
            Path.Combine(directory, "apply.ps1"), "-PlanPath", Path.Combine(directory, "plan.json"), "-Broker" }) start.ArgumentList.Add(argument);
        if (requiresElevation) start.ArgumentList.Add("-Elevate");
        installer = Process.Start(start) ?? throw new IOException("无法启动后台更新程序。");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // UAC 可能需要输入管理员凭据，给用户足够的响应时间。
        deadline.CancelAfter(TimeSpan.FromMinutes(3));
        while (!File.Exists(Path.Combine(directory, "ready")))
        {
            if (installer.HasExited)
            {
                string error = Path.Combine(directory, "error.txt");
                throw new IOException(File.Exists(error) ? await File.ReadAllTextAsync(error, cancellationToken) : "后台更新程序启动失败。");
            }
            await Task.Delay(100, deadline.Token);
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    public void Commit()
    {
        if (installer is null || installer.HasExited) throw new IOException("后台更新程序已退出，请重试。");
        File.WriteAllText(Path.Combine(directory, "apply"), "apply");
        committed = true;
    }

    public void Dispose()
    {
        if (!committed)
        {
            try
            {
                if (installer is { HasExited: false }) File.WriteAllText(Path.Combine(directory, "cancel"), "cancel");
                else
                {
                    foreach (string name in new[] { "update.exe", "plan.json", "apply.ps1", "ready", "apply", "cancel", "error.txt", "Dom2API.settings.json", "complete", "launch", "launch-result" })
                        File.Delete(Path.Combine(directory, name));
                    Directory.Delete(directory, recursive: false);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
        installer?.Dispose();
    }
}
