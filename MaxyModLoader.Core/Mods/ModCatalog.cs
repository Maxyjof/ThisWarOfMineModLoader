using System.Text.Json;

namespace MaxyModLoader.Mods;

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
            //允许清单引用模组目录内的Markdown文件并限制说明体积
            if (manifest.DescriptionFile.Length > 0)
            {
                var descriptionPath = ResolveLocalFile(directory, manifest.DescriptionFile);
                var bytes = File.ReadAllBytes(descriptionPath);
                if (bytes.Length > 16384) throw new InvalidDataException("模组Markdown说明不能超过16KiB");
                try
                {
                    var markdown = new System.Text.UTF8Encoding(false, true).GetString(bytes).TrimStart('\ufeff');
                    manifest = manifest with { Description = markdown };
                }
                catch (System.Text.DecoderFallbackException exception)
                {
                    throw new InvalidDataException("模组Markdown说明必须使用有效UTF8编码", exception);
                }
            }
            //禁用模组不需要有效入口但仍需可解析的清单
            var entry = manifest.Enabled ? ResolveEntry(directory, manifest.Entry) : "";
            //启用模组的每个显式模块都必须属于同一个模组目录
            if (manifest.Enabled)
            {
                foreach (var path in manifest.Modules.Values) _ = ResolveEntry(directory, path);
                if (manifest.NativeContentFile.Length > 0) _ = ResolveLocalFile(directory, manifest.NativeContentFile);
            }
            result.Add(new(directory, manifest, entry));
        }
        return result;
    }

    /// <summary>
    /// 解析模组内部Lua入口并拒绝目录逃逸和符号链接
    /// </summary>
    public static string ResolveEntry(string directory, string relative)
    {
        //文件边界校验与说明文件共用入口额外限定Lua扩展名
        var path = ResolveLocalFile(directory, relative);
        //确认终点是实际存在的Lua文件
        if (!string.Equals(Path.GetExtension(path), ".lua", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Lua 入口扩展名不正确：{path}");
        return path;
    }

    /// <summary>
    /// 解析模组目录内的文本文件并拒绝目录逃逸或符号链接
    /// </summary>
    public static string ResolveLocalFile(string directory, string relative)
    {
        //拒绝绝对路径、驱动器限定及特殊路径段
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':'))
            throw new InvalidDataException("模组文件必须使用目录内的相对路径");
        var segments = relative.Replace('\\', '/').Split('/');
        if (segments.Any(s => s is "" or "." or "..")) throw new InvalidDataException("入口含有非法路径段。");
        //逐段校验符号链接避免解析后进入模组目录之外
        var path = Path.GetFullPath(directory);
        foreach (var segment in segments)
        {
            path = Path.Combine(path, segment);
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("模组文件路径不能经过符号链接");
        }
        //文件必须存在且是普通文件防止说明路径指向目录
        if (!File.Exists(path)) throw new InvalidDataException($"模组文件不存在：{path}");
        return path;
    }
}
