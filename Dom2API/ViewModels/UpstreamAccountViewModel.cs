using System.ComponentModel;
using System.Runtime.CompilerServices;
using Dom2API.Models;
using Dom2API.Services;

namespace Dom2API.ViewModels;

/// <summary>单个账号的可观察界面状态，密钥只在编辑窗口中显示为密码。</summary>
internal sealed class UpstreamAccountViewModel(UpstreamAccount account) : INotifyPropertyChanged
{
    private int order;
    private int accountCount;
    private UpstreamHealth health = new(UpstreamHealthStatus.Normal, 0, 0, 0);
    private UpstreamBillingState billing = UpstreamBillingState.Empty;
    private (int Successful, int Failed, int Total, long Input, long Output, long Tokens) statistics;
    public event PropertyChangedEventHandler? PropertyChanged;
    public UpstreamAccount Account { get; private set; } = account;
    public int Order
    {
        get => order;
        set { if (order == value) return; order = value; Notify(); Notify(nameof(DisplayName)); Notify(nameof(CanMoveUp)); Notify(nameof(CanMoveDown)); }
    }
    public bool CanMoveUp => Order > 1;
    public bool CanMoveDown => Order < accountCount;
    public void UpdatePosition(int position, int count)
    {
        accountCount = count;
        Order = position;
        Notify(nameof(CanMoveDown));
    }
    public string DisplayName => string.IsNullOrWhiteSpace(Account.Name) ? $"账号{Order}" : Account.Name;
    public string BaseUrl => Account.BaseUrl;
    public bool IsEnabled => Account.IsEnabled;
    public string RateText => billing.RateText;
    public string BillingDetails => string.Join("\n", new[] { billing.Status, billing.Details }.Where(value => value.Length > 0));
    public bool CanRefresh => !billing.IsRefreshing;
    public string HealthText => health.Status switch
    {
        UpstreamHealthStatus.Error => "错误",
        UpstreamHealthStatus.Degraded => "不佳",
        _ => "正常"
    };
    public bool IsUnhealthy => health.IsUnhealthy;
    public bool HasError => health.Status == UpstreamHealthStatus.Error;
    public string? HealthToolTip => HasError ? $"错误原因：{health.ErrorReason}"
        : health.HealthLevel is { } level ? $"健康等级：{level:0}" : null;
    public int ActiveConnections => health.ActiveConnections;
    public string ResponseDurationText => $"{health.ResponseMilliseconds / 1000:N1} s";
    public string ResponseText => $"当前响应：{ResponseDurationText}（进行中取最大响应耗时；空闲时取周期内最近结束连接的最后响应间隔）";
    public int SuccessfulRequests => statistics.Successful;
    public int FailedRequests => statistics.Failed;
    public int TotalRequests => statistics.Total;
    public long InputTokens => statistics.Input;
    public long OutputTokens => statistics.Output;
    public long TotalTokens => statistics.Tokens;

    /// <summary>按账号请求快照汇总，统计口径与服务页总统计一致。</summary>
    public void UpdateStatistics(IEnumerable<CaptureRecord> requests)
    {
        var updated = (Successful: 0, Failed: 0, Total: 0, Input: 0L, Output: 0L, Tokens: 0L);
        foreach (var request in requests)
        {
            updated.Total++;
            if (request.IsSuccess) updated.Successful++;
            if (request.IsCompleted && request.IsFailure) updated.Failed++;
            updated.Input += request.Usage?.InputTokenCount ?? 0;
            updated.Output += request.Usage?.OutputTokenCount ?? 0;
            updated.Tokens += request.Usage?.TotalTokenCount ?? 0;
        }
        if (statistics == updated) return;
        statistics = updated;
        Notify(nameof(SuccessfulRequests));
        Notify(nameof(FailedRequests));
        Notify(nameof(TotalRequests));
        Notify(nameof(InputTokens));
        Notify(nameof(OutputTokens));
        Notify(nameof(TotalTokens));
    }

    public void Replace(UpstreamAccount value)
    {
        Account = value;
        Notify(nameof(Account));
        Notify(nameof(DisplayName));
        Notify(nameof(BaseUrl));
        Notify(nameof(IsEnabled));
    }

    public void Update(UpstreamHealth newHealth, UpstreamBillingState newBilling)
    {
        if (health != newHealth)
        {
            bool changedHealth = health.Status != newHealth.Status;
            bool changedCount = health.ActiveConnections != newHealth.ActiveConnections;
            string previousText = ResponseText;
            string? previousToolTip = HealthToolTip;
            health = newHealth;
            if (previousToolTip != HealthToolTip) Notify(nameof(HealthToolTip));
            if (changedHealth) { Notify(nameof(HealthText)); Notify(nameof(IsUnhealthy)); Notify(nameof(HasError)); }
            if (changedCount) Notify(nameof(ActiveConnections));
            if (previousText != ResponseText)
            {
                Notify(nameof(ResponseText));
                Notify(nameof(ResponseDurationText));
            }
        }
        if (billing != newBilling)
        {
            billing = newBilling;
            Notify(nameof(RateText));
            Notify(nameof(BillingDetails));
            Notify(nameof(CanRefresh));
        }
    }

    private void Notify([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
