namespace MaxyModLoader.Bootstrap;

/// <summary>
/// 提供不显示控制台的原生游戏替换入口
/// </summary>
internal static class Program
{
    /// <summary>
    /// 将游戏启动参数交给加载器主流程
    /// </summary>
    public static Task<int> Main(string[] args)
    {
        //通过同一进程执行加载器避免额外控制台窗口和进程名变化
        return MaxyModLoader.Cli.Program.Main(args);
    }
}
