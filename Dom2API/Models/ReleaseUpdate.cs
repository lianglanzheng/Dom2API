namespace Dom2API.Models;

/// <summary>比本地版本更新的 GitHub 正式发布。</summary>
internal sealed record ReleaseUpdate(string Version, Uri ReleaseUrl)
{
    public IReadOnlyList<ReleaseAsset> Assets { get; init; } = [];
}

/// <summary>GitHub 发布页提供的可下载文件及完整性信息。</summary>
internal sealed record ReleaseAsset(string Name, Uri DownloadUrl, long Size, string? Digest);
