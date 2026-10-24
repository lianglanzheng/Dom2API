using Dom2API.Models;

namespace Dom2API.Services;

/// <summary>统一导入基址和重复密钥的识别，保留部署路径的大小写。</summary>
internal static class SiteAccountMapping
{
    internal static Uri ValidateBaseUrl(string value)
    {
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0
            || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new SiteApiException("站点网址必须是 HTTP 或 HTTPS 基址，不能包含账号、查询参数或片段。");
        return uri;
    }

    public static string GatewayBaseUrl(string siteUrl)
        => SiteAccountMapping.ValidateBaseUrl(siteUrl).AbsoluteUri.TrimEnd('/') + "/v1";

    public static bool SameGateway(string left, string right)
    {
        try
        {
            var a = SiteAccountMapping.ValidateBaseUrl(left);
            var b = SiteAccountMapping.ValidateBaseUrl(right);
            return a.Scheme == b.Scheme && a.IdnHost == b.IdnHost && a.Port == b.Port
                && NormalizePath(a) == NormalizePath(b);
        }
        catch (SiteApiException) { return false; }
    }

    public static UpstreamAccount? Find(IReadOnlyList<UpstreamAccount> accounts, string baseUrl, string? key)
        => string.IsNullOrWhiteSpace(key) ? null : accounts.FirstOrDefault(account =>
            account.ApiKey.Trim() == key.Trim() && SameGateway(account.BaseUrl, baseUrl));

    public static string? FindDisplayName(IReadOnlyList<UpstreamAccount> accounts, string baseUrl, string? key)
    {
        var account = Find(accounts, baseUrl, key);
        if (account is null) return null;
        return string.IsNullOrWhiteSpace(account.Name)
            ? $"账号{accounts.TakeWhile(item => item.Id != account.Id).Count() + 1}" : account.Name;
    }

    private static string NormalizePath(Uri uri)
    {
        string path = uri.AbsolutePath.TrimEnd('/');
        return path.EndsWith("/v1", StringComparison.Ordinal) ? path[..^3] : path;
    }
}
