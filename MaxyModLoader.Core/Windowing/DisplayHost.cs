using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MaxyModLoader.Mcp;

namespace MaxyModLoader.Windowing;

/// <summary>
/// 为内置设置提供随游戏启动的后台窗口辅助服务
/// </summary>
public static class DisplayHost
{
    private static readonly JsonSerializerOptions OwnershipOptions = new()
    {
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };

    /// <summary>
    /// 为当前单文件自包含应用安装隐藏启动入口
    /// </summary>
    public static void Install(string game, string source)
    {
        //写入前完整验证当前发行结构和入口归属
        ValidateInstall(game, source);
        var root = Path.Combine(Path.GetFullPath(game), "MaxyModLoader");
        var host = Path.Combine(root, "host");
        var scriptPath = Path.Combine(root, "display-host.vbs");

        //脚本从自身位置推导游戏路径不固化开发机器目录
        Directory.CreateDirectory(host);
        File.WriteAllText(scriptPath, StartupScript(), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(host, "ownership.json"), JsonSerializer.Serialize(new HostOwnership(Fingerprint(scriptPath))));
    }

    /// <summary>
    /// 在修改游戏资源前验证辅助发行结构与现有入口所有权
    /// </summary>
    public static void ValidateInstall(string game, string source)
    {
        //只接受当前发行结构不复制框架依赖运行库或迁移历史部署
        source = Path.GetFullPath(source);
        if (!File.Exists(Path.Combine(source, "MaxyModLoader.exe")) ||
            !File.Exists(Path.Combine(source, ".self-contained")) ||
            File.ReadAllText(Path.Combine(source, ".self-contained")) != "single-file\n")
            throw new InvalidDataException("窗口辅助服务需要当前单文件自包含发行包");
        var root = Path.Combine(Path.GetFullPath(game), "MaxyModLoader");
        var host = Path.Combine(root, "host");
        var ownershipPath = Path.Combine(host, "ownership.json");
        var scriptPath = Path.Combine(root, "display-host.vbs");

        //覆盖入口前核验现有所有权拒绝未知格式或第三方修改
        if (File.Exists(ownershipPath))
        {
            var previous = ReadOwnership(ownershipPath);
            if (File.Exists(scriptPath) && Fingerprint(scriptPath) != previous.StartupSha256)
                throw new InvalidDataException("窗口辅助入口已被修改拒绝覆盖");
        }
        else if (File.Exists(scriptPath)) throw new InvalidDataException("窗口辅助入口缺少当前所有权记录");
    }

    /// <summary>
    /// 按当前所有权指纹移除隐藏入口并保留玩家偏好
    /// </summary>
    public static void Uninstall(string game)
    {
        //缺少记录时不推测现存文件归属也不查找历史目录
        var root = Path.Combine(Path.GetFullPath(game), "MaxyModLoader");
        var manifest = Path.Combine(root, "host", "ownership.json");
        if (!File.Exists(manifest)) return;
        var ownership = ReadOwnership(manifest);
        var scriptPath = Path.Combine(root, "display-host.vbs");

        //全部核验完成后只删除当前记录拥有的入口和记录本身
        if (File.Exists(scriptPath) && Fingerprint(scriptPath) != ownership.StartupSha256)
            throw new InvalidDataException("窗口辅助文件已被修改拒绝自动移除：display-host.vbs");
        if (File.Exists(scriptPath)) File.Delete(scriptPath);
        File.Delete(manifest);
    }

    /// <summary>
    /// 读取唯一的当前所有权格式并拒绝缺失或未知字段
    /// </summary>
    private static HostOwnership ReadOwnership(string path)
    {
        //旧部署的运行库清单和模式字典不是当前契约
        var ownership = JsonSerializer.Deserialize<HostOwnership>(File.ReadAllText(path), OwnershipOptions);
        if (ownership?.StartupSha256 is not { Length: 64 } || !ownership.StartupSha256.All(char.IsAsciiHexDigit))
            throw new InvalidDataException("窗口辅助所有权记录无效");
        return ownership;
    }

    /// <summary>
    /// 保存隐藏启动入口的唯一所有权指纹
    /// </summary>
    private sealed record HostOwnership([property: System.Text.Json.Serialization.JsonRequired] string StartupSha256);

    /// <summary>
    /// 生成调用当前自包含程序的隐藏脚本
    /// </summary>
    private static string StartupScript()
    {
        //程序和游戏路径都由脚本位置决定启动时隐藏控制台
        return "Option Explicit\r\nDim fs, folder, game, shell, launcher, command\r\n" +
            "Set fs = CreateObject(\"Scripting.FileSystemObject\")\r\n" +
            "folder = fs.GetParentFolderName(WScript.ScriptFullName)\r\n" +
            "game = fs.GetParentFolderName(folder)\r\n" +
            "Set shell = CreateObject(\"WScript.Shell\")\r\n" +
            "launcher = fs.BuildPath(folder, \"app\\MaxyModLoader.exe\")\r\n" +
            "command = Chr(34) & launcher & Chr(34) & \" display-host \" & Chr(34) & game & Chr(34)\r\n" +
            "shell.Run command, 0, False\r\n";
    }

    /// <summary>
    /// 计算单个自有文件的内容指纹
    /// </summary>
    private static string Fingerprint(string path)
    {
        //流式读取不将运行库整体加载进内存
        using var file = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(file));
    }

    /// <summary>
    /// 通过内置设置服务请求模式并等待同一请求的确认
    /// </summary>
    public static async Task<GameDisplay.DisplayState> RequestAsync(string game, string mode)
    {
        //MCP和CLI共用原版设置提交路径避免偏好和界面状态不同步
        if (!IsMode(mode)) throw new ArgumentException("显示模式无效");
        var reply = await new GameBridgeClient(game).CallAsync("display_request", mode);
        if (!reply.GetProperty("ok").GetBoolean()) throw new IOException(reply.GetProperty("error").GetString());
        var id = reply.GetProperty("result").GetProperty("id").GetString();
        var response = Path.Combine(Path.GetFullPath(game), "MaxyModLoader", "display-response.txt");
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(15))
        {
            //旧请求的结果不能被当成本次切换成功原子写入保证整行读取
            if (File.Exists(response))
            {
                //读取时允许原子替换避免客户端轮询阻塞后台发布确认文件
                using var stream = new FileStream(response, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                var lines = (await reader.ReadToEndAsync()).TrimEnd('\n').Split('\n');
                if (lines.Length == 3 && lines[0] == id)
                {
                    if (lines[1] != "ok" || lines[2] != mode) throw new IOException(lines[2]);
                    return await GameDisplay.ReadAsync(game);
                }
            }
            await Task.Delay(100);
        }
        throw new TimeoutException("显示设置结果未确认请先查询实际状态且不要自动重试");
    }

    /// <summary>
    /// 解析固定显示请求并拒绝额外内容和无效模式
    /// </summary>
    public static (string Id, string Mode) ParseRequest(string value)
    {
        //严格协议避免把请求文件内容作为系统命令执行
        var lines = value.Replace("\r", "", StringComparison.Ordinal).Split('\n');
        if (lines.Length != 4 || lines[0] != "MMLD1" || lines[3] != "" || lines[1].Length != 32 ||
            lines[1].Any(character => !char.IsAsciiHexDigit(character)) || !IsMode(lines[2]))
            throw new InvalidDataException("显示请求格式或模式无效");
        return (lines[1], lines[2]);
    }

    /// <summary>
    /// 判断显示模式是否属于内置白名单
    /// </summary>
    public static bool IsMode(string value) => value is "windowed" or "fullscreen" or "borderless";

    /// <summary>
    /// 在单个游戏会话期间处理设置请求并在确认后保存偏好
    /// </summary>
    public static async Task RunAsync(string game)
    {
        //每个安装目录只允许一个辅助进程避免同时处理同一请求
        game = Path.GetFullPath(game);
        var root = Path.Combine(game, "MaxyModLoader");
        Directory.CreateDirectory(root);
        using var lease = new FileStream(Path.Combine(root, "display-host.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var bridge = new GameBridgeClient(game);
        var started = Stopwatch.StartNew();
        Process? process = null;
        try
        {
            //游戏初始化主窗口前短暂等待且不自动启动游戏或激活窗口
            while (process is null && started.Elapsed < TimeSpan.FromSeconds(30))
            {
                //原版副本使用独立进程名但保持经过校验的实际可执行路径
                foreach (var candidate in Process.GetProcessesByName("This War of Mine")
                    .Concat(Process.GetProcessesByName("MaxyModLoader.Original")))
                {
                    if (string.Equals(candidate.MainModule?.FileName, MaxyModLoader.Deployment.GameExecutableInstaller.ResolveOriginalExecutablePath(game), StringComparison.OrdinalIgnoreCase) && candidate.MainWindowHandle != 0)
                    {
                        if (process is not null) { candidate.Dispose(); throw new IOException("存在多个目标游戏窗口"); }
                        process = candidate;
                    }
                    else candidate.Dispose();
                }
                if (process is null) await Task.Delay(200);
            }
            if (process is null) throw new IOException("未找到目标游戏会话");
            //片头或资源载入可能暂不处理Lua帧先等待只读探测成功才执行偏好变更
            await WaitForBridgeAsync(bridge, process);
            //旧请求不跨会话重放保存的偏好只在本次启动中应用一次
            var request = Path.Combine(root, "display-request.txt");
            if (File.Exists(request)) File.Delete(request);
            var preference = Path.Combine(root, "display-mode.txt");
            if (File.Exists(preference))
            {
                var mode = File.ReadAllText(preference).Trim();
                if (!IsMode(mode)) throw new InvalidDataException("已保存的显示模式无效");
                await GameDisplay.ApplyAsync(game, mode);
            }
            await NotifyAsync(bridge, await GameDisplay.ReadAsync(game));
            while (!process.HasExited)
            {
                //请求先原子领取再执行一次失败不自动重放显示状态变更
                if (File.Exists(request))
                {
                    var claimed = Path.Combine(root, "display-request.processing");
                    File.Move(request, claimed, true);
                    string id = "";
                    try
                    {
                        if (new FileInfo(claimed).Length > 128) throw new InvalidDataException("显示请求过长");
                        var parsed = ParseRequest(File.ReadAllText(claimed));
                        id = parsed.Id;
                        var state = await GameDisplay.ApplyAsync(game, parsed.Mode);
                        //核验完成才原子保存偏好切换失败不会覆盖上次成功模式
                        var temporary = preference + ".tmp";
                        File.WriteAllText(temporary, parsed.Mode + "\n", new UTF8Encoding(false));
                        await PublishAsync(temporary, preference);
                        await NotifyAsync(bridge, state);
                        await WriteResponseAsync(root, id, true, parsed.Mode);
                    }
                    catch (Exception exception) when (exception is IOException or ArgumentException or TimeoutException or InvalidOperationException or JsonException or UnauthorizedAccessException)
                    {
                        await WriteResponseAsync(root, id, false, exception.Message);
                    }
                    finally { File.Delete(claimed); }
                }
                await Task.Delay(150);
                process.Refresh();
            }
        }
        catch (Exception exception)
        {
            //隐藏辅助进程的启动或传输失败也留下本机诊断避免只有无响应现象
            File.AppendAllText(Path.Combine(root, "display-host.log"), DateTime.UtcNow.ToString("O") + " " + exception.Message + "\n");
            throw;
        }
        finally { process?.Dispose(); }
    }

    /// <summary>
    /// 等待加载期间的只读控制桥探测而不重放任何窗口操作
    /// </summary>
    private static async Task WaitForBridgeAsync(GameBridgeClient bridge, Process process)
    {
        //游戏窗口出现不代表Lua主线程已经处理请求资源加载时间较长时保持有界等待
        var watch = Stopwatch.StartNew();
        while (!process.HasExited && watch.Elapsed < TimeSpan.FromSeconds(120))
        {
            try
            {
                var reply = await bridge.CallAsync("game_state");
                if (reply.GetProperty("ok").GetBoolean()) return;
            }
            catch (Exception exception) when (exception is IOException or TimeoutException or JsonException)
            {
                //这里只重试状态读取偏好应用和玩家请求仍各执行一次
            }
            await Task.Delay(300);
            process.Refresh();
        }
        throw new TimeoutException("等待游戏控制桥初始化超时");
    }

    /// <summary>
    /// 把真实模式同步给游戏内设置行
    /// </summary>
    private static async Task NotifyAsync(GameBridgeClient bridge, GameDisplay.DisplayState state)
    {
        //游戏UI只显示辅助进程确认的模式不会以请求值伪装成功
        var mode = state.Borderless ? "borderless" : state.Fullscreen ? "fullscreen" : "windowed";
        var reply = await bridge.CallAsync("display_host_ready", mode);
        if (!reply.GetProperty("ok").GetBoolean()) throw new IOException("无法同步原版设置显示状态");
    }

    /// <summary>
    /// 原子写回显示请求的实际执行结果
    /// </summary>
    private static async Task WriteResponseAsync(string root, string id, bool ok, string message)
    {
        //只传递单行说明防止错误文本破坏协议边界
        message = message.Replace('\r', ' ').Replace('\n', ' ');
        var temporary = Path.Combine(root, "display-response.tmp");
        File.WriteAllText(temporary, id + "\n" + (ok ? "ok" : "error") + "\n" + message + "\n", new UTF8Encoding(false));
        await PublishAsync(temporary, Path.Combine(root, "display-response.txt"));
    }

    /// <summary>
    /// 在短暂读取共享冲突结束后发布结果文件而不重复执行显示变更
    /// </summary>
    private static async Task PublishAsync(string temporary, string destination)
    {
        //游戏Lua读取使用系统默认共享模式仅重试文件改名不重放已完成操作
        var watch = Stopwatch.StartNew();
        while (true)
        {
            try { File.Move(temporary, destination, true); return; }
            catch (IOException exception) when ((exception.HResult & 0xffff) is 32 or 33 && watch.Elapsed < TimeSpan.FromSeconds(2))
            {
                await Task.Delay(25);
            }
        }
    }
}
