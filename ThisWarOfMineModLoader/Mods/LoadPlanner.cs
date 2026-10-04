namespace ThisWarOfMineModLoader.Mods;

/// <summary>
/// 保存完整加载顺序或阻止加载的错误
/// </summary>
public sealed record LoadPlan(IReadOnlyList<DiscoveredMod> Ordered, IReadOnlyList<string> Errors)
{
    public bool IsValid => Errors.Count == 0;
}

/// <summary>
/// 生成模组依赖拓扑顺序并检查冲突
/// </summary>
public static class LoadPlanner
{
    /// <summary>
    /// 校验完整模组集合并生成稳定的加载计划
    /// </summary>
    public static LoadPlan Create(IEnumerable<DiscoveredMod> discovered)
    {
        //先拒绝重复身份防止字典覆盖清单
        var errors = new List<string>();
        var all = discovered.ToArray();
        foreach (var group in all.GroupBy(m => m.Manifest.Id).Where(g => g.Count() > 1))
            errors.Add($"重复的模组 ID：{group.Key}");
        if (errors.Count > 0) return new([], errors);
        //只允许启用模组满足依赖并逐项检查最低版本和冲突
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
        //使用入度计数避免长依赖链触发递归栈溢出
        var indegrees = enabled.ToDictionary(pair => pair.Key, pair => pair.Value.Manifest.Dependencies.Count, StringComparer.Ordinal);
        var dependents = enabled.Keys.ToDictionary(id => id, _ => new List<string>(), StringComparer.Ordinal);
        foreach (var mod in enabled.Values)
            foreach (var dependency in mod.Manifest.Dependencies.Keys)
                dependents[dependency].Add(mod.Manifest.Id);
        var ready = new PriorityQueue<string, string>(StringComparer.Ordinal);
        foreach (var id in indegrees.Where(pair => pair.Value == 0).Select(pair => pair.Key)) ready.Enqueue(id, id);
        var ordered = new List<DiscoveredMod>();

        //每次取字典序最小的可加载模组依赖完成后降低其依赖方的入度
        while (ready.TryDequeue(out var id, out _))
        {
            ordered.Add(enabled[id]);
            foreach (var dependent in dependents[id])
                if (--indegrees[dependent] == 0) ready.Enqueue(dependent, dependent);
        }

        //仍有正入度节点说明存在循环拒绝输出可能误用的部分计划
        if (ordered.Count != enabled.Count)
            errors.Add($"循环依赖或被循环阻塞：{string.Join(", ", indegrees.Where(pair => pair.Value > 0).Select(pair => pair.Key).Order(StringComparer.Ordinal))}");
        return errors.Count > 0 ? new([], errors) : new(ordered, errors);
    }
}
