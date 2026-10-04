namespace ThisWarOfMineModLoader.Mods;

public sealed record LoadPlan(IReadOnlyList<DiscoveredMod> Ordered, IReadOnlyList<string> Errors)
{
    public bool IsValid => Errors.Count == 0;
}

public static class LoadPlanner
{
    public static LoadPlan Create(IEnumerable<DiscoveredMod> discovered)
    {
        var errors = new List<string>();
        var all = discovered.ToArray();
        foreach (var group in all.GroupBy(m => m.Manifest.Id).Where(g => g.Count() > 1))
            errors.Add($"重复的模组 ID：{group.Key}");
        if (errors.Count > 0) return new([], errors);
        var enabled = all.Where(m => m.Manifest.Enabled).ToDictionary(m => m.Manifest.Id, StringComparer.Ordinal);
        foreach (var mod in enabled.Values)
        {
            foreach (var (id, minimum) in mod.Manifest.Dependencies)
            {
                if (!enabled.TryGetValue(id, out var dependency))
                    errors.Add($"{mod.Manifest.Id} 需要启用依赖 {id} >= {minimum}");
                else if (ModVersion.Parse(dependency.Manifest.Version).CompareTo(ModVersion.Parse(minimum)) < 0)
                    errors.Add($"{mod.Manifest.Id} 的依赖 {id} 版本不足（需要 >= {minimum}）");
            }
            foreach (var id in mod.Manifest.Conflicts.Where(enabled.ContainsKey))
                errors.Add($"{mod.Manifest.Id} 与 {id} 冲突");
        }
        if (errors.Count > 0) return new([], errors);
        var states = new Dictionary<string, int>(StringComparer.Ordinal);
        var ordered = new List<DiscoveredMod>();
        var stack = new List<string>();
        foreach (var id in enabled.Keys.Order(StringComparer.Ordinal)) Visit(id);
        return errors.Count > 0 ? new([], errors.Distinct().ToArray()) : new(ordered, errors);

        void Visit(string id)
        {
            if (states.TryGetValue(id, out var state))
            {
                if (state == 1) errors.Add($"循环依赖：{string.Join(" -> ", stack.Append(id))}");
                return;
            }
            states[id] = 1;
            stack.Add(id);
            foreach (var dependency in enabled[id].Manifest.Dependencies.Keys.Order(StringComparer.Ordinal)) Visit(dependency);
            stack.RemoveAt(stack.Count - 1);
            states[id] = 2;
            ordered.Add(enabled[id]);
        }
    }
}
