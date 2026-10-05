using System.Globalization;
using System.Text;
using System.Text.Json;
using MaxyModLoader.Archives;
using MaxyModLoader.Deployment;
using MaxyModLoader.Mods;
using MaxyModLoader.Mcp;
using MaxyModLoader.Windowing;

namespace MaxyModLoader.Cli;

/// <summary>
/// 提供模组规划、容器检查和实验加载器部署命令
/// </summary>
public static class Program
{
    /// <summary>
    /// 分发命令并将可预期错误转换为非零退出码
    /// </summary>
    public static async Task<int> Main(string[] args)
    {
        //仅命令行管理模式配置控制台避免GUI游戏入口访问控制台句柄
        var currentExecutable = Environment.ProcessPath;
        var bootstrapGameRoot = currentExecutable is not null && GameExecutableInstaller.IsBootstrapPath(currentExecutable)
            ? GameExecutableInstaller.ResolveGameDirectory(currentExecutable)
            : null;
        if (bootstrapGameRoot is null)
        {
            Console.OutputEncoding = Encoding.UTF8;
            Console.InputEncoding = Encoding.UTF8;
        }
        try
        {
            //原生游戏入口由已安装的同名单文件引导程序启动时自动进入模组加载流程
            if (bootstrapGameRoot is not null) return await GameLauncher.PlayAsync(bootstrapGameRoot, args, true);

            //匹配固定命令形态未知参数直接显示用法
            switch (args)
            {
                case ["mcp", "--game", var game]:
                    await new McpServer(new GameBridgeClient(game)).RunAsync(Console.In, Console.Out);
                    return 0;
                case ["rpc", var game, var command]: return await Rpc(game, command, "");
                case ["rpc", var game, var command, var argument]: return await Rpc(game, command, argument);
                case ["screenshot", var game]: Console.WriteLine(GameScreenshot.Capture(game)); return 0;
                case ["display", var game]: Console.WriteLine(JsonSerializer.Serialize(await GameDisplay.ReadAsync(game))); return 0;
                case ["display", var game, var mode]: Console.WriteLine(JsonSerializer.Serialize(await DisplayHost.RequestAsync(game, mode))); return 0;
                case ["display-host", var game]: await DisplayHost.RunAsync(game); return 0;
                case ["play", var game, .. var forwarded]: return await GameLauncher.PlayAsync(game, forwarded);
                case ["install-wrapper", var game]:
                    GameExecutableInstaller.Install(game, Path.Combine(Path.GetFullPath(game), "MaxyModLoader", "app", "MaxyModLoader.Bootstrap.exe"));
                    Console.WriteLine("已核验并安装游戏自启动引导程序");
                    return 0;
                case ["prepare-host", var game]:
                    DisplayHost.Install(game, Path.Combine(Path.GetFullPath(game), "MaxyModLoader", "app"));
                    Console.WriteLine("已准备自包含显示设置辅助入口");
                    return 0;
                case ["plan", var root]: return Plan(root);
                case ["hash", var path]: Console.WriteLine($"{ResourceHash.Compute(path):x8}"); return 0;
                case ["inspect", var container]: return Inspect(container);
                case ["extract", var source, var hash, var destination]: return Extract(source, hash, destination);
                case ["build", var source, var hash, var mods, var output]: return Build(source, hash, mods, output);
                case ["build", var source, var hash, var mods, var output, var resources]: return Build(source, hash, mods, output, resources);
                case ["install", var game, var package]:
                    PackageInstaller.Install(game, package);
                    DisplayHost.Install(game, AppContext.BaseDirectory);
                    Console.WriteLine("已备份原容器并安装加载器可使用restore恢复");
                    return 0;
                case ["restore", var game]:
                    if (File.Exists(Path.Combine(game, "MaxyModLoader", "install-state.json")) ||
                        File.Exists(Path.Combine(game, "TWOMLoader", "install-state.json")))
                        PackageInstaller.Restore(game);
                    DisplayHost.Uninstall(game);
                    GameExecutableInstaller.Restore(game);
                    Console.WriteLine("已核验并恢复原版容器和游戏启动入口");
                    return 0;
                default:
                    Console.WriteLine("MaxyModLoader《这是我的战争》模组加载器\nplay <游戏根目录> [Steam启动程序及参数]\nplan <模组目录>\nhash <容器内相对路径>\ninspect <容器路径不含扩展名>\nextract <容器路径> <八位十六进制哈希> <输出文件>\nbuild <容器路径> <Main哈希> <模组目录> <新输出目录> [DDS资源目录]\ninstall <游戏根目录> <部署包目录>\ninstall-wrapper <游戏根目录>\nrestore <游戏根目录>\nmcp --game <游戏根目录>\nrpc <游戏根目录> <游戏命令> [参数]\nscreenshot <游戏根目录>\ndisplay <游戏根目录> [borderless|windowed|fullscreen]");
                    return args.Length == 0 || args is ["--help"] ? 0 : 1;
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException or DecoderFallbackException or TimeoutException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            //预期的输入和文件错误不输出无关堆栈但保留明确退出码
            if (bootstrapGameRoot is not null)
            {
                //游戏入口没有控制台时将启动错误写入游戏目录便于排查
                var log = Path.Combine(bootstrapGameRoot, "MaxyModLoader", "startup.log");
                Directory.CreateDirectory(Path.GetDirectoryName(log)!);
                File.AppendAllText(log, $"{DateTime.UtcNow:O} PID={Environment.ProcessId} EXE={Environment.ProcessPath} {exception.Message}{Environment.NewLine}", Encoding.UTF8);
            }
            else Console.Error.WriteLine($"错误：{exception.Message}");
            return 2;
        }
    }

    /// <summary>
    /// 执行内置MCP控制桥的单个白名单游戏命令
    /// </summary>
    private static async Task<int> Rpc(string game, string command, string argument)
    {
        //只有游戏确认的响应才能返回成功退出码
        var result = await new GameBridgeClient(game).CallAsync(command, argument);
        Console.WriteLine(result.GetRawText());
        return result.GetProperty("ok").GetBoolean() ? 0 : 2;
    }

    /// <summary>
    /// 输出模组加载顺序或全部规划错误
    /// </summary>
    private static int Plan(string root)
    {
        //只有完整有效的计划才会包含可加载模组
        var plan = LoadPlanner.Create(ModCatalog.Discover(root));
        foreach (var error in plan.Errors) Console.Error.WriteLine(error);
        foreach (var mod in plan.Ordered) Console.WriteLine($"{mod.Manifest.Id} {mod.Manifest.Version} {mod.Manifest.Name}");
        return plan.IsValid ? 0 : 2;
    }

    /// <summary>
    /// 列出容器版本与每个资源的存储信息
    /// </summary>
    private static int Inspect(string container)
    {
        //检查整个索引后输出资源列表不读取大型数据文件的全部内容
        var archive = LiquidArchive.Open(container);
        Console.WriteLine($"容器：{archive.BasePath}\n资源数：{archive.Entries.Count}\n索引版本：{Convert.ToHexString(archive.Header.AsSpan(0, 3))}");
        foreach (var entry in archive.Entries) Console.WriteLine($"{entry.Hash:x8} {entry.Size} bytes {(entry.Compressed ? "gzip" : "raw")}");
        return 0;
    }

    /// <summary>
    /// 将指定哈希资源提取到不存在的输出文件
    /// </summary>
    private static int Extract(string source, string hash, string destination)
    {
        //读取时限制资源大小并拒绝覆盖已有输出文件
        var bytes = LiquidArchive.Open(source).Read(ParseHash(hash));
        using var output = new FileStream(destination, FileMode.CreateNew);
        output.Write(bytes);
        Console.WriteLine($"已提取 {bytes.Length} 字节：{destination}");
        return 0;
    }

    /// <summary>
    /// 构建包含原始指纹与生成指纹的离线部署包
    /// </summary>
    private static int Build(string source, string hash, string mods, string output, string? resources = null)
    {
        //构建阶段不修改游戏安装目录
        var manifest = PackageBuilder.Build(source, ParseHash(hash), mods, output, resources);
        Console.WriteLine($"已生成部署包：{Path.GetFullPath(output)}\n模组数量：{manifest.Mods.Length}\n此命令不会安装或修改游戏");
        return 0;
    }

    /// <summary>
    /// 解析固定八位十六进制资源哈希
    /// </summary>
    private static uint ParseHash(string value)
    {
        //禁止含糊的十进制输入以避免替换错误资源
        if (value.Length != 8 || !uint.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hash))
            throw new ArgumentException("哈希必须是八位十六进制");
        return hash;
    }
}
