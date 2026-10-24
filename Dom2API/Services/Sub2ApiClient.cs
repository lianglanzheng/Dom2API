using System.IO;
using System.Net.Http;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dom2API.Models;

namespace Dom2API.Services;

/// <summary>同步窗口内缓存站点会话；关闭时退出登录，令牌不写入磁盘。</summary>
internal sealed class Sub2ApiClient : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly OutboundClients httpClients;
    private readonly SemaphoreSlim operationGate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly CancellationToken lifetimeToken;
    private readonly Dictionary<string, Session> sessions = [];
    private readonly object disposeGate = new();
    private Task? disposeTask;

    private sealed record Session(SiteConnection Site, string AccessToken, string? RefreshToken);

    public Sub2ApiClient(HttpMessageHandler? handler = null)
    {
        lifetimeToken = lifetime.Token;
        httpClients = new(handler: handler);
    }

    public Task<Sub2ApiSiteData> LoadAsync(SiteConnection site, CancellationToken token = default)
        => WithSessionAsync(site, (session, cancellation) => LoadCoreAsync(session, cancellation), token);

    private async Task<Sub2ApiSiteData> LoadCoreAsync(Session session, CancellationToken token)
    {
        Uri baseUri = SiteAccountMapping.ValidateBaseUrl(session.Site.BaseUrl);
        string accessToken = session.AccessToken;
        using var request = CreateRequest(HttpMethod.Get, baseUri, "/auth/me", accessToken);
        var user = await SendAsync(session.Site, request, token).ConfigureAwait(false);
        if (user.ValueKind != JsonValueKind.Object || !user.TryGetProperty("balance", out var balanceValue) || balanceValue.ValueKind != JsonValueKind.Number
            || !balanceValue.TryGetDecimal(out decimal balance))
            throw new SiteApiException("站点返回的余额格式无效。");

        var keys = new List<Sub2ApiKey>();
        var ids = new HashSet<long>();
        // 服务端可能限制 page_size，必须遵循实际返回的分页信息。
        for (int page = 1; ; page++)
        {
            using var keysRequest = CreateRequest(HttpMethod.Get, baseUri,
                $"/keys?page={page}&page_size=100&sort_by=id&sort_order=asc", accessToken);
            var data = await SendAsync(session.Site, keysRequest, token).ConfigureAwait(false);
            var items = data.ValueKind == JsonValueKind.Array ? data
                : data.TryGetProperty("items", out var value) ? value
                : throw new SiteApiException("站点返回的密钥分页格式无效。");
            var batch = Deserialize<List<Sub2ApiKey>>(items);
            foreach (var key in batch)
            {
                if (key is null || key.Id <= 0 || key.Name is null || !ids.Add(key.Id))
                    throw new SiteApiException("密钥列表分页不一致，请刷新重试。");
                keys.Add(key);
            }
            if (data.ValueKind == JsonValueKind.Array) break;
            int? total = ReadInt(data, "total"), pages = ReadInt(data, "pages");
            if (total.HasValue && keys.Count >= total.Value) break;
            if (pages.HasValue && page >= pages.Value)
            {
                if (total > keys.Count) throw new SiteApiException("站点返回的密钥列表不完整，请刷新重试。");
                break;
            }
            if (batch.Count == 0)
            {
                if (total > keys.Count || pages > page) throw new SiteApiException("站点返回的密钥列表不完整，请刷新重试。");
                break;
            }
            if (total is null && pages is null && batch.Count < (ReadInt(data, "page_size") ?? 100)) break;
            if (page >= 10000) throw new SiteApiException("站点密钥分页过多，请检查站点响应。");
        }

        using var groupsRequest = CreateRequest(HttpMethod.Get, baseUri, "/groups/available", accessToken);
        var groups = Deserialize<List<Sub2ApiGroup>>(await SendAsync(session.Site, groupsRequest, token).ConfigureAwait(false));
        if (groups.Any(group => group is null || group.Id <= 0 || string.IsNullOrWhiteSpace(group.Name)))
            throw new SiteApiException("站点返回的分组数据无效。");
        return new(balance, keys, groups);
    }

    public Task<Sub2ApiKey> CreateKeyAsync(SiteConnection site, Sub2ApiGroup group, string name,
        CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new SiteApiException("API 密钥名称不能为空。");
        return WithSessionAsync(site, (session, cancellation) => CreateKeyCoreAsync(session, group, name.Trim(), cancellation), token);
    }

    private async Task<Sub2ApiKey> CreateKeyCoreAsync(Session session, Sub2ApiGroup group, string name, CancellationToken token)
    {
        using var request = CreateRequest(HttpMethod.Post, SiteAccountMapping.ValidateBaseUrl(session.Site.BaseUrl), "/keys", session.AccessToken);
        request.Content = JsonContent.Create(new { name, group_id = group.Id });
        // 写请求超时或结果不明时不自动重试，用户应先刷新，以免重复创建。
        var key = Deserialize<Sub2ApiKey>(await SendAsync(session.Site, request, token).ConfigureAwait(false));
        if (key.Id <= 0 || key.Name is null || key.GroupId != group.Id)
            throw new SiteApiException("站点返回的新密钥数据无效，请先刷新密钥列表。");
        return key;
    }

    /// <summary>使用官方用户接口删除指定密钥，复用窗口登录会话。</summary>
    public Task DeleteKeyAsync(SiteConnection site, long keyId, CancellationToken token = default)
    {
        if (keyId <= 0) throw new SiteApiException("API 密钥标识无效。");
        return WithSessionAsync(site, async (session, cancellation) =>
        {
            using var request = CreateRequest(HttpMethod.Delete, SiteAccountMapping.ValidateBaseUrl(session.Site.BaseUrl),
                $"/keys/{keyId}", session.AccessToken);
            return await SendAsync(session.Site, request, cancellation).ConfigureAwait(false);
        }, token);
    }

    private async Task<Session> LoginAsync(SiteConnection site, CancellationToken token)
    {
        Uri baseUri = SiteAccountMapping.ValidateBaseUrl(site.BaseUrl);
        using var request = CreateRequest(HttpMethod.Post, baseUri, "/auth/login", null);
        request.Content = JsonContent.Create(new { email = site.Username, password = site.Password });
        var data = await SendAsync(site, request, token).ConfigureAwait(false);
        if (data.ValueKind != JsonValueKind.Object) throw new SiteApiException("站点登录响应格式无效。");
        if (data.TryGetProperty("requires_2fa", out var twoFactor) && twoFactor.ValueKind == JsonValueKind.True)
            throw new SiteApiException("此账号需要双因素验证，当前同步暂不支持；可在站点获取密钥后手动添加账号。");
        string? accessToken = ReadString(data, "access_token");
        if (string.IsNullOrWhiteSpace(accessToken)) throw new SiteApiException("站点未返回登录令牌。");
        return new(site with { }, accessToken, ReadString(data, "refresh_token"));
    }

    /// <summary>串行维护窗口会话，仅在缓存认证失败时允许一次重新登录。</summary>
    private async Task<T> WithSessionAsync<T>(SiteConnection site,
        Func<Session, CancellationToken, Task<T>> action, CancellationToken token)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, lifetimeToken);
        token = cancellation.Token;
        await operationGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            token.ThrowIfCancellationRequested();
            sessions.TryGetValue(site.Id, out var session);
            if (session is not null && (SiteAccountMapping.ValidateBaseUrl(session.Site.BaseUrl) != SiteAccountMapping.ValidateBaseUrl(site.BaseUrl)
                || session.Site.Username != site.Username || session.Site.Password != site.Password || session.Site.Proxy != site.Proxy))
            {
                sessions.Remove(site.Id);
                await LogoutCoreAsync(session).ConfigureAwait(false);
                session = null;
            }
            bool usedCachedSession = session is not null;
            if (session is null)
                sessions[site.Id] = session = await LoginAsync(site, token).ConfigureAwait(false);
            try { return await action(session, token).ConfigureAwait(false); }
            catch (SiteApiException exception) when (exception.IsAuthenticationFailure && usedCachedSession)
            {
                // 只有明确的 401 才重新登录；网络异常、权限不足或写入结果不明均不重放。
                sessions.Remove(site.Id);
                await LogoutCoreAsync(session).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                sessions[site.Id] = session = await LoginAsync(site, token).ConfigureAwait(false);
                return await action(session, token).ConfigureAwait(false);
            }
        }
        finally { operationGate.Release(); }
    }

    /// <summary>移除站点时退出其会话并清除缓存。</summary>
    public async Task LogoutSiteAsync(string siteId)
    {
        await operationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (sessions.Remove(siteId, out var session)) await LogoutCoreAsync(session).ConfigureAwait(false);
        }
        finally { operationGate.Release(); }
    }

    /// <summary>尽力撤销当前会话的刷新令牌，退出失败不阻止本地清理。</summary>
    private async Task LogoutCoreAsync(Session session)
    {
        // 退出使用独立超时，不继承已取消的窗口请求令牌。
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            using var request = CreateRequest(HttpMethod.Post, SiteAccountMapping.ValidateBaseUrl(session.Site.BaseUrl), "/auth/logout", session.AccessToken);
            request.Content = JsonContent.Create(new { refresh_token = session.RefreshToken });
            await SendAsync(session.Site, request, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is SiteApiException or OperationCanceledException)
        {
            // 退出失败也必须释放本地会话，不能让断网站点阻止窗口关闭。
        }
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, Uri baseUri, string path, string? token)
    {
        var request = new HttpRequestMessage(method, baseUri.AbsoluteUri.TrimEnd('/') + "/api/v1" + path);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("Dom2API/1.0");
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private async Task<JsonElement> SendAsync(SiteConnection site, HttpRequestMessage request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            using var response = await httpClients.Get(site.Proxy).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                throw new SiteApiException("站点认证失败，请检查账号密码或重试。") { IsAuthenticationFailure = true };
            await response.Content.LoadIntoBufferAsync(4 * 1024 * 1024, timeout.Token).ConfigureAwait(false);
            using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new SiteApiException("站点未返回 Sub2API 数据，请检查站点基址。");
            if (!response.IsSuccessStatusCode || ReadInt(root, "code") != 0)
            {
                string reason = ReadString(root, "reason") ?? "";
                if (reason.Contains("CAPTCHA", StringComparison.OrdinalIgnoreCase))
                    throw new SiteApiException("站点要求人机验证码，当前同步暂不支持；可在站点获取密钥后手动添加账号。");
                string message = ReadString(root, "message") ?? $"HTTP {(int)response.StatusCode}";
                // 仅显示有界错误消息，不显示整个响应、令牌或请求凭据。
                throw new SiteApiException("站点请求失败：" + message[..Math.Min(message.Length, 240)]);
            }
            if (!root.TryGetProperty("data", out var data) || data.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
                throw new SiteApiException("站点响应缺少有效的 data 字段。");
            return data.Clone();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (OperationCanceledException exception) { throw new SiteApiException("连接站点超时，请重试。", exception); }
        catch (Exception exception) when (exception is HttpRequestException or IOException or JsonException)
        {
            throw new SiteApiException("无法连接站点，或响应不是有效的 Sub2API 数据，请检查网址和网络。", exception);
        }
    }

    private static T Deserialize<T>(JsonElement data) where T : class
    {
        try { return data.Deserialize<T>(JsonOptions) ?? throw new JsonException(); }
        catch (JsonException exception) { throw new SiteApiException("站点返回的数据格式无效。", exception); }
    }

    private static string? ReadString(JsonElement data, string name)
        => data.ValueKind == JsonValueKind.Object && data.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? ReadInt(JsonElement data, string name)
        => data.ValueKind == JsonValueKind.Object && data.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number) ? number : null;

    /// <summary>幂等地取消业务操作、退出全部会话并释放客户端。</summary>
    public ValueTask DisposeAsync()
    {
        lock (disposeGate) return new(disposeTask ??= CloseSessionsAsync());
    }

    /// <summary>等待在途操作结束后清空缓存，并行退出各站点。</summary>
    private async Task CloseSessionsAsync()
    {
        lifetime.Cancel();
        await operationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var pending = sessions.Values.ToArray();
            sessions.Clear();
            try { await Task.WhenAll(pending.Select(LogoutCoreAsync)).ConfigureAwait(false); }
            finally { Array.Clear(pending); }
        }
        finally
        {
            httpClients.Dispose();
            lifetime.Dispose();
            operationGate.Release();
        }
    }
}

internal sealed record Sub2ApiSiteData(decimal Balance, IReadOnlyList<Sub2ApiKey> Keys,
    IReadOnlyList<Sub2ApiGroup> Groups);

internal sealed record Sub2ApiKey(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("key")] string? Key,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("group_id")] long? GroupId,
    [property: JsonPropertyName("group")] Sub2ApiGroup? Group = null);

internal sealed record Sub2ApiGroup(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string? Description = null,
    [property: JsonPropertyName("rate_multiplier")] decimal? RateMultiplier = null);
