using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;

namespace MaxyModLoader.Mcp;

/// <summary>
/// 只读捕获经过路径校验的游戏窗口并生成PNG截图
/// </summary>
public static class GameScreenshot
{
    /// <summary>
    /// 捕获游戏窗口不会激活窗口或发送键鼠输入
    /// </summary>
    public static string Capture(string gameDirectory)
    {
        //严格限制目标为指定安装目录内的游戏进程
        if (!OperatingSystem.IsWindows()) throw new IOException("游戏窗口截图仅支持Windows");
        //避免系统DPI虚拟化将窗口尺寸缩小从而裁掉右侧和底部
        SetProcessDpiAwarenessContext(-4);
        var expected = Path.Combine(Path.GetFullPath(gameDirectory), "x64", "This War of Mine.exe");
        var candidates = Process.GetProcessesByName("This War of Mine").Where(process =>
            string.Equals(process.MainModule?.FileName, expected, StringComparison.OrdinalIgnoreCase) && process.MainWindowHandle != 0).ToArray();
        if (candidates.Length != 1) throw new IOException("未找到唯一的目标游戏窗口");
        var window = candidates[0].MainWindowHandle;
        //最小化窗口只能返回标题条不能作为游戏画面验证
        if (IsIconic(window)) throw new IOException("游戏窗口已最小化请恢复显示后再截图");
        if (!GetWindowRect(window, out var rectangle)) throw new IOException("无法读取游戏窗口尺寸");
        var width = rectangle.Right - rectangle.Left;
        var height = rectangle.Bottom - rectangle.Top;
        if (width <= 0 || height <= 0 || (long)width * height > 16777216) throw new IOException("游戏窗口尺寸不适合截图");
        var source = GetWindowDC(window);
        nint target = 0, bitmap = 0, original = 0;
        try
        {
            //部分分配失败也进入统一释放分支避免泄漏GDI句柄
            if (source == 0) throw new IOException("无法读取游戏窗口绘图上下文");
            target = CreateCompatibleDC(source);
            bitmap = CreateCompatibleBitmap(source, width, height);
            if (target == 0 || bitmap == 0) throw new IOException("无法创建窗口截图缓冲区");
            original = SelectObject(target, bitmap);
            if (original == 0 || original == -1) throw new IOException("无法选择窗口截图位图");
            //前台游戏直接读取自己的绘图上下文避免独占全屏的PrintWindow返回黑帧
            if (GetForegroundWindow() == window)
            {
                if (!BitBlt(target, 0, 0, width, height, source, 0, 0, 0x00CC0020))
                    throw new IOException("无法读取前台游戏画面");
            }
            else if (!PrintWindow(window, target, 2)) throw new IOException("游戏未提供后台画面截图失败");
            SelectObject(target, original);
            var header = new BitmapHeader { Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32 };
            var pixels = new byte[checked(width * height * 4)];
            if (GetDIBits(target, bitmap, 0, (uint)height, pixels, ref header, 0) != height)
                throw new IOException("无法读取截图像素");
            //黑帧不伪装成有效画面也不截取其他前台应用
            if (!pixels.Where((value, index) => index % 4 != 3).Any(value => value > 8))
                throw new IOException("截图为黑帧当前渲染模式不支持后台捕获");
            var directory = Path.Combine(Path.GetFullPath(gameDirectory), "MaxyModLoader", "mcp", "screenshots");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N") + ".png");
            WritePng(path, width, height, pixels);
            return path;
        }
        finally
        {
            //无论截图是否成功都释放本次分配的GDI对象
            if (original != 0 && original != -1) SelectObject(target, original);
            if (bitmap != 0) DeleteObject(bitmap);
            if (target != 0) DeleteDC(target);
            if (source != 0) ReleaseDC(window, source);
        }
    }

    /// <summary>
    /// 将顶向下BGRA像素编码为不含外部依赖的RGB格式PNG
    /// </summary>
    private static void WritePng(string path, int width, int height, byte[] pixels)
    {
        //每行使用无过滤模式交由标准ZLib压缩器压缩
        using var compressed = new MemoryStream();
        using (var zip = new ZLibStream(compressed, CompressionLevel.Fastest, true))
        {
            var row = new byte[1 + width * 3];
            for (var y = 0; y < height; y++)
            {
                for (var x = 0; x < width; x++)
                {
                    var source = (y * width + x) * 4;
                    var target = 1 + x * 3;
                    row[target] = pixels[source + 2]; row[target + 1] = pixels[source + 1]; row[target + 2] = pixels[source];
                }
                zip.Write(row);
            }
        }
        //PNG尺寸和块校验均使用大端编码
        using var output = File.Create(path);
        output.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8; header[9] = 2;
        WriteChunk(output, "IHDR", header);
        WriteChunk(output, "IDAT", compressed.ToArray());
        WriteChunk(output, "IEND", []);
    }

    /// <summary>
    /// 写入PNG数据块和标准CRC32校验
    /// </summary>
    private static void WriteChunk(Stream output, string name, byte[] payload)
    {
        //校验覆盖块类型和负载长度不参与CRC计算
        var type = Encoding.ASCII.GetBytes(name);
        var word = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(word, payload.Length);
        output.Write(word); output.Write(type); output.Write(payload);
        uint crc = 0xffffffff;
        foreach (var value in type.Concat(payload))
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? 0xedb88320 ^ (crc >> 1) : crc >> 1;
        }
        BinaryPrimitives.WriteUInt32BigEndian(word, crc ^ 0xffffffff);
        output.Write(word);
    }

    /// <summary>
    /// 描述系统窗口边界
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct WindowRectangle { public int Left, Top, Right, Bottom; }

    /// <summary>
    /// 描述无压缩位图的像素排列
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapHeader
    {
        public uint Size; public int Width, Height; public ushort Planes, BitCount;
        public uint Compression, SizeImage; public int XPelsPerMeter, YPelsPerMeter; public uint ClrUsed, ClrImportant;
    }

    /// <summary>
    /// 读取指定窗口边界
    /// </summary>
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out WindowRectangle rectangle);
    /// <summary>
    /// 判断目标游戏窗口是否已最小化
    /// </summary>
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    /// <summary>
    /// 为截图辅助进程启用真实像素尺寸
    /// </summary>
    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(nint context);
    /// <summary>
    /// 读取当前前台窗口句柄
    /// </summary>
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    /// <summary>
    /// 获取窗口绘图上下文
    /// </summary>
    [DllImport("user32.dll")] private static extern nint GetWindowDC(nint window);
    /// <summary>
    /// 释放窗口绘图上下文
    /// </summary>
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint context);
    /// <summary>
    /// 请求窗口将画面绘制到截图缓冲区
    /// </summary>
    [DllImport("user32.dll")] private static extern bool PrintWindow(nint window, nint context, uint flags);
    /// <summary>
    /// 创建与源上下文兼容的内存上下文
    /// </summary>
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint context);
    /// <summary>
    /// 创建与源上下文兼容的位图
    /// </summary>
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleBitmap(nint context, int width, int height);
    /// <summary>
    /// 选择上下文使用的绘图对象
    /// </summary>
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint context, nint item);
    /// <summary>
    /// 删除本次创建的绘图对象
    /// </summary>
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint item);
    /// <summary>
    /// 删除本次创建的内存上下文
    /// </summary>
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint context);
    /// <summary>
    /// 读取位图的标准像素缓冲区
    /// </summary>
    [DllImport("gdi32.dll")] private static extern int GetDIBits(nint context, nint bitmap, uint start, uint lines, byte[] pixels, ref BitmapHeader header, uint usage);
    /// <summary>
    /// 将当前游戏窗口的可见像素复制到内存缓冲区
    /// </summary>
    [DllImport("gdi32.dll")] private static extern bool BitBlt(nint target, int x, int y, int width, int height, nint source, int sourceX, int sourceY, uint operation);
}
