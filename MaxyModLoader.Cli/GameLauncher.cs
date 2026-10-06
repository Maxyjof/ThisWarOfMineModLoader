using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MaxyModLoader.Deployment;
using MaxyModLoader.Mods;
using MaxyModLoader.Runtime;
using MaxyModLoader.Windowing;

namespace MaxyModLoader.Cli;

/// <summary>
/// 根据游戏目录中的模组ZIP持久部署并启动加载器
/// </summary>
internal static class GameLauncher
{
    /// <summary>
    /// 按需构建或复用持久模组部署并启动游戏
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

        //清理上次异常退出留下的请求后进入可恢复的运行循环
        var staleRestart = Path.Combine(loaderDirectory, "restart-request.txt");
        if (File.Exists(staleRestart)) File.Delete(staleRestart);
        while (true)
        {
            //校验并读取活动部署的原版恢复点作为稳定缓存身份
            var installedState = PackageInstaller.ReadInstalledStateForLaunch(gameDirectory);
            var packageKey = ComputePackageKey(modsDirectory, gameDirectory, installedState?.Package);
            var packageDirectory = Path.Combine(cacheDirectory, packageKey);
            //只在完整内容键一致时复用缓存运行库变化必须重建游戏容器
            TraceStartup(gameDirectory, "检查模组包", packageDirectory);
            var packageInstalled = installedState is not null && Directory.Exists(packageDirectory) &&
                                   PackageInstaller.IsInstalledPackage(gameDirectory, packageDirectory, installedState);

            //模组组合发生变化时先回到受校验的原版基线再构建新部署包
            if (installedState is not null && !packageInstalled)
            {
                PackageInstaller.Restore(gameDirectory, true);
                installedState = null;
            }

            //缓存缺失时只在原版容器上重建并拒绝复用不完整缓存
            if (!Directory.Exists(packageDirectory)) BuildPackage(gameDirectory, modsDirectory, workDirectory, packageDirectory);
            else ValidateCachedPackage(packageDirectory);
            TraceStartup(gameDirectory, "模组包准备完成", packageDirectory);

            //模组容器仅首次或配置变更时替换且正常退出后持续保留
            var displayHostInstalled = false;
            var exitCode = -1;
            try
            {
                if (!packageInstalled) PackageInstaller.Install(gameDirectory, packageDirectory, true);
                //部署已经具备可恢复状态后仅保留当前缓存包避免旧组合长期占用空间
                PruneOldPackages(cacheDirectory, packageDirectory);
                DisplayHost.Install(gameDirectory, Path.Combine(gameDirectory, "MaxyModLoader", "app"));
                displayHostInstalled = true;
                var start = CreateGameStartInfo(executable, gameDirectory, forwardedArguments, executableBootstrap);
                var gameStartedAt = DateTimeOffset.UtcNow;
                var timer = Stopwatch.StartNew();
                var executableHash = FingerprintFile(executable);
                TraceStartup(gameDirectory, "启动原版程序", $"EXE={start.FileName} 原版SHA256={executableHash} 部署={packageKey}");
                Console.WriteLine(packageInstalled
                    ? "MaxyModLoader已复用持久模组部署正在启动游戏"
                    : "MaxyModLoader模组部署完成正在启动游戏并保留部署结果");
                using var process = Process.Start(start) ?? throw new IOException("无法启动游戏进程");
                TraceStartup(gameDirectory, "原版进程已启动", $"PID={process.Id} EXE={start.FileName} 部署={packageKey}");
                await process.WaitForExitAsync();
                exitCode = process.ExitCode;
                timer.Stop();
                GameRunDiagnostics.Write(gameDirectory, new GameRunReport(1,
                    Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown", executable,
                    executableHash, process.Id, gameStartedAt, DateTimeOffset.UtcNow, timer.ElapsedMilliseconds,
                    exitCode, $"0x{unchecked((uint)exitCode):X8}", packageKey,
                    ReadPackageMods(packageDirectory), forwardedArguments.Length));
                var exitCodeHex = $"0x{unchecked((uint)exitCode):X8}";
                TraceStartup(gameDirectory, "原版进程已退出", $"PID={process.Id} 退出码={exitCode}({exitCodeHex}) 运行毫秒={timer.ElapsedMilliseconds}");
            }
            finally
            {
                //显示设置辅助入口仍按单次运行管理而模组部署与恢复点保持不变
                try
                {
                    if (displayHostInstalled) DisplayHost.Uninstall(gameDirectory);
                }
                finally
                {
                    //工作区仅保存本次临时解包内容不承担持久部署职责
                    DeleteOwnedTree(workDirectory, loaderDirectory);
                }
            }

            //只有游戏正常退出并留下有效请求时才重新构建和启动
            if (!ModStartupState.ConsumeRestartRequest(gameDirectory)) return exitCode;
            TraceStartup(gameDirectory, "重启并应用", "按新模组启用状态重新构建");
        }
    }

    /// <summary>
    /// 从ZIP模组和解压目录构建未安装的离线部署包
    /// </summary>
    private static void BuildPackage(string gameDirectory, string modsDirectory, string workDirectory, string packageDirectory)
    {
        //临时解包目录随机命名且独立于玩家保存的压缩包
        var stagingDirectory = Path.Combine(workDirectory, Guid.NewGuid().ToString("N"));
        try
        {
            _ = ModPackageImporter.ImportAll(modsDirectory, stagingDirectory);
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
    /// 根据模组来源内容和游戏容器指纹生成缓存键
    /// </summary>
    private static string ComputePackageKey(string modsDirectory, string gameDirectory, PackageManifest? installedPackage)
    {
        //哈希输入包含当前契约版本和游戏容器内容
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes("MaxyModLoaderModPackageV2\0"));
        //运行库界面或API改变时重新生成游戏入口而加载器工具更新仍复用已构建模组包
        hash.AppendData(Encoding.ASCII.GetBytes(LuaBundle.GetRuntimeFingerprint()));
        //持久部署期间游戏容器是模组版本哈希必须改用恢复点中的原版指纹
        var originalContainers = installedPackage is null
            ? null
            : new[] { installedPackage.Scripts, installedPackage.Textures };
        foreach (var name in new[] { "common", "textures-s3" })
        {
            var container = originalContainers?.SingleOrDefault(item => item.Container == name);
            foreach (var extension in new[] { ".idx", ".dat" })
            {
                var fingerprint = container is null
                    ? PackageBuilder.Fingerprint(Path.Combine(gameDirectory, name + extension))
                    : extension == ".idx" ? container.OriginalIndexSha256 : container.OriginalDataSha256;
                hash.AppendData(Encoding.ASCII.GetBytes(fingerprint));
            }
        }
        foreach (var path in Directory.EnumerateFiles(modsDirectory, "*.zip", SearchOption.TopDirectoryOnly).Order(StringComparer.OrdinalIgnoreCase))
        {
            //文件名参与哈希避免不同ZIP顺序或命名产生缓存歧义
            hash.AppendData(Encoding.UTF8.GetBytes(Path.GetFileName(path) + "\0"));
            using var input = File.OpenRead(path);
            var buffer = new byte[1024 * 1024];
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0) hash.AppendData(buffer.AsSpan(0, read));
        }
        //解压模组按目录名相对路径和文件字节参与缓存键确保编辑后重新部署
        foreach (var directory in Directory.EnumerateDirectories(modsDirectory, "*", SearchOption.TopDirectoryOnly).Order(StringComparer.OrdinalIgnoreCase))
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Mods子目录不能是符号链接");
            if (!File.Exists(Path.Combine(directory, "mod.json"))) continue;
            hash.AppendData(Encoding.UTF8.GetBytes(Path.GetFileName(directory) + "\0folder\0"));
            var pending = new Stack<string>();
            var files = new List<string>();
            long totalSize = 0;
            pending.Push(directory);
            while (pending.TryPop(out var current))
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(current).Order(StringComparer.OrdinalIgnoreCase))
                {
                    var attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("解压模组不能包含符号链接");
                    if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
                    else
                    {
                        var length = new FileInfo(entry).Length;
                        totalSize += length;
                        if (files.Count >= 10000 || length > 128L * 1024 * 1024 || totalSize > 512L * 1024 * 1024)
                            throw new InvalidDataException("解压模组文件数量或体积超过上限");
                        files.Add(entry);
                    }
                }
            }
            foreach (var path in files.Order(StringComparer.OrdinalIgnoreCase))
            {
                //相对路径参与哈希以区分重命名和不同目录结构
                hash.AppendData(Encoding.UTF8.GetBytes(Path.GetRelativePath(directory, path).Replace('\\', '/') + "\0"));
                using var input = File.OpenRead(path);
                var buffer = new byte[1024 * 1024];
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0) hash.AppendData(buffer.AsSpan(0, read));
            }
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

    /// <summary>
    /// 计算目标文件的SHA256指纹
    /// </summary>
    private static string FingerprintFile(string path)
    {
        //使用流式读取避免把大型游戏程序整体载入内存
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    /// <summary>
    /// 读取本次部署包中的模组标识
    /// </summary>
    private static string[] ReadPackageMods(string packageDirectory)
    {
        //部署清单缺失时返回空集合并由启动阶段日志保留实际错误
        var path = Path.Combine(packageDirectory, "package.json");
        if (!File.Exists(path)) return [];
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        return document.RootElement.TryGetProperty("Mods", out var mods) && mods.ValueKind == JsonValueKind.Array
            ? mods.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).ToArray()
            : [];
    }

}
