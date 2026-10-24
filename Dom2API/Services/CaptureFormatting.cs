using System.IO;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text;

namespace Dom2API.Services;

/// <summary>
/// 使用标准编码与压缩组件将捕获内容转换为可读文本。
/// </summary>
internal static class CaptureFormatting
{
    /// <summary>
    /// 按照 HTTP 内容编码创建解压缩读取流。
    /// </summary>
    public static Stream DecodeStream(Stream stream, IEnumerable<string> encodings)
    {
        foreach (string encoding in encodings.Reverse())
        {
            stream = encoding.Trim().ToLowerInvariant() switch
            {
                "gzip" => new GZipStream(stream, CompressionMode.Decompress, leaveOpen: true),
                "deflate" => new ZLibStream(stream, CompressionMode.Decompress, leaveOpen: true),
                "br" => new BrotliStream(stream, CompressionMode.Decompress, leaveOpen: true),
                "identity" or "" => stream,
                _ => throw new NotSupportedException($"无法解析内容编码：{encoding}。")
            };
        }

        return stream;
    }

    /// <summary>
    /// 解码已完整捕获的响应，二进制或无效文本保留为 Base64。
    /// </summary>
    public static string BodyToText(byte[] bytes, string? contentType, IEnumerable<string> encodings)
    {
        if (bytes.Length == 0)
        {
            return "";
        }

        try
        {
            using var source = new MemoryStream(bytes);
            using var decoded = DecodeStream(source, encodings);
            using var output = new MemoryStream();
            decoded.CopyTo(output);
            byte[] data = output.ToArray();
            if (contentType is not null && !contentType.Contains("json", StringComparison.OrdinalIgnoreCase)
                && !contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
                && !contentType.Contains("xml", StringComparison.OrdinalIgnoreCase))
            {
                return "Base64:\n" + Convert.ToBase64String(data);
            }

            var encoding = new UTF8Encoding(false, true);
            if (MediaTypeHeaderValue.TryParse(contentType, out var mediaType)
                && !string.IsNullOrWhiteSpace(mediaType.CharSet))
            {
                return Encoding.GetEncoding(mediaType.CharSet.Trim('"'), EncoderFallback.ExceptionFallback,
                    DecoderFallback.ExceptionFallback).GetString(data);
            }

            return encoding.GetString(data);
        }
        catch (Exception exception) when (exception is InvalidDataException or ArgumentException
            or NotSupportedException or IOException)
        {
            return "Base64（原始字节）:\n" + Convert.ToBase64String(bytes);
        }
    }

    /// <summary>
    /// 将请求头转换为可读文本，同时隐藏认证和 Cookie 值。
    /// </summary>
    public static string HeadersToText(IEnumerable<KeyValuePair<string, IEnumerable<string>>> headers)
    {
        return string.Join(Environment.NewLine, headers.Select(header =>
            $"{header.Key}: {(IsSensitiveHeader(header.Key) ? "[已隐藏]" : string.Join(", ", header.Value))}"));
    }

    /// <summary>
    /// 判断请求头是否含有认证或会话凭据。
    /// </summary>
    private static bool IsSensitiveHeader(string name)
    {
        return name.Contains("authorization", StringComparison.OrdinalIgnoreCase)
            || name.Contains("api-key", StringComparison.OrdinalIgnoreCase)
            || name.Contains("apikey", StringComparison.OrdinalIgnoreCase)
            || name.Equals("cookie", StringComparison.OrdinalIgnoreCase)
            || name.Equals("set-cookie", StringComparison.OrdinalIgnoreCase);
    }
}
