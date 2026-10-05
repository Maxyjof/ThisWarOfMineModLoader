using System.Text.Json;
using MaxyModLoader.Windowing;

namespace MaxyModLoader.Mcp;

/// <summary>
/// 描述公开工具与游戏白名单命令的映射
/// </summary>
internal sealed record GameTool(string Name, string Command, string Description, string? Parameter = null,
    string ParameterType = "string", bool ReadOnly = true);

/// <summary>
/// 提供兼容初始化握手的本地标准输入输出MCP工具服务
/// </summary>
public sealed class McpServer(GameBridgeClient bridge)
{
    private bool initialized;
    private static readonly GameTool[] Tools =
    [
        new("end_day", "end_day", "通过游戏内部接口结束当前庇护所白天并进入夜间安排", ReadOnly: false),
        new("display_mode", "display_mode", "切换游戏窗口显示模式支持无边框全屏窗口、普通窗口和游戏全屏", "mode", ReadOnly: false),
        new("game_state", "game_state", "读取真实场景天数、暂停状态和角色饥饿疲劳疾病受伤参数"),
        new("game_screenshot", "screenshot", "只读捕获目标游戏窗口返回PNG图像不激活窗口或发送键鼠输入"),
        new("inspect_type", "inspect_type", "列出公开Lua类型的绑定方法仅用于接口诊断不执行任意源码", "name"),
        new("item_config", "item_config", "读取原生物品注册状态及已公开的价值堆叠和枪械参数不生成物资", "name"),
        new("mods_list", "mods_list", "列出MaxyModLoader发现的模组及详细介绍和真实加载状态"),
        new("mod_manager", "mod_manager", "读取独立模组管理面板的可见状态当前选择滚动窗口偏移和鼠标滚轮记录"),
        new("mod_scroll", "mod_scroll", "将模组管理滚动窗口移到指定列表条目或介绍行格式为list|序号或detail|行号", "target", ReadOnly: false),
        new("quit_game", "quit", "请求游戏正常退出以进行可恢复部署", ReadOnly: false),
        new("set_pause", "pause", "明确设置游戏用户暂停状态", "paused", "boolean", false),
        new("settings_state", "settings_state", "读取原版窗口设置行真实模式待应用选择后台服务和错误状态"),
        new("ui_click", "ui_click", "按元素名称在游戏当前原生界面触发点击不控制Windows桌面", "name", ReadOnly: false),
        new("ui_adjust", "ui_adjust", "临时调整加载器自有控件位置大小缩放或颜色格式为控件名|属性|数字列表不执行源码", "edit", ReadOnly: false),
        new("ui_catalog", "ui_catalog", "读取当前原生界面的可见与隐藏控件用于配方诊断"),
        new("ui_click_point", "ui_click_point", "在游戏内部按归一化屏幕坐标命中控件并触发点击不移动桌面鼠标", "point", ReadOnly: false),
        new("ui_hit_test", "ui_hit_test", "读取截图坐标对应的原生命中控件及父链参数格式为0到1之间的x,y", "point"),
        new("ui_tree", "ui_tree", "读取游戏内部界面树及元素名称可见性和启用状态")
    ];

    /// <summary>
    /// 逐行处理JSON消息并独立报告协议错误和游戏工具错误
    /// </summary>
    public async Task RunAsync(TextReader input, TextWriter output)
    {
        //逐个工具调用保持游戏主线程命令顺序不并行修改场景
        while (await input.ReadLineAsync() is { } line)
        {
            if (line.Length == 0) continue;
            object? response;
            try
            {
                using var document = JsonDocument.Parse(line);
                response = await HandleAsync(document.RootElement);
            }
            catch (JsonException)
            {
                response = new { jsonrpc = "2.0", id = (object?)null, error = new { code = -32700, message = "无法解析JSON消息" } };
            }
            catch (InvalidOperationException)
            {
                //畸形字段属于协议错误不得终止整个标准输入输出服务
                response = new { jsonrpc = "2.0", id = (object?)null, error = new { code = -32600, message = "无效的JSONRPC请求字段" } };
            }
            if (response is null) continue;
            await output.WriteLineAsync(JsonSerializer.Serialize(response));
            await output.FlushAsync();
        }
    }

    /// <summary>
    /// 分发初始化工具发现与调用请求
    /// </summary>
    private async Task<object?> HandleAsync(JsonElement request)
    {
        //通知没有响应未知请求返回标准JSONRPC错误
        if (!request.TryGetProperty("id", out var id)) return null;
        if (!request.TryGetProperty("method", out var method) || method.ValueKind != JsonValueKind.String)
            return Error(id, -32600, "请求缺少方法名称");
        var name = method.GetString();
        if (name == "initialize")
        {
            initialized = true;
            return Result(id, new { protocolVersion = "2025-06-18", capabilities = new { tools = new { listChanged = false } },
                serverInfo = new { name = "MaxyModLoader", version = "0.3.0" }, instructions = "工具操作限于本机游戏，超时代表结果未确认，状态变更命令不要自动重试" });
        }
        if (name == "ping") return Result(id, new { });
        if (!initialized) return Error(id, -32002, "请先初始化MCP服务");
        if (name == "tools/list")
        {
            //确定性排序和明确参数模式便于客户端稳定发现工具
            var tools = Tools.Select(tool => new { name = tool.Name, description = tool.Description,
                inputSchema = new { type = "object", properties = tool.Parameter is null ? new Dictionary<string, object>() :
                    new Dictionary<string, object> { [tool.Parameter] = new { type = tool.ParameterType } },
                    required = tool.Parameter is null ? Array.Empty<string>() : new[] { tool.Parameter }, additionalProperties = false },
                annotations = new { readOnlyHint = tool.ReadOnly, destructiveHint = false, idempotentHint = tool.ReadOnly || tool.Name == "set_pause", openWorldHint = false } });
            return Result(id, new { tools });
        }
        if (name != "tools/call") return Error(id, -32601, "不支持的方法");
        if (!request.TryGetProperty("params", out var parameters) || !parameters.TryGetProperty("name", out var toolName))
            return Error(id, -32602, "工具调用缺少名称");
        var selected = Tools.FirstOrDefault(tool => tool.Name == toolName.GetString());
        if (selected is null) return Error(id, -32602, "未知工具");
        try
        {
            //再次验证参数而不是仅依赖客户端遵守输入模式
            var argument = "";
            if (parameters.TryGetProperty("arguments", out var arguments))
            {
                if (arguments.ValueKind != JsonValueKind.Object || arguments.EnumerateObject().Any(property => property.Name != selected.Parameter))
                    throw new ArgumentException("工具参数包含未知字段");
            }
            if (selected.Parameter is { } parameter)
            {
                if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty(parameter, out var value))
                    throw new ArgumentException("工具调用缺少必填参数");
                argument = selected.ParameterType == "boolean" ? value.GetBoolean().ToString().ToLowerInvariant() : value.GetString()
                    ?? throw new ArgumentException("参数不能为空");
            }
            if (selected.Command == "screenshot")
            {
                //截图由受限辅助进程读取画面不在游戏主线程等待窗口消息
                var capture = await bridge.CaptureAsync();
                return Result(id, new { content = new object[] { new { type = "image", data = capture.Data, mimeType = "image/png" },
                    new { type = "text", text = capture.Path } }, isError = false });
            }
            if (selected.Command == "display_mode")
            {
                //窗口模式在游戏外按已核验的游戏进程和Windows公开窗口接口调整
                var state = await DisplayHost.RequestAsync(bridge.GameDirectory, argument);
                return Result(id, new { content = new[] { new { type = "text", text = JsonSerializer.Serialize(state) } }, isError = false });
            }
            var reply = await bridge.CallAsync(selected.Command, argument);
            var success = reply.GetProperty("ok").GetBoolean();
            return Result(id, new { content = new[] { new { type = "text", text = reply.GetRawText() } }, isError = !success });
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or ArgumentException or InvalidOperationException)
        {
            //游戏失败属于工具结果使模型能读取错误并修正操作
            return Result(id, new { content = new[] { new { type = "text", text = exception.Message } }, isError = true });
        }
    }

    /// <summary>
    /// 构造保持调用ID的成功协议响应
    /// </summary>
    private static object Result(JsonElement id, object result)
    {
        //复制ID使响应不依赖请求文档的生命周期
        return new { jsonrpc = "2.0", id = id.Clone(), result };
    }

    /// <summary>
    /// 构造保持调用ID的协议错误响应
    /// </summary>
    private static object Error(JsonElement id, int code, string message)
    {
        //协议错误与游戏命令失败使用不同通道
        return new { jsonrpc = "2.0", id = id.Clone(), error = new { code, message } };
    }
}
