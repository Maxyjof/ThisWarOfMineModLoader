return {
    --<summary>
    --注册共享记录服务并接入游戏场景和动作回调
    --</summary>
    on_load = function(context)
        --模块采用模组内部缓存同一记录器供依赖模组使用
        local telemetry = context.require("telemetry")
        context.services.provide("telemetry", telemetry)
        context.require("scenes").install(telemetry)
        context.require("actions").install(telemetry)
        telemetry.record("bridge.ready", {version = context.api_version})
    end
}
