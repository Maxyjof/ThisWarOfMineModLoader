using System.Security.Cryptography;
using System.Text.Json;
using MaxyModLoader.Archives;
using MaxyModLoader.Mods;
using MaxyModLoader.Runtime;

namespace MaxyModLoader.Deployment;

/// <summary>
/// 保存离线构建容器的原始和生成文件指纹
/// </summary>
public sealed record ContainerManifest(string Container, string OriginalIndexSha256, string OriginalDataSha256,
    string BuiltIndexSha256, string BuiltDataSha256);

/// <summary>
/// 保存当前部署包必需的双容器与模组清单
/// </summary>
public sealed record PackageManifest(ContainerManifest Scripts, ContainerManifest Textures, string[] Mods, NativePackage? Native);

/// <summary>
/// 将模组加载计划编译为可验证的实验部署包
/// </summary>
public static class PackageBuilder
{
    /// <summary>
    /// 在新目录内创建完整容器和指纹清单
    /// </summary>
    public static PackageManifest Build(string sourceBase, uint mainHash, string modDirectory, string outputDirectory,
        string? resourceDirectory = null)
    {
        //先验证计划和源容器输出目录必须不存在
        var plan = LoadPlanner.Create(ModCatalog.Discover(modDirectory));
        if (!plan.IsValid) throw new InvalidDataException(string.Join(Environment.NewLine, plan.Errors));
        var source = LiquidArchive.Open(sourceBase);
        if (Directory.Exists(outputDirectory) || File.Exists(outputDirectory)) throw new IOException("部署包目录已存在请使用新的路径。");
        var name = Path.GetFileName(source.BasePath);
        var outputBase = Path.Combine(outputDirectory, name);
        var originalIndex = Fingerprint(source.BasePath + ".idx");
        var originalData = Fingerprint(source.BasePath + ".dat");
        //原生内容由本机官方工具编译成独立差异包不会覆盖主脚本资源
        var native = NativeContentCompiler.Compile(Path.GetDirectoryName(source.BasePath)!, plan, outputDirectory);

        //编译后写入独立目录并确认源文件没有在构建期间发生变化
        var compiled = LuaBundle.Compile(source.Read(mainHash), plan);
        var resources = ReadBuiltinResources();
        if (resourceDirectory is not null)
            foreach (var (hash, bytes) in ReadResources(resourceDirectory))
                if (!resources.TryAdd(hash, bytes)) throw new InvalidDataException("外部资源与加载器内置资源哈希冲突");
        //原版纹理查找限定textures挂载点不能将图片追加到common脚本容器
        var textureBase = Path.Combine(Path.GetDirectoryName(source.BasePath)!, "textures-s3");
        var textureArchive = LiquidArchive.Open(textureBase);
        var originalTextureIndex = Fingerprint(textureBase + ".idx");
        var originalTextureData = Fingerprint(textureBase + ".dat");
        source.WriteReplacement(mainHash, compiled, outputBase);
        var textureOutput = Path.Combine(outputDirectory, "textures-s3");
        textureArchive.WriteAdditions(resources, textureOutput);
        if (originalIndex != Fingerprint(source.BasePath + ".idx") || originalData != Fingerprint(source.BasePath + ".dat"))
            throw new IOException("源容器在构建过程中发生变化请删除输出后重试。");
        if (originalTextureIndex != Fingerprint(textureBase + ".idx") || originalTextureData != Fingerprint(textureBase + ".dat"))
            throw new IOException("原版纹理容器在构建期间发生变化");

        //记录全部文件指纹供安装与恢复时检查身份
        var manifest = new PackageManifest(
            new ContainerManifest(name, originalIndex, originalData,
                Fingerprint(outputBase + ".idx"), Fingerprint(outputBase + ".dat")),
            new ContainerManifest("textures-s3", originalTextureIndex, originalTextureData,
                Fingerprint(textureOutput + ".idx"), Fingerprint(textureOutput + ".dat")),
            plan.Ordered.Select(m => m.Manifest.Id).ToArray(), native);
        File.WriteAllText(Path.Combine(outputDirectory, "package.json"), JsonSerializer.Serialize(manifest, ModManifest.JsonOptions));
        return manifest;
    }

    /// <summary>
    /// 将内置界面素材转换为原生纹理保证普通构建也能显示背景
    /// </summary>
    private static Dictionary<uint, byte[]> ReadBuiltinResources()
    {
        //素材随CLI程序集分发不依赖开发目录或额外命令行参数
        var assembly = typeof(PackageBuilder).Assembly;
        var result = new Dictionary<uint, byte[]>();
        const string prefix = "MaxyModLoader.Assets.";
        foreach (var name in assembly.GetManifestResourceNames().Where(name => name.StartsWith(prefix, StringComparison.Ordinal)))
        {
            using var input = assembly.GetManifestResourceStream(name)!;
            using var buffer = new MemoryStream();
            input.CopyTo(buffer);
            var path = "UI/MaxyModLoader/ModManager/" + Path.ChangeExtension(name[prefix.Length..], ".texture");
            result.Add(ResourceHash.Compute(path), LiquidTexture.FromDds(buffer.ToArray()));
        }
        return result;
    }

    /// <summary>
    /// 转换外部DDS资源并使用原生纹理路径计算容器哈希
    /// </summary>
    private static Dictionary<uint, byte[]> ReadResources(string directory)
    {
        //拒绝不存在的资源根目录以及根目录本身的链接
        var root = Path.GetFullPath(directory);
        if (!Directory.Exists(root) || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("自定义资源目录不存在或是链接");
        var result = new Dictionary<uint, byte[]>();
        long totalBytes = 0;

        /// <summary>
        /// 递归收集普通目录中的DDS文件并校验目录边界
        /// </summary>
        void Visit(string current, int depth)
        {
            if (depth > 16) throw new InvalidDataException("自定义资源目录层级超出上限");
            foreach (var file in Directory.EnumerateFiles(current).Order(StringComparer.Ordinal))
            {
                //拒绝链接文件与未核验格式避免将本机外部文件打入游戏容器
                if (result.Count >= 4096) throw new InvalidDataException("自定义资源文件数量超出上限");
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0 ||
                    !Path.GetExtension(file).Equals(".dds", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"自定义资源只接受普通DDS文件：{file}");
                var info = new FileInfo(file);
                totalBytes += info.Length;
                if (info.Length is <= 0 or > 64 * 1024 * 1024 || totalBytes > 256 * 1024 * 1024)
                    throw new InvalidDataException("自定义DDS资源超出文件或总大小上限");
                var bytes = File.ReadAllBytes(file);
                var texture = LiquidTexture.FromDds(bytes);
                var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
                var hash = ResourceHash.Compute(Path.ChangeExtension(relative, ".texture"));
                if (!result.TryAdd(hash, texture)) throw new InvalidDataException($"自定义资源路径哈希重复：{relative}");
            }
            foreach (var child in Directory.EnumerateDirectories(current).Order(StringComparer.Ordinal))
            {
                //目录链接可能在遍历过程中逃逸资源根目录
                if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException($"自定义资源路径不能经过链接：{child}");
                Visit(child, depth + 1);
            }
        }

        Visit(root, 0);
        return result;
    }

    /// <summary>
    /// 流式计算文件的SHA256指纹
    /// </summary>
    public static string Fingerprint(string path)
    {
        //避免将大型游戏资源整个读入内存
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
