using System.Text;
using System.Text.RegularExpressions;

namespace MaxyModLoader.Mods;

/// <summary>
/// 读取玩家暂存的模组启用状态并消费受限的重启请求
/// </summary>
public static class ModStartupState
{
    private const string StateHeader = "MMLS1";
    private const string RestartRequest = "MMLR1\n";

    /// <summary>
    /// 读取并严格校验模组管理界面写入的启用状态
    /// </summary>
    public static IReadOnlyDictionary<string, bool> Read(string path)
    {
        //状态文件缺失时沿用各模组清单中的默认启用值
        if (!File.Exists(path)) return new Dictionary<string, bool>(StringComparer.Ordinal);

        //限制文件尺寸和编码避免损坏状态拖垮启动流程
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length > 65536) throw new InvalidDataException("模组启用状态文件超过64KiB");
        var content = new UTF8Encoding(false, true).GetString(bytes);
        var lines = content.Split('\n');
        if (lines.Length < 2 || lines[0] != StateHeader || lines[^1] != "")
            throw new InvalidDataException("模组启用状态文件格式无效");

        //每个标识必须唯一且值只能是明确的启用或禁用状态
        var states = new Dictionary<string, bool>(StringComparer.Ordinal);
        for (var index = 1; index < lines.Length - 1; index++)
        {
            var fields = lines[index].Split('\t');
            if (fields.Length != 2 || !Regex.IsMatch(fields[0], "^[a-z][a-z0-9]*(?:[._-][a-z0-9]+)*$") ||
                fields[1] is not ("0" or "1") || !states.TryAdd(fields[0], fields[1] == "1"))
                throw new InvalidDataException("模组启用状态包含重复或无效项目");
        }
        return states;
    }

    /// <summary>
    /// 在当前游戏正常退出后验证并消费模组重启请求
    /// </summary>
    public static bool ConsumeRestartRequest(string gameDirectory)
    {
        //重启标记固定在加载器目录且必须逐字节匹配
        var path = Path.Combine(Path.GetFullPath(gameDirectory), "MaxyModLoader", "restart-request.txt");
        if (!File.Exists(path)) return false;
        if (new FileInfo(path).Length > 16 || File.ReadAllText(path, Encoding.UTF8) != RestartRequest)
            throw new InvalidDataException("模组重启请求格式无效");

        //删除已核验标记避免下一轮游戏退出时重复重启
        File.Delete(path);
        return true;
    }
}
