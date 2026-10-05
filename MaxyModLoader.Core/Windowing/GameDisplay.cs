using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using MaxyModLoader.Mcp;

namespace MaxyModLoader.Windowing;

/// <summary>
/// 协调游戏显示设置和目标窗口边框且保留同一进程的窗口恢复信息
/// </summary>
public static class GameDisplay
{
    private const long FrameMask = 0x00cf0000;
    private const long EdgeMask = 0x00000301;

    /// <summary>
    /// 表示当前游戏窗口的真实样式和显示器边界
    /// </summary>
    public sealed record DisplayState(int ProcessId, bool Borderless, bool Fullscreen, int Left, int Top, int Width, int Height);

    /// <summary>
    /// 表示经过工作区约束的普通窗口位置和尺寸
    /// </summary>
    public sealed record WindowBounds(int Left, int Top, int Width, int Height);

    /// <summary>
    /// 保留可见窗口位置并将超出工作区的窗口按比例缩小居中
    /// </summary>
    public static WindowBounds FitWindowBounds(int left, int top, int width, int height, int workLeft, int workTop, int workWidth, int workHeight)
    {
        //限制几何范围避免无效显示器或溢出坐标进入窗口调用
        if (width is < 1 or > 16384 || height is < 1 or > 16384 || workWidth is < 640 or > 16384 || workHeight is < 480 or > 16384 ||
            Math.Abs((long)left) > 32768 || Math.Abs((long)top) > 32768 || Math.Abs((long)workLeft) > 32768 || Math.Abs((long)workTop) > 32768)
            throw new ArgumentException("窗口或显示器工作区无效");
        if (left >= workLeft && top >= workTop && (long)left + width <= (long)workLeft + workWidth &&
            (long)top + height <= (long)workTop + workHeight) return new(left, top, width, height);
        //全屏尺寸留下的普通窗口需要同时留出标题栏任务栏和周围空间
        var factor = Math.Min(1, Math.Min(workWidth * 0.8 / width, workHeight * 0.8 / height));
        width = (int)Math.Floor(width * factor);
        height = (int)Math.Floor(height * factor);
        return new(workLeft + (workWidth - width) / 2, workTop + (workHeight - height) / 2, width, height);
    }

    /// <summary>
    /// 保存同一游戏进程中窗口化后的样式与外部尺寸
    /// </summary>
    private sealed record WindowBackup(int ProcessId, long Started, long Handle, long Style, long ExtendedStyle,
        int Left, int Top, int Width, int Height);

    /// <summary>
    /// 表示Windows窗口矩形
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Rectangle
    {
        public int Left, Top, Right, Bottom;
    }

    /// <summary>
    /// 表示显示器完整边界和工作区
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInformation
    {
        public int Size;
        public Rectangle Monitor, Work;
        public uint Flags;
    }

    /// <summary>
    /// 读取目标窗口状态而不激活或移动任何窗口
    /// </summary>
    public static async Task<DisplayState> ReadAsync(string game)
    {
        //同时读取引擎全屏开关避免把独占全屏误认为无边框窗口
        var reply = await new GameBridgeClient(game).CallAsync("display_state");
        if (!reply.GetProperty("ok").GetBoolean()) throw new IOException(reply.GetProperty("error").GetString());
        var fullscreen = reply.GetProperty("result").GetProperty("fullscreen").GetBoolean();
        //按安装路径核验唯一游戏进程防止同名程序成为修改目标
        using var process = FindGame(game);
        var window = process.MainWindowHandle;
        var bounds = Bounds(window);
        var monitor = Monitor(window);
        if (monitor.Right - monitor.Left is < 640 or > 8192 || monitor.Bottom - monitor.Top is < 480 or > 8192)
            throw new IOException("当前显示器尺寸超出游戏显示模式范围");
        var borderless = !fullscreen && (GetWindowLongPtr(window, -16).ToInt64() & FrameMask) == 0 &&
            bounds.Left == monitor.Left && bounds.Top == monitor.Top &&
            bounds.Right == monitor.Right && bounds.Bottom == monitor.Bottom;
        return new(process.Id, borderless, fullscreen, bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top);
    }

    /// <summary>
    /// 在游戏主线程切换显示模式后调整边框并复读结果
    /// </summary>
    public static async Task<DisplayState> ApplyAsync(string game, string mode)
    {
        //固定模式白名单禁止将外部输入解释成窗口句柄或系统命令
        if (mode is not ("borderless" or "windowed" or "fullscreen")) throw new ArgumentException("显示模式无效");
        using var process = FindGame(game);
        var window = process.MainWindowHandle;
        if (IsIconic(window)) throw new IOException("游戏窗口最小化时不能切换显示模式");
        var monitor = Monitor(window);
        var root = Path.Combine(Path.GetFullPath(game), "MaxyModLoader");
        Directory.CreateDirectory(root);
        var backupPath = Path.Combine(root, "window-session.json");
        WindowBackup? backup = null;
        if (File.Exists(backupPath))
        {
            //只接受当前进程与当前窗口的备份旧会话不会影响新窗口
            var candidate = JsonSerializer.Deserialize<WindowBackup>(File.ReadAllText(backupPath));
            if (candidate is not null && candidate.ProcessId == process.Id && candidate.Started == process.StartTime.ToUniversalTime().Ticks &&
                candidate.Handle == window.ToInt64() && candidate.Width is > 0 and <= 16384 && candidate.Height is > 0 and <= 16384 &&
                Math.Abs((long)candidate.Left) <= 32768 && Math.Abs((long)candidate.Top) <= 32768) backup = candidate;
        }
        if (mode != "borderless" && backup is not null) Restore(window, backup);

        //先让引擎退出独占全屏否则只去掉标题栏并不等于无边框窗口化
        var bridge = new GameBridgeClient(game);
        var prepared = await bridge.CallAsync("display_prepare", $"{mode}|{monitor.Right - monitor.Left}|{monitor.Bottom - monitor.Top}");
        if (!prepared.GetProperty("ok").GetBoolean()) throw new IOException(prepared.GetProperty("error").GetString());
        await Task.Delay(500);
        process.Refresh();
        if (process.HasExited || process.MainWindowHandle != window) throw new IOException("显示重建期间目标窗口发生变化");
        if (mode == "borderless")
        {
            //首次进入无边框时保存引擎窗口化后的状态重复应用不会覆盖备份
            if (backup is null)
            {
                var bounds = Bounds(window);
                backup = new(process.Id, process.StartTime.ToUniversalTime().Ticks, window.ToInt64(),
                    GetWindowLongPtr(window, -16).ToInt64(), GetWindowLongPtr(window, -20).ToInt64(),
                    bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top);
                var temporary = backupPath + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(backup));
                File.Move(temporary, backupPath, true);
            }
            try
            {
                //保持显示器刷新率与窗口层级不变仅去掉框架并覆盖当前显示器
                Style(window, -16, backup.Style & ~FrameMask);
                Style(window, -20, backup.ExtendedStyle & ~EdgeMask);
                Position(window, monitor.Left, monitor.Top, monitor.Right - monitor.Left, monitor.Bottom - monitor.Top);
            }
            catch
            {
                //窗口样式修改失败时恢复当前会话的窗口状态并保留错误
                Restore(window, backup);
                throw;
            }
        }
        else if (mode == "windowed")
        {
            if (backup is not null) Restore(window, backup);
            //无边框启动可能没有较小的旧窗口确保普通窗口不会把标题栏放到屏幕外
            var information = new MonitorInformation { Size = Marshal.SizeOf<MonitorInformation>() };
            if (!GetMonitorInfo(MonitorFromWindow(window, 2), ref information)) throw Failure("无法读取窗口工作区");
            var bounds = backup is null ? Bounds(window) : new Rectangle
                { Left = backup.Left, Top = backup.Top, Right = backup.Left + backup.Width, Bottom = backup.Top + backup.Height };
            var work = information.Work;
            var fitted = FitWindowBounds(bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top,
                work.Left, work.Top, work.Right - work.Left, work.Bottom - work.Top);
            Position(window, fitted.Left, fitted.Top, fitted.Width, fitted.Height);
        }

        //异步窗口请求完成后核验边框和显示边界不能只返回设置成功
        var watch = Stopwatch.StartNew();
        DisplayState state;
        do
        {
            await Task.Delay(100);
            state = await ReadAsync(game);
            if (mode == "borderless" && state.Borderless || mode == "windowed" && !state.Fullscreen && !state.Borderless ||
                mode == "fullscreen" && state.Fullscreen) return state;
        } while (watch.Elapsed < TimeSpan.FromSeconds(3));
        if (backup is not null) Restore(window, backup);
        throw new IOException("游戏显示模式核验失败已恢复可用的窗口备份");
    }

    /// <summary>
    /// 找到指定安装中的唯一游戏窗口并启用物理像素坐标
    /// </summary>
    private static Process FindGame(string game)
    {
        //窗口功能仅面向Windows且不允许静默选择多个安装中的任意进程
        if (!OperatingSystem.IsWindows()) throw new IOException("窗口模式仅支持Windows");
        SetProcessDpiAwarenessContext(-4);
        var expected = MaxyModLoader.Deployment.GameExecutableInstaller.ResolveOriginalExecutablePath(game);
        var matches = new List<Process>();
        //通过Steam引导的副本使用加载器标识仍按完整路径绑定目标安装
        foreach (var process in Process.GetProcessesByName("This War of Mine")
            .Concat(Process.GetProcessesByName("MaxyModLoader.Original")))
        {
            //非目标进程及时释放查询句柄查询失败也不会留下句柄
            try
            {
                if (process.MainWindowHandle != 0 && string.Equals(process.MainModule?.FileName, expected, StringComparison.OrdinalIgnoreCase))
                    matches.Add(process);
                else process.Dispose();
            }
            catch
            {
                process.Dispose();
                foreach (var match in matches) match.Dispose();
                throw;
            }
        }
        if (matches.Count != 1)
        {
            foreach (var match in matches) match.Dispose();
            throw new IOException("未找到唯一目标游戏窗口");
        }
        //仅绑定已实测的原生发行版本不对未知更新执行显示扩展
        using var executable = File.OpenRead(expected);
        if (Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(executable)) !=
            "7E114E63D2371B3A31C6070011BA3869FECB2248895AFC0171B248C3E0B69BCB")
        {
            matches[0].Dispose();
            throw new IOException("游戏版本未经过显示模式验证");
        }
        return matches[0];
    }

    /// <summary>
    /// 读取目标窗口的外部矩形
    /// </summary>
    private static Rectangle Bounds(nint window)
    {
        //读取失败时不构造默认矩形防止后续移动到错误位置
        if (!GetWindowRect(window, out var bounds)) throw Failure("无法读取游戏窗口边界");
        return bounds;
    }

    /// <summary>
    /// 读取目标窗口所在显示器的完整物理边界
    /// </summary>
    private static Rectangle Monitor(nint window)
    {
        //使用当前显示器完整边界使无边框覆盖任务栏区域
        var information = new MonitorInformation { Size = Marshal.SizeOf<MonitorInformation>() };
        if (!GetMonitorInfo(MonitorFromWindow(window, 2), ref information)) throw Failure("无法读取游戏显示器");
        return information.Monitor;
    }

    /// <summary>
    /// 设置自有目标窗口的指定样式并检查系统错误
    /// </summary>
    private static void Style(nint window, int index, long value)
    {
        //合法原值可能为零必须结合最近错误判断失败
        Marshal.SetLastPInvokeError(0);
        if (SetWindowLongPtr(window, index, (nint)value) == 0 && Marshal.GetLastPInvokeError() != 0) throw Failure("无法设置游戏窗口样式");
    }

    /// <summary>
    /// 异步设置窗口边界而不改变前台焦点或窗口层级
    /// </summary>
    private static void Position(nint window, int left, int top, int width, int height)
    {
        //跨线程窗口调整采用异步消息避免游戏绘制时阻塞辅助进程
        if (!SetWindowPos(window, 0, left, top, width, height, 0x4034)) throw Failure("无法调整游戏窗口边界");
    }

    /// <summary>
    /// 恢复当前游戏进程中已保存的窗口框架与边界
    /// </summary>
    private static void Restore(nint window, WindowBackup backup)
    {
        //先恢复样式再刷新非客户区使标题栏和边框重新生效
        Style(window, -16, backup.Style);
        Style(window, -20, backup.ExtendedStyle);
        Position(window, backup.Left, backup.Top, backup.Width, backup.Height);
    }

    /// <summary>
    /// 将Windows错误转换为可报告的文件与系统操作错误
    /// </summary>
    private static IOException Failure(string message)
    {
        //保存调用点的系统错误说明便于诊断且不自动重放窗口操作
        return new IOException(message + "：" + new Win32Exception(Marshal.GetLastPInvokeError()).Message);
    }

    /// <summary>
    /// 读取窗口样式
    /// </summary>
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)] private static extern nint GetWindowLongPtr(nint window, int index);
    /// <summary>
    /// 设置窗口样式
    /// </summary>
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)] private static extern nint SetWindowLongPtr(nint window, int index, nint value);
    /// <summary>
    /// 设置窗口位置大小和刷新标记
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    /// <summary>
    /// 读取窗口矩形
    /// </summary>
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowRect(nint window, out Rectangle bounds);
    /// <summary>
    /// 查找窗口所属显示器
    /// </summary>
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    /// <summary>
    /// 读取显示器信息
    /// </summary>
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", SetLastError = true)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInformation information);
    /// <summary>
    /// 检查窗口是否最小化
    /// </summary>
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    /// <summary>
    /// 设置辅助进程的物理像素坐标模式
    /// </summary>
    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(nint context);
}
