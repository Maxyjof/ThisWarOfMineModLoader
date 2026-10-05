return {
    --交易变更由原生差异包装载
    on_load = function(context)
        --记录依赖与交易扩展均已进入模组生命周期
        context.log("军火交易扩展已加载")
    end
}
