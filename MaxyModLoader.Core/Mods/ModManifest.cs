using System.Text.Json;
using System.Text.RegularExpressions;

namespace MaxyModLoader.Mods;

/// <summary>
/// 描述Lua模组身份、入口以及依赖关系
/// </summary>
public sealed record ModManifest
{
    public int SchemaVersion { get; init; } = 1;
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Version { get; init; }
    public string Author { get; init; } = "";
    public string Description { get; init; } = "";
    public string DescriptionFile { get; init; } = "";
    public string NativeContentFile { get; init; } = "";
    public string[] Features { get; init; } = [];
    public string Compatibility { get; init; } = "";
    public string Website { get; init; } = "";
    public string License { get; init; } = "";
    public string Entry { get; init; } = "main.lua";
    public bool Enabled { get; init; } = true;
    public Dictionary<string, string> Dependencies { get; init; } = new(StringComparer.Ordinal);
    public string[] Conflicts { get; init; } = [];
    public Dictionary<string, string> Modules { get; init; } = new(StringComparer.Ordinal);
    public Dictionary<string, JsonElement> Settings { get; init; } = new(StringComparer.Ordinal);

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };

    /// <summary>
    /// 校验清单版本、身份字段及依赖声明
    /// </summary>
    public void Validate()
    {
        //校验规范版本与必填字段
        if (SchemaVersion != 1) throw new InvalidDataException("不支持的模组清单版本。");
        ValidateId(Id);
        if (string.IsNullOrWhiteSpace(Name)) throw new InvalidDataException("模组名称不能为空。");
        _ = ModVersion.Parse(Version);
        if (Dependencies is null || Conflicts is null || Modules is null || Settings is null)
            throw new InvalidDataException("依赖、冲突、模块和配置不能为null");
        //管理界面读取原始UTF8文本不允许空引用或无效网页协议
        if (Author is null || Description is null || Description.Length > 16384 || NativeContentFile is null ||
            NativeContentFile.Length > 240 || NativeContentFile.Length > 0 && Path.GetExtension(NativeContentFile) != ".json" || DescriptionFile is null ||
            DescriptionFile.Length > 240 || DescriptionFile.Length > 0 &&
            !Path.GetExtension(DescriptionFile).Equals(".md", StringComparison.OrdinalIgnoreCase) &&
            !Path.GetExtension(DescriptionFile).Equals(".markdown", StringComparison.OrdinalIgnoreCase) ||
            Features is null || Features.Any(string.IsNullOrWhiteSpace) ||
            Compatibility is null || Website is null || License is null)
            throw new InvalidDataException("模组介绍字段不能为null功能列表不能包含空项");
        if (Website.Length > 0 && (!Uri.TryCreate(Website, UriKind.Absolute, out var website) ||
            website.Scheme is not ("https" or "http"))) throw new InvalidDataException("模组主页只支持HTTP或HTTPS地址");
        //模块名仅作为模组内部键使用实际文件路径仍单独检查
        foreach (var name in Modules.Keys)
            if (!Regex.IsMatch(name, "^[a-z][a-z0-9._-]*$")) throw new InvalidDataException($"模块名无效：{name}");
        //逐项校验依赖与冲突并拒绝自身依赖
        foreach (var (id, minimum) in Dependencies)
        {
            ValidateId(id);
            if (id == Id) throw new InvalidDataException($"模组 {Id} 不能依赖自身。");
            _ = ModVersion.Parse(minimum);
        }
        foreach (var id in Conflicts) ValidateId(id);
    }

    /// <summary>
    /// 校验模组ID是否符合小写稳定标识规范
    /// </summary>
    private static void ValidateId(string? id)
    {
        //限定可用字符并拒绝空标识或连续分隔符
        if (id is null || !Regex.IsMatch(id, "^[a-z][a-z0-9]*(?:[._-][a-z0-9]+)*$"))
            throw new InvalidDataException($"无效的模组 ID：{id}。使用小写字母、数字和 . _ -。");
    }
}

/// <summary>
/// 表示v1规范支持的三段稳定版本号
/// </summary>
public readonly record struct ModVersion(int Major, int Minor, int Patch) : IComparable<ModVersion>
{
    /// <summary>
    /// 解析三段非负整数版本并拒绝前导零或预发布后缀
    /// </summary>
    public static ModVersion Parse(string value)
    {
        //先校验语法再处理整数溢出
        if (value is null || !Regex.IsMatch(value, "^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)$"))
            throw new InvalidDataException($"版本必须是 major.minor.patch：{value}");
        var parts = value.Split('.');
        if (!int.TryParse(parts[0], out var major) || !int.TryParse(parts[1], out var minor) ||
            !int.TryParse(parts[2], out var patch)) throw new InvalidDataException("版本数字超出范围。");
        return new(major, minor, patch);
    }

    /// <summary>
    /// 按主版本、次版本和补丁版本依次比较
    /// </summary>
    public int CompareTo(ModVersion other)
    {
        //只在更高位版本相等时比较下一位
        var result = Major.CompareTo(other.Major);
        if (result == 0) result = Minor.CompareTo(other.Minor);
        return result == 0 ? Patch.CompareTo(other.Patch) : result;
    }
}

/// <summary>
/// 保存已发现模组的目录、清单及入口路径
/// </summary>
public sealed record DiscoveredMod(string Directory, ModManifest Manifest, string EntryPath);
