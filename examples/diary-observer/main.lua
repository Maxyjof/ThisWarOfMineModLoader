return {
    --<summary>
    --包装游戏日记函数并广播加载器事件
    --</summary>
    on_load = function(context)
        --其他游戏版本或独立测试环境可能没有此函数先检查能力
        if type(_G.logEvent) ~= "function" then
            context.log("当前环境没有logEvent函数已跳过游戏事件接入")
            return
        end

        --先保留原版日记记录行为再通知订阅者只覆盖经此函数记录的事件
        context.wrap(_G, "logEvent", function(previous, event_name)
            --<summary>
            --收集原函数返回值并保留其中的空值
            --</summary>
            --记录原函数的全部返回值并保留尾部nil
            local function pack(...)
                return {n = select("#", ...), ...}
            end
            local result = pack(previous(event_name))

            --原函数正常完成后才广播本模组观察到的事件
            MaxyModLoader.emit("game.diary", event_name)

            --返回原函数的全部结果不改变已有调用者行为
            return unpack(result, 1, result.n)
        end)

        --记录订阅收到的事件供开发者观察实际游戏回调
        context.events.on("game.diary", function(event_name)
            context.log("游戏日记事件：" .. tostring(event_name))
        end)
        context.log("已接入游戏logEvent函数")
    end
}
