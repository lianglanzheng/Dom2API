using System.IO;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;

namespace Dom2API.Services;

/// <summary>从捕获副本读取模型，不消费或改写实际转发的请求正文。</summary>
internal static class RequestModelReader
{
    public static async Task<string?> ReadAsync(byte[] bytes, string text, string? contentType,
        IEnumerable<string> encodings, CancellationToken cancellationToken)
    {
        try
        {
            if (MediaTypeHeaderValue.TryParse(contentType, out var mediaType)
                && mediaType.MediaType.Equals("multipart/form-data", StringComparison.OrdinalIgnoreCase))
            {
                string? boundary = HeaderUtilities.RemoveQuotes(mediaType.Boundary).Value;
                if (string.IsNullOrEmpty(boundary)) return null;
                using var source = new MemoryStream(bytes, writable: false);
                using var decoded = CaptureFormatting.DecodeStream(source, encodings);
                var reader = new MultipartReader(boundary, decoded);
                while (await reader.ReadNextSectionAsync(cancellationToken).ConfigureAwait(false) is { } section)
                {
                    if (ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition)
                        && disposition.IsFormDisposition()
                        && HeaderUtilities.RemoveQuotes(disposition.Name).Value == "model")
                        return await section.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    await section.Body.CopyToAsync(Stream.Null, cancellationToken).ConfigureAwait(false);
                }
                return null;
            }
            using var document = JsonDocument.Parse(text);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.String
                ? model.GetString() : null;
        }
        catch (Exception exception) when (exception is JsonException or IOException or ArgumentException or NotSupportedException)
        {
            // 协议解析失败不影响透传；保留原有“未指定模型”的行为。
            return null;
        }
    }
}
