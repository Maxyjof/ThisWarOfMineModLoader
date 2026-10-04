using System.Text.Json;

namespace ThisWarOfMineModLoader.Mods;

/// <summary>
/// 扫描模组目录并解析清单
/// </summary>
public static class ModCatalog
{
    /// <summary>
    /// 扫描根目录的直接子目录并校验启用模组入口
    /// </summary>
    public static IReadOnlyList<DiscoveredMod> Discover(string root)
    {
        //按路径稳定排序并拒绝模组目录符号链接
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"找不到模组目录：{root}");
        var result = new List<DiscoveredMod>();
        foreach (var directory in Directory.EnumerateDirectories(root).Order(StringComparer.Ordinal))
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"模组目录不能是符号链接：{directory}");
            //跳过没有清单的目录并严格反序列化规范字段
            var manifestFile = Path.Combine(directory, "mod.json");
            if (!File.Exists(manifestFile)) continue;
            var manifest = JsonSerializer.Deserialize<ModManifest>(File.ReadAllText(manifestFile), ModManifest.JsonOptions)
                ?? throw new InvalidDataException($"清单为空：{manifestFile}");
            manifest.Validate();
            //禁用模组不需要有效入口但仍需可解析的清单
            var entry = manifest.Enabled ? ResolveEntry(directory, manifest.Entry) : "";
            result.Add(new(directory, manifest, entry));
        }
        return result;
    }

    /// <summary>
    /// 解析模组内部Lua入口并拒绝目录逃逸和符号链接
    /// </summary>
    public static string ResolveEntry(string directory, string relative)
    {
        //拒绝绝对路径、驱动器限定及特殊路径段
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':'))
            throw new InvalidDataException("入口必须是模组目录内的相对路径。");
        var segments = relative.Replace('\\', '/').Split('/');
        if (segments.Any(s => s is "" or "." or "..")) throw new InvalidDataException("入口含有非法路径段。");
        //逐段校验符号链接避免解析后进入模组目录之外
        var path = Path.GetFullPath(directory);
        foreach (var segment in segments)
        {
            path = Path.Combine(path, segment);
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("入口路径不能经过符号链接。");
        }
        //确认终点是实际存在的Lua文件
        if (!File.Exists(path) || !string.Equals(Path.GetExtension(path), ".lua", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Lua 入口文件不存在或扩展名不正确：{path}");
        return path;
    }
}
