namespace MaxyModLoader;

/// <summary>
/// 提供由当前程序集生成的加载器版本号
/// </summary>
internal static class LoaderVersion
{
    /// <summary>
    /// 获取不含构建元数据的三段式版本号
    /// </summary>
    internal static string GetCurrent()
    {
        //程序集版本由统一构建属性定义并由发行脚本覆盖
        var version = typeof(LoaderVersion).Assembly.GetName().Version
            ?? throw new InvalidOperationException("当前加载器程序集缺少版本号");
        return $"{version.Major}.{version.Minor}.{version.Build}";
    }
}
