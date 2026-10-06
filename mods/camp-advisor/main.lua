return {
    --<summary>
    --注册供本机AI客户端使用的有限营地管理工具
    --</summary>
    on_load = function(context)
        --仅在庇护所阶段读取幸存者数据避免访问未初始化场景
        local function require_shelter()
            if context.game.phase.current() ~= "shelter" then
                error("该动作只能在庇护所阶段执行")
            end
            return context.game.scene.state()
        end

        --暴露可审计的只读报告不返回原生对象或任意Lua值
        context.actions.register("shelter_report", {
            description = "读取当前游戏日和全体幸存者的饥饿、疲劳、疾病及伤势",
            properties = {},
            required = {},
            read_only = true
        }, function()
            local state = require_shelter()
            local survivors = {}
            for index = 0, state.character_count - 1 do
                local character = context.game.characters.get(index)
                survivors[#survivors + 1] = {
                    index = index,
                    name = character.name,
                    hungry = character.get_parameter("Hungry"),
                    tired = character.get_parameter("Tired"),
                    sick = character.get_parameter("Sick"),
                    wounded = character.get_parameter("Wounded")
                }
            end
            return {day = state.day, survivors = survivors}
        end)

        --恢复动作将索引和幅度限制在当前营地角色与安全范围内
        context.actions.register("rest_survivor", {
            description = "降低指定幸存者的疲劳值，恢复幅度限制为5到20点",
            properties = {
                index = {type = "integer", description = "幸存者序号，从0开始"},
                recovery = {type = "integer", description = "降低疲劳点数，范围5到20"}
            },
            required = {"index", "recovery"},
            destructive = true
        }, function(arguments)
            local state = require_shelter()
            if arguments.index < 0 or arguments.index >= state.character_count then error("幸存者序号超出当前队伍") end
            if arguments.recovery < 5 or arguments.recovery > 20 then error("恢复幅度必须介于5到20点") end

            --只写入疲劳这一项并返回修改前后的数值
            local character = context.game.characters.get(arguments.index)
            local previous = character.get_parameter("Tired")
            local current = math.max(0, previous - arguments.recovery)
            character.set_parameter("Tired", current)
            return {name = character.name, previous = previous, current = current}
        end)

        --切换依赖模组的共享规则并让下一次搜刮立即采用新策略
        context.actions.register("set_scavenge_mode", {
            description = "切换搜刮耗时策略为careful、balanced或urgent",
            properties = {
                mode = {type = "string", description = "careful、balanced或urgent"}
            },
            required = {"mode"},
            destructive = true
        }, function(arguments)
            require_shelter()
            if arguments.mode ~= "careful" and arguments.mode ~= "balanced" and arguments.mode ~= "urgent" then
                error("未知搜刮策略")
            end
            context.rules.set("twom.play.scavenger-tactics", "fatigue_mode", arguments.mode)
            return {mode = arguments.mode, applies = "next_scavenge_action"}
        end)
    end
}
