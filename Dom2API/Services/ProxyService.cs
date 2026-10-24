using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.ServerSentEvents;
using System.Text.Json;
using Dom2API.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Dom2API.Services;

/// <summary>多上游 HTTP 透传、延迟并发调度及独立尝试记录。</summary>
internal sealed class ProxyService : IAsyncDisposable
{
    private static readonly string[] HopByHopHeaders =
    [
        "Connection", "Keep-Alive", "Proxy-Authenticate", "Proxy-Authorization",
        "TE", "Trailer", "Transfer-Encoding", "Upgrade"
    ];
    private readonly CaptureStore store;
    private readonly ProtocolAnalyzer analyzer = new();
    private readonly NewApiBillingLookup newApiBilling = new();
    private readonly object accountsGate = new();
    private readonly Dictionary<string, AccountRuntime> accounts = new();
    private readonly List<AccountRuntime> retiredAccounts = [];
    private readonly CancellationTokenSource lifetime = new();
    private readonly Task healthLoop;
    private volatile AccountRuntime[] routingAccounts = [];
    private WebApplication? application;
    private OutboundClients? clients;
    private CancellationTokenSource? shutdown;
    private readonly OutboundClients billingClients = new(DecompressionMethods.All);

    public ProxyService(CaptureStore store)
    {
        this.store = store;
        healthLoop = RefreshHealthAsync();
    }

    public bool IsRunning => application is not null;

    public UpstreamBillingState GetBillingState(string id)
    {
        lock (accountsGate) return accounts.GetValueOrDefault(id)?.Billing.State ?? UpstreamBillingState.Empty;
    }

    public UpstreamHealth GetHealth(string id)
    {
        lock (accountsGate) return accounts.GetValueOrDefault(id)?.Health.State ?? new(UpstreamHealthStatus.Normal, 0, 0, 0);
    }

    public Task RefreshBillingAsync(UpstreamSettings settings)
        => GetAccount(settings).Billing.RefreshAsync();

    /// <summary>同步可供倍率回退查询使用的站点账号，包含尚未写入磁盘的连接。</summary>
    public void UpdateBillingSites(IEnumerable<SiteConnection> sites) => newApiBilling.UpdateSites(sites);

    /// <summary>停止时修改有效的健康参数后立即同步账号状态，不必等待下次启动。</summary>
    public void UpdateHealthSettings(ProxySettings settings)
    {
        lock (accountsGate)
            foreach (var account in accounts.Values)
                account.ConfigureHealth(settings.TimeoutThreshold, settings.Cooldown);
    }

    public async Task ForgetChangedAccountsAsync(IEnumerable<UpstreamAccount> configured)
    {
        var inputs = configured.ToDictionary(account => account.Id);
        var removed = new List<AccountRuntime>();
        lock (accountsGate)
        {
            foreach (var pair in accounts.ToArray())
            {
                if (inputs.TryGetValue(pair.Key, out var input)
                    && Uri.TryCreate(input.BaseUrl, UriKind.Absolute, out var uri)
                    && uri == pair.Value.Settings.UpstreamBaseUri && input.ApiKey.Trim() == pair.Value.Settings.ApiKey
                    && Socks5Proxy.Normalize(input.Proxy) == pair.Value.Settings.Proxy) continue;
                accounts.Remove(pair.Key);
                removed.Add(pair.Value);
            }
        }
        foreach (var account in removed) await account.Billing.DisposeAsync().ConfigureAwait(false);
    }

    public async Task UpdateAccountsAsync(ProxySettings settings)
    {
        var configured = settings.Upstreams.Select(account => GetAccount(account, settings)).ToArray();
        AccountRuntime[] removed;
        lock (accountsGate)
        {
            var retained = settings.Upstreams.Select(account => account.Id).ToHashSet();
            foreach (var id in accounts.Keys.Where(id => !retained.Contains(id)).ToArray())
            {
                retiredAccounts.Add(accounts[id]);
                accounts.Remove(id);
            }
            removed = retiredAccounts.ToArray();
            retiredAccounts.Clear();
            routingAccounts = configured;
        }
        foreach (var old in removed) await old.Billing.DisposeAsync().ConfigureAwait(false);
        if (IsRunning)
        {
            foreach (var account in configured)
            {
                if (account.Settings.IsEnabled) account.Billing.Start();
                else await account.Billing.StopAutomaticAsync().ConfigureAwait(false);
            }
        }
    }

    private AccountRuntime GetAccount(UpstreamSettings settings, ProxySettings? proxy = null)
    {
        lock (accountsGate)
        {
            if (accounts.TryGetValue(settings.Id, out var existing)
                && existing.Settings.UpstreamBaseUri == settings.UpstreamBaseUri
                && existing.Settings.ApiKey == settings.ApiKey && existing.Settings.Proxy == settings.Proxy)
            {
                existing.Settings = settings;
                if (proxy is not null) existing.ConfigureHealth(proxy.TimeoutThreshold, proxy.Cooldown);
                return existing;
            }
            if (existing is not null) retiredAccounts.Add(existing);
            var timeout = proxy?.TimeoutThreshold ?? existing?.Timeout ?? TimeSpan.FromSeconds(30);
            var cooldown = proxy?.Cooldown ?? existing?.Cooldown ?? TimeSpan.FromMinutes(10);
            // 只有判定参数一致才复用健康对象，避免新配置配上旧阈值或旧冷却窗口。
            var health = existing is not null && existing.Timeout == timeout && existing.Cooldown == cooldown
                ? existing.Health : null;
            var account = new AccountRuntime(settings, billingClients.Get(settings.Proxy), newApiBilling, timeout, cooldown, health);
            accounts[settings.Id] = account;
            return account;
        }
    }

    // 全应用只有一个健康刷新循环：每个账号每个 tick 最多一次，空闲时也能准时冷却恢复。
    private async Task RefreshHealthAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        try
        {
            while (await timer.WaitForNextTickAsync(lifetime.Token).ConfigureAwait(false))
            {
                AccountRuntime[] snapshot;
                lock (accountsGate) snapshot = accounts.Values.ToArray();
                foreach (var account in snapshot) account.Health.Refresh();
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }

    public async Task StartAsync(ProxySettings settings)
    {
        if (application is not null) throw new InvalidOperationException("服务已经启动。");
        await UpdateAccountsAsync(settings).ConfigureAwait(false);
        var configured = routingAccounts;
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = [], ApplicationName = typeof(ProxyService).Assembly.FullName,
            ContentRootPath = AppContext.BaseDirectory
        });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(IPAddress.Loopback, settings.Port);
            options.Limits.MaxRequestBodySize = null;
            options.AddServerHeader = false;
        });
        builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(5));
        var newClients = new OutboundClients();
        var newShutdown = new CancellationTokenSource();
        var app = builder.Build();
        app.Run(context => ForwardAsync(context, settings.TimeoutThreshold, newClients, newShutdown.Token));
        try
        {
            await app.StartAsync().ConfigureAwait(false);
            clients = newClients;
            shutdown = newShutdown;
            application = app;
            foreach (var account in configured.Where(account => account.Settings.IsEnabled)) account.Billing.Start();
        }
        catch
        {
            await app.DisposeAsync().ConfigureAwait(false);
            newClients.Dispose();
            newShutdown.Dispose();
            throw;
        }
    }

    public async Task StopAsync()
    {
        if (application is not { } app) return;
        try
        {
            await shutdown!.CancelAsync().ConfigureAwait(false);
            AccountRuntime[] snapshot;
            lock (accountsGate) snapshot = accounts.Values.Concat(retiredAccounts).ToArray();
            await Task.WhenAll(snapshot.Select(account => account.Billing.StopAutomaticAsync())).ConfigureAwait(false);
            await app.StopAsync().ConfigureAwait(false);
        }
        finally
        {
            await app.DisposeAsync().ConfigureAwait(false);
            clients?.Dispose();
            shutdown?.Dispose();
            application = null;
            clients = null;
            shutdown = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        await lifetime.CancelAsync().ConfigureAwait(false);
        await healthLoop.ConfigureAwait(false);
        AccountRuntime[] snapshot;
        lock (accountsGate) snapshot = accounts.Values.Concat(retiredAccounts).ToArray();
        foreach (var account in snapshot) await account.Billing.DisposeAsync().ConfigureAwait(false);
        billingClients.Dispose();
        lifetime.Dispose();
    }

    private sealed class AccountRuntime(UpstreamSettings settings, HttpClient billingClient, NewApiBillingLookup newApiBilling, TimeSpan timeout, TimeSpan cooldown,
        UpstreamRuntime? health = null)
    {
        public volatile UpstreamSettings Settings = settings;
        public TimeSpan Timeout { get; private set; } = timeout;
        public TimeSpan Cooldown { get; private set; } = cooldown;
        public UpstreamRuntime Health { get; private set; } = health ?? new(settings, timeout, cooldown);
        public UpstreamBillingMonitor Billing { get; } = new(billingClient, settings, fallback: newApiBilling.QueryAsync);
        public void ConfigureHealth(TimeSpan newTimeout, TimeSpan newCooldown)
        {
            if (Timeout == newTimeout && Cooldown == newCooldown) return;
            Timeout = newTimeout;
            Cooldown = newCooldown;
            Health = new(Settings, newTimeout, newCooldown);
        }
    }

    private sealed class Attempt(AccountRuntime account, CancellationToken token) : IDisposable
    {
        public AccountRuntime Account { get; } = account;
        public CancellationTokenSource Cancellation { get; } = CancellationTokenSource.CreateLinkedTokenSource(token);
        // 非 2xx 响应头到达时返回自身供兜底选择；未就绪便结束时返回 null。
        public TaskCompletionSource<Attempt?> Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Selected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Completion { get; set; } = Task.CompletedTask;
        public volatile bool Superseded;
        public void Dispose() => Cancellation.Dispose();
    }

    private static AccountRuntime? ChooseAccount(IEnumerable<AccountRuntime> candidates, HashSet<string> tried)
        => candidates.Where(account => account.Settings.IsEnabled && !tried.Contains(account.Settings.Id))
            .Select(account => (Account: account, Health: account.Health.Refresh(), Rate: account.Billing.State.EffectiveRate))
            .OrderBy(item => item.Health.Status)
            .ThenBy(item => item.Health.HealthLevel ?? 0)
            .ThenBy(item => item.Rate is null)
            .ThenBy(item => item.Rate)
            .ThenBy(item => item.Account.Settings.Order)
            .Select(item => item.Account).FirstOrDefault();

    /// <summary>每个下游拥有独立的有限尝试池，超时只追加账号，绝不取消尚未胜出的旧连接。</summary>
    private async Task ForwardAsync(HttpContext context, TimeSpan timeout,
        OutboundClients httpClients, CancellationToken stopToken)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, stopToken);
        var token = cancellation.Token;
        var attempts = new List<Attempt>();
        var pending = new List<Attempt>();
        Attempt? errorResponse = null;
        var tried = new HashSet<string>();
        var firstResponse = new TaskCompletionSource<Attempt>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var delays = CancellationTokenSource.CreateLinkedTokenSource(token);
        try
        {
            using var requestCapture = new MemoryStream();
            await context.Request.Body.CopyToAsync(requestCapture, token).ConfigureAwait(false);
            byte[] requestBytes = requestCapture.ToArray();
            string rawTarget = context.Features.Get<IHttpRequestFeature>()?.RawTarget
                ?? context.Request.Path + context.Request.QueryString;
            // 此处只缓存可重放的下游正文，不创建额外的“父请求”记录。
            Task nextHedge = Task.CompletedTask;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                // SSE 在首个有效完整事件处决出胜者，普通响应以首字节就绪；错误响应仅作兜底。
                if (firstResponse.Task.IsCompletedSuccessfully)
                {
                    var winner = firstResponse.Task.Result;
                    foreach (var other in attempts.Where(attempt => attempt != winner))
                    {
                        // 已收到 HTTP 错误的尝试按错误结束，不记为被淘汰的正常关闭。
                        if (other.Ready.Task.IsCompletedSuccessfully && other.Ready.Task.Result is not null)
                        {
                            other.Selected.TrySetResult(false);
                            continue;
                        }
                        other.Superseded = true;
                        other.Cancellation.Cancel();
                    }
                    winner.Selected.TrySetResult(true);
                    await winner.Completion.ConfigureAwait(false);
                    return;
                }
                var failed = pending.FirstOrDefault(attempt => attempt.Ready.Task.IsCompleted);
                if (failed is not null)
                {
                    pending.Remove(failed);
                    if (failed.Ready.Task.Result is { } response)
                    {
                        errorResponse?.Selected.TrySetResult(false);
                        errorResponse = response;
                        if (!routingAccounts.Any(account => account.Settings.IsEnabled && !tried.Contains(account.Settings.Id)))
                            firstResponse.TrySetResult(response);
                    }
                    nextHedge = Task.CompletedTask;
                    continue;
                }

                if (pending.Count == 0 || nextHedge.IsCompleted)
                {
                    var account = ChooseAccount(routingAccounts, tried);
                    if (account is not null)
                    {
                        tried.Add(account.Settings.Id);
                        var attempt = new Attempt(account, token);
                        attempts.Add(attempt);
                        pending.Add(attempt);
                        attempt.Completion = RunAttemptAsync(context, attempt, rawTarget, requestBytes,
                            httpClients.Get(account.Settings.Proxy), stopToken, firstResponse);
                        nextHedge = Task.Delay(timeout, delays.Token);
                    }
                    else
                    {
                        if (errorResponse is not null)
                        {
                            // 没有未尝试账号时，立即转发保留的 HTTP 错误并关闭仍等待的连接。
                            firstResponse.TrySetResult(errorResponse);
                            continue;
                        }
                        if (pending.Count == 0) break;
                        nextHedge = Task.Delay(Timeout.InfiniteTimeSpan, delays.Token);
                    }
                }
                await Task.WhenAny(pending.Select(attempt => (Task)attempt.Ready.Task).Append(nextHedge).Append(firstResponse.Task))
                    .WaitAsync(token).ConfigureAwait(false);
            }
            context.Response.StatusCode = StatusCodes.Status502BadGateway;
            await context.Response.WriteAsJsonAsync(new { error = new { message = "所有可用上游连接均失败。", type = "proxy_error" } }, token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { context.Abort(); }
        catch (Exception exception) when (exception is IOException or BadHttpRequestException)
        {
            if (context.RequestAborted.IsCancellationRequested || context.Response.HasStarted) context.Abort();
            else
            {
                context.Response.StatusCode = exception is BadHttpRequestException bad ? bad.StatusCode : 502;
                await context.Response.CompleteAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            delays.Cancel();
            cancellation.Cancel();
            // 观察全部任务及异常，释放所有连接后才结束请求作用域。
            await Task.WhenAll(attempts.Select(attempt => attempt.Completion)).ConfigureAwait(false);
            foreach (var attempt in attempts) attempt.Dispose();
        }
    }

    private async Task RunAttemptAsync(HttpContext context, Attempt attempt, string rawTarget, byte[] requestBytes,
        HttpClient httpClient, CancellationToken stopToken, TaskCompletionSource<Attempt> firstResponse)
    {
        var account = attempt.Account;
        var settings = account.Settings;
        var token = attempt.Cancellation.Token;
        var clock = Stopwatch.StartNew();
        var record = new CaptureRecord
        {
            Method = context.Request.Method, Path = rawTarget,
            UpstreamId = settings.Id, UpstreamName = $"{settings.Order} · {settings.DisplayName}"
        };
        record.SessionId = record.Id;
        account.Health.Begin(record.Id);
        store.Publish(record);
        using var responseCapture = new MemoryStream();
        bool isEventStream = false;
        bool selected = false;
        string? contentType = null;
        string[] encodings = [];
        byte[] first = new byte[1];
        string stage = "准备上游请求";
        string? failureReason = null;
        try
        {
            string requestBody = CaptureFormatting.BodyToText(requestBytes, context.Request.ContentType,
                SplitHeader(context.Request.Headers.ContentEncoding.ToString()));
            AddNetworkMessage(record, "下游 → 上游", "Request Headers", CaptureFormatting.HeadersToText(
                context.Request.Headers.Select(header => new KeyValuePair<string, IEnumerable<string>>(header.Key, header.Value.ToArray()!))));
            AddNetworkMessage(record, "下游 → 上游", "Request Body", requestBody);
            analyzer.AnalyzeRequest(record, requestBody,
                context.Request.Headers["session-id"].ToString(), context.Request.Headers["X-Session-Id"].ToString(),
                context.Request.Headers["thread-id"].ToString(), context.Request.Headers["X-Thread-Id"].ToString());
            var target = BuildTarget(settings.UpstreamBaseUri, rawTarget);
            record.UpstreamUrl = target.AbsoluteUri;
            using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), target);
            if (requestBytes.Length > 0 || context.Request.ContentLength.HasValue || context.Request.ContentType is not null)
                request.Content = new ByteArrayContent(requestBytes);
            var excluded = GetExcludedHeaders(context.Request.Headers.Connection.ToString());
            excluded.UnionWith(["Host", "Authorization", "Content-Length"]);
            foreach (var header in context.Request.Headers)
                if (!excluded.Contains(header.Key) && !request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()))
                    request.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
            if (settings.ApiKey.Length > 0) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
            store.Publish(record);
            stage = "发送上游请求并等待响应头";
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            record.StatusCode = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
            {
                failureReason = $"上游返回 HTTP {(int)response.StatusCode} {response.ReasonPhrase}".TrimEnd();
                account.Health.ReportFailure(record.Id, failureReason);
            }
            AddNetworkMessage(record, "上游 → 下游", "Response Headers", $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}\n"
                + CaptureFormatting.HeadersToText(response.Headers.Concat(response.Content.Headers)));
            contentType = response.Content.Headers.ContentType?.ToString();
            encodings = response.Content.Headers.ContentEncoding.ToArray();
            isEventStream = string.Equals(response.Content.Headers.ContentType?.MediaType, "text/event-stream", StringComparison.OrdinalIgnoreCase);
            record.IsEventStream = isEventStream;
            bool waitForSse = response.IsSuccessStatusCode && isEventStream
                && !HttpMethods.IsHead(context.Request.Method) && response.StatusCode is not (HttpStatusCode.NoContent or HttpStatusCode.ResetContent);
            store.Publish(record);
            if (!response.IsSuccessStatusCode)
            {
                // 响应头即可触发切换，不等待可能迟迟不来的错误正文。
                attempt.Ready.TrySetResult(attempt);
                selected = await attempt.Selected.Task.WaitAsync(token).ConfigureAwait(false);
                if (!selected) return;
            }
            stage = "等待上游正文首字节";
            await using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            int count = await source.ReadAsync(first, token).ConfigureAwait(false);
            if (count > 0)
            {
                record.FirstByteMilliseconds = clock.Elapsed.TotalMilliseconds;
                account.Health.FirstByte(record.Id, record.FirstByteMilliseconds.Value);
                store.Publish(record);
            }

            // 只有选中的尝试才能提交下游响应头，等待 SSE 时连响应头也不提前发送。
            async Task StartDownstreamAsync()
            {
                selected = await attempt.Selected.Task.WaitAsync(token).ConfigureAwait(false);
                if (!selected) throw new OperationCanceledException(token);
                stage = "向下游发送响应头";
                context.Response.StatusCode = record.StatusCode.Value;
                var excludedHeaders = GetExcludedHeaders(string.Join(",", response.Headers.Connection));
                foreach (var header in response.Headers.Concat(response.Content.Headers))
                    if (!excludedHeaders.Contains(header.Key)) context.Response.Headers[header.Key] = header.Value.ToArray();
                context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
                await WriteDownstreamAsync(() => context.Response.StartAsync(token)).ConfigureAwait(false);
                stage = "读取上游响应并转发到下游";
            }

            if (!waitForSse)
            {
                // 普通响应仍以首字节就绪；HEAD / 204 等无正文响应以正常 EOF 就绪。
                if (response.IsSuccessStatusCode) firstResponse.TrySetResult(attempt);
                await StartDownstreamAsync().ConfigureAwait(false);
            }
            else stage = "等待上游首个完整有效 SSE 事件";
            using var prefixed = new PrefixReadStream(source, first.AsMemory(0, count));
            using var relay = new RelayReadStream(prefixed, context.Response.Body, responseCapture, () => { }, deferForwarding: waitForSse);
            if (isEventStream)
            {
                double lastPublished = clock.Elapsed.TotalMilliseconds;
                Task? pendingPublish = null;
                try
                {
                    using var decoded = CaptureFormatting.DecodeStream(relay, encodings);
                    await using var events = SseParser.Create(decoded).EnumerateAsync(token).GetAsyncEnumerator(token);
                    while (true)
                    {
                        var nextEvent = events.MoveNextAsync().AsTask();
                        // 即使上游暂停发送，也在节流窗口结束时发布最后收到的事件和响应间隔。
                        if (pendingPublish is not null
                            && await Task.WhenAny(nextEvent, pendingPublish).ConfigureAwait(false) == pendingPublish)
                        {
                            store.Publish(record);
                            lastPublished = clock.Elapsed.TotalMilliseconds;
                            pendingPublish = null;
                        }
                        if (!await nextEvent.ConfigureAwait(false)) break;
                        var item = events.Current;
                        AddNetworkMessage(record, "上游 → 下游", "SSE · " + item.EventType, item.Data);
                        if (waitForSse && !selected && item.Data.Trim() == "[DONE]")
                            throw new IOException("上游 SSE 在首个有效事件前已结束（[DONE]）。");
                        if (!IsEffectiveSseEvent(item))
                        {
                            // 心跳可记录与透传，但不决定胜者，也不重置有效响应的等待时间。
                            pendingPublish ??= Task.Delay(TimeSpan.FromMilliseconds(100), token);
                            continue;
                        }
                        if (waitForSse && !selected)
                        {
                            if (IsSseError(item)) throw new IOException("上游首个 SSE 事件报告错误：" + item.Data);
                            firstResponse.TrySetResult(attempt);
                        }
                        bool isFirstResponse = record.FirstResponseMilliseconds is null;
                        record.RecordResponse(clock.Elapsed.TotalMilliseconds);
                        account.Health.Response(record.Id, record.ResponseMilliseconds!.Value, isEventStream: true);
                        if (isFirstResponse)
                        {
                            account.Health.FirstResponse(record.Id, record.FirstResponseMilliseconds!.Value);
                        }
                        // 首响应耗时和首条 SSE 一起发布，避免等待后续事件才显示首条消息。
                        if (isFirstResponse || clock.Elapsed.TotalMilliseconds - lastPublished >= 100)
                        {
                            store.Publish(record);
                            lastPublished = clock.Elapsed.TotalMilliseconds;
                            pendingPublish = null;
                        }
                        else pendingPublish ??= Task.Delay(TimeSpan.FromMilliseconds(
                            Math.Max(0, 100 - (clock.Elapsed.TotalMilliseconds - lastPublished))), token);
                        if (waitForSse && !selected)
                        {
                            await StartDownstreamAsync().ConfigureAwait(false);
                            await relay.StartForwardingAsync(token).ConfigureAwait(false);
                        }
                    }
                    if (waitForSse && !selected)
                        throw new IOException("上游 SSE 已结束，但未收到完整有效事件。");
                }
                catch (Exception exception) when (exception is InvalidDataException or NotSupportedException)
                {
                    record.AnalysisNote = "SSE 解析未完成：" + exception.Message;
                    if (waitForSse && !selected) throw new IOException("无法读取首个有效 SSE 事件。", exception);
                    await relay.CopyToAsync(Stream.Null, token).ConfigureAwait(false);
                }
            }
            else
            {
                if (count > 0 && record.FirstResponseMilliseconds is null)
                {
                    record.RecordResponse(clock.Elapsed.TotalMilliseconds);
                    account.Health.Response(record.Id, record.ResponseMilliseconds!.Value, isEventStream: false);
                    account.Health.FirstResponse(record.Id, record.FirstResponseMilliseconds!.Value);
                    store.Publish(record);
                }
                await relay.CopyToAsync(Stream.Null, token).ConfigureAwait(false);
            }
            stage = "完成下游响应";
            await WriteDownstreamAsync(context.Response.CompleteAsync).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or HttpRequestException or IOException
            or InvalidOperationException or ArgumentException)
        {
            bool downstreamClosed = context.RequestAborted.IsCancellationRequested || exception is DownstreamWriteException;
            if (downstreamClosed || stopToken.IsCancellationRequested || attempt.Superseded)
                record.CloseReason = stopToken.IsCancellationRequested ? "服务停止，连接已关闭。"
                    : downstreamClosed ? "下游连接已终止。" : "其他上游已获得响应，关闭此尝试。";
            else
            {
                failureReason = $"上游传输失败：{exception.GetBaseException().Message}\n发生阶段：{stage}";
                record.Error = $"上游传输失败。\n发生阶段：{stage}\n异常详情：\n{exception}";
            }
            if (selected) context.Abort();
        }
        finally
        {
            clock.Stop();
            record.DurationMilliseconds = clock.Elapsed.TotalMilliseconds;
            account.Health.End(record.Id, record.IsFailure, failureReason);
            if (record.FirstByteMilliseconds is not null && responseCapture.Length == 0)
                responseCapture.WriteByte(first[0]);
            string body = CaptureFormatting.BodyToText(responseCapture.ToArray(), contentType, encodings);
            if (body.Length > 0) AddNetworkMessage(record, "上游 → 下游", isEventStream ? "Full Event Stream" : "Response Body", body);
            if (record.Error.Length > 0) AddNetworkMessage(record, "本地", "Transport Error", record.Error);
            if (record.IsClosed) AddNetworkMessage(record, "本地", "Connection Closed", record.CloseReason);
            bool transferFailed = record.IsFailure;
            try
            {
                await analyzer.AnalyzeResponseAsync(record, body, isEventStream).ConfigureAwait(false);
                if (!transferFailed && record.IsFailure) account.Health.ReportFailure(record.Id, record.Error);
            }
            catch (Exception exception)
            {
                record.AnalysisNote = "响应结构化解析未完成：" + exception.Message;
            }
            finally
            {
                record.IsCompleted = true;
                store.Publish(record);
                attempt.Ready.TrySetResult(null);
            }
        }
    }

    /// <summary>忽略空数据、终止标记和显式心跳事件；其他完整数据事件可参与响应计时。</summary>
    private static bool IsEffectiveSseEvent(SseItem<string> item)
        => !string.IsNullOrWhiteSpace(item.Data) && item.Data.Trim() != "[DONE]"
            && !string.Equals(item.EventType, "ping", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(item.EventType, "heartbeat", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(item.EventType, "keepalive", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(item.EventType, "keep-alive", StringComparison.OrdinalIgnoreCase);

    /// <summary>首事件中的明确协议错误不能抢占成功响应；不要求通用 SSE 内容必须为 JSON。</summary>
    private static bool IsSseError(SseItem<string> item)
    {
        if (item.EventType is "error" or "response.failed") return true;
        try
        {
            using var document = JsonDocument.Parse(item.Data);
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && ((root.TryGetProperty("error", out var error) && error.ValueKind is not (JsonValueKind.Null or JsonValueKind.False))
                    || (root.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String
                        && type.GetString() is "error" or "response.failed"));
        }
        catch (JsonException) { return false; }
    }

    private static async Task WriteDownstreamAsync(Func<Task> write)
    {
        try { await write().ConfigureAwait(false); }
        catch (Exception exception) when (exception is IOException or OperationCanceledException)
        { throw new DownstreamWriteException(exception); }
    }

    /// <summary>
    /// 合并 API 基址与下游路径，避免重复添加基址中的版本前缀。
    /// </summary>
    internal static Uri BuildTarget(Uri baseUri, string rawTarget)
    {
        string prefix = baseUri.AbsolutePath.TrimEnd('/');
        string suffix = rawTarget.StartsWith('/') ? rawTarget : "/" + rawTarget;
        if (prefix.Length > 0 && (suffix.StartsWith(prefix + "/", StringComparison.Ordinal)
            || suffix == prefix || suffix.StartsWith(prefix + "?", StringComparison.Ordinal)))
        {
            suffix = suffix[prefix.Length..];
        }
        else if (prefix.EndsWith("/v1", StringComparison.Ordinal) && suffix.StartsWith("/v1/", StringComparison.Ordinal))
        {
            suffix = suffix[3..];
        }

        return new Uri(baseUri.GetLeftPart(UriPartial.Authority) + prefix + suffix);
    }

    /// <summary>
    /// 获取固定及 Connection 指定的逐跳请求头集合。
    /// </summary>
    private static HashSet<string> GetExcludedHeaders(string connection)
        => new(HopByHopHeaders.Concat(SplitHeader(connection)), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 按 HTTP 逗号列表规则拆分简单标记请求头。
    /// </summary>
    private static string[] SplitHeader(string value)
        => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// 在请求流末尾追加一条带序号的网络消息。
    /// </summary>
    private static void AddNetworkMessage(CaptureRecord record, string direction, string kind, string content)
    {
        record.NetworkMessages.Add(new NetworkMessage
        {
            Sequence = record.NetworkMessages.Count + 1,
            Direction = direction,
            Kind = kind,
            Content = content
        });
    }
}
