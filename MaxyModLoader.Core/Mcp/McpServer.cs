using System.Text.Json;
using System.Text;
using System.Text.RegularExpressions;
using MaxyModLoader.Windowing;

namespace MaxyModLoader.Mcp;

/// <summary>
/// 描述公开工具与游戏白名单命令的映射
/// </summary>
internal sealed record GameTool(string Name, string Command, string Description, string? Parameter = null,
    string ParameterType = "string", bool ReadOnly = true);

/// <summary>
/// 描述由已加载模组显式注册的MCP工具
/// </summary>
internal sealed record ModGameTool(string Name, string ActionId, string Description, JsonElement InputSchema,
    bool ReadOnly, bool Destructive);

/// <summary>
/// 提供标准初始化握手的本地标准输入输出MCP工具服务
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
        new("inventory_item", "inventory_item", "读取已注册物品的真实全局库存用于检查物品是否实际获取", "name"),
        new("debug_give_item", "give_item", "向庇护所测试存档注入物资并读取库存前后值格式为物品名|1到20数量|0到15角色序号，会改变存档物资，不代表自然获取或制作验证", "item", ReadOnly: false),
        new("mods_list", "mods_list", "列出MaxyModLoader发现的模组及详细介绍和真实加载状态"),
        new("mod_manager", "mod_manager", "读取独立模组管理面板的可见状态当前选择滚动窗口偏移和鼠标滚轮记录"),
        new("mod_scroll", "mod_scroll", "将模组管理滚动窗口移到指定列表条目或介绍行格式为list|序号或detail|行号", "target", ReadOnly: false),
        new("quit_game", "quit", "请求游戏正常退出以进行可恢复部署", ReadOnly: false),
        new("rule_list", "rules_list", "读取运行中的模组规则及其当前值、默认值、范围和来源模组"),
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
                serverInfo = new { name = "MaxyModLoader", version = "0.4.2" }, instructions = "工具操作限于本机游戏，超时代表结果未确认，状态变更命令不要自动重试" });
        }
        if (name == "ping") return Result(id, new { });
        if (!initialized) return Error(id, -32002, "请先初始化MCP服务");
        if (name == "tools/list")
        {
            //将固定工具与已加载模组提供的结构化工具合并
            IReadOnlyList<ModGameTool> modTools;
            try { modTools = await ReadModToolsAsync(); }
            catch (Exception exception) when (exception is InvalidDataException or JsonException or InvalidOperationException or KeyNotFoundException)
            {
                //桥接契约损坏必须显式报告不能静默退回固定工具列表
                return Error(id, -32001, "游戏模组工具契约无效：" + exception.Message);
            }
            var tools = Tools.Select(tool => (object)new { name = tool.Name, description = tool.Description,
                inputSchema = new { type = "object", properties = tool.Parameter is null ? new Dictionary<string, object>() :
                    new Dictionary<string, object> { [tool.Parameter] = new { type = tool.ParameterType } },
                    required = tool.Parameter is null ? Array.Empty<string>() : new[] { tool.Parameter }, additionalProperties = false },
                annotations = new { readOnlyHint = tool.ReadOnly, destructiveHint = false, idempotentHint = tool.ReadOnly || tool.Name == "set_pause", openWorldHint = false } })
                .Concat(modTools.Select(tool => (object)new { name = tool.Name, description = tool.Description,
                    inputSchema = tool.InputSchema,
                    annotations = new { readOnlyHint = tool.ReadOnly, destructiveHint = tool.Destructive,
                        idempotentHint = tool.ReadOnly, openWorldHint = false } })).ToArray();
            return Result(id, new { tools });
        }
        if (name != "tools/call") return Error(id, -32601, "不支持的方法");
        if (!request.TryGetProperty("params", out var parameters) || !parameters.TryGetProperty("name", out var toolName))
            return Error(id, -32602, "工具调用缺少名称");
        var selected = Tools.FirstOrDefault(tool => tool.Name == toolName.GetString());
        try
        {
            //模组工具发现失败属于当前调用错误不接受旧桥接协议
            var modTool = selected is null ? (await ReadModToolsAsync()).FirstOrDefault(tool => tool.Name == toolName.GetString()) : null;
            if (selected is null && modTool is null) return Error(id, -32602, "未知工具");
            //再次验证参数而不是仅依赖客户端遵守输入模式
            var argument = "";
            var arguments = parameters.TryGetProperty("arguments", out var suppliedArguments) ? suppliedArguments : default;
            if (modTool is not null)
            {
                //模组工具参数按其运行时JSON模式再次校验后编码
                argument = EncodeModActionArguments(modTool, arguments);
            }
            else if (arguments.ValueKind != JsonValueKind.Undefined)
            {
                if (arguments.ValueKind != JsonValueKind.Object || arguments.EnumerateObject().Any(property => property.Name != selected!.Parameter))
                    throw new ArgumentException("工具参数包含未知字段");
            }
            if (modTool is null && selected!.Parameter is { } parameter)
            {
                if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty(parameter, out var value))
                    throw new ArgumentException("工具调用缺少必填参数");
                argument = selected.ParameterType == "boolean" ? value.GetBoolean().ToString().ToLowerInvariant() : value.GetString()
                    ?? throw new ArgumentException("参数不能为空");
            }
            if (selected is { Command: "screenshot" })
            {
                //截图由受限辅助进程读取画面不在游戏主线程等待窗口消息
                var capture = await bridge.CaptureAsync();
                return Result(id, new { content = new object[] { new { type = "image", data = capture.Data, mimeType = "image/png" },
                    new { type = "text", text = capture.Path } }, isError = false });
            }
            if (selected is { Command: "display_mode" })
            {
                //窗口模式在游戏外按已核验的游戏进程和Windows公开窗口接口调整
                var state = await DisplayHost.RequestAsync(bridge.GameDirectory, argument);
                return Result(id, new { content = new[] { new { type = "text", text = JsonSerializer.Serialize(state) } }, isError = false });
            }
            var reply = await bridge.CallAsync(modTool is null ? selected!.Command : "mod_action_call", argument);
            var success = reply.GetProperty("ok").GetBoolean();
            return Result(id, new { content = new[] { new { type = "text", text = reply.GetRawText() } }, isError = !success });
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or TimeoutException or ArgumentException or InvalidOperationException or JsonException or KeyNotFoundException)
        {
            //游戏失败属于工具结果使模型能读取错误并修正操作
            return Result(id, new { content = new[] { new { type = "text", text = exception.Message } }, isError = true });
        }
    }

    /// <summary>
    /// 从游戏主线程读取已加载模组发布的动作及参数模式
    /// </summary>
    private async Task<IReadOnlyList<ModGameTool>> ReadModToolsAsync()
    {
        try
        {
            //仅传输不可用时保留离线固定工具服务有效响应必须遵守当前契约
            var reply = await bridge.CallAsync("mod_actions_list", timeout: TimeSpan.FromSeconds(2));
            if (!reply.GetProperty("ok").GetBoolean()) throw new InvalidDataException("游戏拒绝当前模组动作发现命令");
            using var document = JsonDocument.Parse(reply.GetProperty("result").GetRawText());
            if (!document.RootElement.TryGetProperty("actions", out var actions) || actions.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("游戏响应缺少当前actions列表");
            var result = new List<ModGameTool>();
            var names = Tools.Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal);
            foreach (var action in actions.EnumerateArray())
            {
                //验证游戏返回的标识和输入模式防止损坏清单污染MCP工具发现
                var actionId = action.GetProperty("id").GetString();
                var owner = action.GetProperty("owner").GetString();
                var actionName = action.GetProperty("name").GetString();
                if (actionId is null || owner is null || actionName is null || actionId.Length > 192 ||
                    !Regex.IsMatch(actionId, "^[a-z][a-z0-9._-]*:[a-z][a-z0-9_]*$") ||
                    !Regex.IsMatch(owner, "^[a-z][a-z0-9._-]*$") || !Regex.IsMatch(actionName, "^[a-z][a-z0-9_]*$"))
                    throw new InvalidDataException("游戏模组动作标识格式无效");
                var schema = action.GetProperty("inputSchema");
                if (!IsSupportedActionSchema(schema)) throw new InvalidDataException("游戏模组动作参数模式无效");
                var ownerName = owner.Replace('.', '_').Replace('-', '_');
                if (ownerName.Length > 48) ownerName = ownerName[..48];
                var suffix = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(actionId)))[..12].ToLowerInvariant();
                var toolName = $"mod_{ownerName}_{actionName}_{suffix}";
                if (!names.Add(toolName)) continue;
                result.Add(new ModGameTool(toolName, actionId,
                    $"模组动作{owner}：{action.GetProperty("description").GetString()}", schema.Clone(),
                    action.GetProperty("readOnly").GetBoolean(), action.GetProperty("destructive").GetBoolean()));
            }
            return result;
        }
        catch (Exception exception) when (exception is TimeoutException or IOException)
        {
            //游戏未响应时仍允许查询内置工具协议错误不在此处吞掉
            return Array.Empty<ModGameTool>();
        }
    }

    /// <summary>
    /// 检查模组动作只暴露扁平标量参数且没有开放属性
    /// </summary>
    private static bool IsSupportedActionSchema(JsonElement schema)
    {
        //限制为对象、简单类型及有界属性数便于跨进程稳定编码
        if (schema.ValueKind != JsonValueKind.Object || !schema.TryGetProperty("type", out var type) ||
            type.GetString() != "object" || !schema.TryGetProperty("properties", out var properties) ||
            properties.ValueKind != JsonValueKind.Object || properties.EnumerateObject().Count() > 32 ||
            !schema.TryGetProperty("required", out var required) || required.ValueKind != JsonValueKind.Array ||
            !schema.TryGetProperty("additionalProperties", out var additional) || additional.ValueKind != JsonValueKind.False)
            return false;
        foreach (var property in properties.EnumerateObject())
        {
            //所有动作参数均使用字符串、数值、整数或布尔标量
            if (!Regex.IsMatch(property.Name, "^[A-Za-z][A-Za-z0-9_]*$") || property.Value.ValueKind != JsonValueKind.Object ||
                !property.Value.TryGetProperty("type", out var propertyType) || propertyType.ValueKind != JsonValueKind.String ||
                propertyType.GetString() is not ("string" or "number" or "integer" or "boolean")) return false;
        }
        foreach (var item in required.EnumerateArray())
            if (item.ValueKind != JsonValueKind.String || !properties.TryGetProperty(item.GetString()!, out _)) return false;
        return true;
    }

    /// <summary>
    /// 按动作声明校验参数并编码为无歧义的逐字段传输格式
    /// </summary>
    private static string EncodeModActionArguments(ModGameTool tool, JsonElement arguments)
    {
        //逐字段十六进制编码避免中文、分隔符和换行破坏游戏桥协议
        if (arguments.ValueKind != JsonValueKind.Object) throw new ArgumentException("模组动作参数必须是JSON对象");
        var schema = tool.InputSchema;
        var properties = schema.GetProperty("properties");
        var values = arguments.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal).ToArray();
        foreach (var property in values)
        {
            //拒绝未声明字段和与模式不一致的JSON类型
            if (!properties.TryGetProperty(property.Name, out var definition)) throw new ArgumentException($"未知模组动作参数：{property.Name}");
            var expected = definition.GetProperty("type").GetString();
            var valid = expected switch
            {
                "string" => property.Value.ValueKind == JsonValueKind.String,
                "boolean" => property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False,
                "integer" => property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt64(out var integer) && Math.Abs((double)integer) <= 9007199254740991d,
                "number" => property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetDouble(out var number) && double.IsFinite(number),
                _ => false
            };
            if (!valid) throw new ArgumentException($"模组动作参数类型错误：{property.Name}");
        }
        foreach (var required in schema.GetProperty("required").EnumerateArray())
            if (!arguments.TryGetProperty(required.GetString()!, out _)) throw new ArgumentException($"缺少模组动作参数：{required.GetString()}");

        var encoded = new StringBuilder(tool.ActionId).Append('\n');
        foreach (var property in values)
        {
            var definition = properties.GetProperty(property.Name);
            var expected = definition.GetProperty("type").GetString();
            var kind = expected == "string" ? 's' : expected == "boolean" ? 'b' : 'n';
            var value = expected switch
            {
                "string" => property.Value.GetString()!,
                "boolean" => property.Value.GetBoolean() ? "true" : "false",
                _ => property.Value.GetRawText()
            };
            encoded.Append(Convert.ToHexString(Encoding.UTF8.GetBytes(property.Name)).ToLowerInvariant()).Append('|')
                .Append(kind).Append('|').Append(Convert.ToHexString(Encoding.UTF8.GetBytes(value)).ToLowerInvariant()).Append('\n');
        }
        return encoded.ToString();
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
