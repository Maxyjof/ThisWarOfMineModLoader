return {
    on_load = function(context)
        context.log("你好，《这是我的战争》！模组入口已经执行。")
        context.events.on("loader.ready", function()
            context.log("所有可用模组加载完成。")
        end)
    end
}
