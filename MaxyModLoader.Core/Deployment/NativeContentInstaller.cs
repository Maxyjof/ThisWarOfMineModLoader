using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using MaxyModLoader.Mods;

namespace MaxyModLoader.Deployment;

/// <summary>
/// 保存原生登记列表的前后指纹用于中断恢复
/// </summary>
public sealed record NativeInstallState(bool ListExisted, string OriginalListSha256, string InstalledListSha256);

/// <summary>
/// 将加载器原生差异包交给游戏已核验的模组挂载入口
/// </summary>
public static class NativeContentInstaller
{
    private const string Prefix = "MaxyModLoaderNative_";
    private const string Registry = "MaxyModLoaderNative|MaxyModLoader原生内容|由加载器管理物品配方和掉落请通过加载器恢复|enabled|local|SWG";

    /// <summary>
    /// 在任何文件写入前核验固定产物与原生列表身份
    /// </summary>
    public static NativeInstallState? Prepare(string game, string package, NativePackage? native)
    {
        //仅含Lua的模组组合不需要原生登记和附加文件
        if (native is null) return null;
        Validate(native);
        Directory.CreateDirectory(Path.Combine(game, "Mods"));
        if ((File.GetAttributes(Path.Combine(game, "Mods")) & FileAttributes.ReparsePoint) != 0) throw new IOException("游戏Mods目录不能是链接");
        foreach (var (name, fingerprint) in native.Files)
        {
            if (File.Exists(Target(game, name))) throw new IOException("原生内容目标已存在需要先恢复");
            if (PackageBuilder.Fingerprint(Path.Combine(package, "native", name)) != fingerprint) throw new InvalidDataException("原生内容包指纹不符");
        }
        var path = ListPath(game); var original = ReadList(path);
        //不改变其他模组顺序启用状态或描述只有自有登记条目追加到末尾
        var updated = AppendRegistry(original);
        return new(File.Exists(path), Hash(original), Hash(updated));
    }

    /// <summary>
    /// 在安装日志生成之前备份登记列表
    /// </summary>
    public static void Backup(string game, string backup, NativeInstallState? state)
    {
        //备份逐字节保存不做编码转换避免恢复时改变用户文本
        if (state is null) return;
        var bytes = ReadList(ListPath(game));
        if (Hash(bytes) != state.OriginalListSha256) throw new IOException("原生模组列表在备份前发生变化");
        File.WriteAllBytes(Path.Combine(backup, "native-Mods.list"), bytes);
    }

    /// <summary>
    /// 写入全部原生文件后最后登记挂载条目
    /// </summary>
    public static void Install(string game, string package, NativePackage? native, NativeInstallState? state)
    {
        //安装日志必须在调用本方法前持久化每个新文件都有可恢复所有权
        if (native is null || state is null) return;
        Validate(native);
        var original = ReadList(ListPath(game));
        if (Hash(original) != state.OriginalListSha256) throw new IOException("原生模组列表在安装期间发生变化");
        foreach (var (name, fingerprint) in native.Files)
        {
            //同卷临时文件核验完成后才出现目标文件避免中断留下半个容器
            CopyFile(Path.Combine(package, "native", name), Target(game, name), fingerprint);
            if (PackageBuilder.Fingerprint(Target(game, name)) != fingerprint) throw new IOException("原生内容写入复读失败");
        }
        var updated = AppendRegistry(original);
        if (Hash(updated) != state.InstalledListSha256) throw new InvalidDataException("原生登记内容与安装日志不符");
        ReplaceList(ListPath(game), updated);
    }

    /// <summary>
    /// 在恢复任何主容器之前拒绝第三方改动
    /// </summary>
    public static void CheckRestore(string game, string backup, NativePackage? native, NativeInstallState? state)
    {
        //原生清单和登记状态必须成对存在防止损坏日志隐去所有权
        if ((native is null) != (state is null)) throw new InvalidDataException("原生恢复日志不完整");
        if (native is null || state is null) return;
        Validate(native);
        if ((File.GetAttributes(Path.Combine(game, "Mods")) & FileAttributes.ReparsePoint) != 0) throw new IOException("原生恢复目录不能是链接");
        foreach (var (name, fingerprint) in native.Files)
            if (File.Exists(Target(game, name)) && PackageBuilder.Fingerprint(Target(game, name)) != fingerprint)
                throw new InvalidDataException("原生内容已有第三方修改停止恢复");
        var original = File.ReadAllBytes(Path.Combine(backup, "native-Mods.list"));
        if (Hash(original) != state.OriginalListSha256) throw new InvalidDataException("原生登记备份指纹不符");
        var current = Hash(ReadList(ListPath(game)));
        if (current != state.OriginalListSha256 && current != state.InstalledListSha256)
            throw new InvalidDataException("原生模组列表已有其他改动停止恢复");
    }

    /// <summary>
    /// 恢复原列表并仅移除本次持有的固定文件
    /// </summary>
    public static void Restore(string game, string backup, NativePackage? native, NativeInstallState? state)
    {
        //重复中断恢复允许文件已被移除但不允许覆盖未知内容
        CheckRestore(game, backup, native, state);
        if (native is null || state is null) return;
        var path = ListPath(game);
        if (state.ListExisted) ReplaceList(path, File.ReadAllBytes(Path.Combine(backup, "native-Mods.list")));
        else if (File.Exists(path)) File.Delete(path);
        foreach (var name in native.Files.Keys)
            if (File.Exists(Target(game, name))) File.Delete(Target(game, name));
    }

    /// <summary>
    /// 校验固定文件集合和物品标识
    /// </summary>
    private static void Validate(NativePackage native)
    {
        //清单文件名不能控制安装目标目录
        if (native.Files is null || !native.Files.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(NativeContentCompiler.FileNames) ||
            native.Files.Values.Any(value => value is null || !Regex.IsMatch(value, "^[0-9A-F]{64}$")) ||
            native.Items is null || native.Items.Length > 1024 ||
            native.Items.Any(item => !NativeContent.ValidName(item) || !item.StartsWith("MML_", StringComparison.Ordinal)))
            throw new InvalidDataException("原生内容清单无效");
    }

    /// <summary>
    /// 读取有界登记文件并拒绝链接
    /// </summary>
    private static byte[] ReadList(string path)
    {
        //不存在的列表按空文件备份恢复时保留不存在的原状态
        if (!File.Exists(path)) return [];
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 || new FileInfo(path).Length > 4 * 1024 * 1024)
            throw new InvalidDataException("原生模组列表是链接或过大");
        return File.ReadAllBytes(path);
    }

    /// <summary>
    /// 保留原字节并追加一条官方格式的自有登记
    /// </summary>
    private static byte[] AppendRegistry(byte[] original)
    {
        //UTF8解码只用于检测重复条目输出仍保留原始字节
        var text = new UTF8Encoding(false, true).GetString(original);
        if (text.Split('\n').Any(line => line.TrimStart('\ufeff').StartsWith("MaxyModLoaderNative|", StringComparison.Ordinal)))
            throw new InvalidDataException("原生登记条目已存在");
        var separator = original.Length > 0 && original[^1] != 10 ? "\r\n" : "";
        return original.Concat(Encoding.UTF8.GetBytes(separator + Registry + "\r\n")).ToArray();
    }

    /// <summary>
    /// 使用同卷临时文件原子替换登记列表
    /// </summary>
    private static void ReplaceList(string path, byte[] bytes)
    {
        //中断前后均是完整已知列表不暴露半行登记
        var temporary = path + ".mml-" + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllBytes(temporary, bytes); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    /// <summary>
    /// 核验临时原生产物后创建不可覆盖的目标文件
    /// </summary>
    private static void CopyFile(string source, string target, string fingerprint)
    {
        //新文件安装不会替换已经存在的玩家文件
        var temporary = target + ".mml-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.Copy(source, temporary, false);
            if (PackageBuilder.Fingerprint(temporary) != fingerprint) throw new IOException("原生临时文件指纹不符");
            File.Move(temporary, target, false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    /// <summary>
    /// 返回自有原生容器的固定绝对目标
    /// </summary>
    private static string Target(string game, string name)
    {
        //名称已经通过固定集合校验不会包含目录分隔符
        return Path.GetFullPath(Path.Combine(game, "Mods", Prefix + name));
    }

    /// <summary>
    /// 返回官方登记文件路径
    /// </summary>
    private static string ListPath(string game)
    {
        //官方Storyteller和当前EXE均使用同一路径
        return Path.GetFullPath(Path.Combine(game, "Mods", "Mods.list"));
    }

    /// <summary>
    /// 对原始列表字节计算可复读身份
    /// </summary>
    private static string Hash(byte[] bytes)
    {
        //空文件也拥有固定指纹不使用空字符串代表其内容
        return Convert.ToHexString(SHA256.HashData(bytes));
    }
}
