using System.Text.Json;

namespace ThisWarOfMineModLoader.Mods;

public static class ModCatalog
{
    public static IReadOnlyList<DiscoveredMod> Discover(string root)
    {
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException($"找不到模组目录：{root}");
        var result = new List<DiscoveredMod>();
        foreach (var directory in Directory.EnumerateDirectories(root).Order(StringComparer.Ordinal))
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"模组目录不能是符号链接：{directory}");
            var manifestFile = Path.Combine(directory, "mod.json");
            if (!File.Exists(manifestFile)) continue;
            var manifest = JsonSerializer.Deserialize<ModManifest>(File.ReadAllText(manifestFile), ModManifest.JsonOptions)
                ?? throw new InvalidDataException($"清单为空：{manifestFile}");
            manifest.Validate();
            // 禁用模组不需要有效的入口，但其清单仍必须可解析。
            var entry = manifest.Enabled ? ResolveEntry(directory, manifest.Entry) : "";
            result.Add(new(directory, manifest, entry));
        }
        return result;
    }

    public static string ResolveEntry(string directory, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':'))
            throw new InvalidDataException("入口必须是模组目录内的相对路径。");
        var segments = relative.Replace('\\', '/').Split('/');
        if (segments.Any(s => s is "" or "." or "..")) throw new InvalidDataException("入口含有非法路径段。");
        var path = Path.GetFullPath(directory);
        foreach (var segment in segments)
        {
            path = Path.Combine(path, segment);
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("入口路径不能经过符号链接。");
        }
        if (!File.Exists(path) || !string.Equals(Path.GetExtension(path), ".lua", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Lua 入口文件不存在或扩展名不正确：{path}");
        return path;
    }
}
