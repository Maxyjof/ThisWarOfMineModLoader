# 疲劳与搜刮战术

这个模组在每次搜刮行动开始时读取实际出发角色的疲劳，再修改这一次行动的耗时。它包装游戏已有的`ScavengeAction.OnBegin`并继续调用原版函数，因此每次任务都根据当前角色状态重新计算；原生ModTools只能编辑静态搜刮配置，不能按出发角色即时切换耗时。

## 战术规则

| 模式 | 效果 |
| --- | --- |
| `balanced` | 疲劳不高于30时耗时乘以0.9；疲劳达到70时乘以1.2；其他情况保持原值 |
| `careful` | 所有搜刮耗时乘以1.2 |
| `urgent` | 所有搜刮耗时乘以0.75 |

本模组只改变`ScavengeAction.Duration`，不会改变战斗风险、敌人或原版静态配置。时长最低为0.25小时。加载器日志会记录命中的模式、角色疲劳和调整前后的时长。

`fatigue_mode`是加载器共享规则，可由直接依赖本模组的模组通过`context.rules.set("twom.play.scavenger-tactics", "fatigue_mode", "urgent")`调整。它也由“营地参谋MCP”模组提供给本机可信AI客户端。

## 依赖与验证范围

依赖`MaxyModLoader游戏事件适配器`，并要求Steam BuildID22193501与MaxyModLoader Modding API 1.2.0。当前自动测试验证包装器保留原函数、按疲劳和规则选取倍率、拒绝无效耗时；新包的实机搜刮耗时需要玩家进游戏验证。
