using System.IO;
using System.Text;
using System.Text.Json;
using Dom2API.Models;

namespace Dom2API.Services;

/// <summary>
/// 在指定程序目录中读写用户明确选择记住的连接配置和服务状态。
/// </summary>
internal sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        NewLine = "\r\n",
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter<SiteType>(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    /// <summary>获取配置文件的完整路径。</summary>
    public string FilePath { get; }

    /// <summary>获取配置文件是否存在。</summary>
    public bool Exists => File.Exists(FilePath);

    /// <summary>
    /// 创建在给定程序目录中保存配置的文件存储器。
    /// </summary>
    public SettingsStore(string applicationDirectory)
    {
        FilePath = Path.Combine(applicationDirectory, "Dom2API.settings.json");
    }

    /// <summary>
    /// 读取连接输入和服务状态，并拒绝缺少连接字段或连接字段为空值的配置文件。
    /// </summary>
    public RememberedSettings Load()
    {
        var settings = JsonSerializer.Deserialize<RememberedSettings>(File.ReadAllText(FilePath), JsonOptions);
        if (settings is null || settings.Port is null || settings.Upstreams is null
            || settings.TimeoutSeconds is null || settings.CooldownMinutes is null
            || settings.Upstreams.Any(account => account is null || account.Id is null || account.Name is null
                || account.BaseUrl is null || account.ApiKey is null))
        {
            throw new JsonException("配置文件必须包含 port 和 upstreams 账号列表，配置字段不能为 null。");
        }
        if (settings.Upstreams.Any(account => string.IsNullOrWhiteSpace(account.Id))
            || settings.Upstreams.Select(account => account.Id).Distinct().Count() != settings.Upstreams.Count)
        {
            throw new JsonException("上游账号 id 必须非空且不能重复。");
        }

        if (settings.Sites is null || settings.Sites.Any(site => site is null
            || string.IsNullOrWhiteSpace(site.Id) || site.Name is null || string.IsNullOrWhiteSpace(site.BaseUrl)
            || string.IsNullOrWhiteSpace(site.Username) || string.IsNullOrWhiteSpace(site.Password)
            || !Enum.IsDefined(site.Type))
            || settings.Sites.Select(site => site.Id).Distinct().Count() != settings.Sites.Count)
            throw new JsonException("站点配置字段不完整、类型无效或标识重复。");
        foreach (var site in settings.Sites)
        {
            try { _ = SiteAccountMapping.ValidateBaseUrl(site.BaseUrl); }
            catch (SiteApiException exception) { throw new JsonException("站点网址无效。", exception); }
        }
        try
        {
            foreach (var account in settings.Upstreams) account.Proxy = Socks5Proxy.Normalize(account.Proxy);
            foreach (var site in settings.Sites) site.Proxy = Socks5Proxy.Normalize(site.Proxy);
        }
        catch (ArgumentException exception) { throw new JsonException("代理配置无效。" + exception.Message, exception); }
        return settings;
    }

    /// <summary>
    /// 先完整写入同目录临时文件，再替换配置，避免写入失败破坏原有内容。
    /// </summary>
    public void Save(RememberedSettings settings)
    {
        string temporaryPath = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, JsonOptions) + "\r\n",
                new UTF8Encoding(true));
            File.Move(temporaryPath, FilePath, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    /// <summary>
    /// 立即删除记忆文件；文件已不存在时不执行额外操作。
    /// </summary>
    public void Delete() => File.Delete(FilePath);
}
