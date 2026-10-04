return {
    --<summary>
    --组合营养、开局补给和每日恢复三个功能模块
    --</summary>
    on_load = function(context)
        --依赖模组提供同一个记录服务所有功能共享一致配置
        local telemetry = context.services.get("twom.play.bridge", "telemetry")
        context.require("nutrition").install(telemetry)
        context.require("supplies").install(telemetry)
        context.require("recovery").install(telemetry)
        context.services.provide("camp", {version = "0.2.0", config = context.config})
    end
}
