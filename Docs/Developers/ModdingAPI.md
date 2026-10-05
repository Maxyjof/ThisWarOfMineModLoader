# MaxyModLoader运行时API参考

当前API契约版本为`1.0.0`，由游戏原生Lua5.1.1虚拟机执行。它扩展现有Lua环境，不是.NET、Unity、Mono、BepInEx或Harmony插件接口。模组与原版Lua脚本拥有相同进程权限，只安装可信来源的模组。

## API能力速览

| 接口 | 能做什么 | 主要边界 |
| --- | --- | --- |
| `context.require` | 加载清单登记的内部Lua模块，自动缓存并检测循环 | 不能从任意磁盘路径加载文件 |
| `context.events` | 订阅加载器就绪事件和模组自定义事件 | 没有自动覆盖所有游戏玩法事件 |
| `context.services` | 向直接依赖模组提供命名服务 | 只能访问自身或清单中直接声明的依赖 |
| `context.wrap` | 包装已存在的Lua表函数并保留原函数链 | 只处理实际经过该Lua函数的调用 |
| `context.rules` | 声明、校验、读取、调整和监听共享规则 | 规则值只存在当前游戏进程，不会自动改变原生玩法 |
| `context.storage` | 按模组隔离保存跨游戏启动的标量数据 | 不写游戏存档，不接受表、路径或代码 |
| `context.actions` | 注册可由内置MCP发现和调用的结构化工具 | 必须声明`mcp.tools`能力并自行检查游戏阶段 |
| `nativeContentFile` | 构建时声明物品、配方、掉落和商人货单差异 | 使用已核验的原生模板，不导入新地图或模型 |

与仅使用官方原生内容工具的模组相比，MaxyModLoader增加了Lua运行时扩展、模组间服务与规则协作、持久模组数据和AI/MCP工具入口。各能力边界与当前已核验内容见[能力总览](CapabilityOverview.md)。

## 模组入口与上下文

入口文件必须返回包含`on_load(context)`的表。加载器按依赖顺序执行启用模组的入口；`on_load`运行结束表示入口已加载，不表示某个具体游戏场景已开始。

```lua
return {
    --<summary>
    --安装本模组的运行时扩展
    --</summary>
    on_load = function(context)
        --记录加载器为当前入口提供的版本和身份
        context.log("模组已加载：" .. context.id)
    end
}
```

| 字段 | 含义 |
| --- | --- |
| `context.id` | 当前模组ID |
| `context.api_version` | 当前运行时API契约版本 |
| `context.loader_version` | 当前加载器版本 |
| `context.config` | 清单`settings`注入的默认配置表 |
| `context.log(message)` | 写入控制台和`MaxyModLoader/runtime.log`的模组日志 |

`settings`来自模组包，不是游戏内设置编辑器。运行时修改`context.config`不会写回清单或磁盘。

## 内部模块

在清单的`modules`对象中为每个内部模块指定路径，再通过`context.require(name)`加载。模块源码以当前上下文作为唯一参数，返回值会被缓存。

```json
{
  "modules": {
    "counter": "modules/counter.lua"
  }
}
```

```lua
local counter = context.require("counter")
counter.increment()
```

只允许载入清单登记的模块名。未知名称和循环载入会明确报错。模块与入口一起编入游戏脚本容器，运行时不需要开发目录。

## 事件

`context.events.on(name, callback)`订阅事件并返回取消函数；`MaxyModLoader.emit(name, ...)`广播事件。回调按订阅顺序运行，单个回调报错会记入日志且不会阻断其他订阅。广播使用订阅快照，因此回调中新建的订阅从下一次广播开始生效；尾部`nil`参数会保留。

加载器目前保证的内置事件是`loader.ready`，表示本次计划内的模组入口均已尝试加载。它不代表进入庇护所、夜间或搜刮场景。模组可以广播自定义事件；接入具体游戏行为时，应在已核验的Lua函数包装器中广播，并清楚说明实际覆盖范围。

```lua
local cancel = context.events.on("loader.ready", function()
    context.log("加载计划已完成")
end)

--需要时取消本模组的监听
cancel()
```

订阅随入口失败自动停用。普通游戏全局副作用不会自动回滚。

## 依赖服务

通过`context.services.provide(name, value)`提供服务，通过`context.services.get(providerId, name)`读取自身或清单中直接声明依赖的服务。提供方入口必须先成功加载。

```lua
--服务提供方
context.services.provide("supplies", {
    version = "1.0.0",
    get_starter_food = function()
        return 2
    end
})

--依赖方的清单需要包含twom.supplies
local supplies = context.services.get("twom.supplies", "supplies")
local amount = supplies.get_starter_food()
```

服务是Lua值，不会自动跨进程或持久化。未声明的依赖、失败的提供方和不存在的服务均会明确报错。入口失败时，它发布的服务会撤销。

## 包装已有Lua函数

`context.wrap(target, key, wrapper)`要求目标表中已经存在函数。包装器第一个参数是原函数，其余参数来自原调用；需要保持原行为时应显式转发参数和返回值。返回的取消函数会停用这一层包装。

```lua
context.wrap(_G, "logEvent", function(previous, event_name)
    local result = previous(event_name)
    MaxyModLoader.emit("example.diary", event_name)
    return result
end)
```

包装不会猜测地址或签名，也不会观察未经过该Lua函数的调用。包装器异常会沿原调用链传播，框架不会吞掉它；入口加载失败时本模组新建的包装层会退化为原函数透传。针对具体游戏函数的名称、参数与玩法效果必须分别实测。

## 共享运行时规则

规则适合把数值或选项调整点共享给其他模组。提供方使用`context.rules.define(name, definition)`注册有类型约束的规则。支持`number`、`boolean`和`string`；数值可设最小值、最大值，字符串可设选项列表和长度限制。ID由提供方ID与规则名组成。

```lua
context.rules.define("daily_fatigue_rate", {
    type = "number",
    default = 1.0,
    minimum = 0.0,
    maximum = 2.0,
    description = "每日疲劳增长倍率"
})

local rate = context.rules.get("daily_fatigue_rate")
context.rules.set("daily_fatigue_rate", 0.8)
```

规则读取、设置和监听支持当前模组简写，也支持显式提供方形式：

```lua
context.rules.get("twom.rules.provider", "daily_fatigue_rate")
context.rules.set("twom.rules.provider", "daily_fatigue_rate", 0.8)
context.rules.on_change("twom.rules.provider", "daily_fatigue_rate", function(value, previous, id)
    context.log(id .. "已从" .. tostring(previous) .. "调整为" .. tostring(value))
end)
```

只能访问自身或清单中直接声明依赖的规则。非法类型、非有限数字、超界值和未列入选项的字符串会被拒绝。规则只活在当前进程；玩法适配器必须在已验证游戏回调内读取它并应用值。`MaxyModLoader.rule_snapshot()`和内置MCP的`rule_list`提供诊断快照，不会直接改写游戏参数。

## 跨启动持久存储

`context.storage`提供每个模组独立的持久键值空间。数据位于游戏目录`MaxyModLoader/storage`下，关闭游戏或更新模组后仍保留，也不会进入游戏存档。加载器使用两份轮换快照，先写完临时文件再替换非活动槽；一份快照损坏时会记录诊断并尝试另一份，有效快照均损坏时入口读取会明确失败。

```lua
local count = context.storage.get("launch_count", 0)
context.storage.set("launch_count", count + 1)

context.storage.set("tutorial_seen", true)
context.storage.set("player_note", "第一次进入庇护所")
local values = context.storage.all()
context.storage.delete("player_note")
```

键名必须以小写字母开头，后续只允许小写字母、数字、点、下划线和连字符，最多64个ASCII字节。值仅限字符串、有限数值和布尔值；字符串最多4096字节，每个模组最多256个键，单份快照最多1MiB。`get(key, default)`的默认值也必须是相同标量类型或`nil`；`set`和`delete`返回是否发生变化。`all()`返回浅复制，不会开放内部表。

写入失败会抛出错误，内存值不会假装已经保存。数据文件只接受当前`MMLSTORE1`格式，不会自动迁移旧格式。删除模组不会删除数据；如需重置，退出游戏后只删除该模组ID对应的`.0.dat`与`.1.dat`文件。

## MCP结构化动作

模组清单需声明`"capabilities": ["mcp.tools"]`，然后调用`context.actions.register(name, definition, callback)`。内置MCP客户端会发现并调用已成功加载的动作，回调在游戏Lua主线程执行。

```lua
context.actions.register("count_items", {
    description = "读取模组诊断计数",
    properties = {
        key = {type = "string", description = "持久数据键名"}
    },
    required = {"key"},
    read_only = true
}, function(arguments)
    --动作声明仍由模组检查具体键名和可读数据
    return {value = context.storage.get(arguments.key)}
end)
```

动作最多声明32个扁平字段，参数类型为`string`、`number`、`integer`或`boolean`；字符串最多4096字节。不接受嵌套对象、Lua源码或自由路径。`required`列出的字段必须存在，未声明字段会被拒绝。`read_only`与`destructive`是客户端提示，不是权限隔离；回调仍须检查数值范围、当前场景和副作用。该能力允许可信模组代码使用其现有Lua权限，不能用来运行任意客户端代码。

动作注册随入口失败撤销。通过返回的取消函数也可停用单个动作。`MaxyModLoader.actions.list()`返回已加载模组动作，`MaxyModLoader.actions.call(id, arguments)`按已登记模式调用动作。

## 原生内容声明

清单字段`nativeContentFile`指向构建时读取的原生内容声明文件。当前声明支持原生模板物品、配方、搜刮掉落和商人出售货单，并要求原创DDS物品图标。该流程使用已核验游戏版本与官方ModTools生成差异，不等于运行时任意资源覆盖。细节见[原生内容API](NativeContent.md)。

## 生命周期、故障与安全

加载器当前只要求入口表实现`on_load(context)`，没有通用热卸载、保存档回调、后台线程、协程调度器或游戏内任意UI注册API。开发者应自行保留取消函数和状态边界；模组管理器启用或禁用改动需重启后应用。

入口失败时，加载器撤销该入口新增的订阅、包装、服务、规则和MCP动作，也回滚它对规则值的临时调整。任意游戏全局变量、原生对象变化和已写入持久存储不能通用回滚。调用点应做好校验并尽量让每次写入幂等。

当前项目只维护[模组清单](Manifest.md)中定义的当前契约，不包含旧API别名、格式回退或迁移适配。API格式发生变化时同步更新运行库、示例、测试和文档。

## 可运行示例

- [`examples/hello`](../../examples/hello)：入口、日志和`loader.ready`
- [`examples/diary-observer`](../../examples/diary-observer)：包装已有游戏Lua函数并广播自定义事件
- [`examples/rule-api`](../../examples/rule-api)：规则提供方、显式依赖和调整方
- [`examples/persistent-storage`](../../examples/persistent-storage)：跨启动持久数据
- [`examples/mcp-tools`](../../examples/mcp-tools)：MCP结构化动作
- [`playtests/mods/survival-camp`](../../playtests/mods/survival-camp)：多模块组合及经过核验的玩法适配示例

示例源码经过Lua5.1运行时测试的范围见[游戏实测矩阵](../Testing/ModTestMatrix.md)。协议测试或模组加载成功不等同于武器射击、地图掉落、制作和交易等玩法已通过实机验证。
