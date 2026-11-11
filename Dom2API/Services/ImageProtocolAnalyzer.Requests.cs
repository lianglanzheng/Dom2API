using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dom2API.Models;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;

namespace Dom2API.Services;

/// <summary>解析 Images API 的显示内容，与请求透传和调度分离。</summary>
internal static partial class ImageProtocolAnalyzer
{
    public static bool IsImagePath(string path)
    {
        string route = path.Split('?')[0].TrimEnd('/');
        return new[] { "/images/generations", "/images/edits", "/images/variations" }
            .Any(suffix => route.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
    }

    public static async Task<string> ReadRequestAsync(byte[] bytes, string text, string? contentType,
        IEnumerable<string> encodings, CancellationToken cancellationToken)
    {
        if (!MediaTypeHeaderValue.TryParse(contentType, out var mediaType)
            || !mediaType.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase)) return text;
        try
        {
            string? boundary = HeaderUtilities.RemoveQuotes(mediaType.Boundary).Value;
            if (string.IsNullOrEmpty(boundary)) return text;
            using var source = new MemoryStream(bytes, writable: false);
            using var decoded = CaptureFormatting.DecodeStream(source, encodings);
            var reader = new MultipartReader(boundary, decoded);
            var root = new JsonObject();
            var images = new JsonArray();
            while (await reader.ReadNextSectionAsync(cancellationToken).ConfigureAwait(false) is { } section)
            {
                if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition)) continue;
                string name = HeaderUtilities.RemoveQuotes(disposition.Name).Value ?? "";
                if (disposition.IsFormDisposition())
                    root[name] = await section.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                else if (disposition.IsFileDisposition())
                {
                    using var file = new MemoryStream();
                    await section.Body.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
                    string filename = HeaderUtilities.RemoveQuotes(disposition.FileNameStar.HasValue
                        ? disposition.FileNameStar : disposition.FileName).Value ?? name;
                    string mime = section.ContentType?.Split(';')[0] ?? "application/octet-stream";
                    var image = new JsonObject
                    {
                        ["field"] = name, ["filename"] = filename,
                        ["image_url"] = $"data:{mime};base64,{Convert.ToBase64String(file.ToArray())}"
                    };
                    if (name == "mask") root["mask"] = image;
                    else images.Add(image);
                }
            }
            if (images.Count > 0) root["images"] = images;
            return root.ToJsonString();
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or NotSupportedException)
        {
            return text;
        }
    }

    public static void AnalyzeRequest(CaptureRecord record, JsonElement root)
    {
        Guid sourceId = Guid.NewGuid();
        string prompt = ReadString(root, "prompt");
        if (prompt.Length > 0)
            record.Messages.Add(new MessageEntry
            {
                RequestId = record.Id, SourceMessageId = sourceId, Direction = "请求", Role = "user",
                Kind = "text", Name = "生图提示词", Content = prompt
            });
        var parameters = new JsonObject();
        foreach (var property in root.EnumerateObject())
        {
            if (property.Name == "prompt") continue;
            if (property.Name is "image" or "image[]" or "images" or "mask")
            {
                var images = ReadInputImages(property.Value, property.Name).ToArray();
                record.Messages.Add(new MessageEntry
                {
                    RequestId = record.Id, SourceMessageId = sourceId, Direction = "请求", Role = "user",
                    Kind = property.Name == "mask" ? "image_mask" : "input_image",
                    Name = property.Name == "mask" ? "编辑遮罩" : $"输入图片（{images.Length} 张）",
                    Content = property.Value.GetRawText(), Images = images
                });
            }
            else parameters[property.Name] = JsonNode.Parse(property.Value.GetRawText());
        }
        record.Messages.Add(new MessageEntry
        {
            RequestId = record.Id, SourceMessageId = sourceId, Direction = "请求", Kind = "image_parameters",
            Name = "生图参数", Content = parameters.ToJsonString()
        });
    }

    public static IReadOnlyList<ImageAttachment> ReadInputAttachments(JsonElement value)
        => ReadInputImages(value, "输入图片").ToArray();

    private static IEnumerable<ImageAttachment> ReadInputImages(JsonElement value, string name)
    {
        if (value.ValueKind == JsonValueKind.Array)
        {
            int index = 0;
            foreach (var item in value.EnumerateArray())
                foreach (var image in ReadInputImages(item, $"{name} {++index}")) yield return image;
        }
        else if (value.ValueKind == JsonValueKind.String)
        {
            string source = value.GetString() ?? "";
            if (source.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                || Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
                yield return new ImageAttachment(name, source);
        }
        else if (value.ValueKind == JsonValueKind.Object)
        {
            string filename = ReadString(value, "filename");
            if (filename.Length > 0) name = filename;
            foreach (string key in new[] { "image_url", "url" })
                if (value.TryGetProperty(key, out var source))
                    foreach (var image in ReadInputImages(source, name)) yield return image;
        }
    }

    private static string ReadString(JsonElement value, string name)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.String ? property.GetString() ?? "" : "";
}
