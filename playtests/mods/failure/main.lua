return {
    --<summary>
    --注册故意破坏时长的包装后失败以验证加载器撤销注册
    --</summary>
    on_load = function(context)
        --若撤销失效真实搜刮动作会变为777秒现场记录应捕获该错误
        context.wrap(ScavengeAction, "OnBegin", function(previous, action, user)
            local result = previous(action, user)
            action.Duration = 777
            return result
        end)
        context.services.provide("invalid", {must_not_leak = true})
        error("预期的故障探针错误用于验证失败隔离")
    end
}
