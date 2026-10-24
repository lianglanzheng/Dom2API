namespace Dom2API.Models;

/// <summary>
/// 表示 JSON 或 XML 的一个树节点，并在展开时按需创建子节点。
/// </summary>
public sealed class ContentTreeNode
{
    private readonly Lazy<IReadOnlyList<ContentTreeNode>> children;

    /// <summary>获取节点的可读名称和标量值。</summary>
    public string Header { get; }

    /// <summary>获取子节点，按需生成后缓存，避免重复解析。</summary>
    public IReadOnlyList<ContentTreeNode> Children => children.Value;

    /// <summary>
    /// 创建节点及可选的子节点生成方法。
    /// </summary>
    public ContentTreeNode(string header, Func<IReadOnlyList<ContentTreeNode>>? createChildren = null)
    {
        Header = header;
        children = new Lazy<IReadOnlyList<ContentTreeNode>>(createChildren ?? (() => []));
    }
}
