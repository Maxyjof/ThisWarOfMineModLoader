return {
    --<summary>
    --按外出幸存者疲劳和战术规则调整本次搜刮时间
    --</summary>
    on_load = function(context)
        --提供受限规则供MCP指挥台或其他声明依赖的模组调整
        context.rules.define("fatigue_mode", {
            type = "string",
            default = "balanced",
            values = {"careful", "balanced", "urgent"},
            description = "根据当前搜刮角色疲劳调整搜刮耗时"
        })

        --包装真实搜刮入口并先让原版计算初始时长
        context.wrap(ScavengeAction, "OnBegin", function(previous, action, user)
            local accepted = previous(action, user)
            if accepted == false or type(action.Duration) ~= "number" or action.Duration <= 0 then
                return accepted
            end

            --从实际外出角色读取疲劳而不是用静态全局配置推测
            local mode = context.rules.get("fatigue_mode")
            local tired = user and user.GetParameterValue and user:GetParameterValue("Tired") or 0
            local multiplier = 1.0
            if mode == "careful" then
                multiplier = 1.2
            elseif mode == "urgent" then
                multiplier = 0.75
            elseif tired <= 30 then
                multiplier = 0.9
            elseif tired >= 70 then
                multiplier = 1.2
            end

            --保留正时长并记录实际改动方便玩家确认玩法是否命中
            local original = action.Duration
            action.Duration = math.max(0.25, original * multiplier)
            if action.Duration ~= original then
                context.log("搜刮战术=" .. mode .. "，角色疲劳=" .. tostring(tired) .. "，耗时=" .. tostring(original) .. "->" .. tostring(action.Duration))
            end
            return accepted
        end)
    end
}
