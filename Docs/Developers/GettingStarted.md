# 制作第一个Lua模组

运行环境是游戏原生Liquid Engine中的Lua5.1.1；C#加载器工具在游戏进程外运行。不要使用Unity、Mono或Harmony写插件。

## 创建目录与清单

创建一个独立目录，加入下面三个文件。ID使用作者前缀以减少冲突。

```text
my-example/
  mod.json
  main.lua
  README.md
```

```json
{
  "schemaVersion": 1,
  "id": "author.example",
  "name": "我的第一个模组",
  "version": "1.0.0",
  "author": "作者",
  "description": "记录游戏加载完成",
  "descriptionFile": "README.md",
  "entry": "main.lua",
  "enabled": true,
  "license": "MIT"
}
```

`main.lua`返回包含`on_load`函数的表。所有类和方法使用中文注释，标记各占一行；逻辑片段也应解释用途。

```lua
return {
    --<summary>
    --订阅加载器就绪事件并记录日志
    --</summary>
    on_load = function(context)
        --只注册当前模组的订阅不修改原生对象
        context.events.on("loader.ready", function()
            --所有入口加载尝试完成后记录通知
            context.log("我的模组收到加载完成通知")
        end)
    end
}
```

`README.md`写真实Markdown，例如标题、段落、列表、代码块和表格。介绍文件会经Markdig解析为结构化节点；当前游戏渲染支持范围见[清单文档](Manifest.md)，网页HTML或任意脚本不会直接执行。

## 校验与打包

开发机安装.NET10SDK，在仓库运行：

```powershell
dotnet run --project MaxyModLoader.Cli -c Release -- plan "<包含各模组目录的父目录>"
Compress-Archive -Path "<模组目录>\*" -DestinationPath "<输出目录>\我的模组.zip"
```

`plan`发现父目录的直接子目录中的清单，检查依赖、版本和冲突；它不执行Lua，不证明游戏效果。ZIP根目录必须直接是`mod.json`，不能外套一层`my-example`目录。可解开查看ZIP结构再放到游戏的`Mods`顶层。

## 在游戏中验证

按[玩家安装指南](../Players/Installation.md)部署当前构建。从Steam启动，在模组管理中确认入口状态，并查看`MaxyModLoader/runtime.log`是否收到事件日志。实际改变角色、搜刮或内容时，使用新存档和内置MCP读取修改前后状态并截图，不以编译成功或清单注册代替玩法验证。

## 扩展功能

- [清单字段](Manifest.md)：模块、配置、依赖和描述
- [ModdingAPI](ModdingAPI.md)：共享事件、服务、规则和可撤销函数包装
- [原生内容声明](NativeContent.md)：物品、配方、地图掉落和交易
- [MCP动作](MCP.md)：提供受参数校验的游戏主线程工具

源码示例：`examples/rule-api`展示提供方与调节方，`examples/mcp-tools`展示模组工具，`playtests/mods/survival-camp`展示多个内部模块。声明依赖后再使用服务，不通过全局变量猜测加载顺序。当前API和安装契约不保留旧版兼容；契约调整时同步改模组与测试。
