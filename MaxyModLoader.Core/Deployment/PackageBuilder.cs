using System.Security.Cryptography;
using System.Text.Json;
using MaxyModLoader.Archives;
using MaxyModLoader.Mods;
using MaxyModLoader.Runtime;

namespace MaxyModLoader.Deployment;

/// <summary>
/// 保存离线构建容器的原始和生成文件指纹
/// </summary>
public sealed record PackageManifest(string Container, string OriginalIndexSha256, string OriginalDataSha256,
    string BuiltIndexSha256, string BuiltDataSha256, string[] Mods);

/// <summary>
/// 将模组加载计划编译为可验证的实验部署包
/// </summary>
public static class PackageBuilder
{
    /// <summary>
    /// 在新目录内创建完整容器和指纹清单
    /// </summary>
    public static PackageManifest Build(string sourceBase, uint mainHash, string modDirectory, string outputDirectory)
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

        //编译后写入独立目录并确认源文件没有在构建期间发生变化
        var compiled = LuaBundle.Compile(source.Read(mainHash), plan);
        source.WriteReplacement(mainHash, compiled, outputBase);
        if (originalIndex != Fingerprint(source.BasePath + ".idx") || originalData != Fingerprint(source.BasePath + ".dat"))
            throw new IOException("源容器在构建过程中发生变化请删除输出后重试。");

        //记录全部文件指纹供安装与恢复时检查身份
        var manifest = new PackageManifest(name, originalIndex, originalData,
            Fingerprint(outputBase + ".idx"), Fingerprint(outputBase + ".dat"),
            plan.Ordered.Select(m => m.Manifest.Id).ToArray());
        File.WriteAllText(Path.Combine(outputDirectory, "package.json"), JsonSerializer.Serialize(manifest, ModManifest.JsonOptions));
        return manifest;
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
