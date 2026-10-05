using System.Text.Json;
using Markdig;
using Markdig.Extensions.EmphasisExtras;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MaxyModLoader.Runtime;

namespace MaxyModLoader.Mods;

/// <summary>
/// 将标准Markdown语法树转换为游戏可渲染的结构化文档
/// </summary>
public static class MarkdownContent
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables().UseTaskLists().UseEmphasisExtras(EmphasisExtraOptions.Strikethrough).UseCjkFriendlyEmphasis().Build();

    /// <summary>
    /// 描述独立块级元素及其嵌套内容
    /// </summary>
    public sealed record BlockNode(string Kind, int Level = 0, bool Ordered = false, int Start = 1,
        bool Header = false, string Language = "", string Alignment = "left")
    {
        public List<BlockNode> Children { get; } = [];
        public List<RunNode> Runs { get; } = [];
    }

    /// <summary>
    /// 描述可组合的行内格式和原始文字
    /// </summary>
    public sealed record RunNode(string Text, bool Strong = false, bool Emphasis = false, bool Strike = false,
        bool Code = false, string Link = "", string Image = "");

    /// <summary>
    /// 按CommonMark与表格任务列表扩展解析说明内容
    /// </summary>
    public static IReadOnlyList<BlockNode> Parse(string source)
    {
        //限制单份文档与结构深度避免游戏界面创建无限控件
        if (source.Length > 16384) throw new InvalidDataException("Markdown说明超过长度上限");
        var document = Markdown.Parse(source, Pipeline);
        var result = document.Select(block => ConvertBlock(block, 0)).Where(block => block is not null).Cast<BlockNode>().ToArray();
        if (CountNodes(result) > 2048) throw new InvalidDataException("Markdown说明结构过于复杂");
        return result;
    }

    /// <summary>
    /// 转换块级语义并保留列表引用表格和代码的独立结构
    /// </summary>
    private static BlockNode? ConvertBlock(Block block, int depth)
    {
        //链接定义由解析器关联到行内节点自身不产生可见块
        if (depth > 24) throw new InvalidDataException("Markdown嵌套超过上限");
        if (block is LinkReferenceDefinitionGroup) return null;
        BlockNode node = block switch
        {
            HeadingBlock heading => new("heading", Level: heading.Level),
            ParagraphBlock => new("paragraph"),
            FencedCodeBlock fenced => new("code", Language: fenced.Info ?? ""),
            CodeBlock => new("code"),
            ThematicBreakBlock => new("rule"),
            QuoteBlock => new("quote"),
            ListBlock list => new("list", Ordered: list.IsOrdered, Start: int.TryParse(list.OrderedStart, out var start) ? start : 1),
            ListItemBlock => new("item"),
            Table => new("table"),
            TableRow row => new("table_row", Header: row.IsHeader),
            TableCell => new("table_cell"),
            HtmlBlock => new("code", Language: "HTML"),
            _ => new("paragraph")
        };
        //代码与HTML保持字面内容不交给游戏执行或解释
        if (block is CodeBlock code) node.Runs.Add(new(code.Lines.ToString().Replace("\r\n", "\n").Replace('\r', '\n'), Code: true));
        else if (block is HtmlBlock html) node.Runs.Add(new(html.Lines.ToString().Replace("\r\n", "\n").Replace('\r', '\n'), Code: true));
        else if (block is LeafBlock leaf && leaf.Inline is not null) AppendInlines(leaf.Inline, node.Runs, new(""), depth);
        if (block is ContainerBlock container)
        {
            foreach (var child in container)
                if (ConvertBlock(child, depth + 1) is { } converted) node.Children.Add(converted);
        }
        //表格列对齐只影响单元格布局不会改写文字或丢弃空单元格
        if (block is Table table)
            foreach (var row in node.Children)
                for (var index = 0; index < row.Children.Count && index < table.ColumnDefinitions.Count; index++)
                    row.Children[index] = row.Children[index] with
                        { Alignment = table.ColumnDefinitions[index].Alignment?.ToString().ToLowerInvariant() ?? "left" };
        return node;
    }

    /// <summary>
    /// 展开行内语法树并保留嵌套强调与链接的组合属性
    /// </summary>
    private static void AppendInlines(ContainerInline container, List<RunNode> result, RunNode inherited, int depth)
    {
        //逐节点读取解析器结果因此转义实体引用式链接不会再次被正则误判
        if (depth > 24) throw new InvalidDataException("Markdown行内嵌套超过上限");
        for (var inline = container.FirstChild; inline is not null; inline = inline.NextSibling)
        {
            switch (inline)
            {
                case LiteralInline literal: result.Add(inherited with { Text = literal.Content.ToString() }); break;
                case HtmlEntityInline entity: result.Add(inherited with { Text = entity.Transcoded.ToString() }); break;
                case CodeInline code: result.Add(inherited with { Text = code.Content, Code = true }); break;
                case HtmlInline html: result.Add(inherited with { Text = html.Tag }); break;
                case LineBreakInline line: result.Add(inherited with { Text = line.IsHard ? "\n" : " " }); break;
                case TaskList task: result.Add(inherited with { Text = task.Checked ? "[x] " : "[ ] " }); break;
                case AutolinkInline link: result.Add(inherited with { Text = link.Url, Link = SafeLink(link.Url) }); break;
                case EmphasisInline emphasis:
                    //粗体斜体删除线独立组合避免嵌套强调覆盖外层格式
                    AppendInlines(emphasis, result, inherited with
                    {
                        Strong = inherited.Strong || emphasis.DelimiterChar != '~' && emphasis.DelimiterCount >= 2,
                        Emphasis = inherited.Emphasis || emphasis.DelimiterChar != '~' && emphasis.DelimiterCount == 1,
                        Strike = inherited.Strike || emphasis.DelimiterChar == '~'
                    }, depth + 1);
                    break;
                case LinkInline link:
                    var url = SafeLink(link.Url ?? "");
                    AppendInlines(link, result, inherited with { Link = link.IsImage ? "" : url, Image = link.IsImage ? link.Url ?? "" : "" }, depth + 1);
                    break;
                case ContainerInline nested: AppendInlines(nested, result, inherited, depth + 1); break;
                default: result.Add(inherited with { Text = inline.ToString() ?? "" }); break;
            }
        }
    }

    /// <summary>
    /// 仅保留普通网页链接供界面显示地址
    /// </summary>
    private static string SafeLink(string value)
    {
        //说明中的脚本和文件协议不成为可操作链接
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? value : "";
    }

    /// <summary>
    /// 统计文档中的块级和行内节点数量
    /// </summary>
    private static int CountNodes(IEnumerable<BlockNode> nodes)
    {
        //已在转换阶段限制深度此处汇总节点以限制渲染工作量
        return nodes.Sum(node => 1 + node.Runs.Count + CountNodes(node.Children));
    }

    /// <summary>
    /// 将结构化文档编码为不含可执行文本的Lua数据表
    /// </summary>
    public static string Encode(IReadOnlyList<BlockNode> nodes)
    {
        //仅允许解析器生成的数据模型进入嵌入式序列化
        var data = JsonSerializer.SerializeToElement(nodes, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        return EncodeValue(data);
    }

    /// <summary>
    /// 序列化文档数组对象与基础值而不改变模组配置的限制
    /// </summary>
    private static string EncodeValue(JsonElement value)
    {
        //所有字符串沿用逐字节转义防止Markdown定界符进入Lua源码语法
        return value.ValueKind switch
        {
            JsonValueKind.Array => "{" + string.Join(',', value.EnumerateArray().Select(EncodeValue)) + "}",
            JsonValueKind.Object => "{" + string.Join(',', value.EnumerateObject().Select(pair => "[" + LuaBundle.Quote(pair.Name) + "]=" + EncodeValue(pair.Value))) + "}",
            _ => LuaBundle.EncodeSetting(value)
        };
    }
}
