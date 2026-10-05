# 模组开发快速入门

本教程从零创建一个可以被当前加载器发现、打包、安装和测试的Lua模组。游戏运行时是原生Liquid Engine中的Lua5.1.1；C#加载器工具在游戏进程外执行，不能用Unity、Mono或Harmony插件方式开发。

## 1. 创建模组

建立目录并准备三个文件：

```text
hello-mod/
  mod.json
  main.lua
  README.md
```

`mod.json`：

```json
{
  "schemaVersion": 1,
  "id": "author.hello",
  "name": "我的第一个模组",
  "version": "1.0.0",
  "author": "作者名",
  "description": "收到加载完成事件后输出一条日志",
  "descriptionFile": "README.md",
  "entry": "main.lua",
  "enabled": true,
  "dependencies": {},
  "conflicts": [],
  "features": ["加载器日志", "监听加载完成事件"],
  "license": "MIT"
}
```

`main.lua`：

```lua
return {
    --<summary>
    --订阅加载器就绪事件并输出日志
    --</summary>
    on_load = function(context)
        --模组ID用于区分同一游戏内的多条日志
        context.log("入口已加载：" .. context.id)

        --在本次计划内所有入口都尝试加载后收到通知
        context.events.on("loader.ready", function()
            context.log("加载计划已完成")
        end)
    end
}
```

`README.md`用真正的Markdown语法撰写。模组管理界面会解析标题、段落、列表、引用、代码块、行内代码、表格等结构；支持范围和限制见[清单与说明文档](Manifest.md)。

## 2. 检查清单

每个模组必须使用当前`schemaVersion: 1`清单。ID全局唯一且使用小写，版本使用三段数字。依赖以ID到最低版本的映射声明；使用`context.services`或跨模组规则前必须声明直接依赖。未知清单字段会报错，不会尝试旧格式迁移。

在仓库根目录运行计划检查：

```powershell
dotnet run --project MaxyModLoader.Cli -c Release -- plan "<模组目录的父目录>"
```

该命令检查清单、依赖、版本、冲突和路径，不执行Lua，也不证明玩法有效。

## 3. 添加多文件模块或配置

模块必须在清单`modules`中声明：

```json
"modules": {
  "supplies": "modules/supplies.lua"
}
```

入口用`context.require("supplies")`取得模块返回值。静态默认配置放在清单`settings`中，会注入`context.config`；修改模组包后需重新安装并重启，运行时改动不会回写清单。

## 4. 保存模组自己的进度

使用`context.storage`保存字符串、有限数字或布尔值。每个模组的数据互相隔离，写入游戏目录`MaxyModLoader/storage`，不修改游戏存档：

```lua
local count = context.storage.get("launch_count", 0)
context.storage.set("launch_count", count + 1)
```

仓库[`examples/persistent-storage`](../../examples/persistent-storage)是完整可运行版本。键数、键名、字符串大小和文件格式都有明确限制，详见[API参考](ModdingAPI.md#跨启动持久存储)。

## 5. 打包安装

ZIP根目录必须直接包含`mod.json`、入口及资源，不能额外套一层模组目录。PowerShell打包示例：

```powershell
Compress-Archive -Path ".\hello-mod\*" -DestinationPath ".\hello-mod.zip"
```

把ZIP放到游戏根目录`Mods`文件夹。Steam启动游戏后，打开主菜单的“模组管理”检查介绍和状态。需要启用或禁用时修改选择，再点击“重启并应用”；回到游戏后检查`<游戏目录>/MaxyModLoader/runtime.log`。

## 6. 验证玩法

先在安全的新存档检查入口日志和模组状态。需要改动角色、物品、搜刮、制作或交易时，找出已核验的原生Lua回调或内容字段，以动作前后状态验证实际效果。内置MCP可检查角色参数、物品注册、实际库存、界面和截图；`debug_give_item`只用于诊断存档，不代表自然获取或武器射击有效。

将结果写入[游戏实测矩阵](../Testing/ModTestMatrix.md)，明确区分Lua测试、数据注册成功和完整玩法验证。真实函数签名、场景边界或字段没有核实时，不要根据类似版本猜测。

## 下一步

- [能力总览](CapabilityOverview.md)：官方原生能力之外的当前扩展与不能做的事情
- [完整运行时API参考](ModdingAPI.md)：事件、包装、服务、规则、持久数据和MCP动作
- [清单字段](Manifest.md)：依赖、静态配置、能力与Markdown
- [原生内容声明](NativeContent.md)：物品、配方、掉落和交易
- [内置MCP](MCP.md)：工具接入、诊断和权限边界

可运行源码示例位于仓库`examples`目录：`hello`、`diary-observer`、`rule-api`、`persistent-storage`和`mcp-tools`。更多组合案例在`playtests/mods/survival-camp`。项目只维护当前模组API和清单契约；契约修改时同步更新运行库、示例、测试与文档。
