namespace Dom2API.Models;

/// <summary>按稳定账号标识保存的启用状态快照。</summary>
public sealed record AccountPreset
{
    /// <summary>跨重命名保持稳定的预设标识。</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    /// <summary>预设名称。</summary>
    public string Name { get; set; } = "";
    /// <summary>各账号的启用状态，不包含连接信息或密钥。</summary>
    public Dictionary<string, bool> AccountStates { get; set; } = [];

    /// <summary>未记录的新账号默认禁用，已删除账号的记录不参与应用。</summary>
    public bool IsAccountEnabled(string accountId) => AccountStates.GetValueOrDefault(accountId);
}
