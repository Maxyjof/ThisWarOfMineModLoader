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
    private static readonly string[] RuntimeFiles = ["MaxyModLoader.dll", "MaxyModLoader.deps.json", "MaxyModLoader.runtimeconfig.json", "MaxyModLoader.Core.dll", "Markdig.dll", "ThirdPartyNotices.txt"];

    /// <summary>
    /// 将当前加载器的运行文件和隐藏启动入口安装到游戏目录
    /// </summary>
    public static void Install(string game, string source)
    {
        //自包含玩家启动器由VBS直接调用不依赖系统安装的dotnet运行时
        source = Path.GetFullPath(source);
        var destination = Path.Combine(Path.GetFullPath(game), "MaxyModLoader", "host");
        var ownershipPath = Path.Combine(destination, "ownership.json");
        if (File.Exists(ownershipPath))
        {
            //升级旧版辅助程序时按所有权指纹移除之前复制的运行库
            var previous = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(ownershipPath))
                ?? throw new InvalidDataException("窗口辅助所有权记录无效");
            if (!previous.TryGetValue("mode", out var previousMode) || previousMode != "self-contained") Uninstall(game);
        }
        var selfContained = File.Exists(Path.Combine(source, "MaxyModLoader.exe")) && File.Exists(Path.Combine(source, ".self-contained"));
        Directory.CreateDirectory(destination);
        if (!selfContained)
        {
            //开发环境兼容旧式框架依赖部署且只复制固定运行文件
            foreach (var name in RuntimeFiles)
                if (!File.Exists(Path.Combine(source, name))) throw new IOException("缺少窗口辅助运行文件：" + name);
            foreach (var name in RuntimeFiles) File.Copy(Path.Combine(source, name), Path.Combine(destination, name), true);
        }

        //启动脚本根据自身位置解析游戏目录不固化开发者路径且不显示控制台
        var script = StartupScript(selfContained);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(destination)!, "display-host.vbs"), script, new UTF8Encoding(false));
        //记录自有文件指纹恢复时只移除没有被第三方修改的辅助文件
        var ownership = selfContained
            ? new Dictionary<string, string> { ["mode"] = "self-contained" }
            : RuntimeFiles.ToDictionary(name => name, name => Fingerprint(Path.Combine(destination, name)));
        ownership["display-host.vbs"] = Fingerprint(Path.Combine(Path.GetDirectoryName(destination)!, "display-host.vbs"));
        File.WriteAllText(Path.Combine(destination, "ownership.json"), JsonSerializer.Serialize(ownership));
    }

    /// <summary>
    /// 按安装指纹移除自有辅助文件并保留玩家偏好和原容器备份
    /// </summary>
    public static void Uninstall(string game)
    {
        //缺少所有权记录时不推测现存文件归属
        var root = Path.Combine(Path.GetFullPath(game), "MaxyModLoader");
        var host = Path.Combine(root, "host");
        var manifest = Path.Combine(host, "ownership.json");
        if (!File.Exists(manifest)) return;
        var ownership = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(manifest))
            ?? throw new InvalidDataException("窗口辅助所有权记录无效");
        if (ownership.TryGetValue("mode", out var mode) && mode == "self-contained")
        {
            //自包含启动器模式只拥有VBS入口不触碰应用目录或其他辅助文件
            if (ownership.Count != 2 || !ownership.ContainsKey("display-host.vbs"))
                throw new InvalidDataException("自包含窗口辅助所有权记录无效");
            var scriptPath = Path.Combine(root, "display-host.vbs");
            if (File.Exists(scriptPath) && Fingerprint(scriptPath) != ownership["display-host.vbs"])
                throw new InvalidDataException("窗口辅助文件已被修改拒绝自动移除：display-host.vbs");
            if (File.Exists(scriptPath)) File.Delete(scriptPath);
            File.Delete(manifest);
            return;
        }
        var names = RuntimeFiles.Append("display-host.vbs").ToArray();
        //兼容升级前不包含Markdown解析器的固定运行文件集合
        if (!ownership.ContainsKey("MaxyModLoader.Core.dll") || ownership.Keys.Any(name => !names.Contains(name)) ||
            names.Where(name => name is not ("Markdig.dll" or "ThirdPartyNotices.txt")).Any(name => !ownership.ContainsKey(name)))
            throw new InvalidDataException("窗口辅助所有权记录包含未知文件");
        names = names.Where(ownership.ContainsKey).ToArray();
        var paths = names.Select(name => Path.Combine(name == "display-host.vbs" ? root : host, name)).ToArray();
        //全部预校验完成后才删除任何文件避免篡改时留下半卸载状态
        for (var index = 0; index < names.Length; index++)
            if (File.Exists(paths[index]) && Fingerprint(paths[index]) != ownership[names[index]])
                throw new InvalidDataException("窗口辅助文件已被修改拒绝自动移除：" + names[index]);
        foreach (var path in paths) if (File.Exists(path)) File.Delete(path);
        File.Delete(manifest);
    }

    /// <summary>
    /// 生成调用自包含程序或开发版运行时的隐藏脚本
    /// </summary>
    private static string StartupScript(bool selfContained)
    {
        //两种启动方式都从脚本位置推导游戏目录并使用隐藏窗口
        var prefix = "Option Explicit\r\nDim fs, folder, game, shell\r\n" +
            "Set fs = CreateObject(\"Scripting.FileSystemObject\")\r\n" +
            "folder = fs.GetParentFolderName(WScript.ScriptFullName)\r\n" +
            "game = fs.GetParentFolderName(folder)\r\n" +
            "Set shell = CreateObject(\"WScript.Shell\")\r\n";
        if (selfContained)
            return prefix + "Dim launcher, command\r\n" +
                "launcher = fs.BuildPath(folder, \"app\\MaxyModLoader.exe\")\r\n" +
                "command = Chr(34) & launcher & Chr(34) & \" display-host \" & Chr(34) & game & Chr(34)\r\n" +
                "shell.Run command, 0, False\r\n";
        return prefix + "shell.Run \"dotnet \"\"\" & folder & \"\\host\\MaxyModLoader.dll\"\" display-host \"\"\" & game & \"\"\"\", 0, False\r\n";
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
                foreach (var candidate in Process.GetProcessesByName("This War of Mine"))
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
