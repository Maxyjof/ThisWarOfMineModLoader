# MaxyModLoader游戏MCP控制桥

MCP属于加载器内置功能。`MaxyModLoader.Core/Runtime/mcp.lua`随运行库自动接入游戏主线程，统一CLI提供进程外的标准输入输出MCP服务。没有独立MCP项目，也不需要扫描或安装额外MCP模组。服务不发送Windows键鼠输入，不激活游戏窗口，也不开放任意Lua执行或原生地址调用。

## 部署

退出游戏，按README的构建、安装流程部署加载器。即使模组目录为空，内置MCP桥也会自动安装。原有独立MCP模组不要再加入新版本的扫描目录，以免重复包装游戏帧回调。

发布MCP服务：

```powershell
dotnet publish MaxyModLoader.Cli -c Release -o artifacts/release
```

MCP客户端启动配置使用实际的绝对路径：

```json
{
  "command": "dotnet",
  "args": [
    "<仓库绝对路径>/artifacts/release/MaxyModLoader.dll",
    "mcp",
    "--game",
    "<游戏安装目录>"
  ]
}
```

需要.NET10运行时。服务采用MCP2025-06-18的stdio传输，输出只包含逐行JSONRPC消息，诊断信息写入标准错误。客户端需完成`initialize`后调用工具。协议依据：[stdio传输](https://modelcontextprotocol.io/specification/2025-06-18/basic/transports)、[工具消息](https://modelcontextprotocol.io/specification/2025-06-18/server/tools)。尚未自动修改任何AI客户端的个人配置。

## 公开工具

| 工具 | 参数 | 功能 |
| --- | --- | --- |
| `game_state` | 无 | 读取天数、暂停和真实角色参数 |
| `mods_list` | 无 | 完整模组介绍与实际加载状态 |
| `mod_manager` | 无 | 独立管理面板可见性、选择和分页 |
| `ui_tree` | 无 | 读取可见原生界面树与完整元素路径 |
| `ui_click` | `name`字符串 | 触发当前可见且启用的原生界面元素 |
| `ui_hit_test` | `point`字符串，例如`0.75,0.60` | 读取屏幕归一化坐标处的原生命中控件与父链 |
| `ui_click_point` | 同上 | 经过原生命中再点击，不移动真实鼠标 |
| `set_pause` | `paused`布尔值 | 明确设置用户暂停 |
| `end_day` | 无 | 请求结束当前庇护所白天 |
| `quit_game` | 无 | 请求游戏正常退出 |
| `inspect_type` | `name`字符串 | 列出公开Lua类型的方法名称 |
| `game_screenshot` | 无 | 捕获游戏窗口并返回PNG图像内容 |

名称重复时`ui_click`拒绝调用，改用`ui_tree`提供的完整路径。调试新增控件时应使用`ui_hit_test`和`ui_click_point`检查实际命中，而不能只调用名字对应的处理函数。`mod_manager`返回当前鼠标命中及最近按下或释放边沿。MCP模拟点击与真实鼠标点击分别验证，工具返回成功只表示已触发请求，场景切换、动画和渲染完成需要后续状态读取与截图确认。

截图仅定位指定安装目录的唯一游戏进程，在独立辅助进程中捕获，避免后台窗口绘制卡住MCP服务。捕获失败或黑帧会明确报错，不能把工具返回的文字状态当作截图验证。PNG保存到游戏目录`MaxyModLoader/mcp/screenshots`，MCP响应同时提供`image/png`图像内容。系统接口依据：[PrintWindow](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-printwindow)、[GetDIBits](https://learn.microsoft.com/en-us/windows/win32/api/wingdi/nf-wingdi-getdibits)。

## 传输与边界

服务与游戏通过固定目录`MaxyModLoader/mcp`交换请求。单个客户端持有文件锁，以临时文件原子发布请求，使用随机请求ID关联返回；请求上限64KB，返回上限1MB。游戏每个CPU时间间隔检查一次，请求在执行前移除，不自动重放。

工具等待超过8秒会返回“结果未确认”。状态变更命令不能因超时自动重试，先查询实际状态。游戏退出、处于加载阶段或不处理帧回调时，工具可能失败。后台轮询通过游戏委托的`CanSleep`包装保持工作，会增加后台游戏的运行开销。

MCP桥拥有普通本机文件读写权限，面向同一台电脑上的可信客户端。目前没有远程HTTP接口、角色路径规划、通用物体交互或自主通关能力。结束白天、点击与暂停需要结合当前真实界面使用；新增世界交互接口需单独核验原生绑定。

开发诊断也可直接调用白名单命令：

```powershell
dotnet run --project MaxyModLoader.Cli -c Release -- rpc "<游戏安装目录>" mods_list
dotnet run --project MaxyModLoader.Cli -c Release -- rpc "<游戏安装目录>" ui_hit_test "0.75,0.60"
dotnet run --project MaxyModLoader.Cli -c Release -- screenshot "<游戏安装目录>"
```

## 验证

`tests/test_mcp.py`使用统一CLI的真实MCP服务进程与临时文件端验证握手、十二项工具发现、参数错误隔离及中文参数往返。这些协议测试不代表游戏效果验证。真实游戏验证与截图检查单独记录在`docs/validation.md`。
