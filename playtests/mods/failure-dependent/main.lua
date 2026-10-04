return {
    --<summary>
    --只在加载器错误执行失败依赖方时产生可检测的副作用
    --</summary>
    on_load = function(context)
        --该入口在正确实现中永远不会被调用
        TWOMPlaytestUnexpectedDependent = true
        context.log("错误：失败依赖方不应执行")
    end
}
