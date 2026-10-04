using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ThisWarOfMineModLoader.Tests;

/// <summary>
/// 使用编译器语法树检查项目中文注释约定
/// </summary>
internal static class CommentConvention
{
    /// <summary>
    /// 校验源码中的类型、方法和构造函数文档以及行注释格式
    /// </summary>
    public static void Verify(string repository)
    {
        //只检查自有源码目录避免扫描构建产物和本机游戏资源
        foreach (var directory in new[] { "ThisWarOfMineModLoader", "ThisWarOfMineModLoader.Cli", "ThisWarOfMineModLoader.Tests" })
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(repository, directory), "*.cs", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(repository, file);
                if (relative.Split(Path.DirectorySeparatorChar).Any(segment => segment is "obj" or "bin")) continue;
                var root = CSharpSyntaxTree.ParseText(File.ReadAllText(file)).GetRoot();

                //按实际语法节点检查文档避免正则把字符串内容当作方法
                foreach (var member in root.DescendantNodes().Where(node => node is BaseTypeDeclarationSyntax or MethodDeclarationSyntax or ConstructorDeclarationSyntax or LocalFunctionStatementSyntax))
                {
                    var documentation = member.GetLeadingTrivia().Where(trivia => trivia.GetStructure() is DocumentationCommentTriviaSyntax).ToArray();
                    if (documentation.Length == 0) throw new InvalidDataException($"缺少XML注释：{relative}：{member.GetLocation().GetLineSpan().StartLinePosition.Line + 1}");
                    foreach (var line in documentation.SelectMany(trivia => trivia.ToFullString().Split('\n')))
                    {
                        var content = line.Trim().TrimStart('/').Trim();
                        if (content.Contains('<') && !Regex.IsMatch(content, "^</?[A-Za-z][A-Za-z0-9]*(?: [^<>]*)?/?>$"))
                            throw new InvalidDataException($"XML标记必须独占一行：{relative}：{content}");
                        CheckText(content, relative);
                    }
                }

                //行注释不能在双斜杠后留空格文档注释保持XML展示格式
                foreach (var trivia in root.DescendantTrivia().Where(trivia => trivia.IsKind(SyntaxKind.SingleLineCommentTrivia)))
                {
                    var content = trivia.ToString()[2..];
                    if (content.StartsWith(' ')) throw new InvalidDataException($"双斜杠后不能留空格：{relative}");
                    CheckText(content, relative);
                }
            }
        }
    }

    /// <summary>
    /// 检查中文注释的标点和中英文间距
    /// </summary>
    private static void CheckText(string content, string file)
    {
        //XML标记和空行不属于自然语言内容无需检查句末标点
        if (string.IsNullOrWhiteSpace(content) || content.StartsWith('<')) return;
        if (!Regex.IsMatch(content, "[\\u4e00-\\u9fff]") || content.EndsWith('。') || content.EndsWith('.') ||
            content.Contains('—') || content.Contains('–') ||
            Regex.IsMatch(content, "[\\u4e00-\\u9fff] +[A-Za-z0-9]|[A-Za-z0-9] +[\\u4e00-\\u9fff]"))
            throw new InvalidDataException($"注释不符合中文标点或间距规范：{file}：{content}");
    }
}
