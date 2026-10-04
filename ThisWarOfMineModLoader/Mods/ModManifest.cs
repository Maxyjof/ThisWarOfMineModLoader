using System.Text.Json;
using System.Text.RegularExpressions;

namespace ThisWarOfMineModLoader.Mods;

public sealed record ModManifest
{
    public int SchemaVersion { get; init; } = 1;
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Version { get; init; }
    public string Author { get; init; } = "";
    public string Description { get; init; } = "";
    public string Entry { get; init; } = "main.lua";
    public bool Enabled { get; init; } = true;
    public Dictionary<string, string> Dependencies { get; init; } = new(StringComparer.Ordinal);
    public string[] Conflicts { get; init; } = [];

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };

    public void Validate()
    {
        if (SchemaVersion != 1) throw new InvalidDataException("不支持的模组清单版本。");
        ValidateId(Id);
        if (string.IsNullOrWhiteSpace(Name)) throw new InvalidDataException("模组名称不能为空。");
        _ = ModVersion.Parse(Version);
        if (Dependencies is null || Conflicts is null) throw new InvalidDataException("依赖和冲突不能为 null。");
        foreach (var (id, minimum) in Dependencies)
        {
            ValidateId(id);
            if (id == Id) throw new InvalidDataException($"模组 {Id} 不能依赖自身。");
            _ = ModVersion.Parse(minimum);
        }
        foreach (var id in Conflicts) ValidateId(id);
    }

    private static void ValidateId(string? id)
    {
        if (id is null || !Regex.IsMatch(id, "^[a-z][a-z0-9]*(?:[._-][a-z0-9]+)*$"))
            throw new InvalidDataException($"无效的模组 ID：{id}。使用小写字母、数字和 . _ -。");
    }
}

// v1 只接受稳定的三段数字版本，避免声称支持未实现的 SemVer 范围。
public readonly record struct ModVersion(int Major, int Minor, int Patch) : IComparable<ModVersion>
{
    public static ModVersion Parse(string value)
    {
        if (value is null || !Regex.IsMatch(value, "^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)$"))
            throw new InvalidDataException($"版本必须是 major.minor.patch：{value}");
        var parts = value.Split('.');
        if (!int.TryParse(parts[0], out var major) || !int.TryParse(parts[1], out var minor) ||
            !int.TryParse(parts[2], out var patch)) throw new InvalidDataException("版本数字超出范围。");
        return new(major, minor, patch);
    }

    public int CompareTo(ModVersion other)
    {
        var result = Major.CompareTo(other.Major);
        if (result == 0) result = Minor.CompareTo(other.Minor);
        return result == 0 ? Patch.CompareTo(other.Patch) : result;
    }
}

public sealed record DiscoveredMod(string Directory, ModManifest Manifest, string EntryPath);
