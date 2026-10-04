using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace MaxyModLoader.Mcp;

/// <summary>
/// 通过关联ID和原子文件与游戏主线程交换白名单命令
/// </summary>
public sealed class GameBridgeClient
{
    private readonly string directory;
    private readonly string game;

    /// <summary>
    /// 绑定单个游戏安装中的本地MCP目录
    /// </summary>
    public GameBridgeClient(string gameDirectory)
    {
        //使用统一品牌的固定目录将协议数据限制在本次游戏安装内
        game = Path.GetFullPath(gameDirectory);
        directory = Path.Combine(game, "MaxyModLoader", "mcp");
        Directory.CreateDirectory(directory);
    }

    /// <summary>
    /// 发布单个命令并等待游戏实际执行的结果
    /// </summary>
    public async Task<JsonElement> CallAsync(string command, string argument = "", TimeSpan? timeout = null)
    {
        //跨进程独占锁避免两个MCP客户端覆盖彼此请求
        using var lease = new FileStream(Path.Combine(directory, "client.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var id = Guid.NewGuid().ToString("N");
        var request = Path.Combine(directory, "request.txt");
        if (File.Exists(request)) throw new IOException("游戏仍有待处理命令请检查状态后再发起操作");
        if (!System.Text.RegularExpressions.Regex.IsMatch(command, "^[a-z_]+$")) throw new ArgumentException("游戏命令名称无效");
        var encoded = Convert.ToHexString(Encoding.UTF8.GetBytes(argument)).ToLowerInvariant();
        if (encoded.Length > 60000) throw new ArgumentException("游戏命令参数过长");

        //先完整写入临时文件再改名游戏只能观察到完整请求
        var temporary = Path.Combine(directory, "request.tmp");
        await File.WriteAllTextAsync(temporary, $"MML1\n{id}\n{command}\n{encoded}\n", new UTF8Encoding(false));
        File.Move(temporary, request);
        var watch = Stopwatch.StartNew();
        var limit = timeout ?? TimeSpan.FromSeconds(8);
        var response = Path.Combine(directory, "response.json");
        while (watch.Elapsed < limit)
        {
            //只接受本次ID旧结果和短暂文件共享冲突都不会误报成功
            try
            {
                if (File.Exists(response) && new FileInfo(response).Length <= 1024 * 1024)
                {
                    using var parsed = JsonDocument.Parse(await File.ReadAllTextAsync(response));
                    if (parsed.RootElement.GetProperty("id").GetString() == id) return parsed.RootElement.Clone();
                }
            }
            catch (IOException) { }
            catch (JsonException) { }
            await Task.Delay(50);
        }

        //超时仅取消尚未领取的请求已经开始的游戏操作不能声称撤销
        try
        {
            if (File.Exists(request) && (await File.ReadAllTextAsync(request)).StartsWith("MML1\n" + id + "\n", StringComparison.Ordinal))
                File.Delete(request);
        }
        catch (IOException) { }
        throw new TimeoutException("游戏MCP控制桥未及时返回执行结果未确认请勿自动重试状态变更命令");
    }

    /// <summary>
    /// 在独立辅助进程中读取游戏画面避免窗口绘制阻塞MCP服务
    /// </summary>
    public async Task<(string Path, string Data)> CaptureAsync()
    {
        //辅助进程只支持固定截图命令不接收任意脚本或桌面输入
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(System.Reflection.Assembly.GetEntryAssembly()?.Location ?? throw new IOException("无法确认加载器CLI入口"));
        start.ArgumentList.Add("screenshot"); start.ArgumentList.Add(game);
        using var process = Process.Start(start) ?? throw new IOException("无法启动截图辅助进程");
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8)); }
        catch (TimeoutException)
        {
            //只终止本次创建的截图辅助进程不会关闭游戏
            process.Kill();
            throw new TimeoutException("游戏截图未及时返回已停止截图辅助进程");
        }
        if (process.ExitCode != 0) throw new IOException((await errors).Trim());
        var path = Path.GetFullPath((await output).Trim());
        var allowed = Path.Combine(directory, "screenshots") + Path.DirectorySeparatorChar;
        if (!path.StartsWith(allowed, StringComparison.OrdinalIgnoreCase) || new FileInfo(path).Length > 16 * 1024 * 1024)
            throw new IOException("截图输出路径或大小无效");
        return (path, Convert.ToBase64String(await File.ReadAllBytesAsync(path)));
    }
}
