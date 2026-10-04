return {
    --<summary>
    --输出欢迎消息并订阅加载器就绪事件
    --</summary>
    on_load = function(context)
        --先记录入口执行再订阅所有模组加载完成的通知
        context.log("你好，《这是我的战争》！模组入口已经执行。")
        context.events.on("loader.ready", function()
            context.log("所有可用模组加载完成。")
        end)
    end
}
