using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using Dom2API.Models;
using Dom2API.Services;

namespace Dom2API.ViewModels;

internal abstract class SyncObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Notify([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

internal sealed class SitesSyncViewModel : SyncObservableObject
{
    /// <summary>显示站点声明的非负分组倍率，缺失或无效时显示占位符。</summary>
    internal static string FormatRateMultiplier(decimal? rate)
        => rate is >= 0 ? $"{rate.Value.ToString("G29")}x" : "—";

    private string status = "";
    public ObservableCollection<SiteViewModel> Sites { get; } = [];
    public string WindowStatus { get => status; set { status = value; Notify(); } }
    public Visibility EmptySitesVisibility => Sites.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public SitesSyncViewModel() => Sites.CollectionChanged += (_, _) =>
    {
        Notify(nameof(EmptySitesVisibility));
        for (int i = 0; i < Sites.Count; i++) Sites[i].UpdatePosition(i, Sites.Count);
    };

    public bool MoveSite(SiteViewModel site, int offset)
    {
        int index = Sites.IndexOf(site);
        int destination = index + offset;
        if (site.IsBusy || index < 0 || destination < 0 || destination >= Sites.Count || offset is not (-1 or 1)) return false;
        Sites.Move(index, destination);
        return true;
    }
}

/// <summary>单站点互斥操作；失败时清空远端快照，避免误显示零余额或可创建分组。</summary>
internal sealed class SiteViewModel(SiteConnection site, SitesClient client,
    Func<IReadOnlyList<UpstreamAccount>> getAccounts) : SyncObservableObject
{
    private bool busy;
    private bool loaded;
    private decimal balance;
    private string balanceDisplay = "—";
    private IReadOnlyList<SiteGroup> groups = [];
    private string status = "尚未连接";
    public SiteConnection Site { get; } = site with { };
    public string TypeText => Site.Type == SiteType.Sub2Api ? "Sub2API" : "New API";
    public string RateDescription => Site.Type == SiteType.Sub2Api
        ? "所属分组的基础倍率，不含用户专属倍率和高峰系数"
        : "当前用户适用的分组倍率，不代表统一模型价格；服务页会独立查询";
    public string DisplayName => string.IsNullOrWhiteSpace(Site.Name) ? new Uri(Site.BaseUrl).Host : Site.Name;
    public bool IsBusy => busy;
    public bool CanOperate => !busy;
    public bool CanMoveUp { get; private set; }
    public bool CanMoveDown { get; private set; }
    public bool CanUseSnapshot => loaded && !busy;
    public string BalanceText => loaded ? balanceDisplay : "—";
    public bool IsBalanceNonPositive => loaded && balance <= 0;
    public string StatusText => status;
    public Visibility StatusVisibility => string.IsNullOrWhiteSpace(StatusText) ? Visibility.Collapsed : Visibility.Visible;
    public ObservableCollection<SiteKeyViewModel> Keys { get; } = [];
    public ObservableCollection<SiteGroupViewModel> UnkeyedGroups { get; } = [];
    public Visibility EmptyKeysVisibility => loaded && Keys.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility EmptyGroupsVisibility => loaded && UnkeyedGroups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    public void UpdatePosition(int index, int count)
    {
        CanMoveUp = index > 0;
        CanMoveDown = index < count - 1;
        Notify(nameof(CanMoveUp)); Notify(nameof(CanMoveDown));
    }

    public Task RefreshAsync(CancellationToken token) => RunAsync("正在同步…", async () =>
    {
        Apply(await client.LoadAsync(Site, token));
        status = "";
    }, token);

    public Task CreateKeyAsync(SiteGroupViewModel group, string name, CancellationToken token)
        => RunAsync("正在创建密钥…", async () =>
        {
            // 复用窗口会话刷新分组，避免旧快照造成重复创建。
            var current = await client.LoadAsync(Site, token);
            Apply(current);
            if (current.Keys.Any(key => key.GroupId == group.Id))
            {
                status = "该分组已存在密钥，列表已刷新。";
                return;
            }
            var available = current.Groups.FirstOrDefault(item => item.Id == group.Id)
                ?? throw new SiteApiException("该分组已不可用，请刷新后重试。");
            await client.CreateKeyAsync(Site, available, name, token);
            Apply(await client.LoadAsync(Site, token));
            status = "密钥已创建，可点击“添加到本地”导入。";
        }, token, creating: true);

    public Task ImportAsync(SiteKeyViewModel key, Func<UpstreamAccount, Task<bool>> addAccount, CancellationToken token)
        => RunAsync("正在添加账号…", async () =>
        {
            if (key.IsAdded) { status = "此密钥已添加到本地。"; return; }
            if (string.IsNullOrWhiteSpace(key.Remote.Key) || key.Remote.Key.Contains('*') || key.Remote.Key.Contains('…'))
                throw new SiteApiException("站点没有返回完整密钥，无法导入。请在站点获取完整密钥后手动添加。");
            bool added = await addAccount(new UpstreamAccount
            {
                Name = $"{key.Name} ({DisplayName})",
                BaseUrl = SiteAccountMapping.GatewayBaseUrl(Site.BaseUrl),
                ApiKey = key.Remote.Key,
                Proxy = Site.Proxy
            });
            RefreshMatches();
            status = added ? "已添加到本地。" : "已取消添加。";
        }, token);

    public Task RemoveLocalAsync(SiteKeyViewModel key, Func<string, Task<bool>> removeAccount, CancellationToken token)
        => RunAsync("正在删除本地账号…", async () =>
        {
            var account = SiteAccountMapping.Find(getAccounts(), Site.BaseUrl, key.Remote.Key);
            bool removed = account is not null && await removeAccount(account.Id);
            RefreshMatches();
            status = removed ? "已从本地删除，站点 API 密钥已保留。" : "此密钥未添加到本地。";
        }, token);

    /// <summary>删除远端密钥后刷新快照，保留独立管理的本地账号配置。</summary>
    public Task DeleteRemoteAsync(SiteKeyViewModel key, CancellationToken token)
        => RunAsync("正在从站点删除密钥…", async () =>
        {
            if (!Keys.Contains(key)) return;
            try { await client.DeleteKeyAsync(Site, key.Remote.Id, token); }
            catch (SiteApiException exception)
            {
                throw new SiteApiException(exception.Message + " 请刷新列表确认站点删除结果。", exception);
            }

            try { Apply(await client.LoadAsync(Site, token)); }
            catch (SiteApiException exception)
            {
                throw new SiteApiException("密钥已从站点删除，但刷新列表失败，请手动刷新。" + exception.Message, exception);
            }
            status = key.IsAdded
                ? "密钥已从站点删除；本地账号配置仍保留，该密钥已失效。"
                : "密钥已从站点删除。";
        }, token);

    public void RefreshMatches()
    {
        foreach (var key in Keys) key.RefreshMatch();
    }

    public string? FindName(SiteApiKey key)
        => SiteAccountMapping.FindDisplayName(getAccounts(), Site.BaseUrl, key.Key);

    public string FindGroupName(SiteApiKey key)
    {
        return key.Group?.Name ?? groups.FirstOrDefault(group => group.Id == key.GroupId)?.Name ?? (string.IsNullOrEmpty(key.GroupId) ? "未分组" : key.GroupId);
    }

    /// <summary>读取当前用户适用的分组倍率；不作为跨站点有效计费倍率。</summary>
    public decimal? FindGroupRateMultiplier(SiteApiKey key)
        => key.Group?.RateMultiplier ?? groups.FirstOrDefault(group => group.Id == key.GroupId)?.RateMultiplier;

    private async Task RunAsync(string message, Func<Task> action, CancellationToken token, bool creating = false)
    {
        if (busy || token.IsCancellationRequested) return;
        busy = true;
        status = message;
        NotifyState();
        try { await action(); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception) when (exception is SiteApiException or ArgumentException)
        {
            loaded = false;
            Keys.Clear();
            UnkeyedGroups.Clear();
            status = exception.Message + (creating ? " 创建结果可能尚未确认，请先刷新列表再操作。" : "");
        }
        finally { busy = false; NotifyState(); }
    }

    private void Apply(SiteData data)
    {
        loaded = true;
        balance = data.Balance;
        balanceDisplay = data.BalanceText;
        groups = data.Groups;
        Keys.Clear();
        foreach (var key in data.Keys) Keys.Add(new(this, key));
        UnkeyedGroups.Clear();
        var assigned = data.Keys.Select(key => key.GroupId).ToHashSet();
        foreach (var group in data.Groups.Where(group => !assigned.Contains(group.Id))) UnkeyedGroups.Add(new(this, group));
    }

    private void NotifyState()
    {
        Notify(nameof(IsBusy)); Notify(nameof(CanOperate)); Notify(nameof(CanUseSnapshot));
        Notify(nameof(BalanceText)); Notify(nameof(IsBalanceNonPositive));
        Notify(nameof(StatusText)); Notify(nameof(StatusVisibility));
        Notify(nameof(EmptyKeysVisibility)); Notify(nameof(EmptyGroupsVisibility));
    }
}

internal sealed class SiteKeyViewModel : SyncObservableObject
{
    public SiteViewModel Parent { get; }
    public SiteApiKey Remote { get; }
    public string Name => string.IsNullOrWhiteSpace(Remote.Name) ? $"密钥 {Remote.Id}" : Remote.Name;
    public string GroupText => $"分组：{Parent.FindGroupName(Remote)}";
    public string RateText => Parent.Site.Type == SiteType.NewApi && Remote.GroupId == "auto" ? "分组倍率：自动 / 未知" : "分组倍率：" + SitesSyncViewModel.FormatRateMultiplier(Parent.FindGroupRateMultiplier(Remote));
    public string? SoftwareAccountName { get; private set; }
    public bool IsAdded => SoftwareAccountName is not null;
    public string SoftwareStatusText => IsAdded ? $"已添加：{SoftwareAccountName}" : "未添加到本地";
    public Visibility AddVisibility => IsAdded ? Visibility.Collapsed : Visibility.Visible;
    public Visibility RemoveVisibility => IsAdded ? Visibility.Visible : Visibility.Collapsed;
    public SiteKeyViewModel(SiteViewModel parent, SiteApiKey key)
    {
        Parent = parent; Remote = key; RefreshMatch();
    }
    public void RefreshMatch()
    {
        SoftwareAccountName = Parent.FindName(Remote);
        Notify(nameof(SoftwareAccountName)); Notify(nameof(IsAdded)); Notify(nameof(SoftwareStatusText)); Notify(nameof(AddVisibility));
        Notify(nameof(RemoveVisibility));
    }
}

internal sealed class SiteGroupViewModel(SiteViewModel parent, SiteGroup group)
{
    public SiteViewModel Parent { get; } = parent;
    public string Id => group.Id;
    public string Name => group.Name;
    public string RateText => Parent.Site.Type == SiteType.NewApi && group.Id == "auto" ? "自动 / 未知" : "分组倍率：" + SitesSyncViewModel.FormatRateMultiplier(group.RateMultiplier);
    public string? Description => group.Description;
    public string DescriptionText { get; } = string.Join(" ", (group.Description ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    public Visibility DescriptionVisibility => string.IsNullOrWhiteSpace(Description) ? Visibility.Collapsed : Visibility.Visible;
}
