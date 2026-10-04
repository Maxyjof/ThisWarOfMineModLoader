using System.Text;
using ThisWarOfMineModLoader.Mods;

Console.OutputEncoding = Encoding.UTF8;
try
{
    if (args is ["plan", var root])
    {
        var plan = LoadPlanner.Create(ModCatalog.Discover(root));
        foreach (var error in plan.Errors) Console.Error.WriteLine(error);
        foreach (var mod in plan.Ordered) Console.WriteLine($"{mod.Manifest.Id} {mod.Manifest.Version} — {mod.Manifest.Name}");
        return plan.IsValid ? 0 : 2;
    }
    Console.WriteLine("《这是我的战争》模组加载器工具\n用法：twom plan <模组目录>");
    return args.Length == 0 || args is ["--help"] ? 0 : 1;
}
catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException)
{
    Console.Error.WriteLine($"错误：{exception.Message}");
    return 2;
}
