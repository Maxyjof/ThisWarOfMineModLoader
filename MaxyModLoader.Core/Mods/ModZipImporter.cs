using System.IO.Compression;

namespace MaxyModLoader.Mods;

/// <summary>
/// 将每个模组压缩包安全展开到独立的临时模组目录
/// </summary>
public static class ModZipImporter
{
    private const int MaximumArchives = 128;
    private const int MaximumEntriesPerArchive = 10000;
    private const long MaximumFileSize = 128L * 1024 * 1024;
    private const long MaximumArchiveSize = 512L * 1024 * 1024;

    /// <summary>
    /// 读取模组目录内的ZIP文件并返回展开后的模组根目录
    /// </summary>
    public static IReadOnlyList<string> ExtractAll(string archiveDirectory, string stagingDirectory)
    {
        //先核验输入目录和目标目录边界
        archiveDirectory = Path.GetFullPath(archiveDirectory);
        stagingDirectory = Path.GetFullPath(stagingDirectory);
        if (!Directory.Exists(archiveDirectory)) throw new DirectoryNotFoundException($"找不到模组压缩包目录：{archiveDirectory}");
        if ((File.GetAttributes(archiveDirectory) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("模组目录不能是符号链接");
        if (Directory.Exists(stagingDirectory) || File.Exists(stagingDirectory)) throw new IOException("临时解包目录已经存在");

        //限制压缩包数量并按稳定顺序处理
        var archives = Directory.EnumerateFiles(archiveDirectory, "*.zip", SearchOption.TopDirectoryOnly)
            .Order(StringComparer.OrdinalIgnoreCase).ToArray();
        if (archives.Length > MaximumArchives) throw new InvalidDataException("模组压缩包数量超过上限");
        Directory.CreateDirectory(stagingDirectory);
        var imported = new List<string>();

        //每个压缩包只允许包含一个模组避免多个模组相互覆盖
        for (var index = 0; index < archives.Length; index++)
        {
            var archivePath = archives[index];
            if ((File.GetAttributes(archivePath) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("模组压缩包不能是符号链接");
            var destination = Path.Combine(stagingDirectory, $"mod-{index:D3}");
            ExtractOne(archivePath, destination);
            imported.Add(destination);
        }

        //返回空列表表示玩家可以只启动管理器而不加载额外模组
        return imported;
    }

    /// <summary>
    /// 展开单个压缩包并拒绝路径逃逸、链接和多模组布局
    /// </summary>
    private static void ExtractOne(string archivePath, string destination)
    {
        //读取ZIP目录并限制条目数量及总展开大小
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count > MaximumEntriesPerArchive) throw new InvalidDataException("模组压缩包条目数量超过上限");
        var files = archive.Entries.Where(entry => !entry.FullName.EndsWith('/') && !entry.FullName.EndsWith('\\')).ToArray();
        if (files.Length is 0 or > MaximumEntriesPerArchive) throw new InvalidDataException("模组压缩包文件数量无效");
        if (files.Any(entry => entry.Length < 0 || entry.Length > MaximumFileSize) || files.Sum(entry => entry.Length) > MaximumArchiveSize)
            throw new InvalidDataException("模组压缩包展开体积超过上限");

        //仅接受ZIP根目录或唯一一级目录中存在清单的常见打包形式
        var normalized = files.Select(entry => (Entry: entry, Name: Normalize(entry.FullName))).ToArray();
        var manifests = normalized.Where(item => item.Name.Equals("mod.json", StringComparison.OrdinalIgnoreCase) ||
            item.Name.Count(character => character == '/') == 1 && item.Name.EndsWith("/mod.json", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (manifests.Length != 1) throw new InvalidDataException("每个模组ZIP必须在根目录或唯一一级目录中包含一个mod.json");
        var prefix = manifests[0].Name.Equals("mod.json", StringComparison.OrdinalIgnoreCase)
            ? ""
            : manifests[0].Name[..^"mod.json".Length];

        //根清单与一级目录清单都要求压缩包内文件属于同一个模组
        if (prefix.Length == 0 && normalized.Any(item => item.Name.EndsWith("/mod.json", StringComparison.OrdinalIgnoreCase)) ||
            prefix.Length > 0 && normalized.Any(item => !item.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("模组压缩包不能混放多个目录或模组");

        //逐个安全写入文件并拒绝大小写折叠后的重复路径
        Directory.CreateDirectory(destination);
        var root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in normalized)
        {
            if (IsSymbolicLink(item.Entry)) throw new InvalidDataException("模组压缩包不能包含符号链接");
            var relative = prefix.Length == 0 ? item.Name : item.Name[prefix.Length..];
            var output = Path.GetFullPath(Path.Combine(destination, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!output.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !written.Add(output))
                throw new InvalidDataException("模组压缩包路径越界或存在重复文件");

            //先创建父目录再以新建方式写文件禁止静默覆盖
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            using var input = item.Entry.Open();
            using var target = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            CopyBounded(input, target, item.Entry.Length);
        }

        //展开结果还必须通过正式模组清单及入口校验
        if (!File.Exists(Path.Combine(destination, "mod.json"))) throw new InvalidDataException("模组压缩包目录层级不受支持");
        _ = ModCatalog.Discover(Path.GetDirectoryName(destination)!).Single(mod => mod.Directory == destination);
    }

    /// <summary>
    /// 规范化压缩包路径并拒绝特殊文件名与目录穿越
    /// </summary>
    private static string Normalize(string path)
    {
        //ZIP统一分隔符后按Windows文件系统限制每段名称
        var normalized = path.Replace('\\', '/');
        if (normalized.StartsWith('/') || normalized.Contains(':')) throw new InvalidDataException("模组压缩包包含绝对路径");
        var segments = normalized.Split('/');
        if (segments.Any(segment => segment is "" or "." or ".." || segment.EndsWith('.') || segment.EndsWith(' ') ||
            segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new InvalidDataException("模组压缩包包含非法路径段");
        return string.Join('/', segments);
    }

    /// <summary>
    /// 识别ZIP外部属性中声明的符号链接
    /// </summary>
    private static bool IsSymbolicLink(ZipArchiveEntry entry)
    {
        //Unix文件类型字段为符号链接时不将其写入Windows磁盘
        return ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000;
    }

    /// <summary>
    /// 按清单长度上限复制压缩内容并确认实际解压字节数
    /// </summary>
    private static void CopyBounded(Stream input, Stream output, long expectedLength)
    {
        //流式复制防止伪造ZIP长度导致超限写入
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += read;
            if (total > MaximumFileSize || total > expectedLength) throw new InvalidDataException("模组压缩包实际展开大小超过声明");
            output.Write(buffer, 0, read);
        }
        if (total != expectedLength) throw new InvalidDataException("模组压缩包文件长度与目录记录不符");
    }
}
