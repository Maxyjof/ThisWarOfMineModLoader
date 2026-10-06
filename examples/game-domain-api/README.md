# 游戏对象API示例

本示例演示如何在`game.day.begin`事件中读取玩法阶段、场景天数、幸存者数量和角色疲劳值。它只记录日志，不改变角色状态或库存。

`context.game.inventory.global_count(name)`用于检查全局可用物资；`context.game.inventory.shelter_count(name)`调用独立的原生庇护所计数查询。角色API另提供受校验的参数修改、通过`dweller:AddItems`增加全局可用物资、消耗物资与工具可用性查询。

共享物资通过已核验的`dweller:AddItems`原生绑定增加，并可用全局物资数确认；单独的`GetShelterItemCount`查询不用于核验共享物资。游戏对象门面只封装本项目已核验的原生Lua绑定，并不覆盖Liquid Engine的全部底层接口。

本示例通过Lua5.1模拟运行时契约测试，尚未在游戏内验证具体回调路径。完整接口和边界见[运行时API参考](../../Docs/Developers/ModdingAPI.md#原生游戏领域api)。
