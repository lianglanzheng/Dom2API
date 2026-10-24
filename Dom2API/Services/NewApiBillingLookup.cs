using Dom2API.Models;

namespace Dom2API.Services;

/// <summary>通过配置的 New API 账号查询密钥倍率，每次查询结束即退出临时会话。</summary>
internal sealed class NewApiBillingLookup(Func<NewApiClient>? createClient = null)
{
    private readonly Func<NewApiClient> createClient = createClient ?? (() => new());
    // 固定数量的锁避免长期增删站点导致锁表增长；同一网关串行，不同网关可并行。
    private readonly SemaphoreSlim[] gates = Enumerable.Range(0, 32).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private SiteConnection[] sites = [];

    /// <summary>发布独立配置快照，后台查询无需访问界面集合。</summary>
    public void UpdateSites(IEnumerable<SiteConnection> configured)
        => Volatile.Write(ref sites, configured.Where(site => site.Type == SiteType.NewApi)
            .Select(site => site with { }).ToArray());

    /// <summary>仅登录同一网关的账号；单个账号失败后继续查找，并在返回前退出。</summary>
    public async Task<UpstreamBillingState> QueryAsync(UpstreamSettings upstream, CancellationToken token)
    {
        // 同一主机的多个密钥串行探测，避免登录与退出相互干扰。
        var gate = gates[(int)((uint)StringComparer.OrdinalIgnoreCase.GetHashCode(upstream.UpstreamBaseUri.IdnHost) % (uint)gates.Length)];
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var candidates = Volatile.Read(ref sites).Where(site => SiteAccountMapping.SameGateway(
                upstream.UpstreamBaseUri.AbsoluteUri, site.BaseUrl)).ToArray();
            if (candidates.Length == 0)
                return new() { Status = "未配置基址匹配的 New API 站点账号，可在同步账号中添加" };
            if (string.IsNullOrWhiteSpace(upstream.ApiKey))
                return new() { Status = "本地上游密钥为空，无法匹配 New API 账号" };

            bool failed = false;
            string? matchedStatus = null;
            foreach (var site in candidates)
            {
                token.ThrowIfCancellationRequested();
                // 与同步窗口完全隔离；DisposeAsync 用独立超时退出，包括失败和取消路径。
                await using var client = createClient();
                try
                {
                    var key = await client.FindKeyAsync(site, upstream.ApiKey, token).ConfigureAwait(false);
                    if (key is null) continue;
                    if (key.GroupId == "auto")
                    {
                        matchedStatus = "New API 密钥使用自动分组，倍率未知";
                        continue;
                    }
                    if (key.Group?.RateMultiplier is not { } rate || rate < 0)
                    {
                        matchedStatus = "已匹配 New API 密钥，但站点未返回有效分组倍率";
                        continue;
                    }
                    return new()
                    {
                        BaseRate = rate,
                        EffectiveRate = rate,
                        Source = "New API 当前用户分组倍率",
                        ReceivedAt = DateTimeOffset.Now
                    };
                }
                catch (SiteApiException)
                {
                    // 不将服务端消息带入倍率状态，避免意外回显凭据或完整密钥。
                    failed = true;
                }
            }
            return new() { Status = matchedStatus ?? (failed
                ? "New API 站点登录或查询失败，且未获取匹配密钥的倍率；请在同步账号中检查后重试"
                : "New API 站点账号中未找到与本地上游相同的完整 API Key") };
        }
        finally { gate.Release(); }
    }
}
