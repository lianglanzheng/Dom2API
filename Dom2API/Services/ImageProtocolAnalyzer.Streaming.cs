using System.Text.Json;
using Dom2API.Models;

namespace Dom2API.Services;

internal static partial class ImageProtocolAnalyzer
{
    public static void AnalyzeEvent(CaptureRecord record, string eventType, string data)
    {
        bool isImages = IsImagePath(record.Path);
        if (!isImages && !IsResponsesPath(record.Path)) return;
        if (string.IsNullOrWhiteSpace(data) || data.Trim() == "[DONE]") return;
        try
        {
            using var document = JsonDocument.Parse(data);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;
            string type = ReadString(root, "type");
            if (type.Length == 0) type = eventType;
            if (isImages && ReadError(record, root)) return;
            if (type == "error" && (isImages || record.ImageStatus.Length > 0))
            {
                record.Error = "生图失败：" + (ReadString(root, "message") is { Length: > 0 } message ? message : data);
                record.ImageStatus = "生成失败";
                return;
            }
            if (isImages && (type.StartsWith("image_generation.", StringComparison.Ordinal)
                || type.StartsWith("image_edit.", StringComparison.Ordinal)))
            {
                int index = ReadIndex(root, "image_index");
                bool final = type.EndsWith(".completed", StringComparison.Ordinal);
                if (final) ReadImageUsage(record, root);
                if (final && root.TryGetProperty("data", out _))
                {
                    AnalyzeResponse(record, data);
                    return;
                }
                var images = ReadEventImages(root, $"生成图片 {index + 1}");
                bool hasFinal = final && images.Count > 0;
                string state = hasFinal ? "生成完成" : type.EndsWith(".partial_image", StringComparison.Ordinal)
                    ? $"局部预览 {ReadIndex(root, "partial_image_index") + 1}" : "生成中";
                SetImageMessage(record, $"images:{index}", hasFinal ? $"生成图片 {index + 1}" : state, data, images, hasFinal);
                record.ImageStatus = state;
                if (hasFinal) SetRevisedPrompt(record, $"images:{index}", ReadString(root, "revised_prompt"));
                return;
            }
            if (type.StartsWith("response.image_generation_call.", StringComparison.Ordinal))
            {
                string key = ResponseKey(root, ReadIndex(root, "output_index"), "item_id");
                string state = type.EndsWith(".partial_image", StringComparison.Ordinal)
                    ? $"局部预览 {ReadIndex(root, "partial_image_index") + 1}"
                    : type.EndsWith(".completed", StringComparison.Ordinal) ? "生成完成，等待图片结果" : "生成中";
                SetImageMessage(record, key, state, data, ReadEventImages(root, "生成图片预览"), false);
                record.ImageStatus = state;
            }
            else if (type is "response.output_item.added" or "response.output_item.done"
                && root.TryGetProperty("item", out var item))
            {
                AnalyzeResponseItem(record, item, ReadIndex(root, "output_index"));
            }
            else if (type is "response.completed" or "response.failed" or "response.incomplete"
                && root.TryGetProperty("response", out var response))
            {
                if (ReadUsage(response) is { } usage) record.Usage = usage;
                if (response.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
                {
                    int index = 0;
                    foreach (var result in output.EnumerateArray()) AnalyzeResponseItem(record, result, index++);
                }
                if (record.ImageStatus.Length > 0 && type != "response.completed")
                    record.ImageStatus = type == "response.failed" ? "生成失败" : "生成未完成";
            }
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
        {
            // 保留无法识别的原始 SSE，不影响正在进行的转发。
        }
    }

    public static bool AnalyzeResponseItem(CaptureRecord record, JsonElement item, int index)
    {
        if (ReadString(item, "type") != "image_generation_call") return false;
        string key = ResponseKey(item, index, "id");
        ReadToolUsage(record, key, item);
        var images = ReadResultImages(item, "生成图片", "");
        string status = ReadString(item, "status");
        if (status == "failed" && record.Error.Length == 0)
            record.Error = "生图工具执行失败：" + (item.TryGetProperty("error", out var error) ? error.GetRawText() : "上游未提供错误详情。");
        bool final = images.Count > 0 && status is not ("in_progress" or "generating" or "failed");
        string state = final ? "生成完成" : status == "failed" ? "生成失败" : "生成中";
        SetImageMessage(record, key, final ? "生成图片" : state, item.GetRawText(), images, final);
        SetRevisedPrompt(record, key, ReadString(item, "revised_prompt"));
        record.ImageStatus = state;
        return true;
    }

    public static void CompleteStream(CaptureRecord record)
    {
        if (record.ImageStatus.Length == 0 && !IsImagePath(record.Path)) return;
        bool incomplete = record.Messages.Any(message => message.ImageKey.Length > 0
            && message.Kind is "image_preview" or "image_progress");
        if (record.Error.Length == 0 && (incomplete || !record.Messages.Any(message => message.Kind == "output_image")))
        {
            record.ImageStatus = "流结束，未收到完整图片结果";
            record.AnalysisNote += "\n未收到全部最终图片，已保留可用的局部预览和网络事件。";
        }
    }

    private static void SetImageMessage(CaptureRecord record, string key, string name, string content,
        IReadOnlyList<ImageAttachment> images, bool final)
    {
        int index = record.Messages.FindIndex(message => message.ImageKey == key);
        var previous = index >= 0 ? record.Messages[index] : null;
        if (previous?.Kind == "output_image" && !final) return;
        if (final && previous?.Kind != "output_image") record.RecordImageCompletion();
        if (images.Count == 0 && previous is not null) images = previous.Images;
        var next = new MessageEntry
        {
            RequestId = record.Id, SourceMessageId = previous?.SourceMessageId ?? Guid.NewGuid(),
            Direction = "响应", Role = "assistant", ImageKey = key,
            Kind = final ? "output_image" : images.Count > 0 ? "image_preview" : "image_progress",
            Name = name, Content = content, Images = images
        };
        // 用替换对象发布快照，避免改变 UI 正在读取的旧消息。
        if (index >= 0) record.Messages[index] = next;
        else record.Messages.Add(next);
    }

    private static void SetRevisedPrompt(CaptureRecord record, string key, string prompt)
    {
        if (prompt.Length == 0) return;
        key += ":prompt";
        int index = record.Messages.FindIndex(message => message.ImageKey == key);
        var entry = new MessageEntry
        {
            RequestId = record.Id, SourceMessageId = index >= 0 ? record.Messages[index].SourceMessageId : Guid.NewGuid(),
            Direction = "响应", Role = "assistant", Kind = "text", ImageKey = key,
            Name = "修订后的提示词", Content = prompt
        };
        if (index >= 0) record.Messages[index] = entry;
        else record.Messages.Add(entry);
    }

    private static IReadOnlyList<ImageAttachment> ReadEventImages(JsonElement root, string name)
    {
        string partial = ReadString(root, "partial_image_b64");
        return partial.Length > 0 ? [new ImageAttachment(name, "data:image/png;base64," + partial)] : ReadResultImages(root, name, "");
    }

    private static string ResponseKey(JsonElement root, int index, string idProperty)
        => "response:" + (ReadString(root, idProperty) is { Length: > 0 } id ? id : $"index:{index}");

    private static int ReadIndex(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int index) ? index : 0;

    private static bool IsResponsesPath(string path)
        => path.Split('?')[0].TrimEnd('/').EndsWith("/responses", StringComparison.OrdinalIgnoreCase);
}
