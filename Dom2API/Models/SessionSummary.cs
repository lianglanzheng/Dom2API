namespace Dom2API.Models;

using System.Globalization;
using System.Text.RegularExpressions;

/// <summary>
/// 表示按明确对话 ID 聚合的一组请求。
/// </summary>
public sealed class SessionSummary
{
    /// <summary>获取对话标识。</summary>
    public required string Id { get; init; }

    /// <summary>获取本地首次捕获该对话请求的时间。</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>获取最新请求消息生成的本地预览标题。</summary>
    public string Title => CreateTitle(Messages);

    /// <summary>获取最近请求时间。</summary>
    public DateTimeOffset LastActivity { get; init; }

    /// <summary>获取最新请求去重后的完整消息条目。</summary>
    public IReadOnlyList<MessageEntry> Messages { get; init; } = [];

    /// <summary>选择最新请求中的第一批连续用户消息，合并其文本并按文本元素截断。</summary>
    private static string CreateTitle(IReadOnlyList<MessageEntry> messages)
    {
        Guid? sourceMessageId = null;
        string? requestId = null;
        var parts = new List<string>();
        foreach (var message in messages)
        {
            if (message.Direction != "请求" || message.Role != "user")
            {
                if (sourceMessageId is not null)
                {
                    break;
                }

                continue;
            }

            // 新请求可能重复携带历史消息，不能将两个请求中的用户消息拼成同一批。
            if (sourceMessageId is not null && requestId != message.RequestId)
            {
                break;
            }

            if (sourceMessageId != message.SourceMessageId)
            {
                parts.Clear();
                sourceMessageId = message.SourceMessageId;
                requestId = message.RequestId;
            }

            if (message.Kind is "text" or "input_text" or "output_text")
            {
                parts.Add(message.Content);
            }
        }

        string text = Regex.Replace(string.Join(" ", parts), @"\s+", " ").Trim();
        if (text.Length == 0)
        {
            return "未命名对话";
        }

        int[] boundaries = StringInfo.ParseCombiningCharacters(text);
        return boundaries.Length > 48 ? text[..boundaries[48]] + "…" : text;
    }
}
