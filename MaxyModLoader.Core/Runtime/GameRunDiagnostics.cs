using System.Text;
using System.Text.Json;

namespace MaxyModLoader.Runtime;

/// <summary>
/// 描述一次原版游戏进程的可诊断运行现场
/// </summary>
public sealed record GameRunReport(int SchemaVersion, string LoaderVersion, string GameExecutable,
    string GameExecutableSha256, int GameProcessId, DateTimeOffset GameStartedAtUtc,
    DateTimeOffset GameExitedAtUtc, long DurationMilliseconds, int ExitCode, string ExitCodeHex,
    string PackageKey, string[] Mods, int ForwardedArgumentCount);

/// <summary>
/// 将游戏进程运行现场写入游戏目录
/// </summary>
public static class GameRunDiagnostics
{
    /// <summary>
    /// 原子写入最近运行摘要并在非零退出时保留崩溃摘要
    /// </summary>
    public static void Write(string gameDirectory, GameRunReport report)
    {
        //报告文件固定写入加载器目录且使用当前JSON契约
        var directory = Path.Combine(Path.GetFullPath(gameDirectory), "MaxyModLoader");
        Directory.CreateDirectory(directory);
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        });
        WriteAtomic(Path.Combine(directory, "last-run.json"), json);
        //零退出码表示正常关闭非零退出码另存便于玩家提交诊断信息
        if (report.ExitCode != 0) WriteAtomic(Path.Combine(directory, "crash-report.json"), json);
    }

    /// <summary>
    /// 使用临时文件和替换写入完整诊断JSON
    /// </summary>
    private static void WriteAtomic(string path, string content)
    {
        //进程中断时保留旧摘要并在下一次写入前覆盖孤立临时文件
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, content, new UTF8Encoding(false));
        File.Move(temporary, path, true);
    }
}
