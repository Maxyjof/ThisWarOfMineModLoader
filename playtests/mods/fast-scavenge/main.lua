return {
    --<summary>
    --按配置缩短所有已有搜刮类型的时长
    --</summary>
    on_load = function(context)
        --限制倍率避免无效配置导致动作不能完成
        local factor = context.config.duration_factor
        if type(factor) ~= "number" or factor <= 0 or factor > 1 then error("无效的搜刮时长倍率") end
        local telemetry = context.services.get("twom.play.bridge", "telemetry")
        for name, params in pairs(igParams.ScavengeConfig) do
            local before = params.Duration
            params.Duration = before * factor
            telemetry.record("scavenge.config", {name = name, before = before, after = params.Duration})
        end
    end
}
