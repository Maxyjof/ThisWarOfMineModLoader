return {
    --<summary>
    --注册不改变游戏状态的MCP回声工具作为结构化调用示例
    --</summary>
    on_load = function(context)
        --清单必须声明mcp.tools能力工具才会出现在MCP发现结果中
        context.actions.register("echo", {
            description = "回传文本和重复次数用于验证模组工具参数传输",
            properties = {
                text = {type = "string", description = "要回传的文本"},
                repeat_count = {type = "integer", description = "一到三之间的重复次数"}
            },
            required = {"text"},
            read_only = true
        }, function(arguments)
            --框架限制参数类型但具体数值语义仍由模组回调检查
            local count = arguments.repeat_count or 1
            if count < 1 or count > 3 then error("重复次数必须介于1和3之间") end
            local output = {}
            for index = 1, count do output[index] = arguments.text end
            return {messages = output}
        end)
    end
}
