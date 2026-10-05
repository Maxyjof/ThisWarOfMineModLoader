using System.Diagnostics;
using System.Text.Json;
using MaxyModLoader.Mods;

namespace MaxyModLoader.Deployment;

/// <summary>
/// 记录安装前备份与当前部署包身份以支持中断恢复
/// </summary>
public sealed record InstallState(PackageManifest Package, string BackupDirectory, NativeInstallState? Native = null);

/// <summary>
/// 安装离线Lua加载器容器并保留可核验的原文件备份
/// </summary>
public static class PackageInstaller
{
    /// <summary>
    /// 校验原版身份并备份后安装部署包
    /// </summary>
    public static void Install(string gameDirectory, string packageDirectory, bool allowCurrentBootstrap = false)
    {
        //拒绝运行中游戏或已有安装日志避免叠加修改原始容器
        gameDirectory = Path.GetFullPath(gameDirectory);
        EnsureGameStopped(gameDirectory, allowCurrentBootstrap);
        var statePath = Path.Combine(gameDirectory, "MaxyModLoader", "install-state.json");
        if (File.Exists(statePath))
            throw new IOException("加载器已有安装或待恢复操作请先执行restore。");
        var package = JsonSerializer.Deserialize<PackageManifest>(File.ReadAllText(Path.Combine(packageDirectory, "package.json")), ModManifest.JsonOptions)
            ?? throw new InvalidDataException("部署包清单为空。");
        var containers = Containers(package);

        //先校验全部来源与目标再开始备份防止第二个容器损坏时部分安装
        foreach (var item in containers)
        {
            var target = Path.Combine(gameDirectory, item.Container);
            var built = Path.Combine(packageDirectory, item.Container);
            Check(target + ".idx", item.OriginalIndexSha256);
            Check(target + ".dat", item.OriginalDataSha256);
            Check(built + ".idx", item.BuiltIndexSha256);
            Check(built + ".dat", item.BuiltDataSha256);
        }
        var nativeState = NativeContentInstaller.Prepare(gameDirectory, packageDirectory, package.Native);
        //为游戏内Lua运行时预先创建仅由加载器管理的模组数据目录
        Directory.CreateDirectory(Path.Combine(gameDirectory, "MaxyModLoader", "storage"));

        //确认没有活动安装状态后清理异常退出留下的孤立会话备份
        var backupRoot = Path.Combine(gameDirectory, "MaxyModLoader", "backups");
        PruneOrphanedBackups(backupRoot + Path.DirectorySeparatorChar);

        //先备份两份原文件并写入恢复日志再开始任何目标替换
        var backup = Path.Combine(backupRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(backup);
        foreach (var item in containers)
        {
            var target = Path.Combine(gameDirectory, item.Container);
            File.Copy(target + ".idx", Path.Combine(backup, item.Container + ".idx"));
            File.Copy(target + ".dat", Path.Combine(backup, item.Container + ".dat"));
            Check(Path.Combine(backup, item.Container + ".idx"), item.OriginalIndexSha256);
            Check(Path.Combine(backup, item.Container + ".dat"), item.OriginalDataSha256);
        }
        NativeContentInstaller.Backup(gameDirectory, backup, nativeState);
        using (var state = new FileStream(statePath, FileMode.CreateNew))
            JsonSerializer.Serialize(state, new InstallState(package, Path.GetRelativePath(gameDirectory, backup), nativeState), ModManifest.JsonOptions);

        //游戏已停止单文件替换使用临时文件后重命名中断时保留恢复日志
        foreach (var item in containers)
        {
            var target = Path.Combine(gameDirectory, item.Container);
            var built = Path.Combine(packageDirectory, item.Container);
            Replace(built + ".dat", target + ".dat");
            Replace(built + ".idx", target + ".idx");
            Check(target + ".dat", item.BuiltDataSha256);
            Check(target + ".idx", item.BuiltIndexSha256);
        }
        NativeContentInstaller.Install(gameDirectory, packageDirectory, package.Native, nativeState);
    }

    /// <summary>
    /// 从校验后的备份恢复原版容器并拒绝覆盖第三方改动
    /// </summary>
    public static void Restore(string gameDirectory, bool allowCurrentBootstrap = false)
    {
        //读取持久化日志以便恢复完整安装或只完成一半的安装
        gameDirectory = Path.GetFullPath(gameDirectory);
        EnsureGameStopped(gameDirectory, allowCurrentBootstrap);
        var statePath = Path.Combine(gameDirectory, "MaxyModLoader", "install-state.json");
        var state = JsonSerializer.Deserialize<InstallState>(File.ReadAllText(statePath), ModManifest.JsonOptions)
            ?? throw new InvalidDataException("安装日志为空。");
        var containers = Containers(state.Package);
        var backup = Path.GetFullPath(Path.Combine(gameDirectory, state.BackupDirectory));
        var backupRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(statePath)!, "backups")) + Path.DirectorySeparatorChar;
        if (!backup.StartsWith(backupRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("备份路径超出本次游戏安装目录。");
        NativeContentInstaller.CheckRestore(gameDirectory, backup, state.Package.Native, state.Native);
        //全部容器校验通过才恢复任意文件中断状态允许各文件仍然是原版
        foreach (var item in containers)
        {
            var target = Path.Combine(gameDirectory, item.Container);
            Check(Path.Combine(backup, item.Container + ".idx"), item.OriginalIndexSha256);
            Check(Path.Combine(backup, item.Container + ".dat"), item.OriginalDataSha256);
            CheckCurrent(target + ".idx", item.OriginalIndexSha256, item.BuiltIndexSha256);
            CheckCurrent(target + ".dat", item.OriginalDataSha256, item.BuiltDataSha256);
        }

        //恢复后复读指纹仅在两份原文件全部恢复成功后删除日志
        foreach (var item in containers)
        {
            var target = Path.Combine(gameDirectory, item.Container);
            Replace(Path.Combine(backup, item.Container + ".dat"), target + ".dat");
            Replace(Path.Combine(backup, item.Container + ".idx"), target + ".idx");
            Check(target + ".dat", item.OriginalDataSha256);
            Check(target + ".idx", item.OriginalIndexSha256);
        }
        NativeContentInstaller.Restore(gameDirectory, backup, state.Package.Native, state.Native);
        File.Delete(statePath);
        try
        {
            DeleteRestoredBackup(backup, backupRoot);
            PruneOrphanedBackups(backupRoot);
        }
        catch (IOException)
        {
            //文件恢复已经完成清理受限时留下无引用备份供玩家手动检查
        }
        catch (UnauthorizedAccessException)
        {
            //权限限制不应把已成功恢复的游戏伪装成恢复失败
        }
    }

    /// <summary>
    /// 校验当前部署契约要求的脚本和纹理容器
    /// </summary>
    private static ContainerManifest[] Containers(PackageManifest package)
    {
        //两个容器都必须存在不接受单容器部署包或递归容器清单
        if (package.Scripts is null || package.Scripts.Container != "common" ||
            package.Textures is null || package.Textures.Container != "textures-s3")
            throw new InvalidDataException("部署包必须包含common脚本容器和textures-s3纹理容器");
        return [package.Scripts, package.Textures];
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
    /// 清理已成功恢复的本次会话备份
    /// </summary>
    private static void DeleteRestoredBackup(string backupDirectory, string backupRoot)
    {
        //恢复日志已删除且容器校验完成后备份不再承担崩溃恢复职责
        if (!Directory.Exists(backupDirectory)) return;
        var rootPath = Path.GetFullPath(backupRoot).TrimEnd(Path.DirectorySeparatorChar);
        if ((File.GetAttributes(rootPath) & FileAttributes.ReparsePoint) != 0) throw new IOException("恢复备份根目录是链接不能自动清理");
        var root = rootPath + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(backupDirectory);
        if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
            (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("恢复完成后的备份清理路径无效");

        //枚举时拒绝链接以免清理目标越过加载器专属备份目录
        var directories = new List<string>();
        var pending = new Stack<string>();
        pending.Push(target);
        while (pending.TryPop(out var current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("恢复备份包含目录链接不能自动清理");
            directories.Add(current);
            foreach (var file in Directory.EnumerateFiles(current))
            {
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("恢复备份包含文件链接不能自动清理");
                File.Delete(file);
            }
            foreach (var child in Directory.EnumerateDirectories(current)) pending.Push(child);
        }

        //按由深至浅的顺序删除空目录避免递归跨过已验证边界
        foreach (var directory in directories.OrderByDescending(path => path.Length)) Directory.Delete(directory);
    }

    /// <summary>
    /// 清理恢复完成后不再关联安装状态的旧会话备份
    /// </summary>
    private static void PruneOrphanedBackups(string backupRoot)
    {
        //只扫描加载器专属备份目录中采用GUID命名的旧会话子目录
        var root = Path.GetFullPath(backupRoot).TrimEnd(Path.DirectorySeparatorChar);
        if (!Directory.Exists(root) || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) return;
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            var name = Path.GetFileName(directory);
            if (name.Length != 32 || !name.All(char.IsAsciiHexDigit)) continue;

            //单个旧目录清理受限时保留该目录而继续处理其他孤立备份
            try
            {
                DeleteRestoredBackup(directory, root + Path.DirectorySeparatorChar);
            }
            catch (IOException)
            {
                //链接或文件占用不影响已经完成的游戏资源恢复
            }
            catch (UnauthorizedAccessException)
            {
                //权限受限的旧目录留给玩家手动检查
            }
        }
    }

    /// <summary>
    /// 确认游戏进程未运行以避免读取到半替换容器
    /// </summary>
    private static void EnsureGameStopped(string gameDirectory, bool allowCurrentBootstrap)
    {
        //按真实原版程序路径识别目标安装允许其他独立测试目录并行运行
        gameDirectory = Path.GetFullPath(gameDirectory);
        var bootstrap = Path.Combine(gameDirectory, "x64", "This War of Mine.exe");
        var processes = Process.GetProcessesByName("This War of Mine")
            .Concat(Process.GetProcessesByName("MaxyModLoader.Original")).ToArray();
        var active = new List<Process>();
        var gamePrefix = gameDirectory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var process in processes)
        {
            //读取实际映像路径区分目标游戏与另一个安装目录中的游戏
            string? path;
            try { path = process.MainModule?.FileName; }
            catch (System.ComponentModel.Win32Exception) { path = null; }
            catch (InvalidOperationException) { path = null; }
            var sameInstallation = path is not null &&
                (string.Equals(Path.GetFullPath(path), gameDirectory, StringComparison.OrdinalIgnoreCase) ||
                 Path.GetFullPath(path).StartsWith(gamePrefix, StringComparison.OrdinalIgnoreCase));
            if (allowCurrentBootstrap && sameInstallation && string.Equals(path, bootstrap, StringComparison.OrdinalIgnoreCase))
            {
                process.Dispose();
                continue;
            }
            //路径不可读取时仍保守拦截同名进程避免并发写入目标容器
            if (path is null || sameInstallation) active.Add(process);
            else process.Dispose();
        }
        if (active.Count > 0)
        {
            foreach (var process in active) process.Dispose();
            throw new IOException("请先退出其他游戏实例再安装或恢复加载器，检测到仍运行的游戏进程");
        }
    }
}
