namespace Dom2API.Models;

/// <summary>持久化的监听、调度参数及多上游账号。</summary>
public sealed class RememberedSettings
{
    /// <summary>监听端口输入。</summary>
    public required string Port { get; set; }
    /// <summary>响应阈值，单位秒。</summary>
    public string TimeoutSeconds { get; set; } = "30";
    /// <summary>冷却时长，单位分钟。</summary>
    public string CooldownMinutes { get; set; } = "10";
    /// <summary>有序上游账号列表。</summary>
    public required List<UpstreamAccount> Upstreams { get; set; }
    /// <summary>所有类型的有序站点连接；仅在记住配置时保存。</summary>
    [System.Text.Json.Serialization.JsonRequired]
    public List<SiteConnection> Sites { get; set; } = [];
    /// <summary>上次服务运行状态。</summary>
    public bool IsRunning { get; set; }
    /// <summary>用户是否选择登录 Windows 时运行，默认关闭。</summary>
    public bool StartOnLogin { get; set; }
}

/// <summary>持久化的单项上游账号。</summary>
public sealed record UpstreamAccount
{
    /// <summary>跨编辑保持稳定的账号标识。</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    /// <summary>可选名称。</summary>
    public string Name { get; set; } = "";
    /// <summary>API 基址。</summary>
    public string BaseUrl { get; set; } = "https://api.openai.com/v1";
    /// <summary>API 密钥。</summary>
    public string ApiKey { get; set; } = "";
    /// <summary>可选的 Socks5 代理地址；未配置时直连。</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? Proxy { get; set; }
    /// <summary>是否参与调度。</summary>
    public bool IsEnabled { get; set; } = true;
}

/// <summary>统一的站点登录配置，类型由公开接口自动识别。</summary>
public sealed record SiteConnection
{
    /// <summary>站点连接的稳定标识。</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    /// <summary>可选的站点名称。</summary>
    public string Name { get; set; } = "";
    /// <summary>面板基址，支持部署子路径。</summary>
    public string BaseUrl { get; set; } = "";
    /// <summary>用户名或邮箱。</summary>
    public string Username { get; set; } = "";
    /// <summary>站点登录密码。</summary>
    public string Password { get; set; } = "";
    /// <summary>可选的 Socks5 代理地址；导入上游时一并复制。</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? Proxy { get; set; }
    /// <summary>已识别的站点类型；配置文件必须提供。</summary>
    [System.Text.Json.Serialization.JsonRequired]
    public SiteType Type { get; set; }
}

/// <summary>受支持的站点协议类型；零值表示尚未识别。</summary>
public enum SiteType
{
    /// <summary>Sub2API 协议。</summary>
    Sub2Api = 1,
    /// <summary>New API 协议。</summary>
    NewApi = 2
}
