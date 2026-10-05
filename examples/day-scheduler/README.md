# 游戏日任务示例

本示例通过`context.schedule`按事件而非现实时间触发任务。运行时应由本项目的`twom.play.bridge`或其他已验证的适配器广播`game.day.begin`。

## 一次性任务

`context.schedule.after_days(2, callback)`在入口加载后的第二次新一天回调中执行。回调接收`scene`与`was_scavenging`参数。

## 周期任务

`context.schedule.every_days(7, callback)`每七次新一天回调执行一次，并返回取消函数。示例监听自定义事件`example.scheduler.stop`来停止周期任务。

这些任务只在游戏运行且昼夜回调触发时推进，不会在菜单、暂停状态或游戏关闭期间计时，也不会自动跨重启保存。事件桥的覆盖范围限于已核验的原生Lua场景函数。
