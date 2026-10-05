return {
    --<summary>
    --读取并递增当前模组自己的持久启动计数
    --</summary>
    on_load = function(context)
        --没有保存记录时从零开始后续启动读取上次写入值
        local count = context.storage.get("launch_count", 0)

        --存储接口只接收有界标量并在返回前写入模组专属数据文件
        context.storage.set("launch_count", count + 1)

        --通过加载器日志显示当前记录用于验证跨启动读取
        context.log("示例启动次数：" .. tostring(count + 1))
    end
}
