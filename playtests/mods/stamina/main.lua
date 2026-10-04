return {
    --<summary>
    --降低后续角色初始化时传递给原生引擎的体力消耗
    --</summary>
    on_load = function(context)
        --复用游戏既有SetStaminaParams初始化流程不替换角色模型
        local factor = context.config.consumption_factor
        if type(factor) ~= "number" or factor <= 0 or factor > 1 then error("无效的体力消耗倍率") end
        local telemetry = context.services.get("twom.play.bridge", "telemetry")
        local run_before, walk_before = igStaminaRunConsumption, igStaminaWalkConsumption
        igStaminaRunConsumption = run_before * factor
        igStaminaWalkConsumption = walk_before * factor
        telemetry.record("stamina.config", {run_before = run_before, run_after = igStaminaRunConsumption,
            walk_before = walk_before, walk_after = igStaminaWalkConsumption})
    end
}
