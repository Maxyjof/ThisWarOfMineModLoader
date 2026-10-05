# MaxyModLoader

《这是我的战争》的社区模组加载器项目，目标是建立可组合的模组规范、Lua 扩展接口和原生引擎适配层。

解决方案为`MaxyModLoader.sln`，仅保留核心库`MaxyModLoader.Core`、命令行入口`MaxyModLoader.Cli`和验证项目`MaxyModLoader.Tests`。核心库包含资源部署、模组运行库、管理界面和内置MCP桥，CLI负责构建、安装、恢复及MCP服务。0.3版的模组管理界面展示禁用、加载失败和依赖失败条目，介绍包括名称、标识、版本、作者、内容、功能、兼容性、依赖、冲突、主页、许可和错误详情。管理界面复用本机原版森林与标题刷痕，列表和介绍使用自制透明炭笔纹理，列表和完整介绍采用独立滚动区域，支持鼠标滚轮、滑块拖动和轨道点击。主菜单入口复用原版按钮的字体、箭头和悬停配方动作。

加载器[内置游戏MCP控制桥](docs/mcp.md)，安装时自动接入游戏Lua主线程，不需要额外MCP模组或独立MCP项目。统一CLI通过`mcp --game`启动服务，提供十九项工具，包括原生物品注册查询和模组规则查询、原生命中诊断、坐标点击、自有界面调整、窗口模式及只读游戏截图。模组清单可引用模组目录内的Markdown说明，构建阶段用Markdig按CommonMark解析Markdown，再将语法树交给游戏内排版器，支持标题、嵌套列表、引用、代码块、分隔线、表格、任务列表和组合行内格式。段落软换行合并、硬换行保留，引用式链接及转义由解析器处理。图片当前显示替代说明，HTML作为字面文本，不提供浏览器网页渲染；原生中文字体的斜体以强调下划线呈现，链接显示但不打开外部浏览器。`MaxyModLoader`为游戏内API名称，`TWOMLoader`保留旧模组兼容；`MaxyModLoader.mods`包含完整目录与实际加载状态，`MaxyModLoader.loaded`记录成功加载的模组。

原版设置中的“全屏”已替换为“窗口模式”，可选择窗口、全屏、无边框全屏，点击原版位置的“应用”后生效，取消会丢弃待应用选择。内置后台辅助服务随游戏启动，确认真实显示状态后保存模式，重启后恢复。普通窗口超出显示器工作区时缩小并居中；无边框覆盖当前显示器。设置行复用原版纸张、字体和箭头配方。当前实测覆盖本机Steam版本及2560×1440单显示器，其他游戏版本会拒绝窗口扩展，详见[显示验证](docs/validation.md)。

## 技术前提

游戏使用 11 bit studios 自研 Liquid Engine，不是 Unity。不能将游戏资源当作 AssetBundle，也不能依赖 Assembly-CSharp、BepInEx 或 Harmony 直接修改其原生代码。

目前 C# / .NET 10 用于**游戏进程外**的管理和资源工具，游戏内接口使用 Lua5.1。首版通过替换 `common` 容器中的 `Main.lua` 引导模组，安装前保存原文件备份，恢复时核验指纹。原生 DLL 注入、运行时资源挂载、新地图和模型导入仍待开发。

依据：[开发者访谈](https://www.gamedeveloper.com/design/road-to-the-igf-11bit-studios-i-this-war-of-mine-i-)、[Lua 与资源容器逆向记录](https://blog.mydayyy.eu/2018/12/18/This-War-of-Mine-Unpacking-gamefiles.html)。这些资料不是本机版本的验证结果。

## 构建与验证

需要.NET10SDK，可直接使用Rider打开解决方案。首次构建会从NuGet还原Markdig1.3.2，用于按CommonMark解析描述文件；其BSD许可保留在`third-party/markdig-license.txt`，发布与安装同时携带许可文本。

```powershell
dotnet build
dotnet run --project MaxyModLoader.Tests
dotnet run --project MaxyModLoader.Cli -- plan examples
```

`plan` 检查清单、依赖、版本、冲突及循环，输出稳定的加载顺序。发现错误时返回非零退出码，并且不产生部分加载计划。

## 使用第一版

日常玩家包可先运行`./tools/prepare-player-mods.ps1 -Destination <新的模组目录>`，准备五个已实装验证的玩法模组，以及更多枪械、弹药补给、野战装备和军火交易四个原生内容模组，再把下方构建命令的`examples`替换为该目录。该脚本不覆盖已有目录，开发示例与故意失败探针不会混入玩家包。原生内容格式见[原生内容模组说明](docs/native-content.md)。

先退出游戏。在仓库根目录运行以下命令，将路径改为自己的安装目录。`artifacts/package-demo` 必须是尚不存在的目录。

```powershell
dotnet run --project MaxyModLoader.Cli -- build "D:\Steam\steamapps\common\This War of Mine\common" 5faa28a2 examples artifacts/package-demo
dotnet run --project MaxyModLoader.Cli -- install "D:\Steam\steamapps\common\This War of Mine" artifacts/package-demo
```

通过 Steam 正常启动游戏。当前已验证版本的日志位于游戏根目录的 `MaxyModLoader/runtime.log`。工具也把日志输出到游戏控制台。日志文件依赖游戏开放 `io` 库以及启动时工作目录；本机 Steam 启动已验证能生成日志。

退出游戏后恢复原容器：

```powershell
dotnet run --project MaxyModLoader.Cli -- restore "D:\Steam\steamapps\common\This War of Mine"
```

安装器只接受与构建来源指纹完全一致的容器。更新游戏、其他模组修改文件或部署包损坏时拒绝安装。已安装时先恢复，再从原容器重新构建，避免把原始脚本包装多次。安装中断可用同一个 `restore` 命令恢复；如果其他工具再次改动文件，恢复器拒绝自动覆盖。恢复后保留备份。安装还会复制五个固定的.NET辅助运行文件和隐藏启动脚本，恢复时按所有权指纹移除这些文件，保留模式偏好和日志；第三方修改的辅助文件不会被自动删除。

首版修改磁盘上的两个 `common` 文件，不修改 EXE 或存档。模组增删、更新和启用状态变化都需恢复、重新构建并安装。模组拥有游戏 Lua 全局环境访问能力，只运行可信模组。入口失败只撤销加载器管理的订阅与包装，无法回滚任意游戏副作用；包装回调的异常也由模组自行处理。

也可用发布工具直接运行相同命令：

```powershell
dotnet publish MaxyModLoader.Cli -c Release -o artifacts/tool
.\artifacts\tool\MaxyModLoader.exe plan examples
```

这份发布产物依赖 .NET 10 运行时，未做自包含打包。不要分发带有游戏原资源的构建部署包，分发工具和模组源码即可让玩家本机生成。

## 模组规范 v1

每个模组独立文件夹，包含 `mod.json` 和 `main.lua`，参考 `examples/hello` 和 `examples/diary-observer`。

- ID 使用小写字母、数字及 `. _ -`，必须以字母开头且全局唯一。
- 版本使用 `major.minor.patch` 三段非负整数，暂不接受预发布或版本范围。
- `dependencies` 是依赖 ID 到最低版本的映射；禁用依赖视为缺失。
- `conflicts` 列出不能同时启用的模组 ID。
- `enabled` 控制是否纳入加载计划，禁用模组仍检查清单格式。
- `name`、`author`、`description`填写展示信息；`features`为功能说明数组，`compatibility`填写兼容条件，`website`只接受HTTP或HTTPS地址，`license`填写许可名称。
- `entry` 必须指向模组目录内真实存在的 Lua 文件，拒绝目录逃逸和入口符号链接。
- `nativeContentFile`可选，指向本机已核验版本上构建的物品、制作配方、地图掉落或商人货单定义，格式见原生内容说明。
- 扫描模组根目录的直接子目录；没有 `mod.json` 的目录跳过。

MaxyModLoader同时提供版本化的LuaModdingAPI，当前契约版本为1.0.0。除模组入口、事件、内部模块、依赖服务和可撤销Lua函数包装外，模组可声明带类型、范围或选项约束的运行时共享规则，依赖模组可受控读取、调整并监听规则变化。内置MCP提供规则只读查询。完整接口、兼容约定和限制见[ModdingAPI开发文档](docs/modding-api.md)。

`TWOMLoader.emit(name, ...)` 广播自定义事件，`loader.ready` 在所有模组尝试加载后触发。静态依赖通过但依赖入口运行失败时，依赖方也会跳过。事件回调异常不影响其他订阅，参数尾部的 `nil` 保留，回调内新增订阅从下一次广播开始生效。

0.2版新增 `modules` 和 `settings` 清单字段。`modules` 将内部模块名映射到模组内 Lua 文件，`context.require(name)` 注入上下文、缓存模块并检测循环；模块文件通过 `local context = ...` 取得上下文。`settings` 支持字符串、有限数字、布尔值和嵌套对象，由 `context.config` 读取，修改后需重新构建部署包。

`context.services.provide(name, service)` 提供本模组服务，`context.services.get(providerId, name)` 获取服务。使用其他模组服务必须在清单中声明依赖，且提供方入口必须成功；入口失败会清理该模组提供的服务。这些接口不是不可信代码沙箱。

小型和中型实装测试包位于 `playtests/mods`，与普通欢迎示例分开。测试包包括真实游戏接口桥、搜刮提速、移动体力、角色状态、生存营地、活动记录、快捷键及两种故意失败探针。基线包只包含观察功能，组合包用于验证模组实际效果及彼此组合，故意失败探针的错误日志属于预期结果。枪械与装备内容使用本机官方ModTools从原版物品模板编译，不带独立模型或动画；弹药补给尚未关联到枪械弹药消耗，枪械伤害、搜刮结果和商人实际库存还需要在具体玩法场景中验证。

`diary-observer` 演示包装本机 `Events.lua` 中的 `logEvent` 并发出 `game.diary` 事件。这只覆盖经过该 Lua 函数的记录，不代表完整的原生日记事件总线。当前没有自动昼夜、角色、物品或场景事件接口。

项目只提交自有代码、文档和示例，不提交游戏二进制或解包资源。将本机游戏、实验数据放入忽略的 `local/`。

## 推进顺序

1. 已完成模组清单、依赖与冲突规划、示例及测试。
2. 已完成容器检查、Lua 引导编译、离线资源输出与安装恢复。
3. 已在本机 Steam 版本验证 Lua 入口和示例模组启动加载。
4. 原生文件系统拦截，做到不覆盖游戏文件的运行时挂载。
5. 逐步提供角色、物品、事件、场景和模型接口。

验证版本、证据和限制见 [验证记录](docs/validation.md)。当前验证覆盖游戏启动入口，未覆盖完整剧情流程或所有官方模组组合。

## 自动化验证与注释规范

C# 测试覆盖依赖规划、长依赖链、损坏索引、资源替换、指纹校验与中断恢复，也使用 SDK 自带 Roslyn 语法树检查所有自有 C# 类、方法和构造函数的中文 XML 注释。具体规范保存在 `AGENTS.md`。

Lua 测试使用 Lupa 的 Lua5.1 运行时，测试依赖仅用于验证，不随加载器部署：

```powershell
python -m pip install -r tests/requirements.txt
dotnet run --project MaxyModLoader.Tests
python -X utf8 tests/test_runtime.py
```

`.github/workflows/verify.yml` 在 Windows 上运行构建、C# 测试和 Lua 测试。

## 管理面板自制纹理

两块内容背景使用[自制炭笔素材](tools/ui-art/README.md)，保留透明边缘与纸张纹理。普通构建自动转换并追加到`textures-s3`，安装与恢复同时校验、备份和处理`common`及纹理容器。部署包包含完整原版纹理容器，约2GB，构建产物和游戏资源不会提交到Git。旧版单容器安装日志仍能恢复。
