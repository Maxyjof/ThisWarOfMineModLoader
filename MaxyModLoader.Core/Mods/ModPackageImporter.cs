using System.IO.Compression;

namespace MaxyModLoader.Mods;

/// <summary>
/// 将ZIP模组和解压目录安全导入临时模组目录
/// </summary>
public static class ModPackageImporter
{
    private const int MaximumPackages = 128;
    private const int MaximumFilesPerPackage = 10000;
    private const long MaximumFileSize = 128L * 1024 * 1024;
    private const long MaximumPackageSize = 512L * 1024 * 1024;

    /// <summary>
    /// 读取模组目录内的ZIP和直接解压目录并返回临时模组根目录
    /// </summary>
    public static IReadOnlyList<string> ImportAll(string sourceDirectory, string stagingDirectory)
    {
        //先核验输入目录和临时目标边界
        sourceDirectory = Path.GetFullPath(sourceDirectory);
        stagingDirectory = Path.GetFullPath(stagingDirectory);
        if (!Directory.Exists(sourceDirectory)) throw new DirectoryNotFoundException($"找不到模组目录：{sourceDirectory}");
        if ((File.GetAttributes(sourceDirectory) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("模组目录不能是符号链接");
        if (Directory.Exists(stagingDirectory) || File.Exists(stagingDirectory)) throw new IOException("临时导入目录已经存在");

        //只识别Mods目录的顶层ZIP和根目录直接包含清单的模组文件夹
        var archives = Directory.EnumerateFiles(sourceDirectory, "*.zip", SearchOption.TopDirectoryOnly)
            .Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var folders = Directory.EnumerateDirectories(sourceDirectory, "*", SearchOption.TopDirectoryOnly)
            .Order(StringComparer.OrdinalIgnoreCase)
            .Where(directory =>
            {
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("模组目录不能是符号链接");
                return File.Exists(Path.Combine(directory, "mod.json"));
            }).ToArray();
        if (archives.Length + folders.Length > MaximumPackages) throw new InvalidDataException("模组数量超过上限");

        //复制目录模组后再导入ZIP确保两种安装方式共用同一目录发现和依赖校验
        Directory.CreateDirectory(stagingDirectory);
        var imported = new List<string>(archives.Length + folders.Length);
        for (var index = 0; index < folders.Length; index++)
        {
            var destination = Path.Combine(stagingDirectory, $"folder-{index:D3}");
            CopyModFolder(folders[index], destination);
            ValidateImportedMod(stagingDirectory, destination);
            imported.Add(destination);
        }

        //每个ZIP仍只允许一个模组避免不同来源静默覆盖
        for (var index = 0; index < archives.Length; index++)
        {
            var archivePath = archives[index];
            if ((File.GetAttributes(archivePath) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("模组ZIP不能是符号链接");
            var destination = Path.Combine(stagingDirectory, $"zip-{index:D3}");
            ExtractOne(archivePath, destination);
            imported.Add(destination);
        }

        //空列表表示玩家可以只启动管理器而不加载额外模组
        return imported;
    }

    /// <summary>
    /// 安全复制解压目录并限制文件数量体积和链接
    /// </summary>
    private static void CopyModFolder(string source, string destination)
    {
        //复制前完整枚举以便在写入前拒绝危险链接和超限目录
        var root = Path.GetFullPath(source);
        var pending = new Stack<string>();
        var files = new List<(string Source, string Relative, long Length)>();
        pending.Push(root);
        long totalSize = 0;
        while (pending.TryPop(out var directory))
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.OrdinalIgnoreCase))
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("解压模组不能包含符号链接");
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(path);
                    continue;
                }

                //检查相对路径并限制单文件和整个模组大小
                var relative = Normalize(Path.GetRelativePath(root, path).Replace('\\', '/'));
                var length = new FileInfo(path).Length;
                if (length > MaximumFileSize) throw new InvalidDataException("解压模组单个文件超过128MiB");
                totalSize += length;
                if (files.Count >= MaximumFilesPerPackage || totalSize > MaximumPackageSize)
                    throw new InvalidDataException("解压模组文件数量或总展开体积超过上限");
                files.Add((path, relative, length));
            }
        }

        //所有输入已通过边界检查后才在私有临时目录中创建副本
        Directory.CreateDirectory(destination);
        var destinationRoot = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        foreach (var file in files.OrderBy(item => item.Relative, StringComparer.OrdinalIgnoreCase))
        {
            var output = Path.GetFullPath(Path.Combine(destination, file.Relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!output.StartsWith(destinationRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("解压模组路径越界");
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            using var input = File.OpenRead(file.Source);
            using var target = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            CopyBounded(input, target, file.Length);
        }
    }

    /// <summary>
    /// 展开单个ZIP并拒绝路径逃逸链接和多模组布局
    /// </summary>
    private static void ExtractOne(string archivePath, string destination)
    {
        //读取ZIP目录并限制条目数量及总展开大小
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count > MaximumFilesPerPackage) throw new InvalidDataException("模组ZIP条目数量超过上限");
        var files = archive.Entries.Where(entry => !entry.FullName.EndsWith('/') && !entry.FullName.EndsWith('\\')).ToArray();
        if (files.Length is 0 or > MaximumFilesPerPackage) throw new InvalidDataException("模组ZIP文件数量无效");
        if (files.Any(entry => entry.Length < 0 || entry.Length > MaximumFileSize) || files.Sum(entry => entry.Length) > MaximumPackageSize)
            throw new InvalidDataException("模组ZIP展开体积超过上限");

        //仅接受ZIP根目录或唯一一级目录中存在清单的常见打包形式
        var normalized = files.Select(entry => (Entry: entry, Name: Normalize(entry.FullName))).ToArray();
        var manifests = normalized.Where(item => item.Name.Equals("mod.json", StringComparison.OrdinalIgnoreCase) ||
            item.Name.Count(character => character == '/') == 1 && item.Name.EndsWith("/mod.json", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (manifests.Length != 1) throw new InvalidDataException("每个模组ZIP必须在根目录或唯一一级目录中包含一个mod.json");
        var prefix = manifests[0].Name.Equals("mod.json", StringComparison.OrdinalIgnoreCase)
            ? ""
            : manifests[0].Name[..^"mod.json".Length];

        //根清单与一级目录清单都要求ZIP文件属于同一个模组
        if (prefix.Length == 0 && normalized.Any(item => item.Name.EndsWith("/mod.json", StringComparison.OrdinalIgnoreCase)) ||
            prefix.Length > 0 && normalized.Any(item => !item.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("模组ZIP不能混放多个目录或模组");

        //逐个安全写入并拒绝大小写折叠后的重复路径
        Directory.CreateDirectory(destination);
        var root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in normalized)
        {
            if (IsSymbolicLink(item.Entry)) throw new InvalidDataException("模组ZIP不能包含符号链接");
            var relative = prefix.Length == 0 ? item.Name : item.Name[prefix.Length..];
            var output = Path.GetFullPath(Path.Combine(destination, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!output.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !written.Add(output))
                throw new InvalidDataException("模组ZIP路径越界或存在重复文件");

            //先创建父目录再以新建方式写文件禁止静默覆盖
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            using var input = item.Entry.Open();
            using var target = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            CopyBounded(input, target, item.Entry.Length);
        }

        //展开结果还必须通过正式模组清单及入口校验
        if (!File.Exists(Path.Combine(destination, "mod.json"))) throw new InvalidDataException("模组ZIP目录层级不受支持");
        ValidateImportedMod(Path.GetDirectoryName(destination)!, destination);
    }

    /// <summary>
    /// 使用正式目录扫描器校验导入结果
    /// </summary>
    private static void ValidateImportedMod(string stagingDirectory, string destination)
    {
        //确保导入结果恰好识别为单个模组而不依赖来源扩展名
        _ = ModCatalog.Discover(stagingDirectory).Single(mod => mod.Directory == destination);
    }

    /// <summary>
    /// 规范化模组相对路径并拒绝特殊文件名与目录穿越
    /// </summary>
    private static string Normalize(string path)
    {
        //统一分隔符后按Windows文件系统限制每段名称
        var normalized = path.Replace('\\', '/');
        if (normalized.StartsWith('/') || normalized.Contains(':')) throw new InvalidDataException("模组文件包含绝对路径");
        var segments = normalized.Split('/');
        if (segments.Any(segment => segment is "" or "." or ".." || segment.EndsWith('.') || segment.EndsWith(' ') ||
            segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new InvalidDataException("模组文件包含非法路径段");
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
    /// 按清单长度上限复制内容并确认实际字节数
    /// </summary>
    private static void CopyBounded(Stream input, Stream output, long expectedLength)
    {
        //流式复制防止文件变化或伪造ZIP长度导致超限写入
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += read;
            if (total > MaximumFileSize || total > expectedLength) throw new InvalidDataException("模组文件实际大小超过声明");
            output.Write(buffer, 0, read);
        }
        if (total != expectedLength) throw new InvalidDataException("模组文件长度与声明不符");
    }
}
