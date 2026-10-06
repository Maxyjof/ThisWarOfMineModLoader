return {
    --<summary>
    --订阅游戏日事件并记录可用的游戏对象信息
    --</summary>
    on_load = function(context)
        --等待事件桥确认新一天开始后读取活动场景
        context.events.on("game.day.begin", function(scene, was_scavenging)
            --通过领域API读取经过校验的当前场景状态
            local phase = context.game.phase.current()
            local state = context.game.scene.state()
            context.log("第" .. tostring(state.day) .. "天，阶段=" .. phase .. "，幸存者=" .. tostring(state.character_count))

            --只在场景中存在幸存者时查询角色参数
            if state.character_count > 0 then
                local survivor = context.game.characters.get(0)
                context.log(survivor.name .. "的疲劳=" .. tostring(survivor.get_parameter("Tired")))
            end

            --记录桥接事件提供的上一夜搜刮状态
            context.log("上一夜是否外出搜刮=" .. tostring(was_scavenging == true))
        end)
    end
}
