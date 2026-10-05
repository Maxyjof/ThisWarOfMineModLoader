local context = ...
local module = {}

--<summary>
--广播真实场景和昼夜回调并保存场景状态
--</summary>
function module.install(telemetry)
    --包装的是本机已经确认存在的Lua类函数保留原版行为
    context.wrap(KosovoScene, "OnAfterInit", function(previous, scene, first_time)
        previous(scene, first_time)
        telemetry.record("scene.ready", {first_time = first_time})
        MaxyModLoader.emit("game.scene.ready", scene, first_time)
    end)
    context.wrap(KosovoScene, "OnDayBegin", function(previous, scene, was_scavenging)
        previous(scene, was_scavenging)
        telemetry.record("day.begin", {day = scene:GetCurrentDay(), was_scavenging = was_scavenging})
        MaxyModLoader.emit("game.day.begin", scene)
        telemetry.snapshot(scene, "day.begin")
    end)
    context.wrap(KosovoScene, "OnEndDay", function(previous, scene)
        previous(scene)
        telemetry.record("day.end", {day = scene:GetCurrentDay()})
        MaxyModLoader.emit("game.day.end", scene)
    end)

    --记录角色原生初始化回调供状态辅助模组执行并复读参数
    context.wrap(KosovoDwellerControllerComponent, "OnAfterInit", function(previous, component)
        previous(component)
        local host = component:GetMyHost()
        telemetry.record("character.ready", {name = host:GetDwellerName(), hungry = host:GetParameterValue("Hungry"),
            run_consumption = igStaminaRunConsumption, walk_consumption = igStaminaWalkConsumption})
        MaxyModLoader.emit("game.character.ready", host, component)
    end)
end
return module
