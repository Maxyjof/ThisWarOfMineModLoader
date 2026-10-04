local context = ...
local module = {}
local supplied = false

--<summary>
--首次实际场景可用时提供开局食品并读取游戏全局物资计数
--</summary>
function module.install(telemetry)
    --每次进程最多发放一次重进场景不会重复领取
    context.events.on("game.day.begin", function(scene)
        if supplied or scene:GetDwellerCount() == 0 then return end
        local dweller = scene:GetDweller(0)
        local before = gKosovoGlobalState:GetGlobalItemCount("CannedFood")
        dweller:AddItems("CannedFood", context.config.starter_food)
        supplied = true
        telemetry.record("supplies.applied", {before = before, after = gKosovoGlobalState:GetGlobalItemCount("CannedFood"),
            requested = context.config.starter_food})
    end)
end
return module
