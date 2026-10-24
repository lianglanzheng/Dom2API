using Dom2API.Models;

namespace Dom2API.Services;

/// <summary>按最近结束的连接和当前等待判定账号健康度；所有索引在同一把锁下更新。</summary>
internal sealed class UpstreamRuntime
{
    private readonly object gate = new();
    private readonly TimeProvider time;
    private readonly TimeSpan timeout;
    private readonly TimeSpan cooldown;
    private readonly Dictionary<string, (long Started, double? FirstByte, double? FirstResponse)> active = new();
    private readonly SortedSet<(long Started, string Id)> waiting = new();
    private readonly SortedSet<(double Milliseconds, string Id)> received = new();
    private readonly SortedSet<(long Started, string Id)> responseWaiting = new();
    private readonly Dictionary<string, string> failedActive = new();
    private readonly Dictionary<string, (long Since, double? Milliseconds, bool Waiting)> responseTimings = new();
    // 单调队列仅保留可能成为窗口最大值的已结束样本，摊销 O(1) 入队/过期。
    private readonly LinkedList<(long Ended, double Milliseconds)> maxima = new();
    private CompletedConnection? latestCompleted;
    private volatile UpstreamHealth state = new(UpstreamHealthStatus.Normal, 0, 0, 0);

    public UpstreamRuntime(UpstreamSettings settings, TimeSpan timeout, TimeSpan cooldown, TimeProvider? time = null)
    {
        Settings = settings;
        this.timeout = timeout;
        this.cooldown = cooldown;
        this.time = time ?? TimeProvider.System;
    }

    public UpstreamSettings Settings { get; }
    public UpstreamHealth State => state;

    public void Begin(string id)
    {
        lock (gate)
        {
            long now = time.GetTimestamp();
            active.Add(id, (now, null, null));
            responseTimings.Add(id, (now, null, true));
            waiting.Add((now, id));
            responseWaiting.Add((now, id));
            Recompute(now);
        }
    }

    public void FirstByte(string id, double milliseconds)
    {
        lock (gate)
        {
            var entry = active[id];
            waiting.Remove((entry.Started, id));
            active[id] = (entry.Started, milliseconds, entry.FirstResponse);
            received.Add((milliseconds, id));
            Recompute(time.GetTimestamp());
        }
    }

    public void FirstResponse(string id, double milliseconds)
    {
        lock (gate)
        {
            var entry = active[id];
            responseWaiting.Remove((entry.Started, id));
            active[id] = (entry.Started, entry.FirstByte, milliseconds);
            Recompute(time.GetTimestamp());
        }
    }

    public void Response(string id, double milliseconds, bool isEventStream)
    {
        lock (gate)
        {
            if (!active.ContainsKey(id)) return;
            long now = time.GetTimestamp();
            responseTimings[id] = (now, milliseconds, isEventStream);
            Recompute(now);
        }
    }

    public void End(string id, bool failed, string? errorReason = null)
    {
        lock (gate)
        {
            if (!active.Remove(id, out var entry)) return;
            bool reportedFailure = failedActive.Remove(id, out var reportedReason);
            long now = time.GetTimestamp();
            double milliseconds = entry.FirstByte ?? time.GetElapsedTime(entry.Started, now).TotalMilliseconds;
            waiting.Remove((entry.Started, id));
            if (entry.FirstByte is { } firstByte) received.Remove((firstByte, id));
            while (maxima.Last is { } last && last.Value.Milliseconds <= milliseconds) maxima.RemoveLast();
            maxima.AddLast((now, milliseconds));
            double responseMilliseconds = entry.FirstResponse ?? time.GetElapsedTime(entry.Started, now).TotalMilliseconds;
            responseWaiting.Remove((entry.Started, id));
            responseTimings.Remove(id, out var responseTiming);
            double lastResponseInterval = responseTiming.Milliseconds ?? time.GetElapsedTime(entry.Started, now).TotalMilliseconds;
            latestCompleted = new(id, now, responseMilliseconds, failed || reportedFailure,
                failed || reportedFailure ? NormalizeErrorReason(errorReason, reportedReason) : null, lastResponseInterval);
            Recompute(now);
        }
    }

    public void ReportFailure(string id, string errorReason)
    {
        lock (gate)
        {
            long now = time.GetTimestamp();
            string reason = NormalizeErrorReason(errorReason);
            if (active.ContainsKey(id)) failedActive[id] = reason;
            // 协议分析可能晚于后续连接结束；只修正对应样本，不覆盖新结果或延长周期。
            else if (latestCompleted is { } completed && completed.Id == id)
                latestCompleted = completed with { Failed = true, ErrorReason = reason };
            Recompute(now);
        }
    }

    public UpstreamHealth Refresh()
    {
        lock (gate)
        {
            Recompute(time.GetTimestamp());
            return state;
        }
    }

    private void Recompute(long now)
    {
        while (maxima.First is { } first && time.GetElapsedTime(first.Value.Ended, now) >= cooldown)
            maxima.RemoveFirst();
        if (latestCompleted is { } completed && time.GetElapsedTime(completed.Ended, now) >= cooldown)
            latestCompleted = null;
        double activeMax = Math.Max(received.Count > 0 ? received.Max.Milliseconds : 0,
            waiting.Count > 0 ? time.GetElapsedTime(waiting.Min.Started, now).TotalMilliseconds : 0);
        double maximum = Math.Max(activeMax, maxima.First?.Value.Milliseconds ?? 0);
        double activeResponseMax = responseWaiting.Count > 0
            ? time.GetElapsedTime(responseWaiting.Min.Started, now).TotalMilliseconds : 0;
        double responseMaximum = Math.Max(activeResponseMax, latestCompleted?.FirstResponseMilliseconds ?? 0);
        double responseMilliseconds = responseTimings.Count > 0
            ? responseTimings.Values.Max(response => response.Waiting
                ? time.GetElapsedTime(response.Since, now).TotalMilliseconds : response.Milliseconds ?? 0)
            : latestCompleted?.ResponseMilliseconds ?? 0;
        var status = latestCompleted is { Failed: true } || failedActive.Count > 0 ? UpstreamHealthStatus.Error
            : responseMilliseconds > timeout.TotalMilliseconds ? UpstreamHealthStatus.Degraded : UpstreamHealthStatus.Normal;
        state = new(status, active.Count, maximum, responseMaximum)
        {
            ResponseMilliseconds = responseMilliseconds,
            HealthLevel = status == UpstreamHealthStatus.Degraded && timeout > TimeSpan.Zero
                ? Math.Floor(responseMilliseconds / timeout.TotalMilliseconds) : null,
            ErrorReason = status == UpstreamHealthStatus.Error
                ? string.Join("\n\n", failedActive.Values
                    .Concat(latestCompleted is { Failed: true, ErrorReason: { } reason } ? [reason] : [])
                    .Distinct(StringComparer.Ordinal)) : null
        };
    }

    private static string NormalizeErrorReason(string? reason, string? fallback = null)
        => !string.IsNullOrWhiteSpace(reason) ? reason
            : !string.IsNullOrWhiteSpace(fallback) ? fallback : "上游请求失败，未提供错误详情。";

    private sealed record CompletedConnection(string Id, long Ended, double FirstResponseMilliseconds, bool Failed,
        string? ErrorReason, double ResponseMilliseconds);
}

internal enum UpstreamHealthStatus { Normal, Degraded, Error }

internal sealed record UpstreamHealth(UpstreamHealthStatus Status, int ActiveConnections, double MaxFirstByteMilliseconds,
    double MaxFirstResponseMilliseconds)
{
    public bool IsUnhealthy => Status != UpstreamHealthStatus.Normal;
    public double? HealthLevel { get; init; }
    public string? ErrorReason { get; init; }
    public double ResponseMilliseconds { get; init; }
}
