using System.ClientModel.Primitives;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dom2API.Models;
using Microsoft.Extensions.AI;
using OpenAI.Chat;
using OpenAI.Responses;

// 固定依赖版本，并仅在协议适配边界使用经确认的官方实验性转换方法。
#pragma warning disable OPENAI001

namespace Dom2API.Services;

/// <summary>
/// 通过 OpenAI SDK 和 Microsoft.Extensions.AI 转换捕获的协议对象。
/// </summary>
internal sealed class ProtocolAnalyzer
{
    private static readonly ModelReaderWriterOptions WireOptions = new("W");

    /// <summary>
    /// 解析输入消息、模型和工具声明；失败时保留原始网络内容。
    /// </summary>
    public void AnalyzeRequest(CaptureRecord record, string body, string sessionIdHeader, string xSessionIdHeader,
        string threadIdHeader, string xThreadIdHeader)
    {
        record.SessionId = FirstNonEmpty(sessionIdHeader, xSessionIdHeader) ?? record.Id;
        record.ThreadId = FirstNonEmpty(threadIdHeader, xThreadIdHeader) ?? record.Id;
        if (string.IsNullOrWhiteSpace(body))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            record.Model = ReadString(root, "model");
            record.SessionId = FirstNonEmpty(
                sessionIdHeader,
                xSessionIdHeader,
                ReadSessionId(root, "metadata"),
                ReadSessionId(root, "client_metadata")) ?? record.Id;
            record.ThreadId = FirstNonEmpty(
                threadIdHeader,
                xThreadIdHeader,
                ReadSessionId(root, "metadata", "thread_id"),
                ReadSessionId(root, "client_metadata", "thread_id")) ?? record.Id;

            if (IsChatCompletions(record.Path) && root.TryGetProperty("messages", out var messages))
            {
                foreach (var message in messages.EnumerateArray())
                {
                    record.Messages.AddRange(MessageProjection.CreateProtocol(message, record.Id, "请求"));
                    try
                    {
                        var converted = new[] { ReadModel<OpenAI.Chat.ChatMessage>(message.GetRawText()) }
                            .AsChatMessages().Single();
                        record.InputMessages.Add(converted);
                    }
                    catch (Exception exception) when (IsParsingException(exception))
                    {
                        record.AnalysisNote = "部分输入消息未能转换为统一模型，已保留协议内容。";
                    }
                }

            }

            if (IsResponses(record.Path))
            {
                AnalyzeResponsesInput(record, root);
            }

            if (IsChatCompletions(record.Path) || IsResponses(record.Path))
            {
                AnalyzeTools(record, root);
            }
        }
        catch (Exception exception) when (IsParsingException(exception))
        {
            record.AnalysisNote = "请求结构化解析未完成：" + exception.Message;
        }
    }

    private static string? ReadSessionId(JsonElement root, string propertyName, string field = "session_id")
        => root.TryGetProperty(propertyName, out var metadata) ? ReadString(metadata, field) : null;

    private static string? FirstNonEmpty(params string?[] values)
        => values.Select(value => value?.Trim()).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    /// <summary>
    /// 使用 SDK 转换完整响应或 SSE 增量，并由统一抽象合并工具参数。
    /// </summary>
    public async Task AnalyzeResponseAsync(CaptureRecord record, string body, bool isEventStream)
    {
        if ((!IsChatCompletions(record.Path) && !IsResponses(record.Path))
            || record.StatusCode is not (>= 200 and < 300))
        {
            return;
        }

        try
        {
            bool hasProtocolMessages = ProjectResponseMessages(record, body, isEventStream);
            var responses = new List<ChatResponse>();
            if (IsResponses(record.Path))
            {
                responses.Add(await ReadResponsesResponseAsync(record, body, isEventStream).ConfigureAwait(false));
            }
            else if (isEventStream)
            {
                var groups = new SortedDictionary<int, List<string>>();
                string? usageEvent = null;
                foreach (var frame in record.NetworkMessages.Where(message => message.Kind.StartsWith("SSE", StringComparison.Ordinal)))
                {
                    if (frame.Content == "[DONE]" || string.IsNullOrWhiteSpace(frame.Content))
                    {
                        continue;
                    }

                    var node = JsonNode.Parse(frame.Content)?.AsObject();
                    if (node?["error"] is JsonObject error)
                    {
                        record.Error = "上游流错误：" + error.ToJsonString();
                    }
                    if (node?["usage"] is JsonObject)
                    {
                        usageEvent = frame.Content;
                    }

                    if (node?["choices"] is not JsonArray choices)
                    {
                        continue;
                    }

                    foreach (var choice in choices)
                    {
                        int index = choice?["index"]?.GetValue<int>() ?? 0;
                        if (!groups.TryGetValue(index, out var updates))
                        {
                            updates = [];
                            groups.Add(index, updates);
                        }

                        var single = (JsonObject)node.DeepClone();
                        single["choices"] = new JsonArray(choice?.DeepClone());
                        single["usage"] = null;
                        updates.Add(single.ToJsonString());
                    }
                }

                if (groups.Count == 0 && usageEvent is not null)
                {
                    groups.Add(0, []);
                }

                foreach (var group in groups.Values)
                {
                    if (usageEvent is not null)
                    {
                        var usage = JsonNode.Parse(usageEvent)!.AsObject();
                        usage["choices"] = new JsonArray();
                        group.Add(usage.ToJsonString());
                    }

                    responses.Add(await ReadChatUpdatesAsync(group).AsChatResponseUpdatesAsync()
                        .ToChatResponseAsync().ConfigureAwait(false));
                }
            }
            else
            {
                var root = JsonNode.Parse(body)?.AsObject();
                if (root?["choices"] is JsonArray choices)
                {
                    foreach (var choice in choices)
                    {
                        var single = (JsonObject)root.DeepClone();
                        single["choices"] = new JsonArray(choice?.DeepClone());
                        responses.Add(ReadModel<ChatCompletion>(single.ToJsonString()).AsChatResponse());
                    }
                }
            }

            foreach (var response in responses)
            {
                if (!hasProtocolMessages)
                {
                    record.Messages.AddRange(MessageProjection.Create(response.Messages, record.Id, "响应", IsResponses(record.Path)));
                }
                record.Usage ??= response.Usage;
                record.Response ??= response;
                if (!string.IsNullOrEmpty(response.ModelId))
                {
                    record.Model = response.ModelId;
                }
            }
        }
        catch (Exception exception) when (IsParsingException(exception))
        {
            record.AnalysisNote += "\n响应结构化解析未完成，原始数据仍可在网络界面查看：" + exception.Message;
        }
    }

    /// <summary>
    /// 转换 Responses 的指令、简写文本、消息数组和工具交互项。
    /// </summary>
    private static void AnalyzeResponsesInput(CaptureRecord record, JsonElement root)
    {
        string instructions = ReadString(root, "instructions");
        if (instructions.Length > 0)
        {
            AddInputMessages(record, [new Microsoft.Extensions.AI.ChatMessage(ChatRole.System, instructions)]);
        }

        if (!root.TryGetProperty("input", out var input))
        {
            return;
        }

        if (input.ValueKind == JsonValueKind.String)
        {
            AddInputMessages(record, [new Microsoft.Extensions.AI.ChatMessage(ChatRole.User, input.GetString())]);
        }
        else if (input.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in input.EnumerateArray())
            {
                record.Messages.AddRange(MessageProjection.CreateProtocol(item, record.Id, "请求"));
                try
                {
                    var node = JsonNode.Parse(item.GetRawText())!.AsObject();
                    if (node["role"] is not null && node["type"] is null)
                    {
                        node["type"] = "message";
                    }

                    if (node["type"]?.GetValue<string>() == "message"
                        && node["content"] is JsonValue content && content.TryGetValue<string>(out var text))
                    {
                        record.InputMessages.Add(new Microsoft.Extensions.AI.ChatMessage(
                            new ChatRole(ReadString(item, "role")), text));
                    }
                    else
                    {
                        var converted = new[] { ReadModel<ResponseItem>(node.ToJsonString()) }.AsChatMessages().ToArray();
                        if (converted.Length == 0)
                        {
                            record.AnalysisNote = "部分输入项未能转换为统一模型，已保留协议内容。";
                        }
                        else
                        {
                            foreach (var message in converted)
                            {
                                record.InputMessages.Add(message);
                            }
                        }
                    }
                }
                catch (Exception exception) when (IsParsingException(exception))
                {
                    record.AnalysisNote = "部分输入项未能转换为统一模型，已保留协议内容。";
                }
            }
        }
    }

    /// <summary>
    /// 将统一输入消息同时添加到结构化记录和界面内容。
    /// </summary>
    private static void AddInputMessages(CaptureRecord record, IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages)
    {
        foreach (var message in messages)
        {
            record.InputMessages.Add(message);
            record.Messages.AddRange(MessageProjection.Create([message], record.Id, "请求"));
        }
    }

    /// <summary>
    /// 优先投影完整响应中的原始角色和类型，流式响应缺少终结消息时由统一模型负责合并。
    /// </summary>
    private static bool ProjectResponseMessages(CaptureRecord record, string body, bool isEventStream)
    {
        string? finalResponse = isEventStream ? null : body;
        if (isEventStream && IsResponses(record.Path))
        {
            foreach (var frame in record.NetworkMessages.Where(message => message.Kind.StartsWith("SSE", StringComparison.Ordinal)))
            {
                if (frame.Content == "[DONE]" || string.IsNullOrWhiteSpace(frame.Content))
                {
                    continue;
                }

                using var document = JsonDocument.Parse(frame.Content);
                var root = document.RootElement;
                if (ReadString(root, "type") is "response.completed" or "response.failed" or "response.incomplete"
                    && root.TryGetProperty("response", out var response))
                {
                    finalResponse = response.GetRawText();
                }
            }
        }

        if (finalResponse is null)
        {
            return false;
        }

        using var finalDocument = JsonDocument.Parse(finalResponse);
        var finalRoot = finalDocument.RootElement;
        if (IsResponses(record.Path) && finalRoot.TryGetProperty("output", out var output)
            && output.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in output.EnumerateArray())
            {
                record.Messages.AddRange(MessageProjection.CreateProtocol(item, record.Id, "响应"));
            }

            return true;
        }

        if (IsChatCompletions(record.Path) && finalRoot.TryGetProperty("choices", out var choices)
            && choices.ValueKind == JsonValueKind.Array)
        {
            foreach (var choice in choices.EnumerateArray())
            {
                if (choice.TryGetProperty("message", out var message))
                {
                    record.Messages.AddRange(MessageProjection.CreateProtocol(message, record.Id, "响应"));
                }
            }

            return true;
        }

        return false;
    }

    /// <summary>
    /// 优先使用 Responses 终结事件的完整响应，缺少终结事件时保留已捕获增量。
    /// </summary>
    private static async Task<ChatResponse> ReadResponsesResponseAsync(CaptureRecord record, string body, bool isEventStream)
    {
        string? finalResponse = isEventStream ? null : body;
        var updates = new List<string>();
        if (isEventStream)
        {
            foreach (var frame in record.NetworkMessages.Where(message => message.Kind.StartsWith("SSE", StringComparison.Ordinal)))
            {
                if (frame.Content == "[DONE]" || string.IsNullOrWhiteSpace(frame.Content))
                {
                    continue;
                }

                using var document = JsonDocument.Parse(frame.Content);
                var root = document.RootElement;
                string type = ReadString(root, "type");
                if (type is "response.completed" or "response.failed" or "response.incomplete"
                    && root.TryGetProperty("response", out var response))
                {
                    finalResponse = response.GetRawText();
                }

                if (type == "error")
                {
                    record.Error = "上游流错误：" + frame.Content;
                }

                updates.Add(frame.Content);
            }
        }

        if (finalResponse is not null)
        {
            using var document = JsonDocument.Parse(finalResponse);
            if (ReadString(document.RootElement, "status") == "failed")
            {
                record.Error = "上游响应失败：" + (document.RootElement.TryGetProperty("error", out var error)
                    ? error.GetRawText() : "未提供错误详情");
            }

            return ReadModel<ResponseResult>(finalResponse).AsChatResponse();
        }

        record.AnalysisNote += "\n未捕获 Responses 终结事件，以下内容由已接收增量合并。";
        return await ReadResponsesUpdatesAsync(updates).AsChatResponseUpdatesAsync().ToChatResponseAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// 将 Responses SSE JSON 交给官方 SDK 读取。
    /// </summary>
    private static async IAsyncEnumerable<StreamingResponseUpdate> ReadResponsesUpdatesAsync(IEnumerable<string> frames)
    {
        foreach (string frame in frames)
        {
            yield return ReadModel<StreamingResponseUpdate>(frame);
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// 将函数声明转换为不可执行的统一工具模型并生成显示内容。
    /// </summary>
    private static void AnalyzeTools(CaptureRecord record, JsonElement root)
    {
        if (!root.TryGetProperty("tools", out var tools) || tools.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var tool in tools.EnumerateArray())
        {
            var function = tool.TryGetProperty("function", out var nested) ? nested : tool;
            string name = ReadString(function, "name");
            string description = ReadString(function, "description");
            if (ReadString(tool, "type") == "function" && function.TryGetProperty("parameters", out var parameters))
            {
                var declaration = AIFunctionFactory.CreateDeclaration(name, description, parameters.Clone());
                record.Messages.Add(MessageProjection.CreateEntry(tool, record.Id, "请求", "", "tool_declaration", declaration.Name));
            }
            else
            {
                record.Messages.Add(MessageProjection.CreateEntry(tool, record.Id, "请求", "", "tool_declaration"));
            }
        }
    }

    /// <summary>
    /// 根据协议路径识别 Chat Completions 调用。
    /// </summary>
    private static bool IsChatCompletions(string path)
        => path.Split('?')[0].TrimEnd('/').EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 根据协议路径识别 Responses 调用。
    /// </summary>
    private static bool IsResponses(string path)
        => path.Split('?')[0].TrimEnd('/').EndsWith("/responses", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 安全读取对象中的可选字符串字段。
    /// </summary>
    private static string ReadString(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    /// <summary>
    /// 使用 OpenAI SDK 的线协议反序列化器读取对象。
    /// </summary>
    private static T ReadModel<T>(string json)
        => ModelReaderWriter.Read<T>(BinaryData.FromString(json), WireOptions)!;

    /// <summary>
    /// 将捕获的 SSE JSON 转换为 SDK 增量序列。
    /// </summary>
    private static async IAsyncEnumerable<StreamingChatCompletionUpdate> ReadChatUpdatesAsync(IEnumerable<string> frames)
    {
        foreach (string frame in frames)
        {
            yield return ReadModel<StreamingChatCompletionUpdate>(frame);
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// 判断异常是否属于可降级处理的协议解析错误。
    /// </summary>
    private static bool IsParsingException(Exception exception)
        => exception is not (OutOfMemoryException or OperationCanceledException);
}

#pragma warning restore OPENAI001
