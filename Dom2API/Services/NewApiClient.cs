using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Dom2API.Models;

namespace Dom2API.Services;

/// <summary>隔离每个账号的 Cookie 和登录令牌，兼容 New API 新旧会话。</summary>
internal sealed class NewApiClient : IAsyncDisposable
{
    // 仅缓存公开请求头名称，不缓存 Cookie、用户 ID 或登录令牌；不同部署路径隔离。
    private static readonly object headerCacheGate = new();
    private static readonly Dictionary<string, (string Header, DateTimeOffset Expires)> headerCache = [];
    private readonly Dictionary<string, Session> sessions = [];
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly Func<HttpMessageHandler>? handlerFactory;
    private readonly object disposeGate = new();
    private Task? disposeTask;

    private sealed class Session(SiteConnection site, HttpMessageHandler? handler) : IDisposable
    {
        public SiteConnection Site { get; } = site with { };
        public CookieContainer Cookies { get; } = new();
        public HttpClient Client { get; } = new(handler ?? OutboundClients.CreateHandler(site.Proxy, DecompressionMethods.All))
            { Timeout = Timeout.InfiniteTimeSpan };
        public string? AccessToken { get; set; }
        public string? SessionId { get; set; }
        public long UserId { get; set; }
        public string UserIdHeader { get; set; } = "New-Api-User";
        public bool HeaderDiscoveryAttempted { get; set; }
        public JsonElement? LoginStatus { get; set; }
        public void Dispose() => Client.Dispose();
    }

    /// <summary>测试时可提供独立的 HTTP 处理器。</summary>
    public NewApiClient(Func<HttpMessageHandler>? handlerFactory = null) => this.handlerFactory = handlerFactory;

    /// <summary>读取余额、可用分组以及所有密钥的完整值。</summary>
    public Task<SiteData> LoadAsync(SiteConnection site, CancellationToken token = default)
        => WithSessionAsync(site, LoadCoreAsync, token);

    private async Task<SiteData> LoadCoreAsync(Session session, CancellationToken token)
    {
        var user = Data(await SendAsync(session, HttpMethod.Get, "/user/self", null, token));
        // 首次同步复用刚为登录读取的公开配置；后续刷新仍读取最新值。
        var config = session.LoginStatus ?? Data(await SendAsync(session, HttpMethod.Get, "/status", null, token));
        session.LoginStatus = null;
        decimal quota = Number(user, "quota");
        decimal unit = Number(config, "quota_per_unit");
        if (unit <= 0) throw new SiteApiException("New API 返回的额度换算单位无效。");
        decimal balance = quota / unit;
        string display = $"$ {balance:0.00}";
        string? currency = Text(config, "quota_display_type");
        if (currency is null && config.TryGetProperty("display_in_currency", out var currencyFlag) && currencyFlag.ValueKind == JsonValueKind.False)
            currency = "TOKENS";
        if (currency == "TOKENS") display = $"{quota:G29} 额度";
        else if (currency is "CNY" or "CUSTOM")
        {
            decimal exchange = Number(config, currency == "CNY" ? "usd_exchange_rate" : "custom_currency_exchange_rate");
            if (exchange <= 0) throw new SiteApiException("New API 返回的汇率无效。");
            display = $"{(currency == "CNY" ? "¥" : Text(config, "custom_currency_symbol"))} {balance * exchange:0.00}";
        }
        var (keys, groups) = await LoadKeysAndGroupsAsync(session, user, token);
        return new(balance, keys, groups, display);
    }

    /// <summary>按完整密钥查找所属分组，不依赖余额及货币显示配置。</summary>
    public Task<SiteApiKey?> FindKeyAsync(SiteConnection site, string apiKey, CancellationToken token = default)
        => WithSessionAsync<SiteApiKey?>(site, async (session, cancellation) =>
        {
            session.LoginStatus = null;
            var user = Data(await SendAsync(session, HttpMethod.Get, "/user/self", null, cancellation));
            var (keys, groups) = await LoadKeysAndGroupsAsync(session, user, cancellation);
            var key = keys.FirstOrDefault(item => string.Equals(item.Key, apiKey.Trim(), StringComparison.Ordinal));
            return key is null ? null : key with { Group = groups.FirstOrDefault(group => group.Id == key.GroupId) };
        }, token);

    private static async Task<(IReadOnlyList<SiteApiKey> Keys, IReadOnlyList<SiteGroup> Groups)> LoadKeysAndGroupsAsync(
        Session session, JsonElement user, CancellationToken token)
    {
        var groupData = Data(await SendAsync(session, HttpMethod.Get, "/user/self/groups", null, token));
        var groups = new List<SiteGroup>();
        foreach (var property in groupData.EnumerateObject())
        {
            decimal? ratio = property.Value.TryGetProperty("ratio", out var value)
                && value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var rate) && rate >= 0 ? rate : null;
            groups.Add(new(property.Name, property.Name, Text(property.Value, "desc"), property.Name == "auto" ? null : ratio));
        }
        var keys = new List<SiteApiKey>();
        var ids = new HashSet<long>();
        for (int page = 1; ; page++)
        {
            var data = Data(await SendAsync(session, HttpMethod.Get, $"/token/?p={page}&page_size=100", null, token));
            var items = data.ValueKind == JsonValueKind.Array ? data : data.GetProperty("items");
            if (items.ValueKind != JsonValueKind.Array) throw new JsonException();
            foreach (var item in items.EnumerateArray())
            {
                long id = item.GetProperty("id").GetInt64();
                if (id <= 0 || !ids.Add(id)) throw new SiteApiException("密钥列表分页不一致，请刷新重试。");
                string group = Text(item, "group") ?? "";
                if (group.Length == 0) group = Text(user, "group") ?? "";
                keys.Add(new(id, Text(item, "key"), Text(item, "name") ?? "", group));
            }
            if (data.ValueKind == JsonValueKind.Array) break;
            long total = data.GetProperty("total").GetInt64();
            if (total < keys.Count) throw new SiteApiException("密钥列表总数发生变化，请刷新重试。");
            if (keys.Count >= total) break;
            if (items.GetArrayLength() == 0 || page >= 10000) throw new SiteApiException("密钥列表不完整，请刷新重试。");
        }
        // 批量读取避免逐个揭示密钥触发站点关键操作限流；旧站点的完整列表值可直接使用。
        foreach (var batch in keys.Where(key => !IsFullKey(key.Key)).ToArray().Chunk(100))
        {
            JsonElement values = default;
            bool single = false;
            try
            {
                var data = Data(await SendAsync(session, HttpMethod.Post, "/token/batch/keys", new { ids = batch.Select(key => key.Id) }, token, allowMissing: true));
                values = data.GetProperty("keys");
            }
            catch (MissingEndpointException) { single = true; }
            foreach (var key in batch)
            {
                string? full = single
                    ? Text(Data(await SendAsync(session, HttpMethod.Post, $"/token/{key.Id}/key", null, token)), "key")
                    : Text(values, key.Id.ToString(CultureInfo.InvariantCulture));
                if (!IsFullKey(full)) throw new SiteApiException("站点没有返回完整密钥，请刷新或在站点手动获取。");
                keys[keys.IndexOf(key)] = key with { Key = full };
            }
        }
        keys = keys.Select(key => key with { Key = key.Key!.StartsWith("sk-", StringComparison.Ordinal) ? key.Key : "sk-" + key.Key }).ToList();
        return (keys, groups);
    }

    /// <summary>创建无额外额度上限、永不过期的分组密钥；成功后由调用方刷新列表。</summary>
    public Task CreateKeyAsync(SiteConnection site, SiteGroup group, string name, CancellationToken token = default)
        => WithSessionAsync(site, async (session, cancellation) =>
        {
            if (string.IsNullOrWhiteSpace(name) || Encoding.UTF8.GetByteCount(name.Trim()) > 50)
                throw new SiteApiException("密钥名称不能为空，且 UTF-8 长度不能超过 50 字节。");
            return await SendAsync(session, HttpMethod.Post, "/token/", new
            {
                name = name.Trim(), group = group.Id, expired_time = -1, unlimited_quota = true,
                remain_quota = 0, model_limits_enabled = false, cross_group_retry = false
            }, cancellation);
        }, token);

    /// <summary>删除当前账号拥有的远端密钥。</summary>
    public Task DeleteKeyAsync(SiteConnection site, long id, CancellationToken token = default)
        => WithSessionAsync(site, (session, cancellation) => SendAsync(session, HttpMethod.Delete, $"/token/{id}", null, cancellation), token);

    private async Task<T> WithSessionAsync<T>(SiteConnection site, Func<Session, CancellationToken, Task<T>> action, CancellationToken token)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        await gate.WaitAsync(cancellation.Token);
        try
        {
            sessions.TryGetValue(site.Id, out var session);
            if (session is not null && (session.Site.BaseUrl != site.BaseUrl || session.Site.Username != site.Username
                || session.Site.Password != site.Password || session.Site.Proxy != site.Proxy))
            {
                sessions.Remove(site.Id);
                await LogoutCoreAsync(session);
                session = null;
            }
            bool cached = session is not null;
            if (session is null) sessions[site.Id] = session = await LoginAsync(site, cancellation.Token);
            try { return await action(session, cancellation.Token); }
            catch (SiteApiException error) when (cached && error.IsAuthenticationFailure)
            {
                sessions.Remove(site.Id);
                await LogoutCoreAsync(session);
                sessions[site.Id] = session = await LoginAsync(site, cancellation.Token);
                return await action(session, cancellation.Token);
            }
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or OverflowException or KeyNotFoundException)
        {
            throw new SiteApiException("New API 返回的数据格式无效，请检查站点版本。", error);
        }
        finally { gate.Release(); }
    }

    private async Task<Session> LoginAsync(SiteConnection site, CancellationToken token)
    {
        var session = new Session(site, handlerFactory?.Invoke());
        try
        {
            var status = Data(await SendAsync(session, HttpMethod.Get, "/status", null, token));
            session.LoginStatus = status;
            if (IsTrue(status, "turnstile_check")) throw new SiteApiException("站点要求人机验证，请在站点获取密钥后手动添加账号。");
            object credentials = new { username = site.Username, password = site.Password };
            if (IsTrue(status, "password_login_encryption_enabled"))
            {
                var encryption = Data(await SendAsync(session, HttpMethod.Get, "/user/login/encryption-key", null, token));
                try
                {
                    using var rsa = RSA.Create();
                    rsa.ImportFromPem(Text(encryption, "public_key") ?? throw new CryptographicException());
                    credentials = new { username = site.Username, password_encrypted = Convert.ToBase64String(rsa.Encrypt(Encoding.UTF8.GetBytes(site.Password), RSAEncryptionPadding.OaepSHA256)), encryption_key_id = Text(encryption, "kid") };
                }
                catch (Exception error) when (error is CryptographicException or ArgumentException)
                { throw new SiteApiException("站点密码加密失败，请检查公钥或在站点手动获取密钥。", error); }
            }
            var login = Data(await SendAsync(session, HttpMethod.Post, "/user/login", credentials, token));
            if (IsTrue(login, "require_2fa") || IsTrue(login, "requires_2fa") || IsTrue(login, "require_verification"))
                throw new SiteApiException("账号需要二次验证，请在站点获取密钥后手动添加账号。");
            session.AccessToken = Text(login, "access_token");
            if (session.AccessToken is null) session.UserIdHeader = GetCachedHeader(site) ?? session.UserIdHeader;
            var user = session.AccessToken is null ? login : login.GetProperty("user");
            session.UserId = user.GetProperty("id").GetInt64();
            if (session.UserId <= 0) throw new JsonException();
            if (session.AccessToken is not null) session.SessionId = Text(login.GetProperty("session"), "sid");
            return session;
        }
        catch { await LogoutCoreAsync(session); throw; }
    }

    /// <summary>请求只在明确未认证时允许上层重新登录；业务失败和网络异常不会重放写操作。</summary>
    private static async Task<JsonElement> SendAsync(Session session, HttpMethod method, string path, object? body, CancellationToken token, bool allowMissing = false)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            var uri = new Uri(SiteAccountMapping.ValidateBaseUrl(session.Site.BaseUrl).AbsoluteUri.TrimEnd('/') + "/api" + path);
            using var request = new HttpRequestMessage(method, uri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.TryAddWithoutValidation("Origin", uri.GetLeftPart(UriPartial.Authority));
            if (session.AccessToken is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
            if (session.UserId > 0)
            {
                string userId = session.UserId.ToString(CultureInfo.InvariantCulture);
                request.Headers.Add(session.UserIdHeader, userId);
            }
            if (session.SessionId is not null) request.Headers.Add("X-Auth-Session", session.SessionId);
            string cookie = session.Cookies.GetCookieHeader(uri);
            if (cookie.Length > 0) request.Headers.TryAddWithoutValidation("Cookie", cookie);
            if (body is not null) request.Content = JsonContent.Create(body);
            using var response = await session.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (allowMissing && response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
                throw new MissingEndpointException();
            if (response.Headers.TryGetValues("Set-Cookie", out var cookies))
                foreach (var value in cookies) session.Cookies.SetCookies(uri, value);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                // 仅对登录后的只读身份查询探测；不重放登录、创建或删除操作。
                if (session.UserId > 0 && session.AccessToken is null && method == HttpMethod.Get
                    && path == "/user/self" && !session.HeaderDiscoveryAttempted)
                {
                    session.HeaderDiscoveryAttempted = true;
                    ForgetCachedHeader(session);
                    string? header = await DiscoverUserIdHeaderAsync(session.Client,
                        SiteAccountMapping.ValidateBaseUrl(session.Site.BaseUrl), timeout.Token);
                    if (header is not null && !header.Equals(session.UserIdHeader, StringComparison.OrdinalIgnoreCase))
                    {
                        session.UserIdHeader = header;
                        return await SendAsync(session, method, path, body, timeout.Token);
                    }
                }
                throw new SiteApiException(path == "/user/login"
                    ? "New API 登录请求被拒绝（HTTP 401），请检查账号或站点登录限制。"
                    : $"New API 请求未通过认证（{method} /api{path.Split('?')[0]}，HTTP 401）。请刷新；若仍失败，可能是站点会话兼容问题。")
                { IsAuthenticationFailure = true };
            }
            await response.Content.LoadIntoBufferAsync(4 * 1024 * 1024, timeout.Token);
            using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(timeout.Token));
            var root = document.RootElement;
            if (!response.IsSuccessStatusCode || !IsTrue(root, "success"))
            {
                string message = Text(root, "message") ?? $"HTTP {(int)response.StatusCode}";
                throw new SiteApiException("New API 请求失败：" + message[..Math.Min(message.Length, 240)]);
            }
            if (path == "/user/self" && method == HttpMethod.Get && session.AccessToken is null
                && session.HeaderDiscoveryAttempted)
                RememberVerifiedHeader(session);
            return root.Clone();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (OperationCanceledException error) { throw new SiteApiException("连接 New API 超时，请先刷新确认操作结果。", error); }
        catch (Exception error) when (error is HttpRequestException or IOException or JsonException or CookieException)
        { throw new SiteApiException("无法连接站点，或站点未返回有效的 New API 数据。", error); }
    }

    private static string HeaderCacheKey(SiteConnection site)
        => SiteAccountMapping.ValidateBaseUrl(site.BaseUrl).AbsoluteUri.TrimEnd('/');

    private static string? GetCachedHeader(SiteConnection site)
    {
        lock (headerCacheGate)
        {
            string key = HeaderCacheKey(site);
            if (!headerCache.TryGetValue(key, out var entry)) return null;
            if (entry.Expires > DateTimeOffset.UtcNow) return entry.Header;
            headerCache.Remove(key);
            return null;
        }
    }

    private static void ForgetCachedHeader(Session session)
    {
        lock (headerCacheGate)
        {
            string key = HeaderCacheKey(session.Site);
            if (headerCache.TryGetValue(key, out var entry) && entry.Header == session.UserIdHeader)
                headerCache.Remove(key);
        }
    }

    private static void RememberVerifiedHeader(Session session)
    {
        lock (headerCacheGate)
        {
            string key = HeaderCacheKey(session.Site);
            if (headerCache.Count >= 128 && !headerCache.ContainsKey(key))
                headerCache.Remove(headerCache.MinBy(entry => entry.Value.Expires).Key);
            headerCache[key] = (session.UserIdHeader, DateTimeOffset.UtcNow.AddMinutes(30));
        }
    }

    /// <summary>从同源页面及脚本的请求头配置发现用户 ID 头；只读取文本，不执行脚本或携带登录凭据。</summary>
    internal static async Task<string?> DiscoverUserIdHeaderAsync(HttpClient client, Uri site, CancellationToken token)
    {
        var pending = new Queue<(Uri Uri, bool Html)>();
        var visited = new HashSet<Uri>();
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        pending.Enqueue((new Uri(site.AbsoluteUri.TrimEnd('/') + "/"), true));
        int remainingBytes = 12 * 1024 * 1024;
        try
        {
            while (pending.Count > 0 && visited.Count < 8)
            {
                // 同层独立脚本最多并行 3 个；预留正文预算，保持总下载和资源数限制。
                var batch = new List<(Uri Uri, bool Html)>();
                int concurrency = Math.Min(3, Math.Max(1, remainingBytes / (4 * 1024 * 1024)));
                while (pending.Count > 0 && visited.Count < 8 && batch.Count < concurrency)
                {
                    var item = pending.Dequeue();
                    var uri = item.Uri;
                    if (uri.Scheme != site.Scheme || uri.IdnHost != site.IdnHost || uri.Port != site.Port
                        || uri.UserInfo.Length > 0 || !visited.Add(uri)) continue;
                    batch.Add(item);
                }
                var sources = await Task.WhenAll(batch.Select(item => ReadSourceAsync(client, item.Uri,
                    Math.Min(4 * 1024 * 1024, remainingBytes), token)));
                for (int index = 0; index < batch.Count; index++)
                {
                    var (uri, html) = batch[index];
                    byte[]? bytes = sources[index];
                    if (bytes is null) continue;
                    remainingBytes -= bytes.Length;
                    string source = Encoding.UTF8.GetString(bytes);
                    // 只接受 headers 对象中明确声明的 *-Api-User 属性，避免从品牌名称或文案猜测。
                    foreach (Match block in Regex.Matches(source, "(?:\\bheaders|[\"']headers[\"'])\\s*:\\s*\\{[^{}]{0,2048}\\}", RegexOptions.None, TimeSpan.FromMilliseconds(250)))
                        foreach (Match match in Regex.Matches(block.Value, "[\"'](?<header>[A-Za-z][A-Za-z0-9-]{0,63}-Api-User)[\"']\\s*:", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(250)))
                            candidates.Add(match.Groups["header"].Value);
                    if (candidates.Count > 1) return null;
                    string pattern = html
                        ? "<script\\b[^>]*\\bsrc\\s*=\\s*[\"'](?<url>[^\"']+)[\"']"
                        : "(?:\\bfrom\\s*|\\bimport\\s*(?:\\(\\s*)?)[\"'](?<url>[^\"']+\\.m?js(?:\\?[^\"']*)?)[\"']";
                    foreach (Match match in Regex.Matches(source, pattern, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(250)))
                    {
                        if (pending.Count >= 32) break;
                        if (Uri.TryCreate(uri, WebUtility.HtmlDecode(match.Groups["url"].Value), out var script))
                            pending.Enqueue((script, false));
                    }
                }
                if (remainingBytes <= 0) break;
            }
            return candidates.Count == 1 ? candidates.Single() : null;
        }
        catch (Exception error) when (error is HttpRequestException or IOException or RegexMatchTimeoutException)
        {
            return null;
        }
    }

    private static async Task<byte[]?> ReadSourceAsync(HttpClient client, Uri uri, int maxBytes, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        if (!response.IsSuccessStatusCode) return null;
        // 只读取前 maxBytes 字节。LoadIntoBufferAsync 会在完整响应超过上限时
        // 直接抛错；大型打包脚本可能把请求头声明放在开头，不能因此整段放弃。
        if (maxBytes <= 0) return [];
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var buffer = new MemoryStream(Math.Min(maxBytes, 64 * 1024));
        byte[] chunk = new byte[Math.Min(maxBytes, 64 * 1024)];
        while (buffer.Length < maxBytes)
        {
            int count = await stream.ReadAsync(chunk.AsMemory(0,
                (int)Math.Min(chunk.Length, maxBytes - buffer.Length)), token);
            if (count == 0) break;
            await buffer.WriteAsync(chunk.AsMemory(0, count), token);
        }
        return buffer.ToArray();
    }

    private static JsonElement Data(JsonElement root) => root.GetProperty("data");
    private static string? Text(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static bool IsTrue(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
    private static decimal Number(JsonElement root, string name) => root.GetProperty(name).GetDecimal();
    private static bool IsFullKey(string? key) => !string.IsNullOrWhiteSpace(key) && !key.Contains('*') && !key.Contains('…');

    /// <summary>仅用于明确不存在的只读揭示接口，允许降级为单个密钥查询。</summary>
    private sealed class MissingEndpointException : Exception;

    /// <summary>退出一个账号并释放其独立客户端。</summary>
    public async Task LogoutSiteAsync(string id)
    {
        await gate.WaitAsync();
        try { if (sessions.Remove(id, out var session)) await LogoutCoreAsync(session); }
        finally { gate.Release(); }
    }

    private static async Task LogoutCoreAsync(Session session)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await SendAsync(session, session.AccessToken is null ? HttpMethod.Get : HttpMethod.Post,
                session.AccessToken is null ? "/user/logout" : "/user/auth/logout", null, timeout.Token);
        }
        catch (Exception error) when (error is SiteApiException or OperationCanceledException) { }
        finally { session.Dispose(); }
    }

    /// <summary>取消同步，等待所有会话退出；可重复调用。</summary>
    public ValueTask DisposeAsync()
    {
        lock (disposeGate) return new(disposeTask ??= CloseAsync());
    }

    private async Task CloseAsync()
    {
        lifetime.Cancel();
        await gate.WaitAsync();
        try
        {
            await Task.WhenAll(sessions.Values.Select(LogoutCoreAsync));
            sessions.Clear();
        }
        finally { gate.Release(); }
    }
}
