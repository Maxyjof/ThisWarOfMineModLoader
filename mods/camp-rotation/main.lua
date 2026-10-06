return {
    --<summary>
    --按游戏日为最疲惫的营地成员安排轮休
    --</summary>
    on_load = function(context)
        --等待已核验的昼夜桥接事件避免在主菜单访问角色
        context.events.on("game.day.begin", function(scene, was_scavenging)
            if was_scavenging == true or context.game.phase.current() ~= "shelter" then return end

            --读取当前游戏日并跳过非轮休日和已处理日期
            local state = context.game.scene.state()
            if state.day % 3 ~= 0 or context.storage.get("last_rotation_day", 0) == state.day then return end
            if state.character_count == 0 then return end

            --选择疲劳最高的成员且只在疲劳至少25时执行
            local selected = nil
            for index = 0, state.character_count - 1 do
                local survivor = context.game.characters.get(index)
                local tired = survivor.get_parameter("Tired")
                if not selected or tired > selected.tired then
                    selected = {character = survivor, tired = tired}
                end
            end
            if not selected or selected.tired < 25 then
                context.storage.set("last_rotation_day", state.day)
                context.log("第" .. tostring(state.day) .. "天轮休检查完成，没有需要恢复的成员")
                return
            end

            --先完成状态修改再持久化日期以避免重复回调重复恢复
            local recovery = math.min(12, selected.tired)
            selected.character.set_parameter("Tired", selected.tired - recovery)
            context.storage.set("last_rotation_day", state.day)
            context.log("第" .. tostring(state.day) .. "天轮休：" .. selected.character.name .. "恢复疲劳" .. tostring(recovery) .. "点")
        end)
    end
}
