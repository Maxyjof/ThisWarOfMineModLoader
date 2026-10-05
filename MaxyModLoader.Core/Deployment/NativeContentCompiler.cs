using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using MaxyModLoader.Archives;
using MaxyModLoader.Mods;

namespace MaxyModLoader.Deployment;

/// <summary>
/// 保存原生内容包中固定文件的指纹及实际编译物品标识
/// </summary>
public sealed record NativePackage(Dictionary<string, string> Files, string[] Items);

/// <summary>
/// 在已核验游戏版本下调用官方工具生成自有物品和配置差异
/// </summary>
public static class NativeContentCompiler
{
    public const string GameFingerprint = "7E114E63D2371B3A31C6070011BA3869FECB2248895AFC0171B248C3E0B69BCB";
    public const string ToolFingerprint = "303D4591F7CD7890E4CF2BC1279288DCC840A464ADBD7FDA40DA4C2C6039078C";
    public static readonly string[] FileNames = ["common.dat", "common.idx", "common.dat_items.dat", "localizations.dat", "localizations.idx"];

    /// <summary>
    /// 生成原生差异包不登记或启用任何游戏模组
    /// </summary>
    public static NativePackage? Compile(string game, LoadPlan plan, string output)
    {
        //没有原生内容时维持普通Lua构建行为包括持续集成的合成容器
        var contents = plan.Ordered.Where(mod => mod.Manifest.NativeContentFile.Length > 0).Select(NativeContent.Read).ToArray();
        if (contents.Length == 0) return null;
        var items = contents.SelectMany(content => content.Items).ToArray();
        if (items.Length > 1024 || items.Select(item => item.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != items.Length)
            throw new InvalidDataException("跨模组原生物品重复或数量超限");
        //官方工具与EXE必须同时匹配不在未知版本上猜测资源结构
        game = Path.GetFullPath(game);
        if (!OperatingSystem.IsWindows() || PackageBuilder.Fingerprint(Path.Combine(game, "x64", "This War of Mine.exe")) != GameFingerprint ||
            PackageBuilder.Fingerprint(Path.Combine(game, "ModTools.exe")) != ToolFingerprint)
            throw new InvalidDataException("原生内容编译只支持已核验的游戏与ModTools版本");
        var relative = "Mods/MaxyModLoaderBuild" + Guid.NewGuid().ToString("N");
        var work = Path.Combine(game, relative.Replace('/', Path.DirectorySeparatorChar));
        if (Directory.Exists(work)) throw new IOException("临时编译目录已存在");
        Directory.CreateDirectory(work);
        var allowClean = true;
        try
        {
            //提取只发生在随机自有目录原版文件不会写入模组源码或Git
            Run(game, "-e", relative);
            Run(game, "-exex", "localizations.dat", relative + "/localizations", ".lang");
            if (!File.Exists(Path.Combine(work, "LootGeneratorsConfig.xml"))) throw new InvalidDataException("官方工具未导出完整配置");
            var knownItems = Directory.GetFiles(Path.Combine(work, "items"), "*.xml").Select(path => Path.GetFileNameWithoutExtension(path))
                .Concat(items.Select(item => item.Id)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var chineseTranslations = new Dictionary<string, string>(StringComparer.Ordinal);
            var englishTranslations = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var item in items)
            {
                //新物品只复制本机原版模板修改自己声明的标量和配方
                var target = Path.Combine(work, "items", item.Id + ".xml");
                if (File.Exists(target)) throw new InvalidDataException("原生物品标识与原版冲突");
                var document = ReadXml(Path.Combine(work, "items", item.BaseItem + ".xml"));
                ApplyItem(document, item, knownItems);
                WriteXml(target, document);
                var nameKey = "MaxyModLoader/Items/" + item.Id + "/Name";
                var descriptionKey = "MaxyModLoader/Items/" + item.Id + "/Description";
                chineseTranslations.Add(nameKey, item.Name);
                chineseTranslations.Add(descriptionKey, item.Description);
                englishTranslations.Add(nameKey, item.EnglishName.Length > 0 ? item.EnglishName : item.Name);
                englishTranslations.Add(descriptionKey, item.EnglishDescription.Length > 0 ? item.EnglishDescription : item.Description);
            }
            var loot = contents.SelectMany(content => content.Loot).ToArray();
            if (loot.Length > 0)
            {
                var path = Path.Combine(work, "LootGeneratorsConfig.xml");
                var document = ReadXml(path); ApplyLoot(document, loot); WriteXml(path, document);
            }
            var trading = contents.SelectMany(content => content.Trading ?? []).ToArray();
            if (trading.Length > 0)
            {
                //商人只引用同一构建计划中已登记的模组物品
                if (trading.Any(entry => !items.Any(item => item.Id == entry.Item)))
                    throw new InvalidDataException("商人货单引用了未安装的模组物品");
                var path = Path.Combine(work, "TradingConfig.xml");
                var document = ReadXml(path); ApplyTrading(document, trading); WriteXml(path, document);
            }
            //新增语言只包含自有键由原版语言加载机制合并不重分发原版翻译
            File.WriteAllBytes(Path.Combine(work, "localizations", "chinese.lang"), LiquidLanguage.Encode(chineseTranslations));
            File.WriteAllBytes(Path.Combine(work, "localizations", "english.lang"), LiquidLanguage.Encode(englishTranslations));
            Run(game, "-getnewfiles", relative + "/items", ".xml");
            Run(game, "-getnewfiles", relative + "/localizations");
            Run(game, "-bcommon", relative, relative + "_common.dat");
            Run(game, "-blocalizations", relative, relative + "_localizations.dat");
            //退出码不能证明成功必须读取容器和物品登记清单逐项核验
            var archive = LiquidArchive.Open(work + "_common");
            foreach (var item in items) _ = archive.Read(ResourceHash.Compute("items/" + item.Id + ".bin"));
            var registered = File.ReadAllLines(work + "_common.dat_items.dat").Where(line => line.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!registered.SetEquals(items.Select(item => "items/" + item.Id + ".bin"))) throw new InvalidDataException("原生物品登记列表与声明不符");
            _ = LiquidArchive.Open(work + "_localizations");
            var directory = Path.Combine(output, "native"); Directory.CreateDirectory(directory);
            var files = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var name in FileNames)
            {
                var destination = Path.Combine(directory, name);
                File.Copy(work + "_" + name, destination, false);
                files.Add(name, PackageBuilder.Fingerprint(destination));
            }
            return new(files, items.Select(item => item.Id).ToArray());
        }
        catch (TimeoutException)
        {
            //工具尚未退出时保留目录避免清理仍在写入的编译文件
            allowClean = false;
            throw;
        }
        finally
        {
            //随机目录只在本次创建成功后清理不递归删除玩家模组或任何备份
            if (allowClean) Clean(game, work);
        }
    }

    /// <summary>
    /// 以官方XML结构替换物品参数并创建独立工作台配方
    /// </summary>
    public static void ApplyItem(XDocument document, NativeItem item, IReadOnlySet<string> known)
    {
        //顶层属性不能通过同名嵌套节点误改材料或被动乘数
        var properties = document.Root?.Element("Properties") ?? throw new InvalidDataException("原版物品模板结构未知");
        Set(properties, "Name", item.Id);
        Set(properties, "StringName", "MaxyModLoader/Items/" + item.Id + "/Name");
        Set(properties, "StringDescription", "MaxyModLoader/Items/" + item.Id + "/Description");
        foreach (var (name, value) in item.Properties) Set(properties, name, value);
        if (item.DamageMultiplier is { } damage)
        {
            var multiplier = Prop(properties, "PassiveMultipliers").Descendants("Properties")
                .SingleOrDefault(node => node.Elements("Prop").Any(value => (string?)value.Attribute("Name") == "ParameterName" &&
                    (string?)value.Attribute("Value") == "DamageMultiplier")) ?? throw new InvalidDataException("模板没有伤害乘数");
            Set(multiplier, "MultiplierValue", damage.ToString("R", CultureInfo.InvariantCulture));
        }
        //删除继承的修复配方避免新枪误用原版损坏物品修复链
        var recipes = Prop(properties, "CraftingRecipes"); recipes.RemoveNodes();
        foreach (var recipe in item.Recipes)
        {
            if (!known.Contains(recipe.Device) || recipe.Ingredients.Keys.Any(key => !known.Contains(key)))
                throw new InvalidDataException("制作配方引用未知工作台或材料");
            var ingredients = recipe.Ingredients.Select(pair => new XElement("Entry",
                new XElement("Properties", new XAttribute("ClassName", "KosovoEquipmentItemEntryCraftingIngredient"),
                    Value("Item", pair.Key), Value("ItemUpgrade", 0), Value("Count", pair.Value), Value("RequiredToShow", 0))));
            recipes.Add(new XElement("Entry", new XElement("Properties", new XAttribute("ClassName", "KosovoItemElementCraftingRecipe"),
                Value("CrafterDevice", recipe.Device), Value("CrafterOperator", ""), Value("CraftTimeInHours", recipe.Hours),
                Value("CraftWaitInHours", 0), Value("CraftTimeRandom", 0), Value("CraftingAtOnceLimit", 0),
                Value("CraftingResultItemsCount", recipe.ResultCount), Value("CrafterAcceptNoIngredients", 0),
                Value("Resipe Validity Season", 0), Value("OnlyWhenChildInShelter", 0), Value("OnlyForChildren", 0),
                new XElement("Prop", new XAttribute("Name", "Ingredients"), ingredients),
                Value("TeachingDialogueTag", ""))));
        }
    }

    /// <summary>
    /// 在明确命名的地图生成器固定池追加自有物品
    /// </summary>
    public static void ApplyLoot(XDocument document, IEnumerable<NativeLoot> entries)
    {
        //只匹配生成器的直接Name属性拒绝模糊地图名称和未知位置
        var generators = document.Descendants("Properties").Where(node => (string?)node.Attribute("ClassName") == "KosovoLootGeneratorConfig").ToArray();
        foreach (var loot in entries)
        {
            var generator = generators.SingleOrDefault(node => node.Elements("Prop").Any(value =>
                (string?)value.Attribute("Name") == "Name" && (string?)value.Attribute("Value") == loot.Generator))
                ?? throw new InvalidDataException("原版地图掉落生成器不存在：" + loot.Generator);
            Prop(generator, "FixedPool").Add(new XElement("Entry", new XElement("Properties", new XAttribute("ClassName", "KosovoItemPoolItemEntry"),
                Value("Name", loot.Item), new XElement("Prop", new XAttribute("Name", "Tags")), Value("MinQuantity", loot.Minimum),
                Value("MaxQuantity", loot.Maximum), Value("UseValueInsteadOfQuantity", 0))));
        }
    }

    /// <summary>
    /// 将自有物品加入存在的原版商人出售货单
    /// </summary>
    public static void ApplyTrading(XDocument document, IEnumerable<NativeTrade> entries)
    {
        //准确匹配商人直接名称和货单集合不改动接受物品规则
        var traders = document.Descendants("Properties").Where(node => (string?)node.Attribute("ClassName") == "KosovoTraderConfig").ToArray();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var trade in entries)
        {
            if (!seen.Add(trade.Trader + "|" + trade.Item)) throw new InvalidDataException("商人同一货单物品重复");
            var trader = traders.SingleOrDefault(node => node.Elements("Prop").Any(value =>
                (string?)value.Attribute("Name") == "Name" && (string?)value.Attribute("Value") == trade.Trader))
                ?? throw new InvalidDataException("原版商人不存在：" + trade.Trader);
            var offers = Prop(trader, "OfferedItems");
            if (offers.Elements("Entry").Any(entry => (string?)entry.Element("Properties")?.Elements("Prop")
                .SingleOrDefault(value => (string?)value.Attribute("Name") == "Name")?.Attribute("Value") == trade.Item))
                throw new InvalidDataException("商人货单已包含相同物品");
            //使用原版货单字段名与有限的数量概率值
            offers.Add(new XElement("Entry", new XElement("Properties", new XAttribute("ClassName", "KosovoTraderItemOfferConfig"),
                Value("Name", trade.Item), Value("ValueMultiplier", trade.ValueMultiplier),
                Value("OccuranceProbability", trade.Probability), Value("MinQuantity", trade.MinimumQuantity),
                Value("MaxQuantity", trade.MaximumQuantity))));
        }
    }

    /// <summary>
    /// 读取官方XML中的旧式控制字符实体并禁止外部实体
    /// </summary>
    private static XDocument ReadXml(string path)
    {
        //原版分类字段包含U0001实体不能按普通XML字符校验拒绝
        using var reader = XmlReader.Create(path, new XmlReaderSettings { CheckCharacters = false, DtdProcessing = DtdProcessing.Prohibit });
        return XDocument.Load(reader);
    }

    /// <summary>
    /// 使用相同实体规则保存编译器输入
    /// </summary>
    private static void WriteXml(string path, XDocument document)
    {
        //保持UTF8和官方层级结构不插入执行脚本
        using var writer = XmlWriter.Create(path, new XmlWriterSettings { CheckCharacters = false, Indent = true, Encoding = new UTF8Encoding(false) });
        document.Save(writer);
    }

    /// <summary>
    /// 查找唯一直接属性避免静默接受未知模板字段
    /// </summary>
    private static XElement Prop(XElement properties, string name)
    {
        //未知或重复属性均阻止生成不完整物品
        return properties.Elements("Prop").SingleOrDefault(node => (string?)node.Attribute("Name") == name)
            ?? throw new InvalidDataException("原版模板属性不存在：" + name);
    }

    /// <summary>
    /// 替换已经存在的标量属性
    /// </summary>
    private static void Set(XElement properties, string name, string value)
    {
        //集合属性不能误当成标量整体覆盖
        var property = Prop(properties, name);
        if (property.Attribute("Value") is null) throw new InvalidDataException("原版属性不是标量：" + name);
        property.SetAttributeValue("Value", value);
    }

    /// <summary>
    /// 生成不受本机区域格式影响的标量节点
    /// </summary>
    private static XElement Value(string name, object value)
    {
        //数值统一使用小数点供原版工具读取
        return new XElement("Prop", new XAttribute("Name", name), new XAttribute("Value", Convert.ToString(value, CultureInfo.InvariantCulture)!));
    }

    /// <summary>
    /// 隐藏运行官方命令并同时排空两个输出流
    /// </summary>
    private static void Run(string game, params string[] arguments)
    {
        //参数逐项传递不拼接Shell命令也不打开工具界面
        var start = new ProcessStartInfo(Path.Combine(game, "ModTools.exe"))
            { WorkingDirectory = game, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("无法启动官方ModTools");
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(120000)) throw new TimeoutException("官方ModTools编译超时");
        Task.WaitAll(stdout, stderr);
        //官方工具返回1表示生成差异返回0也可能失败输出文件另行核验
        if (process.ExitCode is not (0 or 1)) throw new InvalidDataException("官方ModTools失败：" + stderr.Result + stdout.Result);
    }

    /// <summary>
    /// 按核验后的绝对边界清理本次编译目录和旁路产物
    /// </summary>
    private static void Clean(string game, string work)
    {
        //仅触及本次随机前缀不使用递归删除或跨Shell文件操作
        var parent = Path.GetFullPath(Path.Combine(game, "Mods")) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(work).StartsWith(parent, StringComparison.OrdinalIgnoreCase)) throw new IOException("临时目录清理路径越界");
        var directories = new List<string>(); var pending = new Stack<string>(); pending.Push(work);
        while (pending.TryPop(out var directory))
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new IOException("临时目录被替换为链接");
            directories.Add(directory);
            foreach (var file in Directory.EnumerateFiles(directory)) File.Delete(file);
            foreach (var child in Directory.EnumerateDirectories(directory)) pending.Push(child);
        }
        foreach (var directory in directories.OrderByDescending(path => path.Length)) Directory.Delete(directory);
        foreach (var suffix in new[] { "_common.dat", "_common.idx", "_common.str", "_common.dat_items.dat", "_localizations.dat", "_localizations.idx", "_localizations.str" })
            if (File.Exists(work + suffix)) File.Delete(work + suffix);
    }
}
