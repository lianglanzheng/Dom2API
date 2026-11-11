namespace Dom2API.Models;

/// <summary>一张输入、预览或生成图片的来源；保留 data URL 或远程 HTTP 地址。</summary>
/// <param name="Name">用于界面展示的图片名称。</param>
/// <param name="Source">图片 data URL 或 HTTP 地址。</param>
public sealed record ImageAttachment(string Name, string Source);
