local context = ...
local module = {}

--<summary>
--记录搜刮和制作的实际执行及完成回调
--</summary>
function module.install(telemetry)
    --在原搜刮入口执行后读取实际动作对象采用的时长
    context.wrap(ScavengeAction, "OnBegin", function(previous, action, user)
        local result = previous(action, user)
        telemetry.record("scavenge.begin", {duration = action.Duration, original = telemetry.originals[action.ScavengeType],
            accepted = result, actor = user and user:GetDwellerName()})
        TWOMLoader.emit("game.scavenge.begin", action, user)
        return result
    end)
    context.wrap(ScavengeAction, "OnComplete", function(previous, action, result)
        --完成入口会清零计时器因此在调用原函数前记录进度
        telemetry.record("scavenge.complete", {timer = action.Timer, duration = action.Duration, result = result})
        previous(action, result)
        TWOMLoader.emit("game.scavenge.complete", result)
    end)

    --制作行为采用原生TickCrafting这里只观察真实开始和完成事件
    context.wrap(UseCrafter, "OnBeginCrafting", function(previous, action, mode)
        previous(action, mode)
        telemetry.record("craft.begin", {shelter_mode = mode})
        TWOMLoader.emit("game.craft.begin", action)
    end)
    context.wrap(KosovoCraftingBaseComponent, "OnCraftingComplete", function(previous, component)
        previous(component)
        telemetry.record("craft.complete", {})
        TWOMLoader.emit("game.craft.complete", component)
    end)
end
return module
