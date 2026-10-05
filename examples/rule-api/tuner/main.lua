return {
    --<summary>
    --演示已声明依赖的模组如何调整并监听共享规则
    --</summary>
    on_load = function(context)
        --规则回调能够区分调整前后值供游戏适配器重新计算状态
        context.rules.on_change("twom.rules.provider", "daily_fatigue_rate", function(current, previous)
            context.log("疲劳倍率从" .. tostring(previous) .. "调整为" .. tostring(current))
        end)

        --有界数值会由规则提供方的声明校验
        context.rules.set("twom.rules.provider", "daily_fatigue_rate", 0.8)
        context.log("当前搜刮风险为" .. context.rules.get("twom.rules.provider", "scavenge_risk"))
    end
}
