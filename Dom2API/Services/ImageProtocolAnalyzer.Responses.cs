using System.Text.Json;
using Dom2API.Models;

namespace Dom2API.Services;

internal static partial class ImageProtocolAnalyzer
{
    public static void AnalyzeResponse(CaptureRecord record, string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return;
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;
            if (ReadError(record, root)) return;
            if (record.StatusCode is not (>= 200 and < 300)) return;
            ReadImageUsage(record, root);
            string model = ReadString(root, "model");
            if (model.Length > 0) record.Model = model;
            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                int index = 0;
                foreach (var item in data.EnumerateArray())
                    AddResult(record, item, ++index, ReadString(root, "output_format"));
            }
        }
        catch (JsonException exception)
        {
            record.AnalysisNote += "\n生图响应解析未完成，原始响应可在网络页查看：" + exception.Message;
        }
    }

    private static bool ReadError(CaptureRecord record, JsonElement root)
    {
        if (!root.TryGetProperty("error", out var error) || error.ValueKind is JsonValueKind.Null or JsonValueKind.False) return false;
        string message = ReadString(error, "message");
        if (message.Length == 0) message = error.ValueKind == JsonValueKind.String ? error.GetString()! : error.GetRawText();
        if (record.Error.Length == 0) record.Error = "生图失败：" + message;
        record.ImageStatus = "生成失败";
        record.Messages.Add(new MessageEntry
        {
            RequestId = record.Id, SourceMessageId = Guid.NewGuid(), Direction = "响应", Kind = "image_error",
            Name = "生图错误", Content = error.GetRawText()
        });
        return true;
    }

    private static void AddResult(CaptureRecord record, JsonElement item, int index, string format)
    {
        if (item.ValueKind != JsonValueKind.Object) return;
        string name = $"生成图片 {index}";
        var images = ReadResultImages(item, name, format);
        string key = $"images:{index - 1}";
        SetImageMessage(record, key, name, item.GetRawText(), images, images.Count > 0);
        record.ImageStatus = images.Count > 0 ? "生成完成" : "未返回可识别图片";
        string prompt = ReadString(item, "revised_prompt");
        SetRevisedPrompt(record, key, prompt);
    }

    private static IReadOnlyList<ImageAttachment> ReadResultImages(JsonElement item, string name, string format)
    {
        string base64 = ReadString(item, "b64_json");
        if (base64.Length == 0) base64 = ReadString(item, "result");
        if (base64.Length > 0)
        {
            string ownFormat = ReadString(item, "output_format");
            if (ownFormat.Length > 0) format = ownFormat;
            string mime = format.ToLowerInvariant() switch { "jpeg" or "jpg" => "image/jpeg", "webp" => "image/webp", _ => "image/png" };
            return [new ImageAttachment(name, $"data:{mime};base64,{base64}")];
        }
        string url = ReadString(item, "url");
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            ? [new ImageAttachment(name, url)] : [];
    }
}
