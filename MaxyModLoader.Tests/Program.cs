using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using MaxyModLoader.Archives;
using MaxyModLoader.Deployment;
using MaxyModLoader.Mcp;
using MaxyModLoader.Mods;
using MaxyModLoader.Runtime;
using MaxyModLoader.Windowing;

namespace MaxyModLoader.Tests;

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

        //自动启动仅允许固定游戏文件名位于x64入口目录
        Test("游戏入口路径检测限制目录与文件名", () =>
        {
            Assert(GameExecutableInstaller.IsBootstrapPath(Path.Combine("D:\\game", "x64", "This War of Mine.exe")));
            Assert(!GameExecutableInstaller.IsBootstrapPath(Path.Combine("D:\\game", "This War of Mine.exe")));
            Assert(!GameExecutableInstaller.IsBootstrapPath(Path.Combine("D:\\game", "x64", "MaxyModLoader.exe")));
        });

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
        Test("实际目录发现示例", () => Assert(ModCatalog.Discover(Path.Combine(Repository, "examples")).Count == 3));
        Test("模组清单必须明确声明当前规范", () =>
        {
            //缺失规范号不能默认为历史格式当前规范仍验证数值本身
            Reject<JsonException>(() => JsonSerializer.Deserialize<ModManifest>("{\"id\":\"sample\",\"name\":\"测试\",\"version\":\"1.0.0\"}", ModManifest.JsonOptions));
            var manifest = new ModManifest { SchemaVersion = 1, Id = "sample", Name = "测试", Version = "1.0.0" };
            manifest.Validate();
            Reject<InvalidDataException>(() => (manifest with { SchemaVersion = 0 }).Validate());
        });
        Test("原生入口不读取历史副本或不完整安装状态", () => InWorkspace(root =>
        {
            //只有历史副本时仍要求当前原生运行路径不自动搬迁文件
            var directory = Path.Combine(root, "MaxyModLoader", "original"); Directory.CreateDirectory(directory);
            var legacyFile = Path.Combine(directory, "This War of Mine.exe"); File.WriteAllText(legacyFile, "fixture");
            Reject<InvalidDataException>(() => GameExecutableInstaller.ResolveOriginalExecutablePath(root));
            Assert(File.ReadAllText(legacyFile) == "fixture");

            //缺失当前事务字段的安装记录不能用于恢复
            var statePath = Path.Combine(root, "MaxyModLoader", "executable-install.json"); File.WriteAllText(statePath, "{}");
            Reject<JsonException>(() => GameExecutableInstaller.Restore(root));
            Assert(File.ReadAllText(statePath) == "{}");
        }));
        Test("模组暂存状态覆盖默认值并允许禁用损坏入口", () => InWorkspace(root =>
        {
            //暂存开关在校验Lua入口前应用因此禁用项只保留介绍
            var mod = Path.Combine(root, "sample"); Directory.CreateDirectory(mod);
            File.WriteAllText(Path.Combine(mod, "mod.json"), "{\"schemaVersion\":1,\"id\":\"sample.mod\",\"name\":\"测试\",\"version\":\"1.0.0\",\"entry\":\"missing.lua\"}");
            var states = ModStartupState.Read(Path.Combine(root, "mod-state.txt"));
            var discovered = ModCatalog.Discover(root, new Dictionary<string, bool>(states) { ["sample.mod"] = false });
            Assert(discovered.Count == 1 && !discovered[0].Manifest.Enabled && discovered[0].EntryPath == "");
        }));
        Test("模组状态严格解析并消费单次重启标记", () => InWorkspace(root =>
        {
            //状态只接受唯一模组标识和二值开关格式
            var state = Path.Combine(root, "state.txt");
            File.WriteAllText(state, "MMLS1\nsample.mod\t1\nother\t0\n", new UTF8Encoding(false));
            var values = ModStartupState.Read(state);
            Assert(values.Count == 2 && values["sample.mod"] && !values["other"]);
            File.WriteAllText(state, "MMLS1\nsample.mod\t2\n");
            Reject<InvalidDataException>(() => ModStartupState.Read(state));

            //固定请求被读取一次后移除防止额外重启
            var loader = Path.Combine(root, "MaxyModLoader"); Directory.CreateDirectory(loader);
            var request = Path.Combine(loader, "restart-request.txt");
            File.WriteAllText(request, "MMLR1\n", new UTF8Encoding(false));
            Assert(ModStartupState.ConsumeRestartRequest(root) && !ModStartupState.ConsumeRestartRequest(root));
        }));
        Test("模组ZIP支持根目录和一级目录布局", () => InWorkspace(root =>
        {
            //每个压缩包代表一个模组且包内文件会被展开到独立目录
            var archives = Path.Combine(root, "Mods"); Directory.CreateDirectory(archives);
            CreateModZip(Path.Combine(archives, "root.zip"), "", "zip.root");
            CreateModZip(Path.Combine(archives, "wrapped.zip"), "Wrapped/", "zip.wrapped");
            var staging = Path.Combine(root, "staging");
            var imported = ModZipImporter.ExtractAll(archives, staging);
            var plan = LoadPlanner.Create(ModCatalog.Discover(staging));
            Assert(imported.Count == 2 && plan.IsValid && plan.Ordered.Count == 2);
            Assert(File.Exists(Path.Combine(imported[0], "main.lua")) && File.Exists(Path.Combine(imported[1], "mod.json")));
        }));
        Test("模组ZIP拒绝目录穿越和混合模组", () => InWorkspace(root =>
        {
            //恶意路径在任何文件写入前被拒绝且不会创建目标目录之外的文件
            var archives = Path.Combine(root, "Mods"); Directory.CreateDirectory(archives);
            var zip = Path.Combine(archives, "unsafe.zip");
            using (var file = File.Create(zip))
            using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
            {
                //先写入一个有效清单使路径穿越项成为实际触发条件
                using (var manifest = new StreamWriter(archive.CreateEntry("mod.json").Open()))
                    manifest.Write("{\"schemaVersion\":1,\"id\":\"unsafe\",\"name\":\"不安全\",\"version\":\"1.0.0\"}");
                using (var escape = new StreamWriter(archive.CreateEntry("../escape.lua").Open()))
                    escape.Write("return {}");
            }
            Reject<InvalidDataException>(() => ModZipImporter.ExtractAll(archives, Path.Combine(root, "staging")));
            Assert(!File.Exists(Path.Combine(root, "escape.lua")));
        }));
        Test("管理目录保留禁用模组且不加载入口", () => InWorkspace(root =>
        {
            //禁用模组仍有介绍静态加载顺序只包含启用项
            File.WriteAllText(Path.Combine(root, "main.lua"), "return {on_load=function() end}");
            var plan = LoadPlanner.Create([Mod("active") with { Directory = root }, Mod("disabled", enabled: false)]);
            Assert(plan.Catalog.Count == 2 && plan.Ordered.Count == 1);
            var bundle = Encoding.UTF8.GetString(LuaBundle.Compile([], plan));
            Assert(bundle.Contains("MaxyModLoader.register_mod") && bundle.Contains("enabled=false"));
        }));
        Test("管理介绍拒绝空引用和危险主页协议", () =>
        {
            //主页仅展示普通网页地址不接受脚本或本机文件协议
            var manifest = new ModManifest { SchemaVersion = 1, Id = "meta", Name = "介绍", Version = "1.0.0", Website = "javascript:alert(1)" };
            Reject<InvalidDataException>(() => manifest.Validate());
            Reject<InvalidDataException>(() => (manifest with { Website = "", Features = null! }).Validate());
        });
        Test("模组能力声明只允许已知且不重复的权限", () =>
        {
            //MCP工具能力必须由模组作者在清单中明确授权
            var manifest = new ModManifest { SchemaVersion = 1, Id = "capability", Name = "能力", Version = "1.0.0" };
            (manifest with { Capabilities = ["mcp.tools"] }).Validate();
            Reject<InvalidDataException>(() => (manifest with { Capabilities = ["native.memory"] }).Validate());
            Reject<InvalidDataException>(() => (manifest with { Capabilities = ["mcp.tools", "mcp.tools"] }).Validate());
        });
        Test("模组能力会随生成入口传递", () => InWorkspace(root =>
        {
            //运行时只能收到构建计划显式验证过的能力清单
            var folder = Path.Combine(root, "action-mod"); Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "main.lua"), "return {on_load=function() end}");
            File.WriteAllText(Path.Combine(folder, "mod.json"), "{\"schemaVersion\":1,\"id\":\"action.mod\",\"name\":\"动作\",\"version\":\"1.0.0\",\"capabilities\":[\"mcp.tools\"]}");
            var plan = LoadPlanner.Create(ModCatalog.Discover(root));
            var bundle = Encoding.UTF8.GetString(LuaBundle.Compile([], plan));
            Assert(plan.IsValid && bundle.Contains("capabilities = {" + LuaBundle.Quote("mcp.tools")));
        }));
        Test("Markdown介绍只从模组目录内读取有效UTF8文件", () => InWorkspace(root =>
        {
            //模组清单引用的Markdown在打包时读取并成为游戏内展示文本
            var folder = Path.Combine(root, "markdown"); Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "main.lua"), "return {}");
            File.WriteAllText(Path.Combine(folder, "README.md"), "# 介绍\n\n- **重点** `代码`\n", new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(folder, "mod.json"), "{\"schemaVersion\":1,\"id\":\"markdown\",\"name\":\"说明\",\"version\":\"1.0.0\",\"descriptionFile\":\"README.md\"}");
            var mod = ModCatalog.Discover(root).Single();
            Assert(mod.Manifest.Description.StartsWith("# 介绍", StringComparison.Ordinal));
            var bundle = Encoding.UTF8.GetString(LuaBundle.Compile([], LoadPlanner.Create([mod])));
            Assert(bundle.Contains(LuaBundle.Quote(mod.Manifest.Description)));
            Reject<InvalidDataException>(() => ModCatalog.ResolveEntry(folder, "../README.md"));
            File.WriteAllText(Path.Combine(folder, "mod.json"), "{\"schemaVersion\":1,\"id\":\"markdown\",\"name\":\"说明\",\"version\":\"1.0.0\",\"descriptionFile\":\"../README.md\"}");
            Reject<InvalidDataException>(() => ModCatalog.Discover(root));
        }));
        Test("标准Markdown保留嵌套转义引用和代码语义", () =>
        {
            //检查标准解析结果而不是仅检查输入标记存在
            var blocks = MarkdownContent.Parse("标题\n====\n\n**粗体与*嵌套***，\\*字面星号\\* &amp; [链接][ref]\n换行  \n硬换行\n\n> 引用\n>\n> - 父级\n>   - 子级\n\n```lua\nprint('**保持原样**')\n```\n\n[ref]: https://example.com\n");
            Assert(blocks[0].Kind == "heading" && blocks[0].Level == 1);
            var runs = blocks[1].Runs;
            Assert(runs.Any(run => run.Strong && run.Emphasis && run.Text == "嵌套"));
            Assert(string.Concat(runs.Select(run => run.Text)).Contains("*字面星号* &"));
            Assert(runs.Any(run => run.Link == "https://example.com") && runs.Any(run => run.Text == "\n"));
            Assert(blocks[2].Kind == "quote" && blocks[2].Children.Any(node => node.Kind == "list"));
            Assert(blocks[3].Kind == "code" && blocks[3].Runs.Single().Text.Contains("**保持原样**"));
        });
        Test("Markdown表格任务列表和危险地址有明确结构", () =>
        {
            //表格列对齐与任务状态保留脚本协议不成为操作链接
            var blocks = MarkdownContent.Parse("|名称|数值|\n|:---|---:|\n|**工具**|12|\n\n- [x] 完成\n- [ ] 待办\n\n[危险](javascript:alert) ![图示](image.png) ~~删除~~\n");
            Assert(blocks[0].Kind == "table" && blocks[0].Children[0].Header);
            Assert(blocks[0].Children[1].Children[1].Alignment == "right");
            Assert(blocks[1].Children[0].Children[0].Runs.Any(run => run.Text.Contains("[x]")));
            Assert(blocks[2].Runs.Any(run => run.Text == "危险" && run.Link == ""));
            Assert(blocks[2].Runs.Any(run => run.Image == "image.png") && blocks[2].Runs.Any(run => run.Strike));
            Reject<InvalidDataException>(() => MarkdownContent.Parse(new string('a', 16385)));
            var encoded = MarkdownContent.Encode(MarkdownContent.Parse("```\n\"} error('注入') --\n```"));
            Assert(encoded.Contains(LuaBundle.Quote("\"} error('注入') --")));
        });
        Test("中型模组模块禁止目录逃逸", () => InWorkspace(root =>
        {
            //入口合法而内部模块非法时必须在扫描阶段拒绝整个模组
            var folder = Path.Combine(root, "mod"); Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "main.lua"), "return {}");
            File.WriteAllText(Path.Combine(folder, "mod.json"), "{\"schemaVersion\":1,\"id\":\"mod\",\"name\":\"测试\",\"version\":\"1.0.0\",\"modules\":{\"escape\":\"../outside.lua\"}}");
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

        //显示辅助只接收固定模式并保留可见的普通窗口几何
        Test("显示请求拒绝额外命令和无效关联标识", () =>
        {
            var id = new string('a', 32);
            foreach (var mode in new[] { "windowed", "fullscreen", "borderless" })
                Assert(DisplayHost.ParseRequest($"MMLD1\n{id}\n{mode}\n") == (id, mode));
            foreach (var value in new[] { $"MMLD1\n{id}\nborderless\nextra", $"MMLD1\n{id}\nborderless & command\n", "MMLD1\ninvalid\nwindowed\n" })
                Reject<InvalidDataException>(() => DisplayHost.ParseRequest(value));
        });
        Test("普通窗口保持已有位置且越界时适配负坐标显示器", () =>
        {
            Assert(GameDisplay.FitWindowBounds(100, 80, 1280, 720, 0, 0, 2560, 1400) == new GameDisplay.WindowBounds(100, 80, 1280, 720));
            var fitted = GameDisplay.FitWindowBounds(-11, -28, 2582, 1466, -1920, 0, 1920, 1040);
            Assert(fitted.Left >= -1920 && fitted.Top >= 0 && fitted.Left + fitted.Width <= 0 && fitted.Top + fitted.Height <= 1040);
            Assert(Math.Abs((double)fitted.Width / fitted.Height - 2582.0 / 1466) < 0.01);
            Reject<ArgumentException>(() => GameDisplay.FitWindowBounds(0, 0, int.MaxValue, 720, 0, 0, 1920, 1080));
        });
        Test("辅助恢复只移除自有指纹文件且拒绝第三方修改", () => InWorkspace(root =>
        {
            //当前辅助程序只安装隐藏入口不复制源目录中的其他文件
            var source = Path.Combine(root, "source"); Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(source, "MaxyModLoader.exe"), "apphost");
            File.WriteAllText(Path.Combine(source, ".self-contained"), "single-file\n");
            File.WriteAllText(Path.Combine(source, "private.txt"), "keep");
            var game = Path.Combine(root, "game");
            //前置验证本身不能创建目录或修改游戏资源
            DisplayHost.ValidateInstall(game, source);
            Assert(!Directory.Exists(game));
            DisplayHost.Install(game, source);
            var host = Path.Combine(game, "MaxyModLoader", "host");
            Assert(!File.Exists(Path.Combine(host, "private.txt")));
            var script = Path.Combine(game, "MaxyModLoader", "display-host.vbs");
            var original = File.ReadAllText(script);
            File.AppendAllText(script, "changed");
            Reject<InvalidDataException>(() => DisplayHost.Uninstall(game));
            Reject<InvalidDataException>(() => DisplayHost.Install(game, source));
            Assert(File.Exists(Path.Combine(host, "ownership.json")));
            File.WriteAllText(script, original);
            File.WriteAllText(Path.Combine(host, "other.txt"), "keep");
            DisplayHost.Uninstall(game);
            Assert(!File.Exists(script) && !File.Exists(Path.Combine(host, "ownership.json")));
            Assert(File.Exists(Path.Combine(host, "other.txt")));
        }));
        Test("辅助部署拒绝旧运行库和旧所有权格式", () => InWorkspace(root =>
        {
            //框架依赖部署不再被识别为当前自包含发行结构
            var source = Path.Combine(root, "source"); Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(source, "MaxyModLoader.dll"), "framework fixture");
            var game = Path.Combine(root, "game");
            Reject<InvalidDataException>(() => DisplayHost.Install(game, source));
            Assert(!Directory.Exists(game));

            //已有旧字典不迁移不删除必须明确报告格式错误
            File.WriteAllText(Path.Combine(source, "MaxyModLoader.exe"), "apphost");
            File.WriteAllText(Path.Combine(source, ".self-contained"), "single-file\n");
            var host = Path.Combine(game, "MaxyModLoader", "host"); Directory.CreateDirectory(host);
            var path = Path.Combine(host, "ownership.json"); File.WriteAllText(path, "{\"mode\":\"self-contained\"}");
            Reject<JsonException>(() => DisplayHost.Install(game, source));
            Reject<JsonException>(() => DisplayHost.Uninstall(game));
            Assert(File.ReadAllText(path) == "{\"mode\":\"self-contained\"}");
        }));
        Test("自包含设置辅助程序不依赖系统dotnet", () => InWorkspace(root =>
        {
            //玩家启动器直接运行自带运行时的应用入口不复制额外运行文件
            var source = Path.Combine(root, "source"); Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(source, "MaxyModLoader.exe"), "apphost");
            File.WriteAllText(Path.Combine(source, ".self-contained"), "single-file\n");
            var game = Path.Combine(root, "game"); Directory.CreateDirectory(Path.Combine(game, "MaxyModLoader", "app"));
            DisplayHost.Install(game, source);
            var script = File.ReadAllText(Path.Combine(game, "MaxyModLoader", "display-host.vbs"));
            Assert(script.Contains("app\\MaxyModLoader.exe") && !script.Contains("dotnet "));
            Assert(!File.Exists(Path.Combine(game, "MaxyModLoader", "host", "MaxyModLoader.dll")));
            DisplayHost.Uninstall(game);
            Assert(!File.Exists(Path.Combine(game, "MaxyModLoader", "display-host.vbs")));
        }));

        //使用合成容器验证格式损坏、资源替换与长度限制
        Test("容器替换保留其他资源和原文件", () => InWorkspace(root =>
        {
            var source = CreateFixture(root);
            var before = PackageBuilder.Fingerprint(source + ".dat");
            var archive = LiquidArchive.Open(source);
            var output = Path.Combine(root, "output");
            var addedHash = ResourceHash.Compute("UI/MaxyModLoader/fixture.dds");
            var asset = Encoding.UTF8.GetBytes("fixture-texture");
            archive.WriteReplacement(MainHash, Encoding.UTF8.GetBytes("replacement"), output, new Dictionary<uint, byte[]> { [addedHash] = asset });
            var written = LiquidArchive.Open(output);
            Assert(written.Entries.Count == 3);
            Assert(written.Entries.Select(entry => entry.Hash).SequenceEqual(written.Entries.Select(entry => entry.Hash).Order()));
            Assert(Encoding.UTF8.GetString(written.Read(MainHash)) == "replacement");
            Assert(written.Read(addedHash).SequenceEqual(asset));
            Assert(written.Read(1).SequenceEqual(new byte[] { 1, 2, 3 }));
            Assert(before == PackageBuilder.Fingerprint(source + ".dat"));
            Reject<InvalidDataException>(() => archive.WriteReplacement(MainHash, [], source));
            Reject<IOException>(() => archive.WriteReplacement(MainHash, [], output));
            Reject<InvalidDataException>(() => archive.WriteReplacement(MainHash, [], Path.Combine(root, "collision"),
                new Dictionary<uint, byte[]> { [MainHash] = asset }));
        }));
        Test("DDS转换原生纹理并随普通部署包加入界面素材", () => InWorkspace(root =>
        {
            var source = CreateFixture(root);
            var resources = Path.Combine(root, "resources", "UI", "MaxyModLoader");
            Directory.CreateDirectory(resources);
            var dds = new byte[132];
            Encoding.ASCII.GetBytes("DDS ").CopyTo(dds, 0);
            BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(4, 4), 124);
            BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(12, 4), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(16, 4), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(20, 4), 4);
            BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(76, 4), 32);
            BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(80, 4), 0x41);
            BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(88, 4), 32);
            BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(92, 4), 0x00FF0000);
            BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(96, 4), 0x0000FF00);
            BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(100, 4), 0x000000FF);
            BinaryPrimitives.WriteUInt32LittleEndian(dds.AsSpan(104, 4), 0xFF000000);
            dds[128] = 7; dds[129] = 11; dds[130] = 19; dds[131] = 127;
            var native = LiquidTexture.FromDds(dds);
            Assert(native.Length == 148 && native.AsSpan(144).SequenceEqual(dds.AsSpan(128)));
            Assert(BinaryPrimitives.ReadUInt32LittleEndian(native.AsSpan(8)) == 21);
            Assert(BinaryPrimitives.ReadUInt32LittleEndian(native.AsSpan(12)) == 1);
            Assert(BinaryPrimitives.ReadUInt32LittleEndian(native.AsSpan(16)) == 0x00010001);
            Assert(BinaryPrimitives.ReadUInt32LittleEndian(native.AsSpan(20)) == 4);
            Assert(native.AsSpan(24, 120).ToArray().All(value => value == 0));
            File.WriteAllBytes(Path.Combine(resources, "probe.dds"), dds);
            var package = Path.Combine(root, "package");
            PackageBuilder.Build(source, MainHash, Path.Combine(Repository, "examples"), package, Path.Combine(root, "resources"));
            var archive = LiquidArchive.Open(Path.Combine(package, "textures-s3"));
            var hash = ResourceHash.Compute("UI/MaxyModLoader/probe.texture");
            Assert(archive.Read(hash).SequenceEqual(native));
            Assert(archive.Entries.Select(entry => entry.Hash).SequenceEqual(archive.Entries.Select(entry => entry.Hash).Order()));
            Assert(!archive.Entries.Any(entry => entry.Hash == ResourceHash.Compute("UI/MaxyModLoader/probe.dds")));
            var plainPackage = Path.Combine(root, "plain-package");
            PackageBuilder.Build(source, MainHash, Path.Combine(Repository, "examples"), plainPackage);
            var builtin = LiquidArchive.Open(Path.Combine(plainPackage, "textures-s3"));
            Assert(builtin.Read(ResourceHash.Compute("UI/MaxyModLoader/ModManager/BrushList.texture")).Length > 144);
            Assert(builtin.Read(ResourceHash.Compute("UI/MaxyModLoader/ModManager/BrushDetail.texture")).Length > 144);
            //模组目录内启用资源自动进入纹理容器不需要额外的CLI参数
            var modRoot = Path.Combine(root, "resource-mods");
            var modFolder = Path.Combine(modRoot, "asset-mod");
            var modResourceRoot = Path.Combine(modFolder, "resources", "UI", "MaxyModLoader", "Items");
            Directory.CreateDirectory(modResourceRoot);
            File.WriteAllText(Path.Combine(modFolder, "mod.json"), "{\"schemaVersion\":1,\"id\":\"asset.mod\",\"name\":\"资源模组\",\"version\":\"1.0.0\"}");
            File.WriteAllText(Path.Combine(modFolder, "main.lua"), "return {}\n");
            File.WriteAllBytes(Path.Combine(modResourceRoot, "icon.dds"), dds);
            var modPackage = Path.Combine(root, "mod-resource-package");
            PackageBuilder.Build(source, MainHash, modRoot, modPackage);
            var modTextures = LiquidArchive.Open(Path.Combine(modPackage, "textures-s3"));
            Assert(modTextures.Read(ResourceHash.Compute("UI/MaxyModLoader/Items/icon.texture"))
                .SequenceEqual(LiquidTexture.FromDds(dds)));
            Reject<InvalidDataException>(() => LiquidTexture.FromDds(dds[..^1]));
            var unsupported = (byte[])dds.Clone();
            BinaryPrimitives.WriteUInt32LittleEndian(unsupported.AsSpan(28, 4), 2);
            Reject<InvalidDataException>(() => LiquidTexture.FromDds(unsupported));
            unsupported = (byte[])dds.Clone();
            BinaryPrimitives.WriteUInt32LittleEndian(unsupported.AsSpan(88, 4), 24);
            Reject<InvalidDataException>(() => LiquidTexture.FromDds(unsupported));
            File.WriteAllBytes(Path.Combine(resources, "invalid.dds"), [1, 2, 3]);
            Reject<InvalidDataException>(() => PackageBuilder.Build(source, MainHash, Path.Combine(Repository, "examples"),
                Path.Combine(root, "bad-package"), Path.Combine(root, "resources")));
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
            Assert(PackageBuilder.Fingerprint(source + ".dat") == manifest.Scripts.BuiltDataSha256);
            //确认恢复后本次会话备份和没有安装日志关联的旧目录都被清理
            var state = JsonSerializer.Deserialize<InstallState>(File.ReadAllText(Path.Combine(game, "MaxyModLoader", "install-state.json")), ModManifest.JsonOptions)!;
            var backup = Path.Combine(game, state.BackupDirectory);
            var staleBackup = Path.Combine(game, "MaxyModLoader", "backups", "00000000000000000000000000000000");
            Directory.CreateDirectory(staleBackup);
            File.WriteAllText(Path.Combine(staleBackup, "stale.marker"), "obsolete session");
            Assert(Directory.Exists(backup));
            PackageInstaller.Restore(game);
            Assert(PackageBuilder.Fingerprint(source + ".dat") == manifest.Scripts.OriginalDataSha256);
            Assert(PackageBuilder.Fingerprint(source + ".idx") == manifest.Scripts.OriginalIndexSha256);
            Assert(!File.Exists(Path.Combine(game, "MaxyModLoader", "install-state.json")));
            Assert(!Directory.Exists(backup));
            Assert(!Directory.Exists(staleBackup));
        }));
        Test("非当前部署格式在修改游戏前被拒绝", () => InWorkspace(root =>
        {
            //缺失纹理容器不再被解释为有效的单容器部署包
            var game = Path.Combine(root, "game"); Directory.CreateDirectory(game);
            var source = CreateFixture(game);
            var package = Path.Combine(root, "package");
            var manifest = PackageBuilder.Build(source, MainHash, Path.Combine(Repository, "examples"), package);
            var path = Path.Combine(package, "package.json");
            File.WriteAllText(path, JsonSerializer.Serialize(manifest with { Textures = null! }, ModManifest.JsonOptions));
            Reject<InvalidDataException>(() => PackageInstaller.Install(game, package));
            Assert(!Directory.Exists(Path.Combine(game, "MaxyModLoader")));
            Assert(PackageBuilder.Fingerprint(source + ".dat") == manifest.Scripts.OriginalDataSha256);

            //旧根级容器字段不会被迁移为当前Scripts字段
            File.WriteAllText(path, "{\"container\":\"common\",\"mods\":[]}");
            Reject<JsonException>(() => PackageInstaller.Install(game, package));
            Assert(!Directory.Exists(Path.Combine(game, "MaxyModLoader")));

            //有效的当前安装仍拒绝叠加安装并支持完整恢复
            File.WriteAllText(path, JsonSerializer.Serialize(manifest, ModManifest.JsonOptions));
            PackageInstaller.Install(game, package);
            Reject<IOException>(() => PackageInstaller.Install(game, package));
            PackageInstaller.Restore(game);
            Assert(PackageBuilder.Fingerprint(source + ".idx") == manifest.Scripts.OriginalIndexSha256);
        }));
        Test("恢复器不读取旧品牌目录的日志", () => InWorkspace(root =>
        {
            //历史目录不属于当前恢复契约读取失败时不能触碰它
            var game = Path.Combine(root, "game"); Directory.CreateDirectory(game);
            var source = CreateFixture(game);
            var directory = Path.Combine(game, "TWOMLoader"); Directory.CreateDirectory(directory);
            var log = Path.Combine(directory, "install-state.json"); File.WriteAllText(log, "legacy fixture");
            var before = PackageBuilder.Fingerprint(source + ".dat");
            Reject<DirectoryNotFoundException>(() => PackageInstaller.Restore(game));
            Assert(File.ReadAllText(log) == "legacy fixture" && PackageBuilder.Fingerprint(source + ".dat") == before);
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
            Assert(PackageBuilder.Fingerprint(source + ".dat") == manifest.Scripts.OriginalDataSha256);
        }));
        Test("部署包篡改在备份前被拒绝", () => InWorkspace(root =>
        {
            var game = Path.Combine(root, "game"); Directory.CreateDirectory(game);
            var source = CreateFixture(game);
            var package = Path.Combine(root, "package");
            var manifest = PackageBuilder.Build(source, MainHash, Path.Combine(Repository, "examples"), package);
            File.AppendAllText(Path.Combine(package, "common.dat"), "tampered");
            Reject<InvalidDataException>(() => PackageInstaller.Install(game, package));
            Assert(PackageBuilder.Fingerprint(source + ".dat") == manifest.Scripts.OriginalDataSha256);
            Assert(!Directory.Exists(Path.Combine(game, "MaxyModLoader")));
        }));
        Test("纹理篡改在安装前拒绝且多容器中断完整恢复", () => InWorkspace(root =>
        {
            //合成两个容器核验预校验不会先修改脚本目标
            var game = Path.Combine(root, "game"); Directory.CreateDirectory(game);
            var source = CreateFixture(game);
            var package = Path.Combine(root, "package");
            var manifest = PackageBuilder.Build(source, MainHash, Path.Combine(Repository, "examples"), package);
            var texturePath = Path.Combine(package, "textures-s3.dat");
            var originalBuilt = File.ReadAllBytes(texturePath);
            File.AppendAllText(texturePath, "tampered");
            Reject<InvalidDataException>(() => PackageInstaller.Install(game, package));
            Assert(!Directory.Exists(Path.Combine(game, "MaxyModLoader")));
            Assert(PackageBuilder.Fingerprint(source + ".dat") == manifest.Scripts.OriginalDataSha256);
            File.WriteAllBytes(texturePath, originalBuilt);
            PackageInstaller.Install(game, package);
            //模拟纹理索引尚未替换以及脚本数据已经恢复的混合中断状态
            var statePath = Path.Combine(game, "MaxyModLoader", "install-state.json");
            var state = JsonSerializer.Deserialize<InstallState>(File.ReadAllText(statePath), ModManifest.JsonOptions)!;
            var backup = Path.Combine(game, state.BackupDirectory);
            File.Copy(Path.Combine(backup, "textures-s3.idx"), Path.Combine(game, "textures-s3.idx"), true);
            File.Copy(Path.Combine(backup, "common.dat"), source + ".dat", true);
            PackageInstaller.Restore(game);
            Assert(PackageBuilder.Fingerprint(source + ".idx") == manifest.Scripts.OriginalIndexSha256);
            Assert(PackageBuilder.Fingerprint(Path.Combine(game, "textures-s3.dat")) == manifest.Textures.OriginalDataSha256);
            Assert(PackageBuilder.Fingerprint(Path.Combine(game, "textures-s3.idx")) == manifest.Textures.OriginalIndexSha256);
        }));

        //输出可由独立Lua5.1解释器执行的完整引导脚本
        Test("原生内容声明拒绝路径重复物品和错误配方", () =>
        {
            //新增物品限定自有前缀成本和时长必须是有限合法值
            var item = new NativeItem("MML_Test", "Machinegun", "测试物品", "中文介绍", new(), [], 45,
                "UI/MaxyModLoader/Items/Test.dds", 17);
            new NativeContent([item], []).Validate();
            Reject<InvalidDataException>(() => new NativeContent([item with { IconTextureName = "UI/KosovoIcons/vanilla.dds" }], []).Validate());
            Reject<InvalidDataException>(() => new NativeContent([item with { IconIndex = 64 }], []).Validate());
            Reject<InvalidDataException>(() => new NativeContent([item with { Id = "../Gun" }], []).Validate());
            Reject<InvalidDataException>(() => new NativeContent([item, item with { Id = "MML_test" }], []).Validate());
            Reject<InvalidDataException>(() => new NativeContent([item with { Recipes = [new("MetalWorkshop3", double.NaN, new() { ["Parts"] = 1 })] }], []).Validate());
            Reject<InvalidDataException>(() => new NativeContent([item], [new("LootGen_Map11", "MML_Unknown", 0, 1)]).Validate());
            Reject<InvalidDataException>(() => new NativeContent([item], [], [new("MissingTrader", "MML_Test", 2, 1)]).Validate());
        });
        Test("全部示例原生物品均使用存在的原创图标", () =>
        {
            //按正式加载顺序解析所有示例模组与依赖
            var mods = ModCatalog.Discover(Path.Combine(Repository, "mods"));
            var plan = LoadPlanner.Create(mods);
            Assert(plan.IsValid);
            var textures = new HashSet<uint>();
            var describedItems = new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["twom.content.more-guns"] = 40,
                ["twom.content.ammunition"] = 4,
                ["twom.content.field-equipment"] = 6
            };

            //真实解析示例说明文件确认标题加粗和物品清单使用标准Markdown结构
            foreach (var (id, expectedCount) in describedItems)
            {
                var mod = plan.Catalog.Single(item => item.Manifest.Id == id);
                var document = MarkdownContent.Parse(mod.Manifest.Description);
                var lists = document.Where(node => node.Kind == "list").ToArray();
                Assert(lists.Sum(list => list.Children.Count) == expectedCount);
                Assert(document.Any(node => node.Kind == "heading" && node.Runs.Count > 0));
                Assert(lists.SelectMany(list => list.Children).SelectMany(item => item.Children)
                    .SelectMany(item => item.Runs).Any(run => run.Strong));
            }

            //逐个验证启用模组中的DDS并收集引擎实际使用的纹理路径哈希
            foreach (var mod in plan.Ordered)
            {
                var resourceRoot = Path.Combine(mod.Directory, "resources");
                if (!Directory.Exists(resourceRoot)) continue;
                foreach (var file in Directory.EnumerateFiles(resourceRoot, "*.dds", SearchOption.AllDirectories))
                {
                    var relative = Path.GetRelativePath(resourceRoot, file).Replace('\\', '/');
                    var texture = LiquidTexture.FromDds(File.ReadAllBytes(file));
                    Assert(textures.Add(ResourceHash.Compute(Path.ChangeExtension(relative, ".texture"))));
                    Assert(texture.Length > 144);
                }
            }

            //逐件核对图标路径与编号确保所有新增物品都有自有贴图
            var itemCount = 0;
            foreach (var mod in plan.Ordered.Where(mod => mod.Manifest.NativeContentFile.Length > 0))
            {
                var content = NativeContent.Read(mod);
                foreach (var item in content.Items)
                {
                    var hash = ResourceHash.Compute(item.IconTextureName[..^4] + ".texture");
                    Assert(textures.Contains(hash));
                    Assert(item.IconIndex is >= 0 and < 64);
                    itemCount++;
                }
            }

            //固定数量断言避免示例模组清单遗漏新增物品
            Assert(itemCount == 50);
        });
        Test("物品配方补丁只改直接属性且保留其他模板数据", () =>
        {
            //合成模板不含官方素材检查同名嵌套属性不会被误改
            var document = XDocument.Parse("""
                <KosovoItemElementConfig><Properties><Prop Name="Name" Value="Machinegun"/><Prop Name="StringName" Value="old"/>
                <Prop Name="StringDescription" Value="old"/><Prop Name="IconTextureName" Value="old.dds"/><Prop Name="IconIndex" Value="0"/>
                <Prop Name="Value" Value="74"/><Prop Name="HP" Value="-1"/>
                <Prop Name="PassiveMultipliers"><Entry><Properties><Prop Name="ParameterName" Value="DamageMultiplier"/>
                <Prop Name="MultiplierValue" Value="50"/><Prop Name="Value" Value="keep"/></Properties></Entry></Prop>
                <Prop Name="CraftingRecipes"><Entry>old</Entry></Prop></Properties></KosovoItemElementConfig>
                """);
            var item = new NativeItem("MML_Test", "Machinegun", "测试", "介绍", new() { ["Value"] = "90" },
                [new("MetalWorkshop3", 1.5, new() { ["WeaponParts"] = 4 })], 45,
                "UI/MaxyModLoader/Items/Test.dds", 17);
            NativeContentCompiler.ApplyItem(document, item, new HashSet<string> { "MetalWorkshop3", "WeaponParts" });
            Assert(document.Descendants("Prop").Single(node => (string?)node.Attribute("Name") == "IconTextureName").Attribute("Value")!.Value == item.IconTextureName);
            Assert(document.Descendants("Prop").Single(node => (string?)node.Attribute("Name") == "IconIndex").Attribute("Value")!.Value == "17");
            Assert(document.Descendants("Prop").Single(node => (string?)node.Attribute("Name") == "HP").Attribute("Value")!.Value == "-1");
            Assert(document.Descendants("Prop").Any(node => (string?)node.Attribute("Value") == "keep"));
            Assert(document.Descendants("Prop").Single(node => (string?)node.Attribute("Name") == "Count").Attribute("Value")!.Value == "4");
            Reject<InvalidDataException>(() => NativeContentCompiler.ApplyItem(document, item with { Properties = new() { ["Missing"] = "0" } }, new HashSet<string>()));
        });
        Test("交易补丁仅加入唯一已知商人的原版货单", () =>
        {
            //合成最小原版商人结构验证货单类型和关键字段
            var document = XDocument.Parse("""
                <KosovoTradingConfig><Properties><Prop Name="Entries"><Entry><Properties ClassName="KosovoTraderConfig">
                <Prop Name="Name" Value="BasicTrader"/><Prop Name="OfferedItems"><Entry><Properties ClassName="KosovoTraderItemOfferConfig">
                <Prop Name="Name" Value="Ammo"/></Properties></Entry></Prop></Properties></Entry></Prop></Properties></KosovoTradingConfig>
                """);
            NativeContentCompiler.ApplyTrading(document, [new("BasicTrader", "MML_Test", 0.25, 1.5, 1, 1)]);
            var offer = document.Descendants("Properties").Single(node => (string?)node.Attribute("ClassName") == "KosovoTraderItemOfferConfig" &&
                node.Elements("Prop").Any(value => (string?)value.Attribute("Value") == "MML_Test"));
            Assert(offer.Elements("Prop").Single(value => (string?)value.Attribute("Name") == "OccuranceProbability").Attribute("Value")!.Value == "0.25");
            Reject<InvalidDataException>(() => NativeContentCompiler.ApplyTrading(document, [new("MissingTrader", "MML_Other", 0.1, 1)]));
        });
        Test("原生语言字典保留中文代理项和长度边界", () =>
        {
            //字符数按UTF16计数与原版二进制语言格式一致
            var data = LiquidLanguage.Encode(new Dictionary<string, string> { ["MML/Test"] = "测试😀" });
            using var reader = new BinaryReader(new MemoryStream(data));
            Assert(reader.ReadUInt32() == data.Length - 4 && reader.ReadUInt32() == 1);
            Assert(Encoding.UTF8.GetString(reader.ReadBytes(reader.ReadUInt16())) == "MML/Test");
            var count = reader.ReadUInt16(); Assert(count == 4 && Encoding.Unicode.GetString(reader.ReadBytes(count * 2)) == "测试😀");
            Assert(reader.BaseStream.Position == data.Length);
        });
        Test("原生登记安装拒绝第三方修改并逐字节恢复原列表", () => InWorkspace(root =>
        {
            //原生产物使用自有合成字节验证安装所有权不运行游戏或官方工具
            var game = Path.Combine(root, "game"); Directory.CreateDirectory(game);
            var source = CreateFixture(game);
            var package = Path.Combine(root, "package");
            var manifest = PackageBuilder.Build(source, MainHash, Path.Combine(Repository, "examples"), package);
            Directory.CreateDirectory(Path.Combine(package, "native"));
            var files = new Dictionary<string, string>();
            foreach (var name in NativeContentCompiler.FileNames)
            {
                var path = Path.Combine(package, "native", name); File.WriteAllText(path, "fixture-" + name);
                files.Add(name, PackageBuilder.Fingerprint(path));
            }
            manifest = manifest with { Native = new(files, ["MML_Test"]) };
            File.WriteAllText(Path.Combine(package, "package.json"), JsonSerializer.Serialize(manifest, ModManifest.JsonOptions));
            Directory.CreateDirectory(Path.Combine(game, "Mods"));
            var list = Path.Combine(game, "Mods", "Mods.list"); var original = Encoding.UTF8.GetBytes("other|已有模组|说明|disabled|local|SWG");
            File.WriteAllBytes(list, original);
            PackageInstaller.Install(game, package);
            var installed = File.ReadAllBytes(list); Assert(Encoding.UTF8.GetString(installed).Contains("MaxyModLoaderNative|"));
            File.AppendAllText(list, "third-party");
            Reject<InvalidDataException>(() => PackageInstaller.Restore(game));
            Assert(PackageBuilder.Fingerprint(source + ".dat") == manifest.Scripts.BuiltDataSha256);
            //模拟游戏移除SWG标记并统一登记文件换行后的实际写回结果
            var gameNormalized = Encoding.UTF8.GetString(installed)
                .Replace("MaxyModLoaderNative|MaxyModLoader原生内容|由加载器管理物品配方和掉落请通过加载器恢复|enabled|local|SWG", "MaxyModLoaderNative|MaxyModLoader原生内容|由加载器管理物品配方和掉落请通过加载器恢复|enabled|local", StringComparison.Ordinal)
                .Replace("\r\n", "\n", StringComparison.Ordinal);
            File.WriteAllText(list, gameNormalized, new UTF8Encoding(false));
            PackageInstaller.Restore(game);
            Assert(File.ReadAllBytes(list).AsSpan().SequenceEqual(original));
            Assert(!Directory.EnumerateFiles(Path.Combine(game, "Mods"), "MaxyModLoaderNative_*").Any());
            PackageInstaller.Install(game, package);
            //模拟登记尚未完成以及部分自有文件已被恢复的混合中断状态
            File.WriteAllBytes(list, original); File.Delete(Path.Combine(game, "Mods", "MaxyModLoaderNative_common.idx"));
            PackageInstaller.Restore(game);
            Assert(File.ReadAllBytes(list).AsSpan().SequenceEqual(original));
            Assert(!Directory.EnumerateFiles(Path.Combine(game, "Mods"), "MaxyModLoaderNative_*").Any());
        }));
        Test("官方未压缩容器格式保留资源并拒绝伪造长度", () => InWorkspace(root =>
        {
            //官方编译器实测使用000300头部资源仍按逐条标志读取
            var source = CreateFixture(root);
            var index = File.ReadAllBytes(source + ".idx");
            index[2] = 0;
            File.WriteAllBytes(source + ".idx", index);
            var archive = LiquidArchive.Open(source);
            Assert(Encoding.UTF8.GetString(archive.Read(MainHash)).Contains("original"));
            //压缩总标记不改变每条资源的边界校验
            index[15] = 255; index[16] = 255; index[17] = 255; index[18] = 127;
            File.WriteAllBytes(source + ".idx", index);
            Reject<InvalidDataException>(() => LiquidArchive.Open(source));
        }));
        Test("导出Lua集成验证入口", () =>
        {
            var plan = LoadPlanner.Create(ModCatalog.Discover(Path.Combine(Repository, "examples")));
            var output = Path.Combine(Repository, "artifacts", "tests"); Directory.CreateDirectory(output);
            var bundle = Encoding.UTF8.GetString(LuaBundle.Compile(Encoding.UTF8.GetBytes("original_ran = true; return 'done'"), plan));
            var assemblyVersion = typeof(LuaBundle).Assembly.GetName().Version ?? throw new InvalidOperationException("测试程序集缺少版本号");
            var currentVersion = $"{assemblyVersion.Major}.{assemblyVersion.Minor}.{assemblyVersion.Build}";
            Assert(bundle.Contains($"--由MaxyModLoader{currentVersion}生成", StringComparison.Ordinal));
            Assert(bundle.Contains($"version = \"{currentVersion}\"", StringComparison.Ordinal));
            File.WriteAllBytes(Path.Combine(output, "bundle.lua"), Encoding.UTF8.GetBytes(bundle));
            Reject<InvalidDataException>(() => LuaBundle.Compile([0x1b, 0x4c], plan));
            //额外导出实际多模组包供独立解释器执行每个内部模块
            var playtestMods = ModCatalog.Discover(Path.Combine(Repository, "playtests", "mods"));
            Assert(playtestMods.Count == 9);
            var playtestPlan = LoadPlanner.Create(playtestMods);
            Assert(playtestPlan.IsValid);
            File.WriteAllBytes(Path.Combine(output, "playtest-bundle.lua"), LuaBundle.Compile(Encoding.UTF8.GetBytes("return true"), playtestPlan));
        });
        Test("MCP服务版本与Lua运行库一致", () => InWorkspace(root =>
        {
            //使用临时游戏目录初始化服务无需启动游戏桥接
            var server = new McpServer(new GameBridgeClient(root));
            var input = new StringReader("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\"}\n");
            var output = new StringWriter();
            server.RunAsync(input, output).GetAwaiter().GetResult();

            //读取MCP握手中的版本并与运行时程序集版本对照
            using var response = JsonDocument.Parse(output.ToString());
            var reportedVersion = response.RootElement.GetProperty("result").GetProperty("serverInfo").GetProperty("version").GetString();
            var assemblyVersion = typeof(LuaBundle).Assembly.GetName().Version ?? throw new InvalidOperationException("测试程序集缺少版本号");
            var expectedVersion = $"{assemblyVersion.Major}.{assemblyVersion.Minor}.{assemblyVersion.Build}";
            Assert(reportedVersion == expectedVersion);
        }));

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
        return new(".", new ModManifest { SchemaVersion = 1, Id = id, Name = id, Version = version,
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
    /// 创建根目录或一级包装目录布局的测试模组压缩包
    /// </summary>
    private static void CreateModZip(string path, string prefix, string id)
    {
        //测试包只含规范清单和Lua入口用于验证安全导入后的正式发现流程
        using var file = File.Create(path);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create);
        using (var manifest = new StreamWriter(archive.CreateEntry(prefix + "mod.json").Open()))
            manifest.Write(JsonSerializer.Serialize(new { schemaVersion = 1, id, name = id, version = "1.0.0" }));
        using (var entry = new StreamWriter(archive.CreateEntry(prefix + "main.lua").Open()))
            entry.Write("return {}\n");
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
        index.Dispose();
        //同时提供独立纹理容器以测试原版条目保留和多容器安装恢复
        File.Copy(source + ".idx", Path.Combine(directory, "textures-s3.idx"));
        File.Copy(source + ".dat", Path.Combine(directory, "textures-s3.dat"));
        return source;
    }
}
