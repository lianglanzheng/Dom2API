#:property TargetFramework=net10.0
#:property LangVersion=latest
#:property Nullable=enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

/// <summary>使用纯 .NET 将 Dom2API SVG 原稿转换为透明 PNG 预览和多尺寸 ICO。</summary>
internal static class Program
{
    /// <summary>ICO 帧尺寸，覆盖常见任务栏、托盘及资源管理器缩放。</summary>
    private static readonly int[] Sizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256];

    /// <summary>读取参数、渲染原稿并输出图标。</summary>
    /// <param name="args">输入 SVG 路径与输出 ICO 路径。</param>
    /// <returns>成功返回零，失败返回一或二。</returns>
    private static int Main(string[] args)
    {
        // 显式传入路径，让工具从任何工作目录运行时都不依赖隐含目录约定。
        if (args.Length != 2 || !Path.GetExtension(args[1]).Equals(".ico", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine("用法：dotnet run --file convert-icon.cs -- <输入.svg> <输出.ico>");
            return 2;
        }

        try
        {
            // 先完成全部解析与编码，再创建输出，避免不支持的原稿覆盖已有产物。
            string source = Path.GetFullPath(args[0]);
            SvgScene scene = SvgScene.Load(source);
            string stem = Path.Combine(Path.GetDirectoryName(source)!, Path.GetFileNameWithoutExtension(source));
            byte[][] frames = Sizes.Select(size =>
            {
                // 同目录下的逐尺寸原稿优先；缺少专用设计时使用高分辨率原稿。
                string variant = $"{stem}.{size}.svg";
                bool hasVariant = File.Exists(variant);
                SvgScene frameScene = hasVariant ? SvgScene.Load(variant) : scene;
                Console.WriteLine($"{size} px ← {Path.GetFileName(hasVariant ? variant : source)}");
                return Png.Encode(frameScene.Render(size), size);
            }).ToArray();
            byte[] preview = Png.Encode(scene.Render(512), 512);
            string output = Path.GetFullPath(args[1]);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            WriteIcon(output, frames);
            File.WriteAllBytes(Path.ChangeExtension(output, ".png"), preview);
            Console.WriteLine($"已生成 {output}（{string.Join("、", Sizes)} 像素）及 512 像素 PNG 预览。");
            return 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or FormatException or XmlException or ArgumentException)
        {
            // 输入、编码与文件系统错误统一报告；不启动外部程序，也不下载运行时依赖。
            Console.Error.WriteLine($"图标转换失败：{exception.Message}");
            return 1;
        }
    }

    /// <summary>将 PNG 图像按 ICO 文件结构封装。</summary>
    /// <param name="path">输出文件路径。</param>
    /// <param name="frames">与尺寸列表对应的 PNG 图像。</param>
    private static void WriteIcon(string path, byte[][] frames)
    {
        // BinaryWriter 按小端写入目录；图像本体使用现代 Windows 支持的 PNG 帧。
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)frames.Length);
        uint offset = (uint)(6 + 16 * frames.Length);
        for (int index = 0; index < frames.Length; index++)
        {
            // ICO 用零表示 256 像素，其余字段显式写入以保证结果可复现。
            writer.Write((byte)(Sizes[index] % 256));
            writer.Write((byte)(Sizes[index] % 256));
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write((uint)frames[index].Length);
            writer.Write(offset);
            offset += (uint)frames[index].Length;
        }

        // writer 拥有文件流，写入完成或异常时都会释放句柄。
        foreach (byte[] frame in frames) writer.Write(frame);
    }
}


/// <summary>保存按 SVG 文档顺序绘制的有限图形集合。</summary>
internal sealed class SvgScene
{
    /// <summary>原稿正方形视图的边长。</summary>
    private readonly double extent;

    /// <summary>按画家顺序排列的图形。</summary>
    private readonly List<Shape> shapes = [];

    /// <summary>初始化正方形绘制场景。</summary>
    /// <param name="extent">视图边长。</param>
    private SvgScene(double extent)
    {
        // 所有几何保留原稿坐标，光栅化时统一缩放。
        this.extent = extent;
    }

    /// <summary>读取并严格验证图标使用的 SVG 子集。</summary>
    /// <param name="path">原稿文件路径。</param>
    /// <returns>可渲染的场景。</returns>
    public static SvgScene Load(string path)
    {
        // 禁止 DTD 与外部实体，不允许 SVG 间接读取其他文件或网络资源。
        using XmlReader reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        XElement root = XElement.Load(reader);
        XNamespace svg = "http://www.w3.org/2000/svg";
        if (root.Name != svg + "svg") throw new FormatException("根元素必须是 SVG 命名空间中的 svg。");
        ValidateAttributes(root, "width", "height", "viewBox");
        double[] viewBox = Numbers((string?)root.Attribute("viewBox") ?? "");
        if (viewBox.Length != 4 || viewBox[0] != 0 || viewBox[1] != 0 || viewBox[2] <= 0 || viewBox[2] != viewBox[3])
        {
            throw new FormatException("仅支持从 0 0 开始的正方形 viewBox。");
        }

        var scene = new SvgScene(viewBox[2]);
        Dictionary<string, Paint> gradients = Paint.LoadGradients(root);
        foreach (XElement element in root.Elements())
        {
            // 渐变定义先统一解析，图形仍按文档顺序绘制，拒绝任意嵌套和样式。
            if (element.Name.Namespace != svg) throw new FormatException("不支持外部命名空间元素。");
            if (element.Name.LocalName is "title" or "desc" or "defs") continue;
            if (element.HasElements) throw new FormatException("不支持嵌套图形。");
            scene.shapes.Add(Shape.Parse(element, gradients));
        }

        if (scene.shapes.Count == 0) throw new FormatException("原稿没有可绘制图形。");
        return scene;
    }

    /// <summary>拒绝未知属性，避免静默忽略改变图像效果的 SVG 特性。</summary>
    /// <param name="element">待验证的元素。</param>
    /// <param name="names">允许的无命名空间属性名称。</param>
    public static void ValidateAttributes(XElement element, params string[] names)
    {
        // xmlns 仅定义命名空间，不参与样式或几何计算。
        foreach (XAttribute attribute in element.Attributes())
        {
            if (attribute.IsNamespaceDeclaration) continue;
            if (attribute.Name.NamespaceName.Length != 0 || !names.Contains(attribute.Name.LocalName))
            {
                throw new FormatException($"不支持 {element.Name.LocalName} 的属性 {attribute.Name}。");
            }
        }
    }

    /// <summary>解析由逗号或空白分隔的有限实数列表。</summary>
    /// <param name="text">数值文本。</param>
    /// <returns>解析后的数值。</returns>
    public static double[] Numbers(string text)
    {
        // 使用不变区域设置，保证不同系统语言生成相同像素。
        string[] tokens = text.Split([' ', ',', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries);
        return tokens.Select(token =>
        {
            double value = double.Parse(token, CultureInfo.InvariantCulture);
            if (!double.IsFinite(value) || Math.Abs(value) > 100000) throw new FormatException("坐标必须是绝对值不超过 100000 的有限数字。");
            return value;
        }).ToArray();
    }

    /// <summary>使用四倍线性超采样生成直通道 RGBA 图像。</summary>
    /// <param name="size">输出图像边长。</param>
    /// <returns>自上而下、从左到右排列的 RGBA 字节。</returns>
    public byte[] Render(int size)
    {
        // 高分辨率画布按文档顺序进行源覆盖混合，保留图形的实际透明度。
        const int samples = 4;
        int highSize = size * samples;
        double scale = highSize / extent;
        uint[] canvas = new uint[highSize * highSize];
        foreach (Shape shape in shapes) shape.Draw(canvas, highSize, scale);
        byte[] result = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int red = 0, green = 0, blue = 0, alpha = 0;
                for (int sy = 0; sy < samples; sy++)
                {
                    for (int sx = 0; sx < samples; sx++)
                    {
                        uint color = canvas[(y * samples + sy) * highSize + x * samples + sx];
                        int sampleAlpha = (int)(color & 255);
                        red += (int)((color >> 24) & 255) * sampleAlpha;
                        green += (int)((color >> 16) & 255) * sampleAlpha;
                        blue += (int)((color >> 8) & 255) * sampleAlpha;
                        alpha += sampleAlpha;
                    }
                }

                // 使用预乘透明度求平均，再还原直通道颜色，避免半透明边缘变暗。
                if (alpha == 0) continue;
                int target = (y * size + x) * 4;
                result[target] = (byte)(red / alpha);
                result[target + 1] = (byte)(green / alpha);
                result[target + 2] = (byte)(blue / alpha);
                result[target + 3] = (byte)((alpha + samples * samples / 2) / (samples * samples));
            }
        }

        return result;
    }
}

/// <summary>二维原稿坐标。</summary>
/// <param name="X">横坐标。</param>
/// <param name="Y">纵坐标。</param>
internal readonly record struct Point(double X, double Y);

/// <summary>一个 sRGB 线性渐变色标。</summary>
/// <param name="Offset">沿渐变轴的位置。</param>
/// <param name="Color">高位为红色、低位为透明度的直通道颜色。</param>
internal readonly record struct ColorStop(double Offset, uint Color);

/// <summary>表示纯色或用户坐标系中的线性渐变，并执行透明混合。</summary>
internal sealed class Paint
{
    /// <summary>渐变轴的起点。</summary>
    private readonly Point start;

    /// <summary>渐变轴的终点。</summary>
    private readonly Point end;

    /// <summary>按偏移量排序的色标，纯色只有一个色标。</summary>
    private readonly ColorStop[] stops;

    /// <summary>当前填充或描边的额外不透明度。</summary>
    private readonly double opacity;

    /// <summary>构建已验证的画刷。</summary>
    /// <param name="start">渐变起点。</param>
    /// <param name="end">渐变终点。</param>
    /// <param name="stops">有序色标。</param>
    /// <param name="opacity">额外不透明度。</param>
    private Paint(Point start, Point end, ColorStop[] stops, double opacity)
    {
        // 色标解析后不再修改，可由同一渐变的多个画刷引用共享。
        this.start = start;
        this.end = end;
        this.stops = stops;
        this.opacity = opacity;
    }

    /// <summary>从 defs 中读取严格受限的线性渐变。</summary>
    /// <param name="root">SVG 根元素。</param>
    /// <returns>按标识符索引的渐变画刷。</returns>
    public static Dictionary<string, Paint> LoadGradients(XElement root)
    {
        // 仅接受本文件内的 userSpaceOnUse 渐变，禁止引用继承和外部资源。
        XNamespace svg = "http://www.w3.org/2000/svg";
        var result = new Dictionary<string, Paint>(StringComparer.Ordinal);
        foreach (XElement definitions in root.Elements(svg + "defs"))
        {
            SvgScene.ValidateAttributes(definitions);
            foreach (XElement gradient in definitions.Elements())
            {
                if (gradient.Name != svg + "linearGradient") throw new FormatException("defs 中仅支持 linearGradient。");
                SvgScene.ValidateAttributes(gradient, "id", "gradientUnits", "x1", "y1", "x2", "y2");
                string id = (string?)gradient.Attribute("id") ?? "";
                if (!Regex.IsMatch(id, "^[A-Za-z][A-Za-z0-9_-]*$") || (string?)gradient.Attribute("gradientUnits") != "userSpaceOnUse") throw new FormatException("渐变需要唯一 id 和显式 userSpaceOnUse 坐标。");
                Point start = new(Scalar(gradient, "x1"), Scalar(gradient, "y1"));
                Point end = new(Scalar(gradient, "x2"), Scalar(gradient, "y2"));
                if (start == end) throw new FormatException("渐变起终点不能重合。");
                var stops = new List<ColorStop>();
                foreach (XElement stop in gradient.Elements())
                {
                    // 色标位置和不透明度必须明确、单调且在合法范围内。
                    if (stop.Name != svg + "stop" || stop.HasElements) throw new FormatException("渐变中仅支持 stop 色标。");
                    SvgScene.ValidateAttributes(stop, "offset", "stop-color", "stop-opacity");
                    double offset = Scalar(stop, "offset");
                    if (offset < 0 || offset > 1 || (stops.Count != 0 && offset <= stops[^1].Offset)) throw new FormatException("色标偏移必须在 0 到 1 内严格递增。");
                    double alpha = stop.Attribute("stop-opacity") is null ? 1 : Scalar(stop, "stop-opacity");
                    ValidateOpacity(alpha);
                    uint color = ParseColor((string?)stop.Attribute("stop-color") ?? "#000000");
                    stops.Add(new ColorStop(offset, (color & 0xFFFFFF00) | (uint)Math.Round(alpha * 255)));
                }

                if (stops.Count < 2) throw new FormatException("渐变至少需要两个色标。");
                if (!result.TryAdd(id, new Paint(start, end, stops.ToArray(), 1))) throw new FormatException($"渐变 id 重复：{id}。");
            }
        }

        return result;
    }

    /// <summary>解析无单位数字属性，色标偏移也支持百分数。</summary>
    /// <param name="element">SVG 元素。</param>
    /// <param name="name">属性名称。</param>
    /// <returns>有限标量。</returns>
    private static double Scalar(XElement element, string name)
    {
        // 只允许 offset 使用百分数；渐变坐标必须直接给出原稿坐标值。
        string text = (string?)element.Attribute(name) ?? throw new FormatException($"渐变缺少 {name}。");
        bool percentage = name == "offset" && text.EndsWith('%');
        double[] values = SvgScene.Numbers(percentage ? text[..^1] : text);
        if (values.Length != 1) throw new FormatException($"{name} 必须是一个数字。");
        return percentage ? values[0] / 100 : values[0];
    }

    /// <summary>解析纯色、none 或本文件中的渐变引用。</summary>
    /// <param name="text">填充或描边属性。</param>
    /// <param name="opacity">填充或描边的不透明度。</param>
    /// <param name="gradients">已解析的渐变集合。</param>
    /// <returns>画刷；none 返回空值。</returns>
    public static Paint? Parse(string text, double opacity, IReadOnlyDictionary<string, Paint> gradients)
    {
        // 即使无填充也校验不透明度，避免静默接受非法参数。
        ValidateOpacity(opacity);
        if (text == "none") return null;
        Match reference = Regex.Match(text, @"^url\(#([A-Za-z][A-Za-z0-9_-]*)\)$");
        if (reference.Success)
        {
            if (!gradients.TryGetValue(reference.Groups[1].Value, out Paint? paint)) throw new FormatException($"未定义渐变：{text}。");
            return new Paint(paint.start, paint.end, paint.stops, opacity);
        }

        return new Paint(default, default, [new ColorStop(0, ParseColor(text))], opacity);
    }

    /// <summary>校验不透明度。</summary>
    /// <param name="value">待验证数值。</param>
    private static void ValidateOpacity(double value)
    {
        // 拒绝越界或非有限值，不对错误输入做隐式修正。
        if (!double.IsFinite(value) || value < 0 || value > 1) throw new FormatException("不透明度必须在 0 到 1 之间。");
    }

    /// <summary>解析六位十六进制颜色。</summary>
    /// <param name="text">颜色文字。</param>
    /// <returns>不透明 RGBA 颜色。</returns>
    private static uint ParseColor(string text)
    {
        // 颜色空间固定为 SVG 默认的 sRGB，不依赖平台色彩管理。
        if (!Regex.IsMatch(text, "^#[0-9a-fA-F]{6}$")) throw new FormatException($"仅支持 #RRGGBB 颜色：{text}。");
        return (uint.Parse(text.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture) << 8) | 255;
    }

    /// <summary>计算指定原稿坐标处的画刷颜色。</summary>
    /// <param name="point">原稿坐标。</param>
    /// <returns>直通道 RGBA 颜色。</returns>
    public uint Sample(Point point)
    {
        // 超出渐变轴范围时沿用边缘颜色，与默认 pad 延伸模式一致。
        double dx = end.X - start.X, dy = end.Y - start.Y;
        double position = stops.Length == 1 ? 0 : Math.Clamp(((point.X - start.X) * dx + (point.Y - start.Y) * dy) / (dx * dx + dy * dy), 0, 1);
        uint color = stops[^1].Color;
        if (position <= stops[0].Offset) color = stops[0].Color;
        else
        {
            for (int index = 1; index < stops.Length; index++)
            {
                if (position > stops[index].Offset) continue;
                double ratio = (position - stops[index - 1].Offset) / (stops[index].Offset - stops[index - 1].Offset);
                color = 0;
                // 分别插值 sRGB 通道和透明度，随后叠加当前元素的额外不透明度。
                for (int shift = 0; shift <= 24; shift += 8)
                {
                    double a = (stops[index - 1].Color >> shift) & 255;
                    double b = (stops[index].Color >> shift) & 255;
                    color |= (uint)Math.Round(a + (b - a) * ratio) << shift;
                }

                break;
            }
        }

        return (color & 0xFFFFFF00) | (uint)Math.Round((color & 255) * opacity);
    }

    /// <summary>按源覆盖规则混合两种直通道颜色。</summary>
    /// <param name="destination">画布当前颜色。</param>
    /// <param name="source">待叠加的颜色。</param>
    /// <returns>保留真实透明度的合成颜色。</returns>
    public static uint Over(uint destination, uint source)
    {
        // 透明和不透明源直接返回，常见纯色路径无需执行浮点混合。
        uint sourceAlpha = source & 255;
        if (sourceAlpha == 0) return destination;
        if (sourceAlpha == 255 || destination == 0) return source;
        double remainder = (destination & 255) * (1 - sourceAlpha / 255d);
        double alpha = sourceAlpha + remainder;
        uint result = (uint)Math.Round(alpha);
        for (int shift = 8; shift <= 24; shift += 8)
        {
            double value = (((source >> shift) & 255) * sourceAlpha + ((destination >> shift) & 255) * remainder) / alpha;
            result |= (uint)Math.Round(value) << shift;
        }

        return result;
    }
}

/// <summary>包含几何判定、渐变填充和半透明描边的 SVG 图形。</summary>
internal sealed class Shape
{
    /// <summary>图形类型。</summary>
    private readonly string kind;

    /// <summary>矩形或圆的几何参数。</summary>
    private readonly double[] geometry;

    /// <summary>多边形或路径的折线坐标。</summary>
    private readonly List<Point> points;

    /// <summary>填充画刷，空值表示不填充。</summary>
    private readonly Paint? fill;

    /// <summary>描边画刷，空值表示不描边。</summary>
    private readonly Paint? stroke;

    /// <summary>描边宽度。</summary>
    private readonly double strokeWidth;

    /// <summary>创建已经验证的图形。</summary>
    /// <param name="kind">图形类型。</param>
    /// <param name="geometry">基本几何参数。</param>
    /// <param name="points">路径折线点。</param>
    /// <param name="fill">填充颜色。</param>
    /// <param name="stroke">描边颜色。</param>
    /// <param name="strokeWidth">描边宽度。</param>
    private Shape(string kind, double[] geometry, List<Point> points, Paint? fill, Paint? stroke, double strokeWidth)
    {
        // 对象拥有解析得到的坐标，不再修改，所有尺寸共享同一几何。
        this.kind = kind;
        this.geometry = geometry;
        this.points = points;
        this.fill = fill;
        this.stroke = stroke;
        this.strokeWidth = strokeWidth;
    }

    /// <summary>解析矩形、圆、多边形或单子路径。</summary>
    /// <param name="element">SVG 图形元素。</param>
    /// <param name="gradients">已经验证的命名渐变。</param>
    /// <returns>验证完成的图形。</returns>
    public static Shape Parse(XElement element, IReadOnlyDictionary<string, Paint> gradients)
    {
        // 属性白名单与严格颜色解析保证不支持的语法不会悄悄丢失。
        string kind = element.Name.LocalName;
        string[] geometricAttributes = kind switch
        {
            "rect" => ["x", "y", "width", "height", "rx"],
            "circle" => ["cx", "cy", "r"],
            "polygon" => ["points"],
            "path" => ["d", "stroke-linecap", "stroke-linejoin"],
            _ => throw new FormatException($"不支持图形：{kind}。")
        };
        SvgScene.ValidateAttributes(element, [.. geometricAttributes, "fill", "stroke", "stroke-width", "fill-opacity", "stroke-opacity"]);
        Paint? fill = Paint.Parse((string?)element.Attribute("fill") ?? "#000000", Number(element, "fill-opacity", 1), gradients);
        Paint? stroke = Paint.Parse((string?)element.Attribute("stroke") ?? "none", Number(element, "stroke-opacity", 1), gradients);
        double strokeWidth = Number(element, "stroke-width", 1);
        if (strokeWidth < 0) throw new FormatException("描边宽度不能为负数。");
        double[] geometry = [];
        List<Point> points = [];
        switch (kind)
        {
            case "rect":
                geometry = [Number(element, "x", 0), Number(element, "y", 0), Number(element, "width", 0), Number(element, "height", 0), Number(element, "rx", 0)];
                if (geometry[2] <= 0 || geometry[3] <= 0 || geometry[4] < 0) throw new FormatException("矩形宽高必须为正且圆角不能为负。");
                break;
            case "circle":
                geometry = [Number(element, "cx", 0), Number(element, "cy", 0), Number(element, "r", 0)];
                if (geometry[2] <= 0) throw new FormatException("圆半径必须为正。");
                break;
            case "polygon":
                if (stroke is not null) throw new FormatException("多边形仅支持填充，不支持描边。");
                double[] values = SvgScene.Numbers((string?)element.Attribute("points") ?? "");
                if (values.Length < 6 || values.Length % 2 != 0) throw new FormatException("多边形至少需要三组成对坐标。");
                for (int index = 0; index < values.Length; index += 2) points.Add(new Point(values[index], values[index + 1]));
                break;
            case "path":
                // 路径描边统一使用显式圆头、圆角连接，与逐线段距离算法一致。
                if (stroke is not null && ((string?)element.Attribute("stroke-linecap") != "round" || (string?)element.Attribute("stroke-linejoin") != "round")) throw new FormatException("路径描边必须显式指定 round 端点和连接。");
                points = ParsePath((string?)element.Attribute("d") ?? "");
                break;
        }

        return new Shape(kind, geometry, points, fill, stroke, strokeWidth);
    }

    /// <summary>读取一个标量属性。</summary>
    /// <param name="element">元素。</param>
    /// <param name="name">属性名称。</param>
    /// <param name="fallback">缺省值。</param>
    /// <returns>有限数值。</returns>
    private static double Number(XElement element, string name, double fallback)
    {
        // 缺少可选属性才使用缺省值，格式错误必须报告。
        if (element.Attribute(name) is not XAttribute attribute) return fallback;
        double[] values = SvgScene.Numbers(attribute.Value);
        if (values.Length != 1) throw new FormatException($"{name} 必须是单个数字。");
        return values[0];
    }

    /// <summary>解析绝对 M、L、C、Z 命令组成的单个子路径。</summary>
    /// <param name="text">SVG 路径文字。</param>
    /// <returns>折线化的坐标序列。</returns>
    private static List<Point> ParsePath(string text)
    {
        // 逐字符验证令牌间隔，拒绝未识别命令、相对坐标和隐式参数组。
        const string pattern = @"[MLCZ]|[-+]?(?:\d*\.\d+|\d+\.?)(?:[eE][-+]?\d+)?";
        MatchCollection matches = Regex.Matches(text, pattern);
        if (Regex.Replace(Regex.Replace(text, pattern, ""), @"[\s,]", "").Length != 0) throw new FormatException("路径仅支持显式绝对 M、L、C、Z 命令。");
        string[] tokens = matches.Select(match => match.Value).ToArray();
        var points = new List<Point>();
        int index = 0;
        while (index < tokens.Length)
        {
            string command = tokens[index++];
            if (points.Count == 0 && command != "M") throw new FormatException("路径必须以 M 开始。");
            if (command == "Z")
            {
                // 当前子集只允许结尾闭合，不允许闭合后开启其他子路径。
                if (index != tokens.Length) throw new FormatException("Z 必须是最后一个路径命令。");
                points.Add(points[0]);
                continue;
            }

            int count = command switch { "M" or "L" => 2, "C" => 6, _ => throw new FormatException("每组坐标必须有显式 M、L 或 C 命令。") };
            if (index + count > tokens.Length) throw new FormatException("路径命令参数不足。");
            double[] values = SvgScene.Numbers(string.Join(" ", tokens[index..(index + count)]));
            index += count;
            if (command == "M" && points.Count != 0) throw new FormatException("仅支持单个子路径。");
            if (command == "C")
            {
                // 按控制点到弦的距离自适应细分；误差控制在原稿坐标的 0.05 以内。
                Flatten(points, points[^1], new Point(values[0], values[1]), new Point(values[2], values[3]), new Point(values[4], values[5]), 0);
            }
            else
            {
                points.Add(new Point(values[0], values[1]));
            }
        }

        if (points.Count < 2) throw new FormatException("路径至少需要两个点。");
        return points;
    }

    /// <summary>自适应细分三次贝塞尔曲线。</summary>
    /// <param name="points">接收折线点的列表。</param>
    /// <param name="a">起点。</param>
    /// <param name="b">第一控制点。</param>
    /// <param name="c">第二控制点。</param>
    /// <param name="d">终点。</param>
    /// <param name="depth">当前递归深度。</param>
    private static void Flatten(List<Point> points, Point a, Point b, Point c, Point d, int depth)
    {
        // 限制递归深度，避免退化曲线导致无限细分。
        if (depth >= 16 || (Distance(b, a, d) <= 0.05 && Distance(c, a, d) <= 0.05))
        {
            points.Add(d);
            return;
        }

        Point ab = Middle(a, b), bc = Middle(b, c), cd = Middle(c, d);
        Point abc = Middle(ab, bc), bcd = Middle(bc, cd), middle = Middle(abc, bcd);
        Flatten(points, a, ab, abc, middle, depth + 1);
        Flatten(points, middle, bcd, cd, d, depth + 1);
    }

    /// <summary>计算两个点的中点。</summary>
    /// <param name="a">第一个点。</param>
    /// <param name="b">第二个点。</param>
    /// <returns>线段中点。</returns>
    private static Point Middle(Point a, Point b)
    {
        // 中点用于德卡斯特里奥细分，不依赖平台图形 API。
        return new Point((a.X + b.X) / 2, (a.Y + b.Y) / 2);
    }

    /// <summary>将图形写入高分辨率画布。</summary>
    /// <param name="canvas">图像颜色缓冲区。</param>
    /// <param name="size">画布边长。</param>
    /// <param name="scale">原稿到画布的比例。</param>
    public void Draw(uint[] canvas, int size, double scale)
    {
        // 曲线路径通过扫描线填充，避免曲线轮廓的密集细分点拖慢逐像素判定。
        if (kind is "path" or "polygon")
        {
            if (fill is not null) FillPath(canvas, size, scale);
            if (stroke is not null && strokeWidth > 0)
            {
                // 先求整条描边的覆盖并集，每个采样点只混合一次，避免接缝变深。
                bool[] coverage = new bool[canvas.Length];
                for (int index = 1; index < points.Count; index++) MarkSegment(coverage, size, scale, points[index - 1], points[index]);
                for (int index = 0; index < coverage.Length; index++)
                {
                    if (coverage[index]) canvas[index] = Paint.Over(canvas[index], stroke.Sample(new Point((index % size + 0.5) / scale, (index / size + 0.5) / scale)));
                }
            }

            return;
        }

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                Point sample = new((x + 0.5) / scale, (y + 0.5) / scale);
                double distance = kind switch
                {
                    "rect" => RectangleDistance(sample),
                    "circle" => Math.Sqrt(Math.Pow(sample.X - geometry[0], 2) + Math.Pow(sample.Y - geometry[1], 2)) - geometry[2],
                    _ => throw new InvalidOperationException("未知的基础几何图形。")
                };
                int target = y * size + x;
                if (distance <= 0 && fill is not null) canvas[target] = Paint.Over(canvas[target], fill.Sample(sample));
                // SVG 先填充再描边；描边中心与几何边界重合。
                if (stroke is not null && Math.Abs(distance) <= strokeWidth / 2) canvas[target] = Paint.Over(canvas[target], stroke.Sample(sample));
            }
        }
    }

    /// <summary>标记圆头线段覆盖的采样点。</summary>
    /// <param name="coverage">整条描边共用的覆盖集合。</param>
    /// <param name="size">画布边长。</param>
    /// <param name="scale">坐标缩放。</param>
    /// <param name="a">起点。</param>
    /// <param name="b">终点。</param>
    private void MarkSegment(bool[] coverage, int size, double scale, Point a, Point b)
    {
        // 圆头线段并集形成圆角连接，此阶段不进行透明混合。
        double radius = strokeWidth / 2;
        int left = Math.Clamp((int)Math.Floor((Math.Min(a.X, b.X) - radius) * scale), 0, size);
        int right = Math.Clamp((int)Math.Ceiling((Math.Max(a.X, b.X) + radius) * scale), 0, size);
        int top = Math.Clamp((int)Math.Floor((Math.Min(a.Y, b.Y) - radius) * scale), 0, size);
        int bottom = Math.Clamp((int)Math.Ceiling((Math.Max(a.Y, b.Y) + radius) * scale), 0, size);
        for (int y = top; y < bottom; y++)
        {
            for (int x = left; x < right; x++)
            {
                if (Distance(new Point((x + 0.5) / scale, (y + 0.5) / scale), a, b) <= radius) coverage[y * size + x] = true;
            }
        }
    }

    /// <summary>计算点到线段的最短距离。</summary>
    /// <param name="point">待检查的点。</param>
    /// <param name="a">线段起点。</param>
    /// <param name="b">线段终点。</param>
    /// <returns>非负距离。</returns>
    private static double Distance(Point point, Point a, Point b)
    {
        // 退化线段视为单点；其他线段将投影限制在两端之间。
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double length = dx * dx + dy * dy;
        double t = length == 0 ? 0 : Math.Clamp(((point.X - a.X) * dx + (point.Y - a.Y) * dy) / length, 0, 1);
        return Math.Sqrt(Math.Pow(point.X - a.X - t * dx, 2) + Math.Pow(point.Y - a.Y - t * dy, 2));
    }

    /// <summary>计算点到圆角矩形边界的有符号距离。</summary>
    /// <param name="point">原稿坐标中的采样点。</param>
    /// <returns>内部为负、外部为正的距离。</returns>
    private double RectangleDistance(Point point)
    {
        // 半径遵循 SVG 的半宽、半高上限，距离用于填充和居中描边。
        double radius = Math.Min(geometry[4], Math.Min(geometry[2], geometry[3]) / 2);
        double x = Math.Abs(point.X - geometry[0] - geometry[2] / 2) - geometry[2] / 2 + radius;
        double y = Math.Abs(point.Y - geometry[1] - geometry[3] / 2) - geometry[3] / 2 + radius;
        return Math.Min(Math.Max(x, y), 0) + Math.Sqrt(Math.Pow(Math.Max(x, 0), 2) + Math.Pow(Math.Max(y, 0), 2)) - radius;
    }

    /// <summary>按非零绕数规则扫描填充路径，并保留渐变的透明度。</summary>
    /// <param name="canvas">像素缓冲区。</param>
    /// <param name="size">画布边长。</param>
    /// <param name="scale">坐标缩放比例。</param>
    private void FillPath(uint[] canvas, int size, double scale)
    {
        // 水平扫描线只检查跨越采样高度的边，填充时隐式连接首尾点。
        int top = Math.Clamp((int)Math.Floor(points.Min(point => point.Y) * scale), 0, size);
        int bottom = Math.Clamp((int)Math.Ceiling(points.Max(point => point.Y) * scale), 0, size);
        var intersections = new List<(double X, int Direction)>();
        for (int y = top; y < bottom; y++)
        {
            intersections.Clear();
            double sampleY = (y + 0.5) / scale;
            for (int index = 0; index < points.Count; index++)
            {
                Point a = points[index], b = points[(index + 1) % points.Count];
                if ((a.Y <= sampleY && b.Y > sampleY) || (b.Y <= sampleY && a.Y > sampleY))
                {
                    intersections.Add((a.X + (sampleY - a.Y) * (b.X - a.X) / (b.Y - a.Y), b.Y > a.Y ? 1 : -1));
                }
            }

            // 同位置交点可依次处理，零宽区间不会绘制，也不会重复混合像素。
            intersections.Sort((a, b) => a.X.CompareTo(b.X));
            int winding = 0;
            for (int index = 0; index + 1 < intersections.Count; index++)
            {
                winding += intersections[index].Direction;
                if (winding == 0) continue;
                int left = Math.Clamp((int)Math.Ceiling(intersections[index].X * scale - 0.5), 0, size);
                int right = Math.Clamp((int)Math.Ceiling(intersections[index + 1].X * scale - 0.5), 0, size);
                for (int x = left; x < right; x++)
                {
                    int target = y * size + x;
                    canvas[target] = Paint.Over(canvas[target], fill!.Sample(new Point((x + 0.5) / scale, sampleY)));
                }
            }
        }
    }
}

/// <summary>使用 ZLibStream 和 PNG 标准块结构编码 RGBA 图像。</summary>
internal static class Png
{
    /// <summary>PNG 文件签名。</summary>
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

    /// <summary>把自上而下的像素编码为八位 RGBA PNG。</summary>
    /// <param name="pixels">RGBA 像素数据。</param>
    /// <param name="size">正方形图像边长。</param>
    /// <returns>完整 PNG 文件字节。</returns>
    public static byte[] Encode(byte[] pixels, int size)
    {
        // 使用无过滤行，避免额外依赖；ZLibStream 输出 PNG 要求的 zlib 数据流。
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, true))
        {
            for (int y = 0; y < size; y++)
            {
                zlib.WriteByte(0);
                zlib.Write(pixels, y * size * 4, size * 4);
            }
        }

        // 所有流由本方法拥有；压缩流关闭后才能取得完整压缩数据。
        using var output = new MemoryStream();
        output.Write(Signature);
        byte[] header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, size);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), size);
        header[8] = 8;
        header[9] = 6;
        WriteChunk(output, "IHDR", header);
        WriteChunk(output, "IDAT", compressed.ToArray());
        WriteChunk(output, "IEND", []);
        return output.ToArray();
    }

    /// <summary>写入带长度与 CRC 的 PNG 块。</summary>
    /// <param name="output">调用方拥有的输出流。</param>
    /// <param name="type">四个 ASCII 字符组成的块类型。</param>
    /// <param name="data">块内容。</param>
    private static void WriteChunk(Stream output, string type, byte[] data)
    {
        // PNG 的整数采用大端；CRC 仅覆盖类型与数据，不包含长度字段。
        byte[] typeBytes = Encoding.ASCII.GetBytes(type);
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, data.Length);
        output.Write(number);
        output.Write(typeBytes);
        output.Write(data);
        uint crc = UpdateCrc(UpdateCrc(0xFFFFFFFF, typeBytes), data) ^ 0xFFFFFFFF;
        BinaryPrimitives.WriteUInt32BigEndian(number, crc);
        output.Write(number);
    }

    /// <summary>更新 PNG 使用的 IEEE CRC-32 校验值。</summary>
    /// <param name="crc">当前未经最终取反的校验值。</param>
    /// <param name="bytes">新增数据。</param>
    /// <returns>更新后的校验值。</returns>
    private static uint UpdateCrc(uint crc, byte[] bytes)
    {
        // 使用 PNG 规范的反射多项式，不依赖第三方哈希库。
        foreach (byte value in bytes)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xEDB88320u);
        }

        return crc;
    }
}

