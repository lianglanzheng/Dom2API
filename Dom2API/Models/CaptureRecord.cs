using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using System.ComponentModel;
using System.Diagnostics;

namespace Dom2API.Models;

/// <summary>
/// 保存一次 API 调用的协议内容、结构化消息和统计数据。
/// </summary>
public sealed class CaptureRecord : INotifyPropertyChanged
{
    private readonly long startedTimestamp = Stopwatch.GetTimestamp();
    private double? lastResponseMilliseconds;
    private long? lastResponseTimestamp;

    /// <summary>通知界面刷新响应等待耗时。</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>获取或设置本地请求标识。</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>获取或设置会话聚合标识。</summary>
    public string SessionId { get; set; } = "";

    /// <summary>获取或设置对话标识。</summary>
    public string ThreadId { get; set; } = "";

    /// <summary>获取或设置请求开始时间。</summary>
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.Now;

    /// <summary>获取或设置 HTTP 方法。</summary>
    public string Method { get; set; } = "";

    /// <summary>获取或设置下游请求路径和查询字符串。</summary>
    public string Path { get; set; } = "";

    /// <summary>获取或设置上游请求地址。</summary>
    public string UpstreamUrl { get; set; } = "";

    /// <summary>此次尝试的上游账号标识。</summary>
    public string UpstreamId { get; set; } = "";

    /// <summary>请求发送时的账号名称和序号。</summary>
    public string UpstreamName { get; set; } = "";

    /// <summary>非上游故障导致的关闭说明。</summary>
    public string CloseReason { get; set; } = "";

    /// <summary>是否正常取消或由下游终止。</summary>
    [JsonIgnore]
    public bool IsClosed => CloseReason.Length > 0;

    /// <summary>是否发生上游传输或协议错误。</summary>
    [JsonIgnore]
    public bool IsFailure => !IsClosed && (Error.Length > 0 || StatusCode is >= 300);

    /// <summary>获取或设置请求的模型名称。</summary>
    public string Model { get; set; } = "";

    /// <summary>获取或设置 HTTP 响应状态码。</summary>
    public int? StatusCode { get; set; }

    /// <summary>获取或设置请求是否已经结束。</summary>
    public bool IsCompleted { get; set; }

    /// <summary>获取或设置传输耗时，单位为毫秒。</summary>
    public double DurationMilliseconds { get; set; }

    /// <summary>获取或设置首个响应内容字节的延迟。</summary>
    public double? FirstByteMilliseconds { get; set; }

    /// <summary>获取或设置首个非响应头事件的延迟。</summary>
    public double? FirstResponseMilliseconds { get; set; }

    /// <summary>获取或设置最近一次完整响应的间隔，首次为从请求开始到首响应的耗时。</summary>
    public double? ResponseMilliseconds { get; set; }

    /// <summary>获取或设置响应是否使用 SSE 事件流。</summary>
    public bool IsEventStream { get; set; }

    /// <summary>按请求开始后的累计耗时记录一个完整响应，保留首响应并更新最近响应间隔。</summary>
    public void RecordResponse(double elapsedMilliseconds)
    {
        FirstResponseMilliseconds ??= elapsedMilliseconds;
        ResponseMilliseconds = elapsedMilliseconds - (lastResponseMilliseconds ?? 0);
        lastResponseMilliseconds = elapsedMilliseconds;
        lastResponseTimestamp = Stopwatch.GetTimestamp();
    }

    /// <summary>获取或设置传输错误。</summary>
    public string Error { get; set; } = "";

    /// <summary>获取或设置结构化解析提示。</summary>
    public string AnalysisNote { get; set; } = "";

    /// <summary>生图工具当前进度或最终状态。</summary>
    public string ImageStatus { get; set; } = "";

    /// <summary>上游单独报告的生图用量，不与 Responses 请求总用量重复相加。</summary>
    public UsageDetails? ImageUsage { get; set; }

    /// <summary>按工具 ID 保存最新用量，终结事件重放时替换而不是累加。</summary>
    internal Dictionary<string, UsageDetails> ImageToolUsages { get; set; } = [];

    /// <summary>从请求开始到最后一张完整图片到达的耗时。</summary>
    public double? ImageCompletionMilliseconds { get; set; }

    /// <summary>记录图片就绪时间；不把局部预览算作完整图片。</summary>
    internal void RecordImageCompletion()
        => ImageCompletionMilliseconds = DurationMilliseconds > 0 ? DurationMilliseconds : Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds;

    /// <summary>完整返回的图片数量。</summary>
    [JsonIgnore]
    public int GeneratedImageCount => Messages.Where(message => message.Kind == "output_image").Sum(message => message.Images.Count);

    /// <summary>是否包含独立生图或 Responses 生图工具内容。</summary>
    [JsonIgnore]
    public bool HasImageActivity => ImageStatus.Length > 0 || Messages.Any(message => message.Kind is "image_parameters" or "image_generation");

    /// <summary>图片就绪耗时，不等同于上游纯计算耗时。</summary>
    [JsonIgnore]
    public string ImageCompletionText => ImageCompletionMilliseconds is { } milliseconds ? $"{milliseconds / 1000:N1} s" : "未知";

    /// <summary>图片数量、就绪耗时和生图用量，未报告部分明确显示未知。</summary>
    [JsonIgnore]
    public string ImageSummary => !HasImageActivity ? "" : $"完整图片：{GeneratedImageCount} 张    图片就绪：{ImageCompletionText}    {ImageStatus}\n"
        + $"生图 Token：输入 {ImageUsage?.InputTokenCount?.ToString("N0") ?? "未知"} / 输出 {ImageUsage?.OutputTokenCount?.ToString("N0") ?? "未知"} / 总计 {ImageUsage?.TotalTokenCount?.ToString("N0") ?? "未知"}"
        + (ImageUsage?.AdditionalCounts is { Count: > 0 }
            ? $"    图片 Token：输入 {ImageCountText("input_image_tokens")} / 输出 {ImageCountText("output_image_tokens")}" : "")
        + (ImageUsage?.AdditionalCounts?.ContainsKey("input_text_tokens") == true ? $"    输入文本 Token：{ImageCountText("input_text_tokens")}" : "")
        + (ImageToolUsages.Count > 0 ? $"    生图工具用量覆盖：{ImageToolUsages.Count} / {Messages.Count(message => message.ImageKey.StartsWith("response:", StringComparison.Ordinal) && message.Kind is "output_image" or "image_preview" or "image_progress")}" : "");

    private string ImageCountText(string name)
        => ImageUsage?.AdditionalCounts?.TryGetValue(name, out long count) == true ? count.ToString("N0") : "未知";

    /// <summary>获取或设置上游报告的用量；为空时表示未报告。</summary>
    public UsageDetails? Usage { get; set; }

    /// <summary>获取或设置结构化输入消息。</summary>
    [JsonIgnore]
    public IList<ChatMessage> InputMessages { get; set; } = [];

    /// <summary>获取或设置结构化响应。</summary>
    [JsonIgnore]
    public ChatResponse? Response { get; set; }

    /// <summary>获取或设置对话内容列表。</summary>
    public List<MessageEntry> Messages { get; set; } = [];

    /// <summary>获取或设置网络消息列表。</summary>
    public List<NetworkMessage> NetworkMessages { get; set; } = [];

    /// <summary>获取请求是否成功完成。</summary>
    [JsonIgnore]
    public bool IsSuccess => IsCompleted && !IsClosed && Error.Length == 0 && StatusCode is >= 200 and < 300;

    /// <summary>获取请求的显示状态。</summary>
    [JsonIgnore]
    public string Status => !IsCompleted ? "进行中" : IsFailure ? "错误" : IsClosed ? "关闭" : IsSuccess ? "成功" : "错误";

    /// <summary>获取合并响应状态码的列表状态，尚未收到状态码时仅显示状态。</summary>
    [JsonIgnore]
    public string StatusText => (IsSuccess ? "完成" : Status) + (StatusCode is { } code ? $" ({code})" : "");

    /// <summary>获取首字节耗时，等待时实时计时，结束仍未收到时显示总耗时并追加加号。</summary>
    [JsonIgnore]
    public string FirstByteText
    {
        get
        {
            double milliseconds = FirstByteMilliseconds ?? (IsCompleted
                ? DurationMilliseconds : Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds);
            return $"{milliseconds / 1000:N1} s" + (IsCompleted && FirstByteMilliseconds is null ? "+" : "");
        }
    }

    /// <summary>获取首响应耗时，等待时实时计时，结束仍未收到时显示总耗时并追加加号。</summary>
    [JsonIgnore]
    public string FirstResponseText
    {
        get
        {
            double milliseconds = FirstResponseMilliseconds ?? (IsCompleted
                ? DurationMilliseconds : Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds);
            return $"{milliseconds / 1000:N1} s" + (IsCompleted && FirstResponseMilliseconds is null ? "+" : "");
        }
    }

    /// <summary>获取当前响应等待时间；SSE 每条完整有效事件后重新计时，结束后保留最近完整间隔。</summary>
    [JsonIgnore]
    public string ResponseText
    {
        get
        {
            double milliseconds = !IsCompleted && (IsEventStream || ResponseMilliseconds is null)
                ? Stopwatch.GetElapsedTime(lastResponseTimestamp ?? startedTimestamp).TotalMilliseconds
                : ResponseMilliseconds ?? DurationMilliseconds;
            return $"{milliseconds / 1000:N1} s" + (IsCompleted && ResponseMilliseconds is null ? "+" : "");
        }
    }

    /// <summary>获取最近已完成响应的间隔说明，供实时等待期间查看。</summary>
    [JsonIgnore]
    public string ResponseToolTip => ResponseMilliseconds is { } milliseconds
        ? $"最近一次响应间隔：{milliseconds / 1000:N1} s"
            + (IsEventStream && !IsCompleted ? "\n当前数值为等待下一条有效 SSE 的耗时，完整有效事件到达后重新计时，心跳不重置。" : "")
        : IsCompleted ? "请求已结束，未收到完整响应。" : "正在等待首个完整响应。";

    /// <summary>刷新等待首字节、首响应和响应的实时耗时。</summary>
    public void RefreshTimings()
    {
        if (!IsCompleted)
        {
            if (FirstByteMilliseconds is null) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FirstByteText)));
            if (FirstResponseMilliseconds is null) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FirstResponseText)));
            if (IsEventStream || ResponseMilliseconds is null)
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ResponseText)));
        }
    }

    /// <summary>
    /// 仅在请求进行中且尚未收到首字节时通知界面更新等待耗时。
    /// </summary>
    public void RefreshFirstByte()
    {
        if (!IsCompleted && FirstByteMilliseconds is null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FirstByteText)));
        }
    }

    /// <summary>获取输入 Token 的显示值。</summary>
    [JsonIgnore]
    public string InputTokens => Usage?.InputTokenCount?.ToString("N0") ?? "未报告";

    /// <summary>获取输出 Token 的显示值。</summary>
    [JsonIgnore]
    public string OutputTokens => Usage?.OutputTokenCount?.ToString("N0") ?? "未报告";

    /// <summary>
    /// 创建只读消费者使用的集合快照，避免界面枚举正在变化的列表。
    /// </summary>
    public CaptureRecord Snapshot()
    {
        var result = (CaptureRecord)MemberwiseClone();
        result.PropertyChanged = null;
        result.Messages = [.. Messages];
        result.NetworkMessages = [.. NetworkMessages];
        return result;
    }
}
