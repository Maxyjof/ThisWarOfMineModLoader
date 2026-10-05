# 跨启动数据示例

这个示例通过`context.storage`保存本模组自己的启动计数。每次进入游戏时，它读取上一次的值并加一，然后立即写入加载器数据目录。

## 运行行为

- 第一次启动记录为`1`
- 后续启动读取上次成功保存的值并递增
- 数据保存在`<游戏目录>/MaxyModLoader/storage`，不会写入游戏存档
- 禁用模组或更新模组不会删除这份数据

## 使用的接口

```lua
local count = context.storage.get("launch_count", 0)
context.storage.set("launch_count", count + 1)
```

键名只能使用小写字母、数字、点、下划线和连字符，且必须以小写字母开头。存储按模组ID隔离，仅支持字符串、有限数值和布尔值；接口不接受任意路径、表或Lua代码。

## 文件

- `main.lua`：读取、递增并保存计数
- `mod.json`：当前清单契约
