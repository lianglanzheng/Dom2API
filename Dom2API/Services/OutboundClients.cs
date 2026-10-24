using System.Net;
using System.Net.Http;
using Dom2API.Models;

namespace Dom2API.Services;

/// <summary>按代理隔离连接池；配置切换后旧连接仍可完成在途请求。</summary>
internal sealed class OutboundClients(DecompressionMethods decompression = DecompressionMethods.None,
    HttpMessageHandler? handler = null) : IDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<string, HttpClient> clients = [];
    private readonly HttpClient? injectedClient = handler is null ? null : new(handler) { Timeout = Timeout.InfiniteTimeSpan };

    public static SocketsHttpHandler CreateHandler(string? proxy, DecompressionMethods decompression = DecompressionMethods.None)
    {
        string? address = Socks5Proxy.Normalize(proxy);
        WebProxy? webProxy = address is null ? null : new WebProxy(address);
        if (webProxy is not null && new Uri(address!).UserInfo is { Length: > 0 } userInfo)
        {
            string[] parts = userInfo.Split(':', 2);
            webProxy.Credentials = new NetworkCredential(Uri.UnescapeDataString(parts[0]),
                parts.Length == 2 ? Uri.UnescapeDataString(parts[1]) : "");
        }
        return new()
        {
            AllowAutoRedirect = false, UseCookies = false,
            UseProxy = webProxy is not null, Proxy = webProxy,
            AutomaticDecompression = decompression,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        };
    }

    public HttpClient Get(string? proxy)
    {
        string key = Socks5Proxy.Normalize(proxy) ?? "";
        lock (gate)
        {
            if (injectedClient is not null) return injectedClient;
            if (!clients.TryGetValue(key, out var client))
                clients[key] = client = new(CreateHandler(key, decompression)) { Timeout = Timeout.InfiniteTimeSpan };
            return client;
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            foreach (var client in clients.Values) client.Dispose();
            clients.Clear();
            injectedClient?.Dispose();
        }
    }
}
