using System.IO;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace Dom2API.Services;

/// <summary>在当前用户的 Windows 会话内按 EXE 完整路径限制实例，并通知已有实例显示窗口。</summary>
internal sealed class SingleInstanceService : IDisposable
{
    private readonly EventWaitHandle activationEvent;
    private readonly Mutex mutex;
    private RegisteredWaitHandle? activationWait;
    private bool disposed;

    /// <summary>当前进程是否持有该路径的实例锁。</summary>
    public bool IsFirstInstance { get; }

    /// <summary>以忽略大小写的绝对路径生成标识，实例锁须在创建它的线程释放。</summary>
    public SingleInstanceService(string executablePath)
    {
        string path = Path.GetFullPath(executablePath).ToUpperInvariant();
        string pathHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path)));
        using var identity = WindowsIdentity.GetCurrent();
        string name = $@"Local\Dom2API-{identity.User!.Value}-{pathHash}";
        // 先创建事件，再争用锁，确保首个进程初始化窗口期间的唤起请求不会丢失。
        activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, name + "-Activate");
        try
        {
            mutex = new Mutex(false, name + "-Instance");
            try
            {
                IsFirstInstance = mutex.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                // 原进程异常退出后，当前线程已取得锁。
                IsFirstInstance = true;
            }
        }
        catch
        {
            activationEvent.Dispose();
            throw;
        }
    }

    /// <summary>在后台等待唤起信号，回调负责将窗口操作调度到 UI 线程。</summary>
    public void ListenForActivation(Action activate)
    {
        if (!IsFirstInstance || activationWait is not null)
            throw new InvalidOperationException("仅首个实例可以监听窗口唤起请求。");
        ObjectDisposedException.ThrowIf(disposed, this);
        activationWait = ThreadPool.RegisterWaitForSingleObject(activationEvent,
            (_, _) => activate(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    /// <summary>通知同路径的已有实例显示窗口，初始化期间的请求会保留到监听开始。</summary>
    public void RequestActivation() => activationEvent.Set();

    /// <summary>停止监听并释放实例锁；由应用的 UI 线程在退出时调用。</summary>
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        activationWait?.Unregister(null);
        if (IsFirstInstance) mutex.ReleaseMutex();
        mutex.Dispose();
        activationEvent.Dispose();
    }
}
