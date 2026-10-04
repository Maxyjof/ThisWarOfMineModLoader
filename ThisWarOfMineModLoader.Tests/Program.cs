using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using ThisWarOfMineModLoader.Archives;
using ThisWarOfMineModLoader.Deployment;
using ThisWarOfMineModLoader.Mods;
using ThisWarOfMineModLoader.Runtime;

namespace ThisWarOfMineModLoader.Tests;

/// <summary>
/// 不依赖测试框架的集成验证入口
/// </summary>
internal static class Program
{
    private static readonly List<(string Name, Action Run)> Tests = [];
    private static readonly string Repository = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../"));
    private const uint MainHash = 0x5faa28a2;

    /// <summary>
    /// 注册并运行规划、资源和部署集成测试
    /// </summary>
    public static int Main()
    {
        //先检查用户约定的类方法文档和中文注释格式
        Test("中文XML与行注释规范", () => CommentConvention.Verify(Repository));

        //校验依赖顺序与完整计划的失败行为
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
        Test("重复ID阻止加载", () => Assert(!LoadPlanner.Create([Mod("a"), Mod("a")]).IsValid));
        Test("循环不会产生部分计划", () =>
        {
            var plan = LoadPlanner.Create([Mod("a", new() { ["b"] = "1.0.0" }), Mod("b", new() { ["a"] = "1.0.0" })]);
            Assert(!plan.IsValid && plan.Ordered.Count == 0 && plan.Errors.Any(e => e.Contains("循环")));
        });
        Test("长依赖链无需递归", () =>
        {
            var mods = Enumerable.Range(0, 5000).Select(i => Mod("m" + i,
                i == 0 ? [] : new() { ["m" + (i - 1)] = "1.0.0" }));
            Assert(LoadPlanner.Create(mods).Ordered.Count == 5000);
        });

        //校验清单边界和真实目录发现行为
        Test("版本严格校验", () => { Reject<InvalidDataException>(() => ModVersion.Parse("01.2.3")); Reject<InvalidDataException>(() => ModVersion.Parse("1.0")); Reject<InvalidDataException>(() => ModVersion.Parse("1.0.0-beta")); });
        Test("入口禁止目录逃逸", () => { Reject<InvalidDataException>(() => ModCatalog.ResolveEntry(".", "../outside.lua")); Reject<InvalidDataException>(() => ModCatalog.ResolveEntry(".", "C:/outside.lua")); });
        Test("实际目录发现示例", () => Assert(ModCatalog.Discover(Path.Combine(Repository, "examples")).Count == 2));
        Test("中型模组模块禁止目录逃逸", () => InWorkspace(root =>
        {
            //入口合法而内部模块非法时必须在扫描阶段拒绝整个模组
            var folder = Path.Combine(root, "mod"); Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "main.lua"), "return {}");
            File.WriteAllText(Path.Combine(folder, "mod.json"), "{\"id\":\"mod\",\"name\":\"测试\",\"version\":\"1.0.0\",\"modules\":{\"escape\":\"../outside.lua\"}}");
            Reject<InvalidDataException>(() => ModCatalog.Discover(root));
        }));
        Test("配置拒绝非有限数值和Lua数组歧义", () =>
        {
            //配置值必须能够在Lua5.1中表达为有限且明确的字面量
            using var huge = JsonDocument.Parse("1e400");
            Reject<InvalidDataException>(() => LuaBundle.EncodeSetting(huge.RootElement));
            using var array = JsonDocument.Parse("[1,null,2]");
            Reject<InvalidDataException>(() => LuaBundle.EncodeSetting(array.RootElement));
        });
        Test("Main资源哈希与实测值一致", () => Assert(ResourceHash.Compute("/Scripts/Main.lua") == MainHash));

        //使用合成容器验证格式损坏、资源替换与长度限制
        Test("容器替换保留其他资源和原文件", () => InWorkspace(root =>
        {
            var source = CreateFixture(root);
            var before = PackageBuilder.Fingerprint(source + ".dat");
            var archive = LiquidArchive.Open(source);
            var output = Path.Combine(root, "output");
            archive.WriteReplacement(MainHash, Encoding.UTF8.GetBytes("replacement"), output);
            Assert(Encoding.UTF8.GetString(LiquidArchive.Open(output).Read(MainHash)) == "replacement");
            Assert(LiquidArchive.Open(output).Read(1).SequenceEqual(new byte[] { 1, 2, 3 }));
            Assert(before == PackageBuilder.Fingerprint(source + ".dat"));
            Reject<InvalidDataException>(() => archive.WriteReplacement(MainHash, [], source));
            Reject<IOException>(() => archive.WriteReplacement(MainHash, [], output));
        }));
        Test("错误版本和索引数量被拒绝", () => InWorkspace(root =>
        {
            var source = CreateFixture(root);
            var bytes = File.ReadAllBytes(source + ".idx");
            bytes[1] = 99; File.WriteAllBytes(source + ".idx", bytes);
            Reject<InvalidDataException>(() => LiquidArchive.Open(source));
            bytes[1] = 3; bytes[3] = 5; File.WriteAllBytes(source + ".idx", bytes);
            Reject<InvalidDataException>(() => LiquidArchive.Open(source));
        }));
        Test("容器越界和超限资源被拒绝", () => InWorkspace(root =>
        {
            var source = CreateFixture(root);
            Reject<InvalidDataException>(() => LiquidArchive.Open(source).Read(MainHash, 1));
            var bytes = File.ReadAllBytes(source + ".idx");
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(23), uint.MaxValue);
            File.WriteAllBytes(source + ".idx", bytes);
            Reject<InvalidDataException>(() => LiquidArchive.Open(source));
        }));
        Test("解压长度不符被拒绝", () => InWorkspace(root =>
        {
            var source = CreateFixture(root);
            LiquidArchive.Open(source).WriteReplacement(MainHash, Encoding.UTF8.GetBytes("longer-content"), Path.Combine(root, "packed"));
            var bytes = File.ReadAllBytes(Path.Combine(root, "packed.idx"));
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(11 + 17 + 8), 1);
            File.WriteAllBytes(Path.Combine(root, "packed.idx"), bytes);
            Reject<InvalidDataException>(() => LiquidArchive.Open(Path.Combine(root, "packed")).Read(MainHash));
        }));

        //测试完整部署包的安装、恢复与指纹保护
        Test("构建安装恢复完整闭环", () => InWorkspace(root =>
        {
            var game = Path.Combine(root, "game"); Directory.CreateDirectory(game);
            var source = CreateFixture(game);
            var package = Path.Combine(root, "package");
            var manifest = PackageBuilder.Build(source, MainHash, Path.Combine(Repository, "examples"), package);
            PackageInstaller.Install(game, package);
            Assert(PackageBuilder.Fingerprint(source + ".dat") == manifest.BuiltDataSha256);
            PackageInstaller.Restore(game);
            Assert(PackageBuilder.Fingerprint(source + ".dat") == manifest.OriginalDataSha256);
            Assert(PackageBuilder.Fingerprint(source + ".idx") == manifest.OriginalIndexSha256);
            Assert(!File.Exists(Path.Combine(game, "TWOMLoader", "install-state.json")));
        }));
        Test("第三方改动阻止自动恢复", () => InWorkspace(root =>
        {
            var game = Path.Combine(root, "game"); Directory.CreateDirectory(game);
            var source = CreateFixture(game);
            var package = Path.Combine(root, "package");
            PackageBuilder.Build(source, MainHash, Path.Combine(Repository, "examples"), package);
            PackageInstaller.Install(game, package);
            File.AppendAllText(source + ".dat", "other-mod");
            var modified = PackageBuilder.Fingerprint(source + ".dat");
            Reject<InvalidDataException>(() => PackageInstaller.Restore(game));
            Assert(modified == PackageBuilder.Fingerprint(source + ".dat"));
        }));
        Test("安装中断状态仍可恢复", () => InWorkspace(root =>
        {
            var game = Path.Combine(root, "game"); Directory.CreateDirectory(game);
            var source = CreateFixture(game);
            var originalIndex = File.ReadAllBytes(source + ".idx");
            var package = Path.Combine(root, "package");
            var manifest = PackageBuilder.Build(source, MainHash, Path.Combine(Repository, "examples"), package);
            PackageInstaller.Install(game, package);
            File.WriteAllBytes(source + ".idx", originalIndex);
            PackageInstaller.Restore(game);
            Assert(PackageBuilder.Fingerprint(source + ".dat") == manifest.OriginalDataSha256);
        }));
        Test("部署包篡改在备份前被拒绝", () => InWorkspace(root =>
        {
            var game = Path.Combine(root, "game"); Directory.CreateDirectory(game);
            var source = CreateFixture(game);
            var package = Path.Combine(root, "package");
            var manifest = PackageBuilder.Build(source, MainHash, Path.Combine(Repository, "examples"), package);
            File.AppendAllText(Path.Combine(package, "common.dat"), "tampered");
            Reject<InvalidDataException>(() => PackageInstaller.Install(game, package));
            Assert(PackageBuilder.Fingerprint(source + ".dat") == manifest.OriginalDataSha256);
            Assert(!Directory.Exists(Path.Combine(game, "TWOMLoader")));
        }));

        //输出可由独立Lua5.1解释器执行的完整引导脚本
        Test("导出Lua集成验证入口", () =>
        {
            var plan = LoadPlanner.Create(ModCatalog.Discover(Path.Combine(Repository, "examples")));
            var output = Path.Combine(Repository, "artifacts", "tests"); Directory.CreateDirectory(output);
            File.WriteAllBytes(Path.Combine(output, "bundle.lua"), LuaBundle.Compile(Encoding.UTF8.GetBytes("original_ran = true; return 'done'"), plan));
            Reject<InvalidDataException>(() => LuaBundle.Compile([0x1b, 0x4c], plan));
            //额外导出实际多模组包供独立解释器执行每个内部模块
            var playtestMods = ModCatalog.Discover(Path.Combine(Repository, "playtests", "mods"));
            Assert(playtestMods.Count == 9);
            var playtestPlan = LoadPlanner.Create(playtestMods);
            Assert(playtestPlan.IsValid);
            File.WriteAllBytes(Path.Combine(output, "playtest-bundle.lua"), LuaBundle.Compile(Encoding.UTF8.GetBytes("return true"), playtestPlan));
        });

        //逐项记录结果任何失败都返回非零退出码
        var failed = 0;
        foreach (var (name, run) in Tests)
        {
            try { run(); Console.WriteLine($"通过：{name}"); }
            catch (Exception e) { failed++; Console.Error.WriteLine($"失败：{name}：{e.Message}"); }
        }
        Console.WriteLine($"{Tests.Count - failed}/{Tests.Count}测试通过");
        return failed == 0 ? 0 : 1;
    }

    /// <summary>
    /// 注册一个命名测试
    /// </summary>
    private static void Test(string name, Action run)
    {
        //收集测试后统一执行便于完整报告失败
        Tests.Add((name, run));
    }

    /// <summary>
    /// 断言条件成立
    /// </summary>
    private static void Assert(bool condition)
    {
        //不满足条件时抛出可由运行入口捕获的测试异常
        if (!condition) throw new Exception("断言失败");
    }

    /// <summary>
    /// 断言操作抛出预期的异常类型
    /// </summary>
    private static void Reject<TException>(Action action) where TException : Exception
    {
        //只接受指定异常避免其他错误掩盖缺失的校验
        try { action(); } catch (TException) { return; }
        throw new Exception("非法数据未被拒绝");
    }

    /// <summary>
    /// 创建用于依赖规划测试的最小模组信息
    /// </summary>
    private static DiscoveredMod Mod(string id, Dictionary<string, string>? dependencies = null, string[]? conflicts = null,
        bool enabled = true, string version = "1.0.0")
    {
        //直接创建发现结果让规划测试不依赖磁盘文件
        return new(".", new ModManifest { Id = id, Name = id, Version = version,
            Dependencies = dependencies ?? [], Conflicts = conflicts ?? [], Enabled = enabled }, "main.lua");
    }

    /// <summary>
    /// 在独立临时目录内运行文件测试并清理本次创建的目录
    /// </summary>
    private static void InWorkspace(Action<string> action)
    {
        //路径由临时根目录和随机标识组成不接收外部删除路径
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "twom-test-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        try { action(root); }
        finally
        {
            //删除前再次确认目录属于系统临时根目录
            var allowed = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (root.StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// 创建含Lua入口与普通资源的合成容器
    /// </summary>
    private static string CreateFixture(string directory)
    {
        //合成数据不包含任何游戏资源可以在持续集成中运行
        var source = Path.Combine(directory, "common");
        var main = Encoding.UTF8.GetBytes("original_ran = true; return 'done'");
        File.WriteAllBytes(source + ".dat", new byte[] { 1, 2, 3 }.Concat(main).ToArray());
        using var index = new BinaryWriter(File.Create(source + ".idx"));

        //按已验证的小端格式写入头部和两个原始资源条目
        index.Write(new byte[] { 0, 3, 1 }); index.Write((uint)2); index.Write((uint)0);
        index.Write((uint)1); index.Write((uint)3); index.Write((uint)3); index.Write((uint)0); index.Write((byte)0);
        index.Write(MainHash); index.Write((uint)main.Length); index.Write((uint)main.Length); index.Write((uint)3); index.Write((byte)0);
        return source;
    }
}
