using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace MaxyModLoader.Deployment;

/// <summary>
/// 在已核验游戏版本上备份并替换原生启动程序
/// </summary>
public static class GameExecutableInstaller
{
    public const string GameFingerprint = "7E114E63D2371B3A31C6070011BA3869FECB2248895AFC0171B248C3E0B69BCB";
    private const string ExecutableName = "This War of Mine.exe";
    private const string RuntimeExecutableName = "MaxyModLoader.Original.exe";

    /// <summary>
    /// 安装加载器启动引导并保留可校验的原游戏程序副本
    /// </summary>
    public static void Install(string gameDirectory, string bootstrapExecutable)
    {
        //先确认目标是已核验游戏并且游戏进程已经退出
        gameDirectory = Path.GetFullPath(gameDirectory);
        bootstrapExecutable = Path.GetFullPath(bootstrapExecutable);
        EnsureGameStopped();
        var target = TargetPath(gameDirectory);
        var backup = BackupPath(gameDirectory);
        var legacyBackup = LegacyBackupPath(gameDirectory);
        var statePath = StatePath(gameDirectory);
        if (!File.Exists(target) || !File.Exists(bootstrapExecutable)) throw new FileNotFoundException("缺少游戏启动程序或加载器启动引导");

        //首次安装只接受经过核验的原版程序并生成独立备份
        ExecutableInstallState? prior = null;
        if (File.Exists(statePath))
        {
            prior = ReadState(statePath);
            if (prior.OriginalSha256 != GameFingerprint) throw new InvalidDataException("原版启动程序指纹不匹配");
            if (!File.Exists(backup) && File.Exists(legacyBackup))
            {
                Check(legacyBackup, prior.OriginalSha256);
                File.Copy(legacyBackup, backup, false);
            }
            Check(backup, prior.OriginalSha256);
        }
        else if (File.Exists(backup))
        {
            if (Fingerprint(backup) != GameFingerprint || Fingerprint(target) != GameFingerprint)
                throw new InvalidDataException("已有原版运行文件但游戏入口身份不明");
        }
        else
        {
            if (Fingerprint(target) != GameFingerprint) throw new InvalidDataException("游戏版本不匹配拒绝替换启动程序");
            Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
            File.Copy(target, backup, false);
            Check(backup, GameFingerprint);
        }

        //原版副本位于x64目录可同时满足引擎依赖查找和Steam入口转发
        var runtimeExecutable = RuntimePath(gameDirectory);
        Check(runtimeExecutable, GameFingerprint);

        //当前文件只能是原版、记录中的旧引导或预期的新引导
        var bootstrapHash = Fingerprint(bootstrapExecutable);
        var currentHash = Fingerprint(target);
        if (prior is not null && currentHash != prior.BootstrapSha256 &&
            currentHash != prior.PreviousBootstrapSha256 && currentHash != GameFingerprint)
            throw new InvalidDataException("游戏启动程序已有未知修改拒绝覆盖");
        if (prior is null && currentHash != GameFingerprint) throw new InvalidDataException("目标启动程序不是原版文件");

        //先记录新旧引导身份再替换使中断后恢复器知道允许的文件状态
        var next = new ExecutableInstallState(GameFingerprint, bootstrapHash,
            currentHash == bootstrapHash ? "" : currentHash == GameFingerprint ? "" : currentHash);
        WriteState(statePath, next);
        if (currentHash != bootstrapHash) Replace(bootstrapExecutable, target, bootstrapHash);

        //替换后复核实际磁盘文件再清除升级过程中的旧引导指纹
        Check(target, bootstrapHash);
        WriteState(statePath, next with { PreviousBootstrapSha256 = "" });
        DeleteLegacyBackup(legacyBackup);
    }

    /// <summary>
    /// 恢复原版启动程序并拒绝覆盖未知改动
    /// </summary>
    public static void Restore(string gameDirectory)
    {
        //没有安装记录时不推测游戏目录文件的所有权
        gameDirectory = Path.GetFullPath(gameDirectory);
        var statePath = StatePath(gameDirectory);
        if (!File.Exists(statePath)) return;
        EnsureGameStopped();

        //备份和当前目标必须处于安装日志明确记录的状态
        var state = ReadState(statePath);
        var target = TargetPath(gameDirectory);
        var backup = BackupPath(gameDirectory);
        if (!File.Exists(backup) && File.Exists(LegacyBackupPath(gameDirectory))) backup = LegacyBackupPath(gameDirectory);
        Check(backup, state.OriginalSha256);
        var currentHash = Fingerprint(target);
        if (state.OriginalSha256 != GameFingerprint ||
            currentHash != state.BootstrapSha256 && currentHash != state.PreviousBootstrapSha256 && currentHash != state.OriginalSha256)
            throw new InvalidDataException("启动程序已被其他程序修改拒绝自动恢复");

        //仅在当前不是原版时用同目录临时文件原子恢复
        if (currentHash != state.OriginalSha256) Replace(backup, target, state.OriginalSha256);
        Check(target, state.OriginalSha256);

        //先删除恢复状态使删除失败时仍保留可供下一次恢复的原版副本
        File.Delete(statePath);

        //只移除本加载器创建且指纹完全匹配的同目录运行副本
        var runtimeExecutable = RuntimePath(gameDirectory);
        if (File.Exists(runtimeExecutable))
        {
            Check(runtimeExecutable, state.OriginalSha256);
            File.Delete(runtimeExecutable);
        }
        DeleteLegacyBackup(LegacyBackupPath(gameDirectory));
    }

    /// <summary>
    /// 返回本机已核验的原版启动程序路径
    /// </summary>
    public static string ResolveOriginalExecutablePath(string gameDirectory)
    {
        //替换安装期间从私有备份读取原版程序身份
        gameDirectory = Path.GetFullPath(gameDirectory);
        var backup = BackupPath(gameDirectory);
        if (File.Exists(backup))
        {
            Check(backup, GameFingerprint);
            return backup;
        }
        var legacyBackup = LegacyBackupPath(gameDirectory);
        if (File.Exists(legacyBackup))
        {
            Check(legacyBackup, GameFingerprint);
            return legacyBackup;
        }

        //未安装启动引导时使用游戏原始路径并逐字节校验身份
        var target = TargetPath(gameDirectory);
        Check(target, GameFingerprint);
        return target;
    }

    /// <summary>
    /// 判断当前程序路径是否为游戏目录中的启动引导位置
    /// </summary>
    public static bool IsBootstrapPath(string executablePath)
    {
        //只有核验过的x64目录和固定程序名才能触发自动启动
        var fullPath = Path.GetFullPath(executablePath);
        return Path.GetFileName(fullPath).Equals(ExecutableName, StringComparison.OrdinalIgnoreCase) &&
            Path.GetFileName(Path.GetDirectoryName(fullPath)!).Equals("x64", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 从原版可执行文件路径取得游戏根目录
    /// </summary>
    public static string ResolveGameDirectory(string executablePath)
    {
        //拒绝把其他位置中同名程序当作游戏入口
        if (!IsBootstrapPath(executablePath)) throw new InvalidDataException("启动引导不在游戏x64目录中");
        return Directory.GetParent(Path.GetDirectoryName(Path.GetFullPath(executablePath))!)!.FullName;
    }

    /// <summary>
    /// 读取并校验原子安装日志
    /// </summary>
    private static ExecutableInstallState ReadState(string path)
    {
        //拒绝空字段和未知版本避免恢复错误的可执行文件
        var state = JsonSerializer.Deserialize<ExecutableInstallState>(File.ReadAllText(path))
            ?? throw new InvalidDataException("启动程序安装日志为空");
        if (state.OriginalSha256.Length != 64 || state.BootstrapSha256.Length != 64 ||
            state.PreviousBootstrapSha256.Length is not (0 or 64))
            throw new InvalidDataException("启动程序安装日志格式无效");
        return state;
    }

    /// <summary>
    /// 原子保存原版和启动引导指纹
    /// </summary>
    private static void WriteState(string path, ExecutableInstallState state)
    {
        //临时文件与安装日志处于同一目录便于原子替换
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(state));
            File.Move(temporary, path, true);
        }
        finally
        {
            //只清理本次创建的临时安装日志
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    /// <summary>
    /// 复制到目标目录临时文件并复核后再替换
    /// </summary>
    private static void Replace(string source, string target, string expected)
    {
        //临时文件留在x64目录以保证最终重命名不会跨卷
        var temporary = target + ".mml-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.Copy(source, temporary, false);
            Check(temporary, expected);
            File.Move(temporary, target, true);
        }
        finally
        {
            //替换失败时只删除本次临时副本
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    /// <summary>
    /// 计算文件指纹并与指定值进行比较
    /// </summary>
    private static void Check(string path, string expected)
    {
        //流式读取大型原版可执行文件并拒绝任何版本差异
        if (!File.Exists(path) || Fingerprint(path) != expected) throw new InvalidDataException("文件指纹不匹配：" + path);
    }

    /// <summary>
    /// 计算单个文件的SHA256指纹
    /// </summary>
    private static string Fingerprint(string path)
    {
        //不把完整游戏程序载入内存
        using var file = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(file));
    }

    /// <summary>
    /// 确认没有游戏实例正在使用目标程序
    /// </summary>
    private static void EnsureGameStopped()
    {
        //引导进程自身允许维护安装状态但其他游戏实例会阻止文件替换
        var processes = Process.GetProcessesByName("This War of Mine")
            .Concat(Process.GetProcessesByName(Path.GetFileNameWithoutExtension(RuntimeExecutableName)))
            .ToArray();
        var active = processes.Where(process => process.Id != Environment.ProcessId).ToArray();
        foreach (var process in processes) process.Dispose();
        if (active.Length > 0) throw new IOException("请先退出游戏再替换或恢复游戏启动程序");
    }

    /// <summary>
    /// 构造游戏本体原生启动文件路径
    /// </summary>
    private static string TargetPath(string gameDirectory)
    {
        //固定使用经过验证的x64游戏入口文件名
        return Path.Combine(gameDirectory, "x64", ExecutableName);
    }

    /// <summary>
    /// 构造保持原生依赖目录的游戏运行副本路径
    /// </summary>
    private static string RuntimePath(string gameDirectory)
    {
        //独立名称避免与Steam固定启动入口冲突同时保留x64依赖目录
        return Path.Combine(gameDirectory, "x64", RuntimeExecutableName);
    }

    /// <summary>
    /// 构造加载器目录内的原版程序备份路径
    /// </summary>
    private static string BackupPath(string gameDirectory)
    {
        //原版运行副本同时承担Steam入口转发目标与启动恢复来源
        return RuntimePath(gameDirectory);
    }

    /// <summary>
    /// 构造旧版加载器保存的重复启动程序副本路径
    /// </summary>
    private static string LegacyBackupPath(string gameDirectory)
    {
        //迁移时兼容旧安装并在新运行副本校验成功后删除重复文件
        return Path.Combine(gameDirectory, "MaxyModLoader", "original", ExecutableName);
    }

    /// <summary>
    /// 删除经指纹校验的旧版重复启动程序副本
    /// </summary>
    private static void DeleteLegacyBackup(string legacyBackup)
    {
        //只删除确认为原版游戏程序的旧副本并保留未知文件供用户检查
        if (!File.Exists(legacyBackup)) return;
        if (Fingerprint(legacyBackup) != GameFingerprint) throw new InvalidDataException("旧版启动程序副本身份不匹配拒绝删除");
        File.Delete(legacyBackup);
        var directory = Path.GetDirectoryName(legacyBackup)!;
        if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
    }

    /// <summary>
    /// 构造启动程序安装状态文件路径
    /// </summary>
    private static string StatePath(string gameDirectory)
    {
        //持久化记录允许程序异常退出后安全恢复
        return Path.Combine(gameDirectory, "MaxyModLoader", "executable-install.json");
    }

    /// <summary>
    /// 保存已允许的原版与启动引导文件指纹
    /// </summary>
    private sealed record ExecutableInstallState(string OriginalSha256, string BootstrapSha256, string PreviousBootstrapSha256);
}
