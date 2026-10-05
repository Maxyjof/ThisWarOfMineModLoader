return {
    --由加载器注册原生内容并保留模组生命周期
    on_load = function(context)
        --记录模组状态便于检查加载结果
        context.log("更多枪械模组已加载")
    end
}
