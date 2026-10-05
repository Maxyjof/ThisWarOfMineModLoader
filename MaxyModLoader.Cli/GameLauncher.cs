using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using MaxyModLoader.Deployment;
using MaxyModLoader.Mods;
using MaxyModLoader.Windowing;

namespace MaxyModLoader.Cli;

/// <summary>
/// 根据游戏目录中的模组ZIP自动构建、安装、启动并恢复加载器
/// </summary>
internal static class GameLauncher
{
    /// <summary>
    /// 构建当前模组包并启动游戏退出后恢复原版资源
    /// </summary>
    public static async Task<int> PlayAsync(string gameDirectory, string[] forwardedArguments, bool executableBootstrap = false)
    {
        //先核验游戏路径并准备游戏目录下的模组与缓存位置
        gameDirectory = Path.GetFullPath(gameDirectory);
        if (!Directory.Exists(gameDirectory)) throw new DirectoryNotFoundException($"找不到游戏目录：{gameDirectory}");
        var executable = GameExecutableInstaller.ResolveOriginalExecutablePath(gameDirectory);
        if (!File.Exists(executable)) throw new FileNotFoundException("找不到已验证版本的游戏程序", executable);
        var modsDirectory = Path.Combine(gameDirectory, "Mods");
        Directory.CreateDirectory(modsDirectory);
        var loaderDirectory = Path.Combine(gameDirectory, "MaxyModLoader");
        var cacheDirectory = Path.Combine(loaderDirectory, "cache");
        var workDirectory = Path.Combine(loaderDirectory, "working");
        Directory.CreateDirectory(loaderDirectory);
        TraceStartup(gameDirectory, "入口", Environment.ProcessPath ?? "未知进程路径");
        //独占同一安装的运行会话防止两个入口同时替换容器
        using var sessionLease = new FileStream(Path.Combine(loaderDirectory, "session.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        TraceStartup(gameDirectory, "取得会话锁", "PID=" + Environment.ProcessId);
        Directory.CreateDirectory(cacheDirectory);
        Directory.CreateDirectory(workDirectory);

        //上次被强制结束时先使用持久化恢复日志还原游戏文件
        var statePath = Path.Combine(loaderDirectory, "install-state.json");
        if (File.Exists(statePath))
            PackageInstaller.Restore(gameDirectory, true);

        //清理上次异常退出留下的请求后进入可恢复的运行循环
        var staleRestart = Path.Combine(loaderDirectory, "restart-request.txt");
        if (File.Exists(staleRestart)) File.Delete(staleRestart);
        while (true)
        {
            //模组压缩包和启用状态共同决定缓存键值
            var packageKey = ComputePackageKey(modsDirectory, gameDirectory);
            var packageDirectory = Path.Combine(cacheDirectory, packageKey);
            TraceStartup(gameDirectory, "检查模组包", packageDirectory);
            if (!Directory.Exists(packageDirectory)) BuildPackage(gameDirectory, modsDirectory, workDirectory, packageDirectory);
            else ValidateCachedPackage(packageDirectory);
            TraceStartup(gameDirectory, "模组包准备完成", packageDirectory);

            //安装和游戏进程置于同一恢复边界内退出或启动失败都会尝试还原
            var displayHostInstalled = false;
            var exitCode = -1;
            try
            {
                PackageInstaller.Install(gameDirectory, packageDirectory, true);
                DisplayHost.Install(gameDirectory, Path.Combine(gameDirectory, "MaxyModLoader", "app"));
                displayHostInstalled = true;
                var start = CreateGameStartInfo(executable, gameDirectory, forwardedArguments, executableBootstrap);
                TraceStartup(gameDirectory, "启动原版程序", start.FileName);
                Console.WriteLine("MaxyModLoader部署完成正在启动游戏退出后会自动恢复原版文件");
                using var process = Process.Start(start) ?? throw new IOException("无法启动游戏进程");
                TraceStartup(gameDirectory, "原版进程已启动", "PID=" + process.Id + " EXE=" + start.FileName);
                await process.WaitForExitAsync();
                exitCode = process.ExitCode;
            }
            finally
            {
                //只在本次加载器状态日志存在时执行恢复避免触碰未安装的游戏
                try
                {
                    if (File.Exists(statePath)) PackageInstaller.Restore(gameDirectory, true);
                }
                finally
                {
                    //原版资源恢复后再移除仅属于本次启动的设置辅助文件
                    try
                    {
                        if (displayHostInstalled) DisplayHost.Uninstall(gameDirectory);
                    }
                    finally
                    {
                        //恢复失败时保留部署缓存供用户排查但仍清理解包工作区
                        if (!File.Exists(statePath)) PruneOldPackages(cacheDirectory, packageDirectory);
                        DeleteOwnedTree(workDirectory, loaderDirectory);
                    }
                }
            }

            //只有游戏正常退出并留下有效请求时才重新构建和启动
            if (!ModStartupState.ConsumeRestartRequest(gameDirectory)) return exitCode;
            TraceStartup(gameDirectory, "重启并应用", "按新模组启用状态重新构建");
        }
    }

    /// <summary>
    /// 从ZIP模组构建未安装的离线部署包
    /// </summary>
    private static void BuildPackage(string gameDirectory, string modsDirectory, string workDirectory, string packageDirectory)
    {
        //临时解包目录随机命名且独立于玩家保存的压缩包
        var stagingDirectory = Path.Combine(workDirectory, Guid.NewGuid().ToString("N"));
        try
        {
            _ = ModZipImporter.ExtractAll(modsDirectory, stagingDirectory);
            //状态文件覆盖模组清单默认值已移除模组的标识不参与当前加载
            var states = ModStartupState.Read(Path.Combine(gameDirectory, "MaxyModLoader", "mod-state.txt"));
            var plan = LoadPlanner.Create(ModCatalog.Discover(stagingDirectory, states));
            if (!plan.IsValid) throw new InvalidDataException(string.Join(Environment.NewLine, plan.Errors));

            //仅从已核验的原始Main资源创建缓存部署包
            try
            {
                PackageBuilder.Build(Path.Combine(gameDirectory, "common"), 0x5faa28a2, stagingDirectory, packageDirectory);
            }
            catch
            {
                //构建中断只清理本次新建的缓存键目录
                DeleteOwnedTree(packageDirectory, Path.Combine(gameDirectory, "MaxyModLoader", "cache"));
                throw;
            }
            Console.WriteLine($"已准备{plan.Ordered.Count}个模组");
        }
        finally
        {
            //清理范围固定在MaxyModLoader工作目录内
            DeleteOwnedTree(stagingDirectory, workDirectory);
        }
    }

    /// <summary>
    /// 根据压缩包内容和游戏容器指纹生成缓存键
    /// </summary>
    private static string ComputePackageKey(string modsDirectory, string gameDirectory)
    {
        //哈希输入包含规范版本、游戏容器和按名称排序的所有ZIP字节
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes("MaxyModLoaderZipPackageV1\0"));
        hash.AppendData(typeof(Program).Assembly.ManifestModule.ModuleVersionId.ToByteArray());
        hash.AppendData(typeof(PackageBuilder).Assembly.ManifestModule.ModuleVersionId.ToByteArray());
        foreach (var name in new[] { "common.idx", "common.dat", "textures-s3.idx", "textures-s3.dat" })
            hash.AppendData(Encoding.ASCII.GetBytes(PackageBuilder.Fingerprint(Path.Combine(gameDirectory, name))));
        foreach (var path in Directory.EnumerateFiles(modsDirectory, "*.zip", SearchOption.TopDirectoryOnly).Order(StringComparer.OrdinalIgnoreCase))
        {
            //文件名参与哈希避免不同ZIP顺序或命名产生缓存歧义
            hash.AppendData(Encoding.UTF8.GetBytes(Path.GetFileName(path) + "\0"));
            using var input = File.OpenRead(path);
            var buffer = new byte[1024 * 1024];
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0) hash.AppendData(buffer.AsSpan(0, read));
        }
        //启停状态加入缓存键值避免复用旧模组入口
        var statePath = Path.Combine(gameDirectory, "MaxyModLoader", "mod-state.txt");
        if (File.Exists(statePath))
        {
            hash.AppendData(Encoding.UTF8.GetBytes("\0ModStartupState\0"));
            hash.AppendData(File.ReadAllBytes(statePath));
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    /// <summary>
    /// 校验缓存部署包包含完整容器和清单
    /// </summary>
    private static void ValidateCachedPackage(string packageDirectory)
    {
        //损坏或被修改的缓存不作为有效部署包使用
        if ((File.GetAttributes(packageDirectory) & FileAttributes.ReparsePoint) != 0 ||
            !File.Exists(Path.Combine(packageDirectory, "package.json")) ||
            !File.Exists(Path.Combine(packageDirectory, "common.idx")) ||
            !File.Exists(Path.Combine(packageDirectory, "common.dat")) ||
            !File.Exists(Path.Combine(packageDirectory, "textures-s3.idx")) ||
            !File.Exists(Path.Combine(packageDirectory, "textures-s3.dat")))
            throw new InvalidDataException("缓存部署包不完整请删除MaxyModLoader/cache后重试");
    }

    /// <summary>
    /// 选择游戏默认程序或Steam传入的原始启动命令
    /// </summary>
    private static ProcessStartInfo CreateGameStartInfo(string executable, string gameDirectory, string[] forwardedArguments, bool executableBootstrap)
    {
        //默认启动已验证的Steam游戏程序并将工作目录设为游戏根目录
        var command = executable;
        var firstArgument = 0;
        if (!executableBootstrap && forwardedArguments.Length > 0)
        {
            //Steam启动选项可传入原命令以保持Steam启动链路
            command = Path.GetFullPath(forwardedArguments[0]);
            firstArgument = 1;
            if (!File.Exists(command)) throw new FileNotFoundException("Steam传入的游戏启动程序不存在", command);
        }
        var start = new ProcessStartInfo(command) { WorkingDirectory = gameDirectory, UseShellExecute = false };
        //保留原版x64目录优先级供引擎解析本机依赖DLL
        var gameBinaryDirectory = Path.Combine(gameDirectory, "x64");
        var existingPath = Environment.GetEnvironmentVariable("PATH") ?? "";
        start.Environment["PATH"] = gameBinaryDirectory + Path.PathSeparator + existingPath;
        for (var index = firstArgument; index < forwardedArguments.Length; index++) start.ArgumentList.Add(forwardedArguments[index]);
        return start;
    }

    /// <summary>
    /// 移除旧缓存部署包保留本次正在使用的包
    /// </summary>
    private static void PruneOldPackages(string cacheDirectory, string currentPackage)
    {
        //只清理缓存根目录下由64位小写十六进制键命名的兄弟目录
        foreach (var directory in Directory.EnumerateDirectories(cacheDirectory))
        {
            if (string.Equals(Path.GetFullPath(directory), Path.GetFullPath(currentPackage), StringComparison.OrdinalIgnoreCase)) continue;
            var name = Path.GetFileName(directory);
            if (name.Length == 64 && name.All(character => char.IsAsciiHexDigit(character)) &&
                File.Exists(Path.Combine(directory, "package.json")))
                DeleteOwnedTree(directory, cacheDirectory);
        }
    }

    /// <summary>
    /// 在明确父目录边界内删除本次生成的临时树
    /// </summary>
    private static void DeleteOwnedTree(string targetDirectory, string allowedParent)
    {
        //先核验绝对路径前缀并拒绝目标本身或父目录链接
        if (!Directory.Exists(targetDirectory)) return;
        var parent = Path.GetFullPath(allowedParent).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(targetDirectory);
        if (!target.StartsWith(parent, StringComparison.OrdinalIgnoreCase) ||
            (File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("临时目录清理路径越界或已变成链接");

        //先完整枚举并逐项拒绝链接再删除文件和空目录
        var directories = new List<string>();
        var pending = new Stack<string>();
        pending.Push(target);
        while (pending.TryPop(out var current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("临时目录包含链接不能自动清理");
            directories.Add(current);
            foreach (var file in Directory.EnumerateFiles(current))
            {
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new IOException("临时目录包含链接不能自动清理");
                File.Delete(file);
            }
            foreach (var child in Directory.EnumerateDirectories(current)) pending.Push(child);
        }
        foreach (var directory in directories.OrderByDescending(path => path.Length)) Directory.Delete(directory);
    }

    /// <summary>
    /// 记录引导阶段以区分加载器和原版游戏进程
    /// </summary>
    private static void TraceStartup(string gameDirectory, string stage, string detail)
    {
        //诊断仅追加到游戏安装目录不向模组或仓库写入本机日志
        var path = Path.Combine(gameDirectory, "MaxyModLoader", "startup.log");
        File.AppendAllText(path, $"{DateTime.UtcNow:O} PID={Environment.ProcessId} 阶段={stage} {detail}{Environment.NewLine}", Encoding.UTF8);
    }
}
