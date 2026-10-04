local context = ...
local telemetry = context.services.get("twom.play.bridge", "telemetry")
local module = {entries = {}, counts = {}}

--<summary>
--将实际游戏事件转为受容量限制的活动条目
--</summary>
function module.append(name)
    --计数与条目容量分离长期运行仍可获得准确事件总数
    module.counts[name] = (module.counts[name] or 0) + 1
    if #module.entries < context.config.entry_limit then table.insert(module.entries, name) end
    telemetry.record("chronicle.event", {name = name, total = module.counts[name]})
end
return module
