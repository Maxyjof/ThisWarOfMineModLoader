return {
    --<summary>
    --注册基于游戏日事件的示例任务
    --</summary>
    on_load = function(context)
        --两天后记录当前游戏日与进入白天时的搜刮状态
        context.schedule.after_days(2, function(scene, was_scavenging)
            context.log("两天后任务触发，当前日=" .. tostring(scene:GetCurrentDay()) .. "，昨夜搜刮=" .. tostring(was_scavenging))
        end)

        --每隔七天重复记录一次当前游戏日
        local cancel_weekly = context.schedule.every_days(7, function(scene)
            context.log("周期任务触发，当前日=" .. tostring(scene:GetCurrentDay()))
        end)

        --示例中保留取消句柄供其他事件或条件主动停止该任务
        context.events.on("example.scheduler.stop", function()
            cancel_weekly()
            context.log("周期任务已取消")
        end)
    end
}
