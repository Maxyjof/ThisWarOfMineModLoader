# MaxyModLoader游戏MCP控制桥

MCP属于加载器内置功能。`MaxyModLoader.Core/Runtime/mcp.lua`随运行库自动接入游戏主线程，统一CLI提供进程外的标准输入输出MCP服务。没有独立MCP项目，也不需要扫描或安装额外MCP模组。服务不发送Windows键鼠输入，不激活游戏窗口，也不开放任意Lua执行或原生地址调用。

## 部署

先按[安装指南](../Players/Installation.md)安装当前加载器；即使没有模组，桥也会随游戏接入。先从Steam启动游戏，再在MCP客户端配置当前安装的自包含程序，无需另装.NET运行时：

```json
{
  "command": "<游戏目录>/MaxyModLoader/app/MaxyModLoader.exe",
  "args": ["mcp", "--game", "<游戏目录>"]
}
```

将占位符换成实际绝对路径。Windows JSON中的反斜杠需写成双反斜杠，也可以用正斜杠。服务不会替你启动游戏或修改AI客户端个人配置。采用MCP2025-06-18的stdio传输，标准输出只含逐行JSONRPC，诊断写标准错误；客户端先`initialize`再调用工具。

仓库调试可使用当前构建的`MaxyModLoader.Cli/bin/Release/net10.0/MaxyModLoader.dll`配合`dotnet`运行`mcp --game`，这只是开发工具，不是玩家安装格式。

## 公开工具

| 工具 | 参数 | 功能 |
| --- | --- | --- |
| `game_state` | 无 | 读取天数、暂停、玩法阶段、加载状态、帧计数、界面根节点可见性和真实角色参数 |
| `mods_list` | 无 | 完整模组介绍与实际加载状态 |
| `mod_manager` | 无 | 独立管理面板可见性、选择、两侧实际偏移与滚轮记录 |
| `mod_scroll` | `target`字符串，例如`list\|10`或`detail\|20` | 定位自有列表条目或介绍行，返回有效偏移与内容边界 |
| `ui_tree` | 无 | 读取可见原生界面树与完整元素路径 |
| `ui_catalog` | 无 | 有界读取可见及隐藏控件用于本机原版配方诊断 |
| `ui_adjust` | `edit`字符串，例如`MML_DETAIL_LINE_1\|color\|1,1,1,1` | 临时调整加载器自有控件的颜色、位置、大小或缩放 |
| `ui_click` | `name`字符串 | 触发当前可见且启用的原生界面元素 |
| `ui_hit_test` | `point`字符串，例如`0.75,0.60` | 读取屏幕归一化坐标处的原生命中控件与父链 |
| `ui_click_point` | 同上 | 经过原生命中再点击，不移动真实鼠标 |
| `set_pause` | `paused`布尔值 | 核心玩法阶段允许暂停；菜单、介绍与加载阶段拒绝新增暂停，解除暂停始终允许 |
| `end_day` | 无 | 请求结束当前庇护所白天 |
| `quit_game` | 无 | 请求游戏正常退出 |
| `inspect_type` | `name`字符串 | 列出公开Lua类型的方法名称 |
| `item_config` | `name`字符串 | 读取物品注册状态及公开数值属性，不生成物资 |
| `inventory_item` | `name`字符串 | 读取已注册物品的真实全局库存 |
| `inventory_batch` | `items`字符串，以英文逗号分隔 | 单次读取最多128种已注册物品的真实全局库存，适合核对整套模组物资 |
| `debug_give_item` | `item`字符串，例如`MML_AK74\|1\|0` | 向庇护所测试存档注入物资并返回库存前后值，数量限1到20，角色序号限0到15且必须存在 |
| `game_screenshot` | 无 | 捕获游戏窗口并返回PNG图像内容 |
| `display_mode` | `mode`字符串：`borderless`、`windowed`或`fullscreen` | 请求引擎切换显示模式并调整Windows窗口边框，随后读取真实全屏状态、样式与显示器边界复核 |
| `settings_state` | 无 | 读取原版设置行、已确认模式、待应用选择、后台服务与错误状态 |
| `rule_list` | 无 | 只读列出运行时模组规则、当前值、约束范围及提供方 |

除二十二项固定工具外，`tools/list`会读取游戏中成功加载的模组工具。模组必须在清单`capabilities`中声明`mcp.tools`，并通过`context.actions.register`提交描述、扁平参数模式和游戏主线程回调。工具名称以`mod_`开头，调用参数由固定服务重新校验；这不会开放任意Lua执行。模组工具能力可调用游戏Lua全局对象，因此模组本身仍必须来自可信来源，详见[ModdingAPI文档](ModdingAPI.md)

名称重复时`ui_click`拒绝调用，改用`ui_tree`提供的完整路径。调试新增控件时应使用`ui_hit_test`和`ui_click_point`检查实际命中，而不能只调用名字对应的处理函数。`mod_manager`返回当前鼠标命中、最近按下或释放边沿及滚轮输入。列表和介绍各自保存偏移，更换所选模组时只将介绍恢复到顶部。`mod_scroll`与真实滚轮共用边界和内容定位逻辑，用于检查原生裁剪与滑块位置，不发送系统滚轮事件。MCP模拟点击与真实鼠标点击分别验证，工具返回成功只表示已触发请求，场景切换、动画和渲染完成需要后续状态读取与截图确认。

截图仅定位指定安装目录的唯一游戏进程，在独立辅助进程中先请求窗口后台绘制。Liquid Engine或独占全屏不提供有效后台帧时，仅当游戏本身位于前台才回退读取屏幕合成后的客户区；不会激活游戏或向桌面发送键鼠。若游戏被遮挡或不在前台，会说明原因并要求恢复到前台后重试。PNG保存到游戏目录`MaxyModLoader/mcp/screenshots`，MCP响应同时提供`image/png`图像内容。捕获路径依据：[PrintWindow](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-printwindow)、[BitBlt](https://learn.microsoft.com/en-us/windows/win32/api/wingdi/nf-wingdi-bitblt)、[GetDIBits](https://learn.microsoft.com/en-us/windows/win32/api/wingdi/nf-wingdi-getdibits)。

## 传输与边界

服务与游戏通过固定目录`MaxyModLoader/mcp`交换请求。单个客户端持有文件锁，其他客户端有界等待，以临时文件原子发布请求，使用随机请求ID关联返回；请求上限64KB，返回上限1MB。游戏每个CPU时间间隔检查一次，请求在执行前移除，不自动重放。

工具等待超过8秒会返回“结果未确认”。状态变更命令不能因超时自动重试，先查询实际状态。游戏退出、处于加载阶段或不处理帧回调时，工具可能失败。后台轮询通过游戏委托的`CanSleep`包装保持工作，会增加后台游戏的运行开销。

`debug_give_item`明确修改当前存档物资，只用于测试存档。接口先确认物品原生注册、角色存在和当前为庇护所核心玩法，拒绝加载、介绍页及夜间阶段；每次返回`diagnostic=true`和真实库存前后值。注入成功只证明物品可以进入库存，不能证明制作、地图掉落、装备用途、射击或交易已经通过。

`game_state.timing`只输出数字计时值或原生对象的类型，不序列化原生计时对象和地址。命令返回值无法编码时，桥会返回带请求ID的明确错误；不会让已消费的请求只能等待超时。设置扩展的按钮输入由设置页面独立处理，不依赖主菜单管理面板，普通帧与暂停帧使用同一按下释放逻辑。

MCP桥拥有普通本机文件读写权限，面向同一台电脑上的可信客户端。目前没有远程HTTP接口、角色路径规划、通用物体交互或自主通关能力。结束白天、点击与暂停需要结合当前真实界面使用；新增世界交互接口需单独核验原生绑定。

开发诊断也可直接调用白名单命令：

```powershell
dotnet run --project MaxyModLoader.Cli -c Release -- rpc "<游戏安装目录>" mods_list
dotnet run --project MaxyModLoader.Cli -c Release -- rpc "<游戏安装目录>" ui_hit_test "0.75,0.60"
dotnet run --project MaxyModLoader.Cli -c Release -- screenshot "<游戏安装目录>"
```

## 验证

`display_mode`与原版设置菜单共用内置后台服务，确认后保存模式并同步游戏内状态，最多等待15秒；超时不能直接重放操作。服务随游戏启动，使用独占会话锁，游戏退出时结束，诊断保存在本机`MaxyModLoader/display-host.log`。读取确认文件允许原子替换；短暂共享冲突只重试发布文件，不重试显示操作。

窗口扩展仅允许Windows目标安装的唯一游戏进程，绑定已验证EXE指纹，并拒绝最小化窗口。窗口保存与进程ID、启动时间和句柄绑定的恢复信息；切换后轮询读取游戏内部全屏开关、Windows边框和显示器范围，无法确认时恢复可用的窗口样式并返回失败。工具会改变游戏显示状态，不会激活窗口或发送键鼠输入。`display <游戏目录> <模式>`与MCP工具使用相同路径，省略模式参数只读取状态。

协议工具返回成功仅表示请求已被处理；游戏效果需要通过实际游戏画面、状态和日志确认。历史游戏版本和源码阶段的运行记录见`Docs/Testing/ValidationHistory.md`。

`ui_adjust`仅作用于加载器拥有的`MML_`及`BUTTON_MAXY_MODS`控件，不允许修改原版控件、执行代码或调用地址。支持`position`、`size`、`scale`、`color`及自有图片的默认`channel`，拒绝空分量、非有限数字和越界数值。更改只在当前会话保留，正式布局需要修改运行库并重新部署。最小化时截图工具明确报错，不能将标题条当作游戏画面。
