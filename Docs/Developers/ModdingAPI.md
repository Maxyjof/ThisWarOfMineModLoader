# MaxyModLoaderModdingAPI

MaxyModLoader不仅负责发现和加载Lua模组，也提供模组之间可组合的运行时扩展API。当前API版本为`1.0.0`，游戏加载器版本通过`MaxyModLoader.version`读取，API契约版本通过`MaxyModLoader.api_version`和`context.api_version`读取

API运行在《这是我的战争》已有的Lua虚拟机中。它不是.NET插件系统，不提供Unity、Mono、BepInEx或Harmony接口。游戏内部对象和回调只在针对本机版本完成核验后才能作为游戏适配器使用

## 入口与上下文

```lua
return {
    --<summary>
    --声明模组入口并从加载器上下文安装功能
    --</summary>
    on_load = function(context)
        context.log("模组入口已加载")
        context.events.on("loader.ready", function()
            context.log("所有入口均已尝试加载")
        end)
    end
}
```

`context`提供模组ID、API版本、加载器版本、配置、日志、内部模块、事件、依赖服务、受限函数包装和运行时规则。入口执行失败时加载器撤销该入口创建的事件订阅、函数包装、服务和规则定义；游戏全局副作用无法由加载器自动回滚

## 事件

`context.events.on(name, callback)`订阅事件并返回取消函数；`MaxyModLoader.emit(name, ...)`广播事件。广播按订阅顺序执行并隔离回调异常，保留尾部`nil`参数，广播期间新建的订阅从下一次广播生效

当前稳定事件是`loader.ready`，表示所有计划内入口均已尝试加载，不代表游戏进入了某个具体剧情阶段。游戏玩法事件需要由针对已核验Lua回调编写的适配模组主动广播；不能将观察到的单个游戏函数包装说成完整原生事件总线

## 模块、依赖服务与函数包装

`context.require(name)`只加载清单中显式声明的模组内部模块，提供缓存并检测循环。模块通过`local context = ...`取得同一入口上下文

`context.services.provide(name, value)`发布模组服务；`context.services.get(providerId, name)`只读取自身或清单中直接声明的依赖模组服务。提供方入口必须先成功执行

`context.wrap(target, key, callback)`包装现存Lua表函数。回调第一个参数是原函数，后续参数和返回值由模组负责传递。包装是运行时修改，不会猜测原生函数地址，也不会隔离恶意模组

## 运行时规则

规则让独立模组使用受校验的共享数值，而不必互相改写配置文件。规则由提供方拥有，ID格式为`模组ID:规则名`。规则名使用小写字母开头以及小写字母、数字、点、下划线或连字符

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

规则支持有限数值、布尔值和字符串。数值可声明最小值与最大值，字符串可声明允许值列表。已声明直接依赖的模组可通过`get(providerId, name)`读取、`set(providerId, name, value)`调整，并用`on_change(providerId, name, callback)`监听变化；未声明依赖不能读取或修改其他模组规则。规则只存在于当前游戏进程，不保存到磁盘

规则API本身不改写游戏角色、物品或昼夜参数。玩法适配器应在已经确认的游戏回调中读取规则、校验运行阶段并应用值；无对应已核验回调时，规则只会改变共享配置值

`MaxyModLoader.rule_snapshot()`返回按ID稳定排序的规则定义与当前值，内置MCP的`rule_list`工具提供只读查询

## 模组注册的MCP工具

模组清单只有在`capabilities`中明确填写`mcp.tools`后，才可以调用`context.actions.register(name, definition, callback)`注册工具。客户端调用`tools/list`时会从正在运行的游戏读取工具定义，之后可像调用加载器内置工具一样调用模组工具。回调在游戏Lua主线程执行

```lua
context.actions.register("echo", {
    description = "回传文本用于检查结构化参数",
    properties = {
        text = {type = "string", description = "输入文本"},
        count = {type = "integer", description = "重复次数"}
    },
    required = {"text"},
    read_only = true
}, function(arguments)
    return {text = arguments.text, count = arguments.count or 1}
end)
```

参数模式限32个扁平字段，类型为`string`、`number`、`integer`或`boolean`，不接受任意函数、Lua源码、嵌套对象或自由文件路径。`required`列出必填键；未列出的键会被拒绝。`read_only`和`destructive`用于生成MCP工具提示，不代替回调自身的游戏状态检查。动作注册随模组入口失败撤销，普通取消函数可撤销已注册动作

授予`mcp.tools`的模组可以注册改变游戏的动作，并以模组代码本身的权限访问Lua全局变量；Lua模组不是安全沙箱，只应加载可信来源。内置桥不会因为该能力开放任意代码执行

## 原生内容与设置配置

`nativeContentFile`声明在构建期间通过本机已核验的官方ModTools生成物品、配方、地图掉落和商人货单差异。该路径是离线内容API，不代表运行时文件挂载或新模型导入，参见[原生内容说明](NativeContent.md)

清单`settings`是模组开发者提供的静态默认配置，在构建部署时注入`context.config`。当前没有通用的游戏内配置编辑器、磁盘热保存或跨启动持久化服务；需要玩家调整的运行时值可由规则定义，但其持久化与原版菜单编辑能力仍需后续实现

## 内部开发契约和验证

依赖模组须在提供方之后加载。项目仍处于内部开发阶段，只维护当前契约，不承诺版本之间的API或配置兼容，也不保留别名、旧目录、旧协议或迁移分支。契约变更时同步更新加载器、模组、工具和测试；非当前格式应明确报错，重新安装当前加载器并按当前规范更新模组。`context.api_version`用于标识当前契约，不用于选择旧实现

`examples/rule-api`演示规则提供方、显式依赖、跨模组调节和变更监听。`examples/mcp-tools`演示声明工具能力和结构化动作。Lua5.1运行时测试覆盖规则类型、边界、依赖授权、事件通知、快照排序和入口失败回滚；MCP协议测试使用真实服务进程检查工具发现、类型和参数编码。真实游戏玩法是否受规则或动作影响，必须为对应适配器单独提供游戏内验证记录
