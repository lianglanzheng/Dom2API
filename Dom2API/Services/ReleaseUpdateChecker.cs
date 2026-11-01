using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dom2API.Models;
using NuGet.Versioning;

namespace Dom2API.Services;

/// <summary>通过 GitHub Latest 对应的官方接口查询最新正式版，按 NuGet 版本规则比较。</summary>
internal sealed class ReleaseUpdateChecker(HttpClient client)
{
    internal const string LatestApiUrl = "https://api.github.com/repos/lianglanzheng/Dom2API/releases/latest";

    public async Task<ReleaseUpdate?> CheckAsync(string localVersion, CancellationToken cancellationToken = default)
    {
        if (!NuGetVersion.TryParse(RemoveTagPrefix(localVersion), out var current))
            throw new ArgumentException("无法识别本地版本号。", nameof(localVersion));

        using var request = new HttpRequestMessage(HttpMethod.Get, LatestApiUrl);
        request.Headers.UserAgent.ParseAdd("Dom2API-UpdateChecker");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        // 仓库尚无正式发布时，GitHub 返回 404。
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        var release = await response.Content.ReadFromJsonAsync<GitHubRelease>(cancellationToken).ConfigureAwait(false)
            ?? throw new JsonException("GitHub 返回了空的发布信息。");
        if (release.Draft || release.Prerelease) return null;
        if (string.IsNullOrWhiteSpace(release.TagName)
            || !NuGetVersion.TryParse(RemoveTagPrefix(release.TagName), out var latest))
            throw new JsonException("无法识别最新正式版的版本号。");
        if (latest.IsPrerelease || VersionComparer.VersionRelease.Compare(latest, current) <= 0) return null;

        // 使用固定仓库地址构造具体版本链接，检查完成后 Latest 再更新也不会指向其他版本。
        return new(release.TagName, new Uri("https://github.com/lianglanzheng/Dom2API/releases/tag/"
            + Uri.EscapeDataString(release.TagName)))
        {
            Assets = (release.Assets ?? []).Where(asset => asset is not null && asset.State == "uploaded"
                    && !string.IsNullOrWhiteSpace(asset.Name) && Uri.TryCreate(asset.DownloadUrl, UriKind.Absolute, out _))
                .Select(asset => new ReleaseAsset(asset.Name, new Uri(asset.DownloadUrl), asset.Size, asset.Digest)).ToArray()
        };
    }

    private static string RemoveTagPrefix(string value)
    {
        string version = value.Trim();
        return version.StartsWith('v') || version.StartsWith('V') ? version[1..] : version;
    }

    private sealed class GitHubRelease
    {
        [JsonRequired, JsonPropertyName("tag_name")]
        public string TagName { get; set; } = "";

        [JsonRequired, JsonPropertyName("draft")]
        public bool Draft { get; set; }

        [JsonRequired, JsonPropertyName("prerelease")]
        public bool Prerelease { get; set; }

        [JsonPropertyName("assets")]
        public List<GitHubAsset>? Assets { get; set; }
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";
        [JsonPropertyName("browser_download_url")]
        public string DownloadUrl { get; set; } = "";
        [JsonPropertyName("size")]
        public long Size { get; set; }
        [JsonPropertyName("digest")]
        public string? Digest { get; set; }
        [JsonPropertyName("state")]
        public string State { get; set; } = "";
    }
}
