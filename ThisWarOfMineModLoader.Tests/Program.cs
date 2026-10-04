using ThisWarOfMineModLoader.Mods;

var tests = new List<(string Name, Action Run)>();
void Test(string name, Action run) => tests.Add((name, run));
void Assert(bool condition) { if (!condition) throw new Exception("断言失败"); }
void Reject(Action action)
{
    try { action(); } catch (InvalidDataException) { return; }
    throw new Exception("非法数据未被拒绝");
}
DiscoveredMod Mod(string id, Dictionary<string, string>? dependencies = null, string[]? conflicts = null,
    bool enabled = true, string version = "1.0.0") => new(".", new ModManifest
    { Id = id, Name = id, Version = version, Dependencies = dependencies ?? [], Conflicts = conflicts ?? [], Enabled = enabled }, "main.lua");

Test("依赖排序与输入顺序无关", () =>
{
    var a = Mod("a", new() { ["z"] = "1.0.0" }); var z = Mod("z");
    var expected = new[] { "z", "a" };
    Assert(LoadPlanner.Create([a, z]).Ordered.Select(m => m.Manifest.Id).SequenceEqual(expected));
    Assert(LoadPlanner.Create([z, a]).Ordered.Select(m => m.Manifest.Id).SequenceEqual(expected));
});
Test("禁用依赖阻止加载", () => Assert(!LoadPlanner.Create([Mod("a", new() { ["b"] = "1.0.0" }), Mod("b", enabled: false)]).IsValid));
Test("版本不足阻止加载", () => Assert(!LoadPlanner.Create([Mod("a", new() { ["b"] = "2.0.0" }), Mod("b")]).IsValid));
Test("冲突阻止加载", () => Assert(!LoadPlanner.Create([Mod("a", conflicts: ["b"]), Mod("b")]).IsValid));
Test("重复 ID 阻止加载", () => Assert(!LoadPlanner.Create([Mod("a"), Mod("a")]).IsValid));
Test("循环依赖不会产生部分加载结果", () =>
{
    var plan = LoadPlanner.Create([Mod("a", new() { ["b"] = "1.0.0" }), Mod("b", new() { ["a"] = "1.0.0" })]);
    Assert(!plan.IsValid && plan.Ordered.Count == 0 && plan.Errors.Any(e => e.Contains("循环")));
});
Test("版本严格校验", () => { Reject(() => ModVersion.Parse("01.2.3")); Reject(() => ModVersion.Parse("1.0")); Reject(() => ModVersion.Parse("1.0.0-beta")); });
Test("入口禁止目录逃逸", () => { Reject(() => ModCatalog.ResolveEntry(".", "../outside.lua")); Reject(() => ModCatalog.ResolveEntry(".", "C:/outside.lua")); });
Test("实际目录发现示例", () =>
{
    var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../examples"));
    var mods = ModCatalog.Discover(root);
    Assert(mods.Count == 1 && LoadPlanner.Create(mods).IsValid);
});

var failed = 0;
foreach (var (name, run) in tests)
{
    try { run(); Console.WriteLine($"通过：{name}"); }
    catch (Exception e) { failed++; Console.Error.WriteLine($"失败：{name}：{e.Message}"); }
}
Console.WriteLine($"{tests.Count - failed}/{tests.Count} 测试通过");
return failed == 0 ? 0 : 1;
