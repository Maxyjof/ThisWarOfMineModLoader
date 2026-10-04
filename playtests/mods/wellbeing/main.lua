return {
    --<summary>
    --在真实角色初始化回调后设置状态并读取原生结果
    --</summary>
    on_load = function(context)
        local telemetry = context.services.get("twom.play.bridge", "telemetry")
        --只对实际生成的玩家角色操作不会在启动时创建测试替身
        context.events.on("game.character.ready", function(host)
            local before = host:GetParameterValue("Hungry")
            host:SetParameterValue("Hungry", context.config.hungry)
            host:SetParameterValue("Tired", context.config.tired)
            host:SolveParameterDependency()
            telemetry.record("wellbeing.applied", {name = host:GetDwellerName(), hungry_before = before,
                hungry_after = host:GetParameterValue("Hungry"), tired_after = host:GetParameterValue("Tired")})
        end)
    end
}
