local context = ...
local module = {}

--<summary>
--降低游戏参数控制器每日饥饿增长并记录原生计时结果
--</summary>
function module.install(telemetry)
    --直接修改已确认被角色参数控制器读取的增长表
    for level, values in pairs(igKosovoParamDefinition.Hungry.Tick) do
        local before = values.Hungry
        values.Hungry = before * context.config.hunger_factor
        telemetry.record("nutrition.config", {level = level, before = before, after = values.Hungry})
    end
    context.wrap(KosovoParamComponent, "TickParameters", function(previous, component, after_scavenge, locked)
        --原生实体读值与控制器实际推进结果分别记录避免只验证配置表
        local host = component:GetMyHost()
        local before = host:GetParameterValue("Hungry")
        previous(component, after_scavenge, locked)
        telemetry.record("nutrition.tick", {before = before, after = host:GetParameterValue("Hungry")})
    end)
end
return module
