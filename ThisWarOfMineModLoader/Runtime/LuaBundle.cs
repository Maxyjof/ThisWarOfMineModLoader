using System.Reflection;
using System.Text;
using System.Text.Json;
using ThisWarOfMineModLoader.Mods;

namespace ThisWarOfMineModLoader.Runtime;

/// <summary>
/// 将原始Main脚本和按依赖顺序排列的模组合成为单个Lua入口
/// </summary>
public static class LuaBundle
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    /// <summary>
    /// 编译可在游戏既有Lua虚拟机中执行的引导源码
    /// </summary>
    public static byte[] Compile(byte[] originalMain, LoadPlan plan)
    {
        //拒绝无效计划和未知字节码版本避免生成不能执行的入口
        if (!plan.IsValid) throw new InvalidDataException("不能编译无效的加载计划。");
        if (originalMain.Length > 0 && originalMain[0] == 0x1b)
            throw new InvalidDataException("Main 是 Lua 字节码，需要分析具体 Lua 版本；当前只接受 UTF-8 Lua 源码。");
        //严格读取UTF8原脚本并去除不参与Lua语法的字节顺序标记
        var original = Utf8.GetString(originalMain).TrimStart('\ufeff');
        var text = new StringBuilder("--由ThisWarOfMineModLoader0.1.0生成\nlocal compile = loadstring or load\n");
        //单独编译原Main以保留其return语句和局部作用域
        text.Append("local original, err = compile(").Append(Quote(original)).Append(", '@common/scripts/Main.lua')\nif not original then error(err) end\noriginal()\n");
        //加载嵌入式运行库确保分发工具无需携带额外源码文件
        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("ThisWarOfMineModLoader.Runtime.bootstrap.lua")!;
        using var reader = new StreamReader(resource, Utf8);
        text.Append(reader.ReadToEnd()).Append('\n');
        //重新解析入口路径防止发现与编译之间发生路径替换
        foreach (var mod in plan.Ordered)
        {
            var source = Utf8.GetString(File.ReadAllBytes(ModCatalog.ResolveEntry(mod.Directory, mod.Manifest.Entry))).TrimStart('\ufeff');
            text.Append("TWOMLoader.load_mod(").Append(Quote(mod.Manifest.Id)).Append(", ").Append(Quote(source)).Append(", {");
            text.AppendJoin(", ", mod.Manifest.Dependencies.Keys.Order(StringComparer.Ordinal).Select(Quote));
            text.Append("}, {modules = {");
            foreach (var (name, path) in mod.Manifest.Modules.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                //模块源码随入口一起打包运行时不依赖原始开发目录
                var module = Utf8.GetString(File.ReadAllBytes(ModCatalog.ResolveEntry(mod.Directory, path))).TrimStart('\ufeff');
                text.Append('[').Append(Quote(name)).Append("] = ").Append(Quote(module)).Append(',');
            }
            text.Append("}, config = {");
            foreach (var (name, value) in mod.Manifest.Settings.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                text.Append('[').Append(Quote(name)).Append("] = ").Append(EncodeSetting(value)).Append(',');
            text.Append("}})\n");
        }
        //所有模组尝试加载后广播就绪事件
        text.Append("TWOMLoader.emit('loader.ready')\n");
        return Utf8.GetBytes(text.ToString());
    }

    /// <summary>
    /// 使用三位十进制UTF8字节转义避免Lua字符串定界符注入和换行转换
    /// </summary>
    public static string Quote(string value)
    {
        //逐字节转义使中文和任意源码在Lua5.1中保持原始字节
        var text = new StringBuilder("\"");
        foreach (var b in Utf8.GetBytes(value)) text.Append('\\').Append(b.ToString("D3", System.Globalization.CultureInfo.InvariantCulture));
        return text.Append('"').ToString();
    }

    /// <summary>
    /// 将有限数值、布尔值、字符串和嵌套对象配置转换为Lua字面量
    /// </summary>
    public static string EncodeSetting(JsonElement value)
    {
        //配置不接受数组和null避免Lua表的空洞及长度语义产生歧义
        switch (value.ValueKind)
        {
            case JsonValueKind.String: return Quote(value.GetString()!);
            case JsonValueKind.True: return "true";
            case JsonValueKind.False: return "false";
            case JsonValueKind.Number:
                if (!value.TryGetDouble(out var number) || !double.IsFinite(number)) throw new InvalidDataException("配置数字必须是有限数值");
                return value.GetRawText();
            case JsonValueKind.Object:
                return "{" + string.Join(",", value.EnumerateObject().Select(property => "[" + Quote(property.Name) + "]=" + EncodeSetting(property.Value))) + "}";
            default: throw new InvalidDataException("配置只支持字符串、有限数值、布尔值及嵌套对象");
        }
    }
}
