using System.Diagnostics;
using System.Text.Json;
using ThisWarOfMineModLoader.Mods;

namespace ThisWarOfMineModLoader.Deployment;

/// <summary>
/// 记录安装前备份与当前部署包身份以支持中断恢复
/// </summary>
public sealed record InstallState(PackageManifest Package, string BackupDirectory);

/// <summary>
/// 安装离线Lua加载器容器并保留可核验的原文件备份
/// </summary>
public static class PackageInstaller
{
    /// <summary>
    /// 校验原版身份并备份后安装部署包
    /// </summary>
    public static void Install(string gameDirectory, string packageDirectory)
    {
        //拒绝运行中游戏或已有安装日志避免叠加修改原始容器
        gameDirectory = Path.GetFullPath(gameDirectory);
        EnsureGameStopped();
        var statePath = Path.Combine(gameDirectory, "TWOMLoader", "install-state.json");
        if (File.Exists(statePath)) throw new IOException("加载器已有安装或待恢复操作请先执行restore。");
        var package = JsonSerializer.Deserialize<PackageManifest>(File.ReadAllText(Path.Combine(packageDirectory, "package.json")), ModManifest.JsonOptions)
            ?? throw new InvalidDataException("部署包清单为空。");
        ValidateContainer(package.Container);

        //只允许指纹与构建来源完全相符的目标安装
        var target = Path.Combine(gameDirectory, package.Container);
        var built = Path.Combine(packageDirectory, package.Container);
        Check(target + ".idx", package.OriginalIndexSha256);
        Check(target + ".dat", package.OriginalDataSha256);
        Check(built + ".idx", package.BuiltIndexSha256);
        Check(built + ".dat", package.BuiltDataSha256);

        //先备份两份原文件并写入恢复日志再开始任何目标替换
        var backup = Path.Combine(gameDirectory, "TWOMLoader", "backups", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(backup);
        File.Copy(target + ".idx", Path.Combine(backup, package.Container + ".idx"));
        File.Copy(target + ".dat", Path.Combine(backup, package.Container + ".dat"));
        Check(Path.Combine(backup, package.Container + ".idx"), package.OriginalIndexSha256);
        Check(Path.Combine(backup, package.Container + ".dat"), package.OriginalDataSha256);
        using (var state = new FileStream(statePath, FileMode.CreateNew))
            JsonSerializer.Serialize(state, new InstallState(package, Path.GetRelativePath(gameDirectory, backup)), ModManifest.JsonOptions);

        //游戏已停止单文件替换使用临时文件后重命名中断时保留恢复日志
        Replace(built + ".dat", target + ".dat");
        Replace(built + ".idx", target + ".idx");
        Check(target + ".dat", package.BuiltDataSha256);
        Check(target + ".idx", package.BuiltIndexSha256);
    }

    /// <summary>
    /// 从校验后的备份恢复原版容器并拒绝覆盖第三方改动
    /// </summary>
    public static void Restore(string gameDirectory)
    {
        //读取持久化日志以便恢复完整安装或只完成一半的安装
        gameDirectory = Path.GetFullPath(gameDirectory);
        EnsureGameStopped();
        var statePath = Path.Combine(gameDirectory, "TWOMLoader", "install-state.json");
        var state = JsonSerializer.Deserialize<InstallState>(File.ReadAllText(statePath), ModManifest.JsonOptions)
            ?? throw new InvalidDataException("安装日志为空。");
        ValidateContainer(state.Package.Container);
        var backup = Path.GetFullPath(Path.Combine(gameDirectory, state.BackupDirectory));
        var backupRoot = Path.GetFullPath(Path.Combine(gameDirectory, "TWOMLoader", "backups")) + Path.DirectorySeparatorChar;
        if (!backup.StartsWith(backupRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("备份路径超出本次游戏安装目录。");
        var package = state.Package;
        var target = Path.Combine(gameDirectory, package.Container);

        //校验备份和当前目标身份中断状态允许目标仍然是原文件
        Check(Path.Combine(backup, package.Container + ".idx"), package.OriginalIndexSha256);
        Check(Path.Combine(backup, package.Container + ".dat"), package.OriginalDataSha256);
        CheckCurrent(target + ".idx", package.OriginalIndexSha256, package.BuiltIndexSha256);
        CheckCurrent(target + ".dat", package.OriginalDataSha256, package.BuiltDataSha256);

        //恢复后复读指纹仅在两份原文件全部恢复成功后删除日志
        Replace(Path.Combine(backup, package.Container + ".dat"), target + ".dat");
        Replace(Path.Combine(backup, package.Container + ".idx"), target + ".idx");
        Check(target + ".dat", package.OriginalDataSha256);
        Check(target + ".idx", package.OriginalIndexSha256);
        File.Delete(statePath);
    }

    /// <summary>
    /// 拒绝含路径段或设备名称的容器名称
    /// </summary>
    private static void ValidateContainer(string name)
    {
        //实验版本只部署已有验证的common脚本容器
        if (name != "common") throw new InvalidDataException("当前安装器只支持common脚本容器。");
    }

    /// <summary>
    /// 校验文件是否与声明的SHA256指纹一致
    /// </summary>
    private static void Check(string path, string expected)
    {
        //任何文件身份不一致都必须停止避免覆盖其他模组或更新后的版本
        if (PackageBuilder.Fingerprint(path) != expected) throw new InvalidDataException($"文件指纹不匹配：{path}");
    }

    /// <summary>
    /// 校验恢复目标是原版文件或本加载器生成文件
    /// </summary>
    private static void CheckCurrent(string path, string original, string built)
    {
        //安装中断时可能只有一份容器完成替换两种已知状态都可以恢复
        var hash = PackageBuilder.Fingerprint(path);
        if (hash != original && hash != built) throw new InvalidDataException($"文件已有其他改动不能自动覆盖：{path}");
    }

    /// <summary>
    /// 复制完整临时文件后替换单个目标文件
    /// </summary>
    private static void Replace(string source, string target)
    {
        //临时文件位于目标所在卷重命名时不会跨卷复制
        var temporary = target + ".twom-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.Copy(source, temporary);
            File.Move(temporary, target, overwrite: true);
        }
        finally
        {
            //只清理本次临时文件安装日志保留给中断恢复
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    /// <summary>
    /// 确认游戏进程未运行以避免读取到半替换容器
    /// </summary>
    private static void EnsureGameStopped()
    {
        //按游戏进程名保守拒绝任何运行实例
        var processes = Process.GetProcessesByName("This War of Mine");
        foreach (var process in processes) process.Dispose();
        if (processes.Length > 0) throw new IOException("请先退出游戏再安装或恢复加载器。");
    }
}
