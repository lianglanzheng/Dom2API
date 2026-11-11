namespace Dom2API.Models;

/// <summary>
/// 表示从 AI 消息中提取的一项可读内容。
/// </summary>
public sealed class MessageEntry
{
    /// <summary>获取或设置所属请求标识。</summary>
    public string RequestId { get; set; } = "";

    /// <summary>获取或设置原始协议消息的本地标识，同一消息的多个内容项共享此标识。</summary>
    public Guid SourceMessageId { get; set; }

    /// <summary>获取或设置消息方向。</summary>
    public string Direction { get; set; } = "";

    /// <summary>获取或设置角色。</summary>
    public string Role { get; set; } = "";

    /// <summary>获取或设置内容类型。</summary>
    public string Kind { get; set; } = "";

    /// <summary>获取或设置工具名或消息标识。</summary>
    public string Name { get; set; } = "";

    /// <summary>获取或设置完整内容。</summary>
    public string Content { get; set; } = "";

    /// <summary>关联图片；预览控件按需加载，原始协议内容继续保留。</summary>
    public IReadOnlyList<ImageAttachment> Images { get; set; } = [];

    /// <summary>同一流式图片的稳定标识，用于替换预览并保持界面选择。</summary>
    internal string ImageKey { get; init; } = "";

    /// <summary>获取原始内容的第一行，不限制字符数或追加省略号。</summary>
    public string Preview
    {
        get
        {
            if (Images.Count > 0) return Name.Length > 0 ? Name : $"图片（{Images.Count} 张）";
            int lineEnd = Content.AsSpan().IndexOfAny('\r', '\n');
            return lineEnd >= 0 ? Content[..lineEnd] : Content;
        }
    }
}
