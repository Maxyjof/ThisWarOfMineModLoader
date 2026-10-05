return {
    --<summary>
    --记录场景、昼夜、搜刮和制作事件并提供活动汇总
    --</summary>
    on_load = function(context)
        --两个内部模块共享缓存服务不直接读取另一个模组的文件
        local journal = context.require("journal")
        context.require("summary").install(journal)
        context.services.provide("journal", journal)
        local names = {"game.scene.before_init", "game.scene.ready", "game.scene.before_switch", "game.day.before_begin", "game.day.begin", "game.day.end", "game.scavenge.entering", "game.scavenge.entered", "game.scavenge.saving", "game.scavenge.saved", "game.radio.broadcast", "game.shelter.item.built", "game.scavenge.begin", "game.scavenge.complete", "game.craft.begin", "game.craft.complete"}
        for _, name in ipairs(names) do
            local event_name = name
            context.events.on(event_name, function() journal.append(event_name) end)
        end
    end
}
