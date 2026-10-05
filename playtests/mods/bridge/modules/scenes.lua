local context = ...
local module = {}

--<summary>
--广播真实场景和昼夜回调并保存场景状态
--</summary>
function module.install(telemetry)
    --在原版场景初始化前发布参数并保留原版返回值
    context.wrap(KosovoScene, "OnBeforeInit", function(previous, scene, is_shelter, first_time)
        MaxyModLoader.emit("game.scene.before_init", scene, is_shelter, first_time)
        return previous(scene, is_shelter, first_time)
    end)

    --包装已确认的场景初始化回调并发布完成事件
    context.wrap(KosovoScene, "OnAfterInit", function(previous, scene, first_time)
        local result = previous(scene, first_time)
        telemetry.record("scene.ready", {first_time = first_time})
        MaxyModLoader.emit("game.scene.ready", scene, first_time)
        return result
    end)

    --在原版昼夜切换逻辑前通知模组并保留原版返回值
    context.wrap(KosovoScene, "OnBeforeDayBegin", function(previous, scene, was_scavenging)
        MaxyModLoader.emit("game.day.before_begin", scene, was_scavenging)
        return previous(scene, was_scavenging)
    end)

    --在原版昼夜逻辑完成后通知模组并记录真实日期
    context.wrap(KosovoScene, "OnDayBegin", function(previous, scene, was_scavenging)
        local result = previous(scene, was_scavenging)
        telemetry.record("day.begin", {day = scene:GetCurrentDay(), was_scavenging = was_scavenging})
        MaxyModLoader.emit("game.day.begin", scene, was_scavenging)
        telemetry.snapshot(scene, "day.begin")
        return result
    end)

    --在原版结束一天逻辑后通知模组
    context.wrap(KosovoScene, "OnEndDay", function(previous, scene)
        local result = previous(scene)
        telemetry.record("day.end", {day = scene:GetCurrentDay()})
        MaxyModLoader.emit("game.day.end", scene)
        return result
    end)

    --发布进入搜刮场景与搜刮状态保存的前后事件
    context.wrap(KosovoScene, "OnEnterScavenge", function(previous, scene)
        MaxyModLoader.emit("game.scavenge.entering", scene)
        local result = previous(scene)
        MaxyModLoader.emit("game.scavenge.entered", scene)
        return result
    end)
    context.wrap(KosovoScene, "OnSaveScavengeState", function(previous, scene)
        MaxyModLoader.emit("game.scavenge.saving", scene)
        local result = previous(scene)
        MaxyModLoader.emit("game.scavenge.saved", scene)
        return result
    end)

    --在原版场景切换清理本地状态前通知模组
    context.wrap(KosovoScene, "OnBeforeSwitchScene", function(previous, scene)
        MaxyModLoader.emit("game.scene.before_switch", scene)
        return previous(scene)
    end)

    --发布广播与营地物品建造完成事件
    context.wrap(KosovoScene, "OnRadioBroadcast", function(previous, scene)
        local result = previous(scene)
        MaxyModLoader.emit("game.radio.broadcast", scene)
        return result
    end)
    context.wrap(KosovoScene, "NewShelterItemBuilt", function(previous, scene, item)
        local result = previous(scene, item)
        MaxyModLoader.emit("game.shelter.item.built", scene, item)
        return result
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
