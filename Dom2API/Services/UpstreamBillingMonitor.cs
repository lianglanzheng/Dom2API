using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using Dom2API.Models;

namespace Dom2API.Services;

/// <summary>优先查询 Sub2API 声明，失败时查找 New API 分组倍率；仅对已确认支持的上游周期探测。</summary>
internal sealed class UpstreamBillingMonitor : IAsyncDisposable
{
    private readonly HttpClient client;
    private readonly UpstreamSettings settings;
    private readonly Func<UpstreamSettings, CancellationToken, Task<UpstreamBillingState>>? fallback;
    private readonly Uri endpoint;
    private readonly string apiKey;
    private readonly TimeSpan interval;
    private readonly TimeSpan requestTimeout;
    private readonly CancellationTokenSource shutdown = new();
    private readonly CancellationToken stopToken;
    private readonly SemaphoreSlim refreshLock = new(1, 1);
    private volatile UpstreamBillingState state = new();
    private volatile bool supported;
    private Task? runner;
    private CancellationTokenSource? automaticShutdown;

    /// <summary>创建连接级监视器；HTTP 客户端在应用退出时统一释放。</summary>
    public UpstreamBillingMonitor(HttpClient client, UpstreamSettings settings,
        TimeSpan? interval = null, TimeSpan? requestTimeout = null,
        Func<UpstreamSettings, CancellationToken, Task<UpstreamBillingState>>? fallback = null)
    {
        this.client = client;
        this.settings = settings;
        this.fallback = fallback;
        apiKey = settings.ApiKey;
        endpoint = ProxyService.BuildTarget(settings.UpstreamBaseUri, "/v1/sub2api/billing");
        this.interval = interval ?? TimeSpan.FromMinutes(30);
        this.requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(10);
        stopToken = shutdown.Token;
    }

    /// <summary>获取可跨线程读取的不可变快照。</summary>
    public UpstreamBillingState State => state;

    /// <summary>立即开始首次查询，不阻塞代理启动。</summary>
    public void Start()
    {
        if (runner is not null || stopToken.IsCancellationRequested)
        {
            return;
        }

        automaticShutdown = CancellationTokenSource.CreateLinkedTokenSource(stopToken);
        runner = RunAsync(automaticShutdown.Token);
    }

    /// <summary>停止自动探测及其在途请求，保留声明和手动查询能力。</summary>
    public async Task StopAutomaticAsync()
    {
        if (automaticShutdown is null)
        {
            return;
        }

        await automaticShutdown.CancelAsync().ConfigureAwait(false);
        if (runner is not null)
        {
            await runner.ConfigureAwait(false);
        }

        automaticShutdown.Dispose();
        automaticShutdown = null;
        runner = null;
    }

    /// <summary>执行首次查询，随后每个周期仅查询已确认支持的上游。</summary>
    private async Task RunAsync(CancellationToken automaticToken)
    {
        try
        {
            await RefreshAsync(automaticToken).ConfigureAwait(false);
            using var timer = new PeriodicTimer(interval);
            while (await timer.WaitForNextTickAsync(automaticToken).ConfigureAwait(false))
            {
                if (supported)
                {
                    await RefreshAsync(automaticToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (automaticToken.IsCancellationRequested)
        {
            // 服务停止时结束定时器。
        }
    }

    /// <summary>手动或定时查询，合并并发触发；失败时不保留可能已过时的倍率。</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (stopToken.IsCancellationRequested || !await refreshLock.WaitAsync(0).ConfigureAwait(false))
        {
            return;
        }

        var previous = state;
        try
        {
            if (stopToken.IsCancellationRequested || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            state = previous with { Status = "正在查询…", IsRefreshing = true };
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(stopToken, cancellationToken);
            var result = await QuerySub2ApiAsync(cancellation.Token).ConfigureAwait(false);
            cancellation.Token.ThrowIfCancellationRequested();
            if (result.EffectiveRate is null && fallback is not null)
            {
                state = new() { Status = "Sub2API 查询失败，正在查询 New API 站点账号…", IsRefreshing = true };
                var alternative = await fallback(settings, cancellation.Token).ConfigureAwait(false);
                result = alternative.EffectiveRate is not null ? alternative
                    : alternative with { Status = result.Status + "\n" + alternative.Status };
            }
            cancellation.Token.ThrowIfCancellationRequested();
            if (result.EffectiveRate is not null) supported = true;
            state = result;
        }
        catch (OperationCanceledException) when (stopToken.IsCancellationRequested || cancellationToken.IsCancellationRequested)
        {
            state = previous;
        }
        finally
        {
            refreshLock.Release();
        }
    }

    /// <summary>Sub2API 的超时只结束第一阶段，仍允许尝试 New API。</summary>
    private async Task<UpstreamBillingState> QuerySub2ApiAsync(CancellationToken token)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(requestTimeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            if (apiKey.Length > 0)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            }

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented)
            {
                supported = false;
                return new() { Status = "上游不支持 Sub2API 倍率查询" };
            }

            if (!response.IsSuccessStatusCode)
            {
                return new() { Status = response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    ? $"查询失败：认证或权限不足（HTTP {(int)response.StatusCode}）"
                    : $"查询失败：HTTP {(int)response.StatusCode}" };
            }

            // 限制探测正文大小，避免将错误路由返回的大页面完整加载到内存。
            await response.Content.LoadIntoBufferAsync(64 * 1024, timeout.Token).ConfigureAwait(false);
            using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("object", out var kind) || kind.ValueKind != JsonValueKind.String
                || kind.GetString() != "sub2api.key_billing"
                || !root.TryGetProperty("schema_version", out var version) || version.ValueKind != JsonValueKind.Number
                || !version.TryGetInt32(out int schemaVersion) || schemaVersion != 1
                || !root.TryGetProperty("billing_scope", out var scope) || scope.ValueKind != JsonValueKind.String
                || scope.GetString() != "token")
            {
                supported = false;
                return new() { Status = "上游未返回支持的 Sub2API 倍率声明" };
            }

            decimal group = ReadRate(root, "group_rate_multiplier");
            decimal resolved = ReadRate(root, "resolved_rate_multiplier");
            decimal effective = ReadRate(root, "effective_rate_multiplier");
            decimal expected = root.TryGetProperty("user_rate_multiplier", out var user) && user.ValueKind != JsonValueKind.Null
                ? ReadRate(root, "user_rate_multiplier") : group;
            if (!root.TryGetProperty("peak_rate_enabled", out var peak) || peak.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                || !root.TryGetProperty("observed_at", out var observed) || observed.ValueKind != JsonValueKind.String
                || !observed.TryGetDateTimeOffset(out var observedAt) || observedAt == default)
            {
                throw new JsonException();
            }

            decimal appliedPeak = peak.GetBoolean() ? ReadRate(root, "applied_peak_multiplier") : 1;
            if (!NearlyEqual(resolved, expected) || !NearlyEqual(effective, resolved * appliedPeak))
            {
                throw new JsonException();
            }

            supported = true;
            return new()
            {
                Status = "",
                BaseRate = resolved,
                EffectiveRate = effective,
                ObservedAt = observedAt,
                ReceivedAt = DateTimeOffset.Now
            };
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return new() { Status = "Sub2API 倍率查询超时，可手动刷新重试" };
        }
        catch (Exception exception) when (exception is JsonException or OverflowException)
        {
            return new() { Status = "上游返回了无效的倍率声明" };
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            return new() { Status = "倍率查询失败：连接异常或响应过大，可手动刷新重试" };
        }
    }

    /// <summary>读取必填非负倍率，拒绝字符串、空值和超出数值范围的声明。</summary>
    private static decimal ReadRate(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number
            || !value.TryGetDecimal(out decimal rate) || rate < 0)
        {
            throw new JsonException();
        }

        return rate;
    }

    /// <summary>允许上游浮点乘法产生的微小舍入误差。</summary>
    private static bool NearlyEqual(decimal left, decimal right)
        => Math.Abs(left - right) <= Math.Max(1, Math.Max(left, right)) * 0.000000001m;

    /// <summary>切换连接或退出应用时取消所有请求并释放临时数据。</summary>
    public async ValueTask DisposeAsync()
    {
        await shutdown.CancelAsync().ConfigureAwait(false);
        await StopAutomaticAsync().ConfigureAwait(false);

        await refreshLock.WaitAsync().ConfigureAwait(false);
        state = UpstreamBillingState.Empty;
        refreshLock.Release();
        shutdown.Dispose();
    }
}
