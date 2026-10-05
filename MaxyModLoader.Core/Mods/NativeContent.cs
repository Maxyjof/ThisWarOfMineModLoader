using System.Text.Json;
using System.Text.RegularExpressions;

namespace MaxyModLoader.Mods;

/// <summary>
/// 描述基于原版模板生成的原生物品及地图掉落补丁
/// </summary>
public sealed record NativeContent(NativeItem[] Items, NativeLoot[] Loot)
{
    /// <summary>
    /// 读取模组内部内容声明并限制规模和标识
    /// </summary>
    public static NativeContent Read(DiscoveredMod mod)
    {
        //只读取已通过目录边界检查的JSON不允许模组提供原版二进制
        var path = ModCatalog.ResolveLocalFile(mod.Directory, mod.Manifest.NativeContentFile);
        if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("原生内容声明超过1MiB");
        var content = JsonSerializer.Deserialize<NativeContent>(File.ReadAllText(path), ModManifest.JsonOptions)
            ?? throw new InvalidDataException("原生内容声明为空");
        content.Validate();
        return content;
    }

    /// <summary>
    /// 校验自有命名空间属性配方和有限掉落范围
    /// </summary>
    public void Validate()
    {
        //限制总量避免错误声明造成无限编译或覆盖原版物品
        if (Items is null || Loot is null || Items.Length is < 1 or > 512 || Loot.Length > 4096)
            throw new InvalidDataException("原生内容物品或掉落数量无效");
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in Items)
        {
            if (item is null || !ValidName(item.Id) || !item.Id.StartsWith("MML_", StringComparison.Ordinal) ||
                !ValidName(item.BaseItem) || !ids.Add(item.Id) || string.IsNullOrWhiteSpace(item.Name) ||
                item.Name.Length > 96 || item.Description is null || item.Description.Length > 2048 ||
                item.Properties is null || item.Properties.Count > 32 || item.Recipes is null || item.Recipes.Length > 16 ||
                item.DamageMultiplier is { } damage && (!double.IsFinite(damage) || damage is < 0 or > 1000))
                throw new InvalidDataException("原生物品身份属性或配方无效");
            //属性只替换原版模板已存在的标量不拼接XML或访问原生偏移
            foreach (var (name, value) in item.Properties)
                if (string.IsNullOrWhiteSpace(name) || name is "Name" or "StringName" or "StringDescription" ||
                    value is null || value.Length > 512 || value.Any(char.IsControl))
                    throw new InvalidDataException("原生物品属性键值无效");
            foreach (var recipe in item.Recipes)
                if (recipe is null || !ValidName(recipe.Device) || !double.IsFinite(recipe.Hours) || recipe.Hours is <= 0 or > 48 ||
                    recipe.ResultCount is < 1 or > 100 || recipe.Ingredients is null || recipe.Ingredients.Count is < 1 or > 16 ||
                    recipe.Ingredients.Any(pair => !ValidName(pair.Key) || pair.Value is < 1 or > 10000))
                    throw new InvalidDataException("原生制作配方无效");
        }
        foreach (var loot in Loot)
            if (loot is null || !ValidName(loot.Generator) || !ids.Contains(loot.Item) ||
                loot.Minimum < 0 || loot.Maximum < loot.Minimum || loot.Maximum > 100)
                throw new InvalidDataException("原生掉落补丁无效");
    }

    /// <summary>
    /// 限定原生查询键为普通英文标识
    /// </summary>
    public static bool ValidName(string? value)
    {
        //拒绝路径分隔符和模板表达式所有资源路径由加载器生成
        return value is not null && value.Length <= 96 && Regex.IsMatch(value, "^[A-Za-z][A-Za-z0-9_]*$");
    }
}

/// <summary>
/// 保存新物品模板展示文本参数和制作变体
/// </summary>
public sealed record NativeItem(string Id, string BaseItem, string Name, string Description,
    Dictionary<string, string> Properties, NativeRecipe[] Recipes, double? DamageMultiplier = null);

/// <summary>
/// 保存工作台成本时长和结果数量
/// </summary>
public sealed record NativeRecipe(string Device, double Hours, Dictionary<string, int> Ingredients, int ResultCount = 1);

/// <summary>
/// 指定地图生成器中的固定池数量上下限
/// </summary>
public sealed record NativeLoot(string Generator, string Item, int Minimum, int Maximum);
