using System.IO;
using System.Net.Http;
using System.Windows.Media.Imaging;
using Dom2API.Models;

namespace Dom2API.Services;

/// <summary>按需读取图片；远程地址不携带上游凭据、Cookie 或 Windows 凭据。</summary>
internal static class ImagePreviewLoader
{
    private static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        UseCookies = false, AllowAutoRedirect = false
    }) { Timeout = TimeSpan.FromSeconds(30) };

    public static async Task<byte[]> ReadAsync(ImageAttachment attachment, CancellationToken cancellationToken)
    {
        if (attachment.Source.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            int comma = attachment.Source.IndexOf(',');
            if (comma < 0 || !attachment.Source[..comma].EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
                throw new FormatException("图片 data URL 必须使用 Base64 编码。");
            return Convert.FromBase64String(attachment.Source[(comma + 1)..]);
        }
        if (!Uri.TryCreate(attachment.Source, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new FormatException("仅支持 HTTP、HTTPS 或 Base64 图片。");
        return await Client.GetByteArrayAsync(uri, cancellationToken).ConfigureAwait(false);
    }

    public static BitmapSource Decode(byte[] bytes, int previewWidth = 0)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        if (previewWidth > 0) bitmap.DecodePixelWidth = previewWidth;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    public static string Extension(byte[] bytes)
    {
        ReadOnlySpan<byte> data = bytes;
        if (data.StartsWith(new byte[] { 137, 80, 78, 71 })) return ".png";
        if (data.StartsWith(new byte[] { 255, 216, 255 })) return ".jpg";
        if (data.StartsWith("RIFF"u8) && data.Length >= 12 && data.Slice(8, 4).SequenceEqual("WEBP"u8)) return ".webp";
        if (data.StartsWith("GIF8"u8)) return ".gif";
        return ".bin";
    }
}
