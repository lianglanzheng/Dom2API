using System.IO;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Dom2API.Models;

namespace Dom2API.Services;

/// <summary>
/// 使用标准解析器识别完整 JSON 或 XML 内容并生成可展开的树结构。
/// </summary>
internal static class ContentTreeBuilder
{
    /// <summary>
    /// 尝试解析完整内容；无法解析时返回空树，调用方继续显示原始文本。
    /// </summary>
    public static bool TryBuild(string content, out string format, out IReadOnlyList<ContentTreeNode> nodes)
    {
        format = "纯文本";
        nodes = [];
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement.Clone();
            nodes = [CreateJsonNode("$", root)];
            format = "JSON";
            return true;
        }
        catch (JsonException)
        {
            // JSON 不匹配时继续识别 XML，不改写原文。
        }

        try
        {
            using var textReader = new StringReader(content);
            using var reader = XmlReader.Create(textReader, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null
            });
            var document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
            var roots = new List<ContentTreeNode>();
            if (document.Declaration is { } declaration)
            {
                roots.Add(new ContentTreeNode(declaration.ToString()));
            }

            roots.AddRange(document.Nodes().Select(CreateXmlNode));
            nodes = roots;
            format = "XML";
            return true;
        }
        catch (XmlException)
        {
            return false;
        }
    }

    /// <summary>
    /// 保留 JSON 属性顺序、数组索引和原始标量表达，容器的子节点按需创建。
    /// </summary>
    private static ContentTreeNode CreateJsonNode(string name, JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.Object => new ContentTreeNode(name + " : {}", () => value.EnumerateObject()
                .Select(property => CreateJsonNode(property.Name, property.Value)).ToArray()),
            JsonValueKind.Array => new ContentTreeNode($"{name} : [{value.GetArrayLength()}]", () => value.EnumerateArray()
                .Select((item, index) => CreateJsonNode($"[{index}]", item)).ToArray()),
            _ => new ContentTreeNode(name + " : " + value.GetRawText())
        };
    }

    /// <summary>
    /// 保留 XML 元素、属性、命名空间、文本、CDATA、注释及处理指令。
    /// </summary>
    private static ContentTreeNode CreateXmlNode(XNode node)
    {
        if (node is XElement element)
        {
            return new ContentTreeNode("<" + element.Name + ">", () => element.Attributes()
                .Select(attribute => new ContentTreeNode($"@{attribute.Name} = {attribute.Value}"))
                .Concat(element.Nodes().Select(CreateXmlNode)).ToArray());
        }

        return node switch
        {
            XCData data => new ContentTreeNode("CDATA : " + data.Value),
            XText text => new ContentTreeNode("#text : " + JsonSerializer.Serialize(text.Value)),
            XComment comment => new ContentTreeNode("<!-- " + comment.Value + " -->"),
            _ => new ContentTreeNode(node.ToString(SaveOptions.DisableFormatting))
        };
    }
}
