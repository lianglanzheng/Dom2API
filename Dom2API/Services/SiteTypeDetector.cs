using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using Dom2API.Models;

namespace Dom2API.Services;

/// <summary>仅通过公开接口识别站点协议，不发送登录凭据，也不根据域名或品牌推测。</summary>
internal sealed class SiteTypeDetector : IDisposable
{
    private readonly HttpClient client;

    /// <summary>不跟随重定向、不使用 Cookie，保留面板部署子路径。</summary>
    public SiteTypeDetector(HttpMessageHandler? handler = null, string? proxy = null)
        => client = new(handler ?? OutboundClients.CreateHandler(proxy))
        { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>同时核对两类公开响应；无法识别或同时匹配时不保存站点。</summary>
    public async Task<SiteType> DetectAsync(string baseUrl, CancellationToken token = default)
    {
        string address = SiteAccountMapping.ValidateBaseUrl(baseUrl).AbsoluteUri.TrimEnd('/');
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            var matches = await Task.WhenAll(
                ProbeAsync(address + "/api/v1/settings/public", SiteType.Sub2Api, timeout.Token),
                ProbeAsync(address + "/api/status", SiteType.NewApi, timeout.Token));
            if (matches[0] && matches[1]) throw new SiteApiException("站点同时返回两种协议，请填写具体面板的部署地址。");
            if (matches[0]) return SiteType.Sub2Api;
            if (matches[1]) return SiteType.NewApi;
            throw new SiteApiException("无法识别站点类型。请确认面板基址及网络，并检查站点是否开放公开设置接口。");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (OperationCanceledException error) { throw new SiteApiException("识别站点类型超时，请检查网址和网络后重试。", error); }
    }

    /// <summary>用响应封装及协议特有字段确认类型，排除 HTML 回退页和业务错误。</summary>
    private async Task<bool> ProbeAsync(string address, SiteType type, CancellationToken token)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, address);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (!response.IsSuccessStatusCode) return false;
            await response.Content.LoadIntoBufferAsync(1024 * 1024, token);
            using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(token));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Object) return false;
            return type == SiteType.Sub2Api
                ? root.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.Number && code.TryGetInt32(out int number) && number == 0
                    && data.TryGetProperty("site_name", out var name) && name.ValueKind == JsonValueKind.String
                    && data.TryGetProperty("registration_enabled", out var enabled) && enabled.ValueKind is JsonValueKind.True or JsonValueKind.False
                : root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.True
                    && data.TryGetProperty("quota_per_unit", out var unit) && unit.ValueKind == JsonValueKind.Number && unit.TryGetDecimal(out decimal quota) && quota > 0
                    && data.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.String;
        }
        catch (Exception error) when (error is HttpRequestException or IOException or JsonException) { return false; }
    }

    /// <summary>释放公开接口查询客户端。</summary>
    public void Dispose() => client.Dispose();
}
