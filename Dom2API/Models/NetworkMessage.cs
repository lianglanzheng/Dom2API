namespace Dom2API.Models;

/// <summary>
/// 表示请求流中的 HTTP 内容或 SSE 事件。
/// </summary>
public sealed class NetworkMessage
{
    /// <summary>获取或设置消息序号。</summary>
    public int Sequence { get; set; }

    /// <summary>获取或设置捕获时间。</summary>
    public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.Now;

    /// <summary>获取或设置传输方向。</summary>
    public string Direction { get; set; } = "";

    /// <summary>获取请求流列表中的方向文字。</summary>
    public string DirectionText => Direction switch
    {
        "下游 → 上游" => "请求",
        "上游 → 下游" => "响应",
        _ => Direction
    };

    /// <summary>获取或设置消息类型。</summary>
    public string Kind { get; set; } = "";

    /// <summary>获取或设置消息内容。</summary>
    public string Content { get; set; } = "";

    /// <summary>获取原始内容的第一行，不限制字符数或追加省略号。</summary>
    public string Preview
    {
        get
        {
            int lineEnd = Content.AsSpan().IndexOfAny('\r', '\n');
            return lineEnd >= 0 ? Content[..lineEnd] : Content;
        }
    }
}
