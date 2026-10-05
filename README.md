# MaxyModLoader

MaxyModLoader是《这是我的战争》WindowsSteam版的模组加载器与Lua模组API。玩家把加载器文件放进游戏目录，再把模组ZIP放进`Mods`文件夹，就能从Steam启动游戏并自动加载模组。

> 当前只接受经过核验的Steam版BuildID22193501。安装器会校验游戏文件指纹，其他版本会被拒绝。加载器不使用Unity、Mono、BepInEx或Harmony。

## 下载与安装

打开[最新Release](https://github.com/Maxyjof/ThisWarOfMineModLoader/releases/latest)，下载同一版本的`MaxyModLoader-版本号-Windows-x64.zip`和可选的`MaxyModLoader-版本号-SampleMods.zip`。Release还附有`SHA256SUMS.txt`供校验下载文件。

1. 在Steam中退出游戏，进入《这是我的战争》属性的“已安装文件”，选择“浏览”打开游戏目录
2. 将加载器压缩包解压到一个临时文件夹，确认其中有`install-loader.ps1`和`app`文件夹
3. 在该文件夹空白处右键并打开终端，运行下面命令，将路径替换为你的Steam游戏目录

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File ".\install-loader.ps1" -GameDirectory "D:\Steam\steamapps\common\This War of Mine"
   ```

4. 安装完成后，把游戏从Steam启动。入口会自动运行MaxyModLoader，再启动原版游戏

安装包自带Windows运行时，不需要另装.NET。它会在游戏目录创建`MaxyModLoader`和`Mods`文件夹，并将已核验的游戏启动入口替换为图形界面的引导程序。游戏实际运行时仍使用原版引擎和原版程序。启动所需的原版程序副本保存在`x64\MaxyModLoader.Original.exe`；不要手动删除或改名。首次启动或模组组合变化时才构建并部署资源，正常退出游戏后会持续保留模组资源和原生登记；下次启动核验无变化后直接复用，不会重新构建。

## 下载、安装和启用模组

1. 从同一Release下载可选的`MaxyModLoader-版本号-SampleMods.zip`，解压后会看到若干独立模组ZIP
2. 把要使用的模组ZIP复制到游戏目录的`Mods`文件夹，不要再解压里面的模组ZIP
3. 从Steam启动游戏，在主菜单打开“模组管理”，确认模组状态和介绍
4. 按介绍启用需要的模组，点击“重启并应用”即可重启游戏并重新加载
5. 不想使用时取消启用，再次点击“重启并应用”

模组管理界面的开关在下一次启动时应用。直接返回时若有未应用修改，界面会提示你选择是否放弃修改。模组ZIP的根目录必须直接包含`mod.json`和入口Lua文件；每个ZIP对应一个模组。加载器会自动检查依赖、版本和冲突并排序。修改或删除ZIP后，下次启动会从原版恢复点重新生成部署包并应用，内容未变时复用已部署资源。

### 随附示例模组

示例模组包包含四个玩法模组、四个原创物品内容模组和一个开局物资测试模组。武器、弹药、装备与工具模组自带独立生成的图标贴图，不会复用原版物品贴图：

| 模组 | 内容 | 依赖 |
| --- | --- | --- |
| 游戏事件与测试记录桥 | 记录已接入的场景、角色、搜刮和制作回调，为其他示例提供事件及诊断服务 | 无 |
| 搜刮提速 | 默认把原版搜刮动作时长调整为一半，不改战利品计算 | 游戏事件与测试记录桥 |
| 节省移动体力 | 默认把跑步和行走的体力消耗调整为一半 | 游戏事件与测试记录桥 |
| 生存营地辅助包 | 减缓饥饿、首次进入庇护所时提供罐头，并在新一天减轻疲劳 | 游戏事件与测试记录桥 |
| 更多枪械模组 | 添加40种枪械定义与对应的原创图标 | 无 |
| 弹药补给模组 | 添加4类弹药、原创图标和原生物品定义 | 无 |
| 野战工具与防护装备 | 添加工具、防护用品及原创图标 | 无 |
| 军火交易扩展 | 为更多枪械模组提供交易内容 | 更多枪械模组 |
| 开局物资实测包 | 新存档首次进入庇护所时，将40种新增枪械、4种弹药补给物品、6种工具装备各发1件，并将4类原版弹药各发30发 | 游戏事件与测试记录桥、更多枪械、弹药补给、野战工具与防护装备、军火交易扩展 |

把需要的独立ZIP放入`Mods`文件夹；交易扩展需要同时放入并启用枪械模组。开局物资实测包会把物品发给第一名幸存者携带栏，不会直接写入庇护所公共仓库；每个加载器安装只发放一次，测试前建议使用新存档。若要在同一安装中重新领取，请退出游戏后删除`MaxyModLoader/storage/twom.play.starter-armory.0.dat`和`MaxyModLoader/storage/twom.play.starter-armory.1.dat`。安装后可在模组管理界面查看每个模组的完整说明、状态和依赖。示例玩法包会调整游戏平衡，建议先备份存档；它不包含故意报错的测试探针。事件桥只记录它实际接收到的回调，不保证覆盖所有游戏行为。更多源码示例见[`examples`](examples)，小型和组合实装测试见[`playtests/mods`](playtests/mods)。

## 卸载和故障恢复

模组资源和原版恢复点会一直保留到模组配置改变或你明确执行恢复。若加载器不能启动或你要彻底移除它，在Steam中使用“验证游戏文件的完整性”恢复原版启动程序，然后删除游戏目录中的`MaxyModLoader`、`x64\MaxyModLoader.Original.exe`和`Mods`文件夹。删除`Mods`会一并移除里面的模组ZIP，请先保留你想留下的模组。

已安装的加载器也提供显式恢复命令。打开PowerShell，在游戏目录外运行：

```powershell
& "D:\Steam\steamapps\common\This War of Mine\MaxyModLoader\app\MaxyModLoader.exe" restore "D:\Steam\steamapps\common\This War of Mine"
```

Steam验证或重新安装会恢复官方游戏文件，但不会替你备份存档或自装模组；Steam云存档状态取决于你的Steam设置。

## 玩家常见问题

- **Steam启动没有加载模组**：确认游戏目录路径正确，`Mods`里放的是ZIP而非解压文件夹，并检查`MaxyModLoader\startup.log`
- **报告版本不受支持**：当前启动引导只允许已核验的BuildID22193501，Steam更新后请等待针对新游戏版本核验的发行包
- **模组显示依赖缺失**：按界面介绍添加并启用其依赖；示例玩法模组都依赖“游戏事件与测试记录桥”
- **更新或删除模组**：退出并重新启动游戏，加载器按ZIP内容自动更新缓存
- **与其他模组冲突**：阅读模组介绍中的兼容和冲突信息，逐个禁用后使用“重启并应用”定位问题

## 模组开发

**内部开发铁律：只维护当前契约，不保留旧版本兼容或迁移代码。** 旧API别名、安装目录、配置及协议格式均不会自动适配。更新加载器时按[安装指南](Docs/Players/Installation.md)清理后重新安装，模组须同步更新到当前规范；`schemaVersion`必须明确填写。

每个模组目录包含`mod.json`和一个Lua入口文件。将这些文件打包成ZIP，清单直接位于ZIP根目录，再放入游戏`Mods`文件夹。下面是最小示例：

```json
{
  "schemaVersion": 1,
  "id": "author.example",
  "name": "我的模组",
  "version": "1.0.0",
  "author": "作者名",
  "description": "模组功能简介",
  "entry": "main.lua",
  "enabled": true
}
```

完整分类文档见[Docs文档导航](Docs/README.md)，包括玩家使用、故障排查、开发入门、能力总览、运行时API参考、原生内容与MCP指南。开发者可以从[快速入门](Docs/Developers/GettingStarted.md)开始，并查看[`examples`](examples)中的可运行示例。当前游戏结果见[模组实测矩阵](Docs/Testing/ModTestMatrix.md)。

开发文档：[额外能力总览](Docs/Developers/CapabilityOverview.md)、[运行时API参考](Docs/Developers/ModdingAPI.md)、[游戏MCP桥](Docs/Developers/MCP.md)、[游戏版本与实测范围](Docs/Testing/ValidationHistory.md)。除原生内容构建外，加载器API支持Lua内部模块、事件、已存在函数包装、依赖服务、共享规则、按模组隔离的跨启动数据和MCP动作。Lua模组与原生游戏脚本权限相同，只安装可信模组。原生物品内容仍需使用本机官方ModTools构建；当前模组资源不包含游戏原版二进制或原版资源。

## 开发者构建

需要.NET10SDK、Python3.12和Lua5.1测试依赖。Windows玩家包由Release工作流自动构建，也可在仓库根目录运行：

```powershell
dotnet build -c Release
dotnet run --project MaxyModLoader.Tests -c Release --no-build
python -m pip install -r tests/requirements.txt
python -X utf8 tests/test_runtime.py
python -X utf8 tests/test_modpack.py
python -X utf8 tests/test_manager.py
python -X utf8 tests/test_mcp.py
python -X utf8 tests/test_display.py
```

构建、玩家包打包和GitHubRelease工作流见[构建和发行指南](Docs/Maintainers/BuildAndRelease.md)。使用未发布的新版本号，不覆盖已有标签。源码文档可能领先于最新Release，玩家以同一Release附带的说明和模组为准。

## 仓库清理原则

Git只跟踪加载器源代码、文档、工具源码和示例模组。游戏程序、解包资源、构建缓存、发行包、日志和本机路径都由`.gitignore`排除。游戏目录中的活动原版恢复点保存在`MaxyModLoader\backups`并由`install-state.json`引用；模组部署期间不要手动删除。Steam游戏启动所需的`MaxyModLoader.Original.exe`是运行文件，不是可安全删除的构建垃圾。

## 许可

MaxyModLoader代码以MIT许可证发布，Markdig依赖的BSD许可见[`third-party/markdig-license.txt`](third-party/markdig-license.txt)。游戏及其原始资源属于各自权利人；本仓库和Release不分发这些内容。
