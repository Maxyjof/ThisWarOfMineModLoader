# MaxyModLoader额外能力总览

MaxyModLoader同时提供离线原生内容构建与游戏内Lua运行时API。官方内容工具负责游戏原生数据结构；加载器API为模组提供运行时协作与诊断入口。两层能力不能互相替代。

## 与基础官方内容模组相比

| 能力 | 官方原生模组内容 | MaxyModLoader当前扩展 |
| --- | --- | --- |
| 物品与配方 | 可使用游戏原生数据结构 | 可以从已核验模板声明新物品和配方，并为新物品提供原创DDS图标 |
| 搜刮与交易 | 按原生配置定义 | 可以为已存在的搜刮生成器和商人货单追加模组物品 |
| 运行时代码 | 依赖游戏原生Lua脚本入口 | Lua入口可包装已存在的表函数、订阅事件并使用模块化上下文 |
| 游戏生命周期 | 原生脚本按固定回调运行 | 经本机脚本核验的场景、昼夜、搜刮状态保存、广播和建造回调可发布事件；可按游戏日安排一次或周期任务 |
| 游戏对象API | 由游戏原生Lua绑定提供 | `context.game`封装阶段、场景、角色参数、物品配置、库存查询、角色物资增减和剧情事件广播 |
| 多模组协作 | 原生文件通常独立构建 | 可用清单依赖、命名服务和带类型约束的共享规则协作 |
| 持久状态 | 通常围绕游戏原生存档内容 | 有按模组隔离的持久标量存储，不修改原版存档 |
| AI与自动化 | 没有加载器通用工具入口 | 可注册有参数模式的MCP动作，供本机可信AI客户端调用 |
| 诊断 | 以原生游戏行为为主 | 可输出模组日志、共享规则快照和MCP工具状态 |

清单静态配置`settings`会注入Lua上下文，但没有通用的游戏内配置编辑器或跨启动保存功能；需要玩家可调整的持久值时，应设计规则默认值，并把选择保存在`context.storage`，或等待未来设置UI能力。

`context.game`面向已核验且适合稳定封装的游戏操作；它不是对Liquid Engine全部C++对象和Lua全局的自动反射。游戏原有Lua绑定仍可由模组直接调用，领域门面负责参数校验和缩小返回对象。已核验的`dweller:AddItems`会增加全局可用物资，模组可用`context.game.characters.get(index).add_item(name,amount)`执行，并通过`context.game.inventory.global_count(name)`查看数量；`GetShelterItemCount`是另一项只读查询。参见[领域API参考](ModdingAPI.md#原生游戏领域api)和[游戏对象API示例](../../examples/game-domain-api)。

## 玩法修改方式

额外运行时玩法通常由适配器完成：在入口中核验目标Lua函数存在，通过`context.wrap`保留原函数行为，再读取配置、规则或持久数据，并应用本机实测确认的参数变化。其他模组可依赖适配器提供的服务或规则。

例如，修改已验证的搜刮耗时可以包装游戏里确实存在的动作函数，并把倍率声明为受限规则。规则本身只管理配置值；只有适配器把该值写入真实游戏对象时，游戏玩法才会改变。不存在已核验回调时，不能把规则注册成功描述成效果生效。

游戏事件同理。加载器内置事件只有`loader.ready`。本项目的游戏事件桥在已核验的`KosovoScene`函数周围发布场景初始化、昼夜、搜刮、广播和建造事件；它只能覆盖经过这些函数的调用路径，这不是完整的游戏事件系统。`context.schedule`以桥发布的`game.day.begin`累计游戏日，不使用现实时间计时，也不是后台线程服务。

## 当前API边界

下列功能当前没有通用框架支持：

- 新地图布局、场景生成、任意建筑放置
- 独立角色骨骼、动画、全新3D模型或运行时模型导入
- 任意Lua全局、原生对象和引擎函数的自动发现或安全反射
- 通用游戏内UI面板注册与热重载
- 通用游戏存档钩子、存档数据改写和角色状态回滚
- 后台线程、现实时间定时器和任意系统文件访问
- 武器弹药类型的真实消耗绑定，除非已找到并实测对应原生字段

新物品会克隆本机已核验的原生模板以复用模型、动画和行为，并通过模组自带原创DDS图标区别外观。当前`native-content.json`不能导入Blender模型或自定义地图。

`game.scavenge.saving`与`game.scavenge.saved`只对应离开搜刮场景时的`KosovoScene:OnSaveScavengeState()`调用。它不是全局存档钩子，不暴露存档字节，也不能观察其他保存路径。

## 示例和验证

- [`examples/hello`](../../examples/hello)：最小Lua入口
- [`examples/diary-observer`](../../examples/diary-observer)：观察已存在游戏Lua函数的有限覆盖示例
- [`examples/rule-api`](../../examples/rule-api)：跨模组规则提供与调整
- [`examples/persistent-storage`](../../examples/persistent-storage)：跨启动键值数据
- [`examples/mcp-tools`](../../examples/mcp-tools)：MCP结构化工具
- [`examples/day-scheduler`](../../examples/day-scheduler)：按游戏日安排一次性与周期任务
- [`examples/game-domain-api`](../../examples/game-domain-api)：访问经核验的场景、角色、物品和库存API
- [`mods/more-guns`](../../mods/more-guns)、[`mods/field-equipment`](../../mods/field-equipment)：原生物品数据示例
- [`playtests/mods/starter-armory`](../../playtests/mods/starter-armory)：依赖事件适配器与原生物品模组的综合实测包

构建成功只表明源码可编译，不等于游戏内玩法已验证。记录功能实测时应包含具体游戏版本、场景、输入和观察结果。
