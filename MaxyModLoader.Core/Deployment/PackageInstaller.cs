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
        if (File.Exists(statePath) || File.Exists(Path.Combine(gameDirectory, "TWOMLoader", "install-state.json")))
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

        //先备份两份原文件并写入恢复日志再开始任何目标替换
        var backup = Path.Combine(gameDirectory, "MaxyModLoader", "backups", Guid.NewGuid().ToString("N"));
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
        //旧版恢复日志保持可读不能因品牌目录更名而丢失原文件恢复能力
        if (!File.Exists(statePath)) statePath = Path.Combine(gameDirectory, "TWOMLoader", "install-state.json");
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
    }

    /// <summary>
    /// 校验脚本主容器及可选纹理容器并兼容旧单容器日志
    /// </summary>
    private static PackageManifest[] Containers(PackageManifest package)
    {
        //只允许已核验的两种固定名称拒绝递归清单和重复容器
        if (package.Container != "common") throw new InvalidDataException("主容器必须是common");
        if (package.Textures is null) return [package];
        if (package.Textures.Container != "textures-s3" || package.Textures.Textures is not null)
            throw new InvalidDataException("附加容器必须是单个textures-s3纹理容器");
        return [package, package.Textures];
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
    private static void EnsureGameStopped(string gameDirectory, bool allowCurrentBootstrap)
    {
        //按真实原版程序路径拒绝运行中的游戏且允许启动引导为本会话部署资源
        gameDirectory = Path.GetFullPath(gameDirectory);
        var bootstrap = Path.Combine(gameDirectory, "x64", "This War of Mine.exe");
        var processes = Process.GetProcessesByName("This War of Mine")
            .Concat(Process.GetProcessesByName("MaxyModLoader.Original")).ToArray();
        var active = new List<Process>();
        foreach (var process in processes)
        {
            //单文件入口可能由Steam进程托管以文件身份识别引导位置而非假定进程ID
            string? path;
            try { path = process.MainModule?.FileName; }
            catch (System.ComponentModel.Win32Exception) { path = null; }
            catch (InvalidOperationException) { path = null; }
            if (allowCurrentBootstrap && string.Equals(path, bootstrap, StringComparison.OrdinalIgnoreCase))
            {
                process.Dispose();
                continue;
            }
            //其他同名进程一律保守拦截避免未知游戏实例同时替换容器
            active.Add(process);
        }
        if (active.Count > 0)
        {
            foreach (var process in active) process.Dispose();
            throw new IOException("请先退出其他游戏实例再安装或恢复加载器，检测到仍运行的游戏进程");
        }
    }
}
