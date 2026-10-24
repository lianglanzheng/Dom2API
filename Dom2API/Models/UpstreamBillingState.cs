namespace Dom2API.Models;

/// <summary>保存当前应用运行期间的上游倍率查询结果，不参与配置持久化。</summary>
internal sealed record UpstreamBillingState
{
    /// <summary>获取尚未获得声明时的空状态。</summary>
    public static UpstreamBillingState Empty { get; } = new() { Status = "" };

    /// <summary>获取查询状态或失败说明。</summary>
    public string Status { get; init; } = "";

    /// <summary>获取是否正在查询。</summary>
    public bool IsRefreshing { get; init; }

    /// <summary>获取上游声明的基准倍率。</summary>
    public decimal? BaseRate { get; init; }

    /// <summary>获取上游在声明时刻生效的倍率。</summary>
    public decimal? EffectiveRate { get; init; }

    /// <summary>获取上游生成声明的时间。</summary>
    public DateTimeOffset? ObservedAt { get; init; }

    /// <summary>获取本地成功获取声明的时间。</summary>
    public DateTimeOffset? ReceivedAt { get; init; }

    /// <summary>倍率来源及含义。</summary>
    public string Source { get; init; } = "Sub2API 有效 token 计费倍率";

    /// <summary>获取界面显示的查询时倍率。</summary>
    public string RateText => EffectiveRate is { } rate ? $"{rate:G29}×" : "—";

    /// <summary>获取成功查询和上游声明时间。</summary>
    public string Details => ReceivedAt is not null ? $"{Source}\n更新于 {ReceivedAt:HH:mm:ss}" : "";
}
