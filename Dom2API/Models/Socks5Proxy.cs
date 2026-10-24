namespace Dom2API.Models;

/// <summary>统一验证配置文件和编辑表单中的 Socks5 地址。</summary>
internal static class Socks5Proxy
{
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme != "socks5" || string.IsNullOrWhiteSpace(uri.Host)
            || uri.Port is < 1 or > 65535 || uri.AbsolutePath is not ("" or "/")
            || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new ArgumentException("代理地址必须为 socks5://主机:端口（端口为 1 至 65535），不能包含路径、查询参数或片段。");
        return uri.AbsoluteUri.TrimEnd('/');
    }

    public static string? FromInput(bool enabled, string value)
        => !enabled ? null : Normalize(value)
            ?? throw new ArgumentException("勾选使用代理后，请填写 Socks5 代理地址。");
}
