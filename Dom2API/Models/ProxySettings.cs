namespace Dom2API.Models;

/// <summary>一次服务运行的监听与调度配置。</summary>
public sealed class ProxySettings
{
    /// <summary>监听端口。</summary>
    public int Port { get; }
    /// <summary>响应健康阈值，同时作为未选定上游时追加账号的等待间隔。</summary>
    public TimeSpan TimeoutThreshold { get; }
    /// <summary>健康状态与延迟样本的保留时长。</summary>
    public TimeSpan Cooldown { get; }
    /// <summary>按界面顺序排列的账号快照。</summary>
    public IReadOnlyList<UpstreamSettings> Upstreams { get; }

    /// <summary>验证并创建运行配置。</summary>
    public ProxySettings(string port, string timeoutSeconds, string cooldownMinutes, IEnumerable<UpstreamAccount> accounts, bool requireEnabled = true)
    {
        if (!int.TryParse(port, out int number) || number is < 1 or > 65535)
            throw new ArgumentException("监听端口必须是 1 至 65535 之间的整数。");
        Port = number;
        TimeoutThreshold = ParseDuration(timeoutSeconds, "超时阈值（秒）", 1000);
        Cooldown = ParseDuration(cooldownMinutes, "冷却时长（分钟）", 60000);
        Upstreams = accounts.Select((account, index) => new UpstreamSettings(account, index + 1, number)).ToArray();
        if (Upstreams.Select(account => account.Id).Distinct().Count() != Upstreams.Count)
            throw new ArgumentException("上游账号标识不能重复。");
        if (requireEnabled && !Upstreams.Any(account => account.IsEnabled))
            throw new ArgumentException("请至少添加并启用一个上游账号。");
    }

    private static TimeSpan ParseDuration(string value, string name, double multiplier)
    {
        if (!double.TryParse(value, out double number) || !double.IsFinite(number)
            || number * multiplier < 1 || number * multiplier > int.MaxValue)
            throw new ArgumentException(name + "必须是大于零且不超过约 24 天的数值。");
        return TimeSpan.FromMilliseconds(number * multiplier);
    }
}

/// <summary>已验证的独立上游连接快照。</summary>
public sealed record UpstreamSettings
{
    /// <summary>稳定账号标识。</summary>
    public string Id { get; }
    /// <summary>账号序号。</summary>
    public int Order { get; }
    /// <summary>显示名称。</summary>
    public string DisplayName { get; }
    /// <summary>是否参与调度。</summary>
    public bool IsEnabled { get; }
    /// <summary>上游基址。</summary>
    public Uri UpstreamBaseUri { get; }
    /// <summary>上游凭据。</summary>
    public string ApiKey { get; }
    /// <summary>已验证的 Socks5 代理地址；空值表示直连。</summary>
    public string? Proxy { get; }

    /// <summary>验证账号，检查回环地址和凭据换行。</summary>
    public UpstreamSettings(UpstreamAccount account, int order, int port)
    {
        if (string.IsNullOrWhiteSpace(account.Id)) throw new ArgumentException("账号标识不能为空。");
        if (!Uri.TryCreate(account.BaseUrl.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0
            || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new ArgumentException("上游 API 基址必须是 HTTP 或 HTTPS 地址，且不能包含账号、查询参数或片段。");
        if (uri.IsLoopback && uri.Port == port)
            throw new ArgumentException("上游不能指向当前监听端口，以免形成转发循环。");
        if (account.ApiKey.Contains('\r') || account.ApiKey.Contains('\n'))
            throw new ArgumentException("API 密钥不能包含换行符。");
        Id = account.Id;
        Order = order;
        DisplayName = string.IsNullOrWhiteSpace(account.Name) ? $"账号{order}" : account.Name.Trim();
        IsEnabled = account.IsEnabled;
        UpstreamBaseUri = uri;
        ApiKey = account.ApiKey.Trim();
        Proxy = Socks5Proxy.Normalize(account.Proxy);
    }
}
