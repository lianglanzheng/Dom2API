using System.Collections.Concurrent;
using Dom2API.Models;

namespace Dom2API.Services;

/// <summary>
/// 管理供界面读取的请求快照。
/// </summary>
internal sealed class CaptureStore
{
    private readonly ConcurrentDictionary<string, CaptureRecord> records = new();
    private long version;

    /// <summary>获取记录集合的更新版本。</summary>
    public long Version => Interlocked.Read(ref version);

    /// <summary>
    /// 发布一份集合独立的捕获快照。
    /// </summary>
    public void Publish(CaptureRecord record)
    {
        records[record.Id] = record.Snapshot();
        Interlocked.Increment(ref version);
    }

    /// <summary>
    /// 按开始时间返回全部请求的当前快照。
    /// </summary>
    public IReadOnlyList<CaptureRecord> GetRecords()
        => records.Values.OrderByDescending(record => record.StartedAt).ToArray();

    /// <summary>
    /// 清空当前进程内的所有请求、消息和统计记录。
    /// </summary>
    public void Clear()
    {
        records.Clear();
        Interlocked.Increment(ref version);
    }
}
