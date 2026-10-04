local context = ...
local module = {}

--<summary>
--在一天结束时从缓存记录模块生成活动摘要
--</summary>
function module.install(journal)
    --再次加载应返回同一个模块对象避免重复创建记录器
    if context.require("journal") ~= journal then error("内部模块缓存失效") end
    context.events.on("game.day.end", function()
        local telemetry = context.services.get("twom.play.bridge", "telemetry")
        telemetry.record("chronicle.summary", {entries = #journal.entries,
            scavenges = journal.counts["game.scavenge.complete"] or 0, crafts = journal.counts["game.craft.complete"] or 0})
    end)
end
return module
