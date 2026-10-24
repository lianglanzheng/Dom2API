using System.Text.Json;
using Dom2API.Models;
using System.ClientModel.Primitives;
using Microsoft.Extensions.AI;
using OpenAI.Chat;
using OpenAI.Responses;

// 与协议分析器使用相同的固定版本官方转换 API。
#pragma warning disable OPENAI001

namespace Dom2API.Services;

/// <summary>
/// 将原始协议及 Microsoft.Extensions.AI 合并后的消息投影为界面条目。
/// </summary>
internal static class MessageProjection
{
    /// <summary>
    /// 使用官方转换 API 将合并后的流消息还原为协议对象，再投影为可读条目。
    /// </summary>
    public static IEnumerable<MessageEntry> Create(IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, string requestId, string direction,
        bool isResponses = false)
    {
        IEnumerable<object> protocolMessages = isResponses ? messages.AsOpenAIResponseItems() : messages.AsOpenAIChatMessages();
        foreach (object message in protocolMessages)
        {
            using var document = JsonDocument.Parse(ModelReaderWriter.Write(message, new ModelReaderWriterOptions("W")).ToMemory());
            foreach (var entry in CreateProtocol(document.RootElement, requestId, direction))
            {
                yield return entry;
            }
        }
    }

    /// <summary>
    /// 从原始协议消息投影显示条目，保留角色与内容类型，包括 SDK 尚未识别的类型。
    /// </summary>
    public static IEnumerable<MessageEntry> CreateProtocol(JsonElement item, string requestId, string direction)
    {
        Guid sourceMessageId = Guid.NewGuid();
        string role = ReadString(item, "role");
        string name = string.Join(" · ", new[] { ReadString(item, "name"), ReadString(item, "tool_call_id") }
            .Where(part => part.Length > 0));
        string itemType = ReadString(item, "type");
        if (itemType.Length > 0 && itemType != "message")
        {
            yield return CreateEntry(item, requestId, direction, role, "message", name, sourceMessageId);
            yield break;
        }

        bool hasEntries = false;
        if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("content", out var content))
        {
            if (content.ValueKind == JsonValueKind.String)
            {
                hasEntries = true;
                yield return CreateEntry(content, requestId, direction, role, "text", name, sourceMessageId);
            }
            else if (content.ValueKind == JsonValueKind.Array)
            {
                foreach (var part in content.EnumerateArray())
                {
                    hasEntries = true;
                    yield return CreateEntry(part, requestId, direction, role, "content", name, sourceMessageId);
                }
            }
        }

        if (item.ValueKind == JsonValueKind.Object)
        {
            if (item.TryGetProperty("tool_calls", out var calls) && calls.ValueKind == JsonValueKind.Array)
            {
                foreach (var call in calls.EnumerateArray())
                {
                    hasEntries = true;
                    yield return CreateEntry(call, requestId, direction, role, "tool_call", name, sourceMessageId);
                }
            }

            foreach (string field in new[] { "function_call", "refusal", "audio" })
            {
                if (item.TryGetProperty(field, out var value) && value.ValueKind != JsonValueKind.Null)
                {
                    hasEntries = true;
                    yield return CreateEntry(value, requestId, direction, role, field, name, sourceMessageId);
                }
            }
        }

        if (!hasEntries)
        {
            yield return CreateEntry(item, requestId, direction, role, "message", name, sourceMessageId);
        }
    }

    /// <summary>
    /// 创建协议条目，未提供类型时使用回退名称，非文本内容保留完整 JSON。
    /// </summary>
    public static MessageEntry CreateEntry(JsonElement value, string requestId, string direction,
        string role, string fallbackKind, string name = "", Guid? sourceMessageId = null)
    {
        string kind = ReadString(value, "type");
        string itemName = ReadString(value, "name");
        if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("function", out var function))
        {
            itemName = ReadString(function, "name");
        }

        string id = ReadString(value, "call_id");
        if (id.Length == 0)
        {
            id = ReadString(value, "id");
        }

        string text = value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.GetRawText();
        if (kind is "text" or "input_text" or "output_text"
            && value.TryGetProperty("text", out var textValue) && textValue.ValueKind == JsonValueKind.String)
        {
            text = textValue.GetString() ?? "";
        }

        return new MessageEntry
        {
            RequestId = requestId,
            SourceMessageId = sourceMessageId ?? Guid.NewGuid(),
            Direction = direction,
            Role = role,
            Kind = kind.Length == 0 ? fallbackKind : kind,
            Name = string.Join(" · ", new[] { itemName.Length == 0 ? name : itemName, id }.Where(part => part.Length > 0)),
            Content = text
        };
    }

    /// <summary>
    /// 安全读取原始协议中可选的字符串属性。
    /// </summary>
    private static string ReadString(JsonElement value, string name)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.String ? property.GetString() ?? "" : "";
}

#pragma warning restore OPENAI001
