using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Reflection;
using System.Runtime.InteropServices;
using Dom2API.Models;
using Dom2API.Services;

namespace Dom2API.ViewModels;

/// <summary>
/// 为三个工作界面提供请求快照、对话聚合和统计结果。
/// </summary>
internal sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly CaptureStore store;
    private long loadedVersion = -1;
    private string serviceStatus = "已停止";
    private string serviceError = "";
    private string settingsError = "";
    private string loginStartupStatus = "";
    private string loginStartupError = "";
    private string port = "1020";
    private string timeoutSeconds = "30";
    private string cooldownMinutes = "10";
    private bool isRunning;
    private bool isBusy;
    private AccountPreset? selectedAccountPreset;
    private ReleaseUpdate? availableUpdate;
    private string updateCheckStatus = "";
    private bool isUpdating;
    private string updateInstallStatus = "";
    private SessionSummary? selectedSession;
    private CaptureRecord? selectedRequest;
    private MessageEntry? selectedMessage;
    private NetworkMessage? selectedNetworkMessage;

    /// <summary>
    /// 创建绑定指定记录集合的主视图模型。
    /// </summary>
    public MainViewModel(CaptureStore store)
    {
        this.store = store;
    }

    /// <summary>在界面属性改变时发出通知。</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>读取当前程序集的发布版本（包含预发布标识和提交信息），兼容单文件发布。</summary>
    public string ApplicationVersion { get; } = typeof(MainViewModel).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(MainViewModel).Assembly.GetName().Version?.ToString() ?? "未知";

    /// <summary>当前进程使用的运行时和体系结构，便于排查问题。</summary>
    public string RuntimeDescription { get; } = $"{RuntimeInformation.FrameworkDescription} · {RuntimeInformation.ProcessArchitecture}";

    /// <summary>启动检查发现的新正式版；未发现更新时为空。</summary>
    public ReleaseUpdate? AvailableUpdate
    {
        get => availableUpdate;
        set { SetField(ref availableUpdate, value); OnPropertyChanged(nameof(CanInstallUpdate)); }
    }

    /// <summary>后台检查进度或非阻断的失败提示。</summary>
    public string UpdateCheckStatus { get => updateCheckStatus; set => SetField(ref updateCheckStatus, value); }

    /// <summary>下载和准备更新期间避免重复操作以及配置变更。</summary>
    public bool IsUpdating
    {
        get => isUpdating;
        set { SetField(ref isUpdating, value); NotifyServiceControls(); }
    }

    /// <summary>是否可以发起更新。</summary>
    public bool CanInstallUpdate => AvailableUpdate is not null && !IsUpdating && !IsBusy;

    /// <summary>更新下载进度或失败原因。</summary>
    public string UpdateInstallStatus { get => updateInstallStatus; set => SetField(ref updateInstallStatus, value); }

    /// <summary>获取或设置端口输入。</summary>
    public string Port
    {
        get => port;
        set => SetField(ref port, value);
    }

    /// <summary>获取或设置响应超时阈值（秒）。</summary>
    public string TimeoutSeconds { get => timeoutSeconds; set => SetField(ref timeoutSeconds, value); }

    /// <summary>获取或设置健康状态冷却时长（分钟）。</summary>
    public string CooldownMinutes { get => cooldownMinutes; set => SetField(ref cooldownMinutes, value); }

    /// <summary>账号行保持对象身份，状态变化仅刷新受影响的绑定。</summary>
    public ObservableCollection<UpstreamAccountViewModel> Upstreams { get; } = [];

    /// <summary>可选的账号启用预设。</summary>
    public ObservableCollection<AccountPreset> AccountPresets { get; } = [];

    /// <summary>当前预设，手动调整账号不覆盖其快照。</summary>
    public AccountPreset? SelectedAccountPreset
    {
        get => selectedAccountPreset;
        set
        {
            SetField(ref selectedAccountPreset, value);
            RefreshAccountPresetState();
        }
    }

    /// <summary>是否选中了可编辑的预设。</summary>
    public bool HasSelectedAccountPreset => SelectedAccountPreset is not null;

    /// <summary>当前账号启用情况是否偏离所选预设。</summary>
    public bool IsAccountPresetModified => SelectedAccountPreset is { } preset
        && Upstreams.Any(row => row.IsEnabled != preset.IsAccountEnabled(row.Account.Id));

    /// <summary>刷新手动修改和账号增删后的预设提示。</summary>
    public void RefreshAccountPresetState()
    {
        OnPropertyChanged(nameof(HasSelectedAccountPreset));
        OnPropertyChanged(nameof(IsAccountPresetModified));
    }

    /// <summary>刷新账号运行状态。</summary>
    public void UpdateUpstreamStatuses(ProxyService service)
    {
        foreach (var row in Upstreams)
            row.Update(service.GetHealth(row.Account.Id), service.GetBillingState(row.Account.Id));
    }

    /// <summary>从当前输入创建独立配置快照。</summary>
    public ProxySettings BuildProxySettings(bool requireEnabled = true)
        => new(Port, TimeoutSeconds, CooldownMinutes, Upstreams.Select(row => row.Account), requireEnabled);

    /// <summary>删除或添加后重新编号，空名称同步显示新序号。</summary>
    public void RenumberAccounts()
    {
        for (int i = 0; i < Upstreams.Count; i++) Upstreams[i].UpdatePosition(i + 1, Upstreams.Count);
    }

    /// <summary>获取或设置记忆配置的读写错误信息。</summary>
    public string SettingsError
    {
        get => settingsError;
        set => SetField(ref settingsError, value);
    }

    /// <summary>获取或设置登录启动项与 Windows 启用状态的说明。</summary>
    public string LoginStartupStatus { get => loginStartupStatus; set => SetField(ref loginStartupStatus, value); }

    /// <summary>获取或设置登录启动项操作失败的原因。</summary>
    public string LoginStartupError { get => loginStartupError; set => SetField(ref loginStartupError, value); }

    /// <summary>获取或设置服务状态文字。</summary>
    public string ServiceStatus
    {
        get => serviceStatus;
        set => SetField(ref serviceStatus, value);
    }

    /// <summary>获取或设置服务错误信息。</summary>
    public string ServiceError
    {
        get => serviceError;
        set => SetField(ref serviceError, value);
    }

    /// <summary>获取或设置服务是否正在运行。</summary>
    public bool IsRunning
    {
        get => isRunning;
        set
        {
            SetField(ref isRunning, value);
            NotifyServiceControls();
        }
    }

    /// <summary>获取或设置服务是否正在启停。</summary>
    public bool IsBusy
    {
        get => isBusy;
        set
        {
            SetField(ref isBusy, value);
            NotifyServiceControls();
        }
    }

    /// <summary>获取配置是否可以编辑。</summary>
    public bool CanEditSettings => !IsRunning && !IsBusy && !IsUpdating;

    /// <summary>获取启动按钮是否可用。</summary>
    public bool CanStart => !IsRunning && !IsBusy && !IsUpdating;

    /// <summary>获取停止按钮是否可用。</summary>
    public bool CanStop => IsRunning && !IsBusy && !IsUpdating;

    /// <summary>账号可以在服务运行中调整，启停或配置切换期间禁用。</summary>
    public bool CanManageAccounts => !IsBusy && !IsUpdating;

    /// <summary>获取清除记录按钮是否可用。</summary>
    public bool CanClearRecords => !IsBusy && ActiveRequests == 0;

    /// <summary>获取全部网络请求。</summary>
    public IReadOnlyList<CaptureRecord> Requests { get; private set; } = [];

    /// <summary>获取全部对话。</summary>
    public IReadOnlyList<SessionSummary> Sessions { get; private set; } = [];

    /// <summary>获取所选对话中的消息。</summary>
    public IReadOnlyList<MessageEntry> SessionMessages => SelectedSession?.Messages ?? [];

    /// <summary>获取所选请求的网络消息。</summary>
    public IReadOnlyList<NetworkMessage> NetworkMessages => SelectedRequest?.NetworkMessages ?? [];

    /// <summary>获取或设置所选对话。</summary>
    public SessionSummary? SelectedSession
    {
        get => selectedSession;
        set
        {
            var previous = SelectedMessage;
            SetField(ref selectedSession, value);
            OnPropertyChanged(nameof(SessionMessages));
            SelectedMessage = FindMatchingMessage(previous) ?? SessionMessages.FirstOrDefault();
        }
    }

    /// <summary>获取或设置所选网络请求。</summary>
    public CaptureRecord? SelectedRequest
    {
        get => selectedRequest;
        set
        {
            string? previousId = selectedRequest?.Id;
            int? sequence = SelectedNetworkMessage?.Sequence;
            SetField(ref selectedRequest, value);
            OnPropertyChanged(nameof(NetworkMessages));
            OnPropertyChanged(nameof(RequestDetails));
            SelectedNetworkMessage = previousId == value?.Id
                ? NetworkMessages.FirstOrDefault(item => item.Sequence == sequence) ?? NetworkMessages.FirstOrDefault()
                : NetworkMessages.FirstOrDefault();
        }
    }

    /// <summary>获取或设置所选对话条目。</summary>
    public MessageEntry? SelectedMessage
    {
        get => selectedMessage;
        set
        {
            SetField(ref selectedMessage, value);
            OnPropertyChanged(nameof(SelectedMessageDetails));
        }
    }

    /// <summary>消息详情标题中的请求 ID 和上游账号。</summary>
    public string SelectedMessageDetails
    {
        get
        {
            if (SelectedMessage is null) return "";
            var request = Requests.FirstOrDefault(item => item.Id == SelectedMessage.RequestId);
            return request is null ? $"请求 ID：{SelectedMessage.RequestId}" :
                $"请求 ID：{request.Id}    上游：{request.UpstreamName}\n{request.ImageSummary}".TrimEnd();
        }
    }

    /// <summary>获取或设置所选网络消息。</summary>
    public NetworkMessage? SelectedNetworkMessage
    {
        get => selectedNetworkMessage;
        set => SetField(ref selectedNetworkMessage, value);
    }

    /// <summary>获取请求详情与分析提示。</summary>
    public string RequestDetails => SelectedRequest is { } request
        ? $"{request.Method} {request.UpstreamUrl}\n请求 ID：{request.Id}    会话：{request.SessionId}    对话：{request.ThreadId}    上游：{request.UpstreamName}\n"
            + $"状态：{request.Status}    HTTP：{request.StatusCode}    总耗时：{request.DurationMilliseconds:N0} ms    "
            + $"首字节：{(request.FirstByteMilliseconds is { } firstByte ? $"{firstByte:N0} ms" : "未收到")}    "
            + $"首响应：{(request.FirstResponseMilliseconds is { } firstResponse ? $"{firstResponse:N0} ms" : "未收到")}\n"
            + $"输入 Token：{request.InputTokens}    输出 Token：{request.OutputTokens}\n{request.ImageSummary}\n{request.Error}\n{request.CloseReason}\n{request.AnalysisNote}".TrimEnd()
        : "";

    /// <summary>获取请求总数。</summary>
    public int TotalRequests => Requests.Count;

    /// <summary>获取成功请求数。</summary>
    public int SuccessfulRequests => Requests.Count(request => request.IsSuccess);

    /// <summary>获取失败请求数。</summary>
    public int FailedRequests => Requests.Count(request => request.IsCompleted && request.IsFailure);

    /// <summary>获取进行中的请求数。</summary>
    public int ActiveRequests => Requests.Count(request => !request.IsCompleted);

    /// <summary>获取进行中请求和活动账号的状态栏摘要。</summary>
    public string ActiveRequestSummary
    {
        get
        {
            var active = Requests.Where(request => !request.IsCompleted).ToArray();
            return active.Length == 0
                ? ""
                : $"进行中请求  {active.Length:N0}    活动账号  {active.Select(request => request.UpstreamId).Distinct(StringComparer.Ordinal).Count():N0}";
        }
    }

    /// <summary>获取已结束请求的平均传输耗时。</summary>
    public string AverageDuration => Requests.Where(request => request.IsCompleted)
        .Select(request => request.DurationMilliseconds).DefaultIfEmpty().Average().ToString("N0") + " ms";

    /// <summary>获取已报告的输入 Token 合计。</summary>
    public string InputTokens => Requests.Sum(request => request.Usage?.InputTokenCount ?? 0).ToString("N0");

    /// <summary>获取已报告的输出 Token 合计。</summary>
    public string OutputTokens => Requests.Sum(request => request.Usage?.OutputTokenCount ?? 0).ToString("N0");

    /// <summary>获取已报告的总 Token 合计。</summary>
    public string TotalTokens => Requests.Sum(request => request.Usage?.TotalTokenCount ?? 0).ToString("N0");

    /// <summary>获取 Token 用量覆盖情况。</summary>
    public string UsageCoverage => $"已报告用量：{Requests.Count(request => request.Usage is not null)} / {TotalRequests} 个请求";

    /// <summary>生图请求与完整图片统计，局部预览不计入数量。</summary>
    public string ImageStatistics
    {
        get
        {
            var images = Requests.Where(request => request.HasImageActivity).ToArray();
            if (images.Length == 0) return "";
            var timings = images.Where(request => request.ImageCompletionMilliseconds is not null)
                .Select(request => request.ImageCompletionMilliseconds!.Value).ToArray();
            string average = timings.Length > 0 ? $"{timings.Average() / 1000:N1} s" : "未知";
            return $"生图请求：{images.Length}    完整图片：{images.Sum(request => request.GeneratedImageCount)} 张    平均图片就绪：{average}    已报告生图用量：{images.Count(request => request.ImageUsage is not null)} / {images.Length}";
        }
    }

    /// <summary>
    /// 清空内存中的请求、对话、消息、网络事件和统计结果。
    /// </summary>
    public void ClearRecords()
    {
        store.Clear();
        Refresh();
    }

    /// <summary>
    /// 仅在记录改变时刷新聚合结果，并保持已有的列表选择。
    /// </summary>
    public void Refresh()
    {
        long version = store.Version;
        if (version == loadedVersion)
        {
            return;
        }

        loadedVersion = version;
        string? sessionId = SelectedSession?.Id;
        string? requestId = SelectedRequest?.Id;
        var message = SelectedMessage;
        int? networkSequence = SelectedNetworkMessage?.Sequence;
        Requests = store.GetRecords();
        var requestsByAccount = Requests.ToLookup(request => request.UpstreamId);
        foreach (var row in Upstreams)
            row.UpdateStatistics(requestsByAccount[row.Account.Id]);
        Sessions = Requests.GroupBy(request => request.ThreadId).Select(group => new SessionSummary
        {
            Id = group.Key,
            CreatedAt = group.Min(request => request.StartedAt),
            LastActivity = group.Max(request => request.StartedAt),
            // 请求通常会重复携带完整历史；最新请求是该对话的权威快照，避免历史消息重复展示。
            Messages = group.OrderByDescending(request => request.StartedAt).First().Messages.ToArray()
        }).OrderByDescending(session => session.LastActivity).ToArray();

        OnPropertyChanged(nameof(Requests));
        OnPropertyChanged(nameof(SelectedMessageDetails));
        OnPropertyChanged(nameof(Sessions));
        SelectedSession = Sessions.FirstOrDefault(session => session.Id == sessionId) ?? Sessions.FirstOrDefault();
        SelectedRequest = Requests.FirstOrDefault(request => request.Id == requestId) ?? Requests.FirstOrDefault();
        SelectedMessage = FindMatchingMessage(message) ?? SessionMessages.FirstOrDefault();
        SelectedNetworkMessage = NetworkMessages.FirstOrDefault(item => item.Sequence == networkSequence)
            ?? NetworkMessages.FirstOrDefault();
        foreach (string property in new[] { nameof(TotalRequests), nameof(SuccessfulRequests), nameof(FailedRequests),
            nameof(ActiveRequests), nameof(ActiveRequestSummary), nameof(CanClearRecords), nameof(AverageDuration), nameof(InputTokens), nameof(OutputTokens), nameof(TotalTokens), nameof(UsageCoverage), nameof(ImageStatistics) })
        {
            OnPropertyChanged(property);
        }
    }

    private MessageEntry? FindMatchingMessage(MessageEntry? previous)
        => previous is null ? null : SessionMessages.FirstOrDefault(item => ReferenceEquals(item, previous)
            || previous.ImageKey.Length > 0 && item.RequestId == previous.RequestId && item.ImageKey == previous.ImageKey);

    /// <summary>
    /// 通知服务启停相关控件更新可用状态。
    /// </summary>
    private void NotifyServiceControls()
    {
        OnPropertyChanged(nameof(CanEditSettings));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanStop));
        OnPropertyChanged(nameof(CanManageAccounts));
        OnPropertyChanged(nameof(CanClearRecords));
        OnPropertyChanged(nameof(CanInstallUpdate));
    }

    /// <summary>
    /// 更新绑定字段并通知界面。
    /// </summary>
    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (!EqualityComparer<T>.Default.Equals(field, value))
        {
            field = value;
            OnPropertyChanged(propertyName);
        }
    }

    /// <summary>
    /// 为指定属性发出变更通知。
    /// </summary>
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
