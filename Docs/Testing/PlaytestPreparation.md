# 实机测试准备

`tools/playtest/prepare-playtest.ps1`根据仓库内的测试模组构建隔离部署包，用于可重复的游戏实测。`baseline`和`combined`模式只在仓库忽略目录`artifacts/playtests`中创建输入和输出。`content`模式还会调用已核验的`ModTools.exe`，在游戏目录`Mods`下创建随机临时编译目录并在正常结束后清理；工具超时会保留目录，避免删除仍在写入的文件。所有模式都不会替换游戏资源容器或操作存档。

## 准备基线包

基线包只包括MaxyModLoader游戏事件适配器，用于观察已核验的原版游戏回调：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/playtest/prepare-playtest.ps1 -GameDirectory "D:\Steam\steamapps\common\This War of Mine" -Mode baseline
```

## 准备组合测试包

组合包包含事件适配器、四个原生内容模组和开局物资实测包，用于验证正式内容之间的依赖与组合：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/playtest/prepare-playtest.ps1 -GameDirectory "D:\Steam\steamapps\common\This War of Mine" -Mode combined
```

运行脚本需要开发机已安装.NET10SDK；完成后脚本输出生成的部署包目录。运行游戏和检验具体玩法仍须按[实测矩阵](ModTestMatrix.md)记录实际状态，不要把部署包构建成功当作游戏内功能已通过。

## 准备内容模组测试包

内容包组合枪械、弹药、野战装备与交易模组，并附带事件适配器：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/playtest/prepare-playtest.ps1 -GameDirectory "D:\Steam\steamapps\common\This War of Mine" -Mode content
```

此模式需要本机安装目录内版本匹配的官方ModTools。构建器会生成原生物品差异包，但是否能自然搜刮、制作、交易、穿戴或开火仍须在游戏中逐项验证。

## 开局物资实测包

玩家示例发行包还包含`开局物资实测包.zip`。将它与事件桥、更多枪械、弹药补给、野战工具与防护装备、军火交易扩展一同放入`Mods`并启用，然后从新存档开始。首次庇护所日开始事件会向共享物资库存发放50种新增物品各1件，以及四种原版弹药各30发。该包使用已核验的原生物品接口，并通过全局库存计数确认写入目标；修复入口上下文传递后，需在游戏内“我们的物品”界面核对结果。同一加载器安装只发放一次；需重新测试时，在游戏退出后删除该模组对应的`.0.dat`和`.1.dat`存储文件。后续实际获取、射击、耐久、装备效果和商人行为需玩家逐项测试并记录。
