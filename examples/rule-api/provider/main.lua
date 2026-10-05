return {
    --<summary>
    --注册可由其他声明依赖的模组读取的生存规则
    --</summary>
    on_load = function(context)
        --定义模块化配置值供玩法适配器在已验证回调中读取
        context.rules.define("daily_fatigue_rate", {
            type = "number",
            default = 1.0,
            minimum = 0.0,
            maximum = 2.0,
            description = "每日疲劳增长倍率"
        })
        context.rules.define("scavenge_risk", {
            type = "string",
            default = "normal",
            values = {"low", "normal", "high"},
            description = "搜刮风险档位"
        })
        context.log("已注册每日疲劳倍率与搜刮风险规则")
    end
}
