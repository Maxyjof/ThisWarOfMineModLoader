# ThisWarOfMineModLoader

《这是我的战争》的社区模组加载器项目，目标是建立可组合的模组规范、Lua 扩展接口和原生引擎适配层。

## 技术前提

游戏使用 11 bit studios 自研 Liquid Engine，不是 Unity。不能将游戏资源当作 AssetBundle，也不能依赖 Assembly-CSharp、BepInEx 或 Harmony 直接修改其原生代码。

目前 C# / .NET 10 用于**游戏进程外**的管理和资源工具；游戏内接口使用 Lua。原生注入入口必须在取得实际 Windows 版本后验证，当前尚未实现。新增地图和模型需要进一步分析引擎格式，暂不宣称支持。

依据：[开发者访谈](https://www.gamedeveloper.com/design/road-to-the-igf-11bit-studios-i-this-war-of-mine-i-)、[Lua 与资源容器逆向记录](https://blog.mydayyy.eu/2018/12/18/This-War-of-Mine-Unpacking-gamefiles.html)。这些资料不是本机版本的验证结果。

## 构建与验证

需要 .NET 10 SDK，可直接使用 Rider 打开解决方案。当前不需要第三方 NuGet 包。

```powershell
dotnet build
dotnet run --project ThisWarOfMineModLoader.Tests
dotnet run --project ThisWarOfMineModLoader.Cli -- plan examples
```

`plan` 检查清单、依赖、版本、冲突及循环，输出稳定的加载顺序。发现错误时返回非零退出码，并且不产生部分加载计划。

## 模组规范 v1

每个模组独立文件夹，包含 `mod.json` 和 `main.lua`，参考 `examples/hello`。

- ID 使用小写字母、数字及 `. _ -`，必须以字母开头且全局唯一。
- 版本使用 `major.minor.patch` 三段非负整数，暂不接受预发布或版本范围。
- `dependencies` 是依赖 ID 到最低版本的映射；禁用依赖视为缺失。
- `conflicts` 列出不能同时启用的模组 ID。
- `enabled` 控制是否纳入加载计划，禁用模组仍检查清单格式。
- `entry` 必须指向模组目录内真实存在的 Lua 文件，拒绝目录逃逸和入口符号链接。
- 扫描模组根目录的直接子目录；没有 `mod.json` 的目录跳过。

项目只提交自有代码、文档和示例，不提交游戏二进制或解包资源。将本机游戏、实验数据放入忽略的 `local/`。

## 推进顺序

1. 模组清单、依赖与冲突规划、示例及测试。
2. 容器只读检查、Lua 引导编译及离线资源输出。
3. 检查实际游戏版本，确认 Lua 加载入口，在游戏内验证首个模组。
4. 原生文件系统拦截，做到不覆盖游戏文件的运行时挂载。
5. 逐步提供角色、物品、事件、场景和模型接口。

第 3 步之前的工具成功运行不代表游戏内加载成功。
