using System.Globalization;
using Dom2API.Models;

namespace Dom2API.Services;

/// <summary>按站点类型分发同步操作，将协议响应转换为统一的界面数据。</summary>
internal sealed class SitesClient : IAsyncDisposable
{
    private readonly Sub2ApiClient sub2Api;
    private readonly NewApiClient newApi;

    /// <summary>复用各协议客户端的独立会话和取消机制。</summary>
    public SitesClient(Sub2ApiClient? sub2Api = null, NewApiClient? newApi = null)
    {
        this.sub2Api = sub2Api ?? new();
        this.newApi = newApi ?? new();
    }

    /// <summary>加载统一快照，切换协议时退出同一标识的旧协议会话。</summary>
    public async Task<SiteData> LoadAsync(SiteConnection site, CancellationToken token = default)
    {
        switch (site.Type)
        {
            case SiteType.NewApi:
                await sub2Api.LogoutSiteAsync(site.Id);
                return await newApi.LoadAsync(site, token);
            case SiteType.Sub2Api:
                await newApi.LogoutSiteAsync(site.Id);
                var data = await sub2Api.LoadAsync(site, token);
                return new(data.Balance, data.Keys.Select(key => new SiteApiKey(key.Id, key.Key, key.Name,
                    key.GroupId?.ToString(CultureInfo.InvariantCulture), key.Group is null ? null : MapGroup(key.Group))).ToArray(),
                    data.Groups.Select(MapGroup).ToArray(), $"$ {data.Balance:0.00}");
            default:
                throw new SiteApiException("站点类型无效，请编辑站点并重新识别。");
        }
    }

    /// <summary>创建成功后由界面重新读取列表，统一两种协议的返回差异。</summary>
    public Task CreateKeyAsync(SiteConnection site, SiteGroup group, string name, CancellationToken token = default)
        => site.Type switch
        {
            SiteType.Sub2Api => sub2Api.CreateKeyAsync(site, new Sub2ApiGroup(long.Parse(group.Id, CultureInfo.InvariantCulture), group.Name), name, token),
            SiteType.NewApi => newApi.CreateKeyAsync(site, group, name, token),
            _ => throw new SiteApiException("站点类型无效。")
        };

    /// <summary>删除站点上的密钥，保留本地上游配置。</summary>
    public Task DeleteKeyAsync(SiteConnection site, long id, CancellationToken token = default)
        => site.Type switch
        {
            SiteType.Sub2Api => sub2Api.DeleteKeyAsync(site, id, token),
            SiteType.NewApi => newApi.DeleteKeyAsync(site, id, token),
            _ => throw new SiteApiException("站点类型无效。")
        };

    /// <summary>移除连接时清理其可能存在的协议会话。</summary>
    public Task LogoutSiteAsync(string id) => Task.WhenAll(sub2Api.LogoutSiteAsync(id), newApi.LogoutSiteAsync(id));

    /// <summary>关闭窗口时取消并退出两种协议的全部会话。</summary>
    public ValueTask DisposeAsync() => new(Task.WhenAll(sub2Api.DisposeAsync().AsTask(), newApi.DisposeAsync().AsTask()));

    private static SiteGroup MapGroup(Sub2ApiGroup group)
        => new(group.Id.ToString(CultureInfo.InvariantCulture), group.Name, group.Description, group.RateMultiplier);
}

/// <summary>同步窗口内存中的余额、密钥和分组快照。</summary>
internal sealed record SiteData(decimal Balance, IReadOnlyList<SiteApiKey> Keys, IReadOnlyList<SiteGroup> Groups, string BalanceText);
/// <summary>统一的站点密钥，保留 Sub2API 返回的关联分组信息。</summary>
internal sealed record SiteApiKey(long Id, string? Key, string Name, string? GroupId, SiteGroup? Group = null);
/// <summary>界面中的分组标识统一为字符串，协议客户端负责转换。</summary>
internal sealed record SiteGroup(string Id, string Name, string? Description, decimal? RateMultiplier);
/// <summary>站点同步错误及明确的认证失效标记。</summary>
internal sealed class SiteApiException(string message, Exception? inner = null) : Exception(message, inner)
{
    public bool IsAuthenticationFailure { get; init; }
}
