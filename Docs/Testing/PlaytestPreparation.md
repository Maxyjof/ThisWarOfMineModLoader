# 实机测试准备

`tools/playtest/prepare-playtest.ps1`根据仓库内的测试模组构建隔离部署包，用于可重复的游戏实测。`baseline`和`combined`模式只在仓库忽略目录`artifacts/playtests`中创建输入和输出。`content`模式还会调用已核验的`ModTools.exe`，在游戏目录`Mods`下创建随机临时编译目录并在正常结束后清理；工具超时会保留目录，避免删除仍在写入的文件。所有模式都不会替换游戏资源容器或操作存档。

## 准备基线包

基线包只包括游戏事件桥、记录模组和MCP诊断控制台，适合观察原版游戏行为：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/playtest/prepare-playtest.ps1 -GameDirectory "D:\Steam\steamapps\common\This War of Mine" -Mode baseline
```

## 准备组合测试包

组合包加入玩法调整、营地辅助和故意失败的模组，用于检查依赖失败隔离及功能组合：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/playtest/prepare-playtest.ps1 -GameDirectory "D:\Steam\steamapps\common\This War of Mine" -Mode combined
```

此包含有故意失败的测试入口，不能当作日常游玩的示例包。运行脚本需要开发机已安装.NET10SDK；完成后脚本输出生成的部署包目录。运行游戏和检验具体玩法仍须按[实测矩阵](ModTestMatrix.md)记录实际状态，不要把部署包构建成功当作游戏内功能已通过。

## 准备内容模组测试包

内容包组合枪械、弹药、野战装备与交易模组，并附带事件桥和生存玩法辅助模组：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/playtest/prepare-playtest.ps1 -GameDirectory "D:\Steam\steamapps\common\This War of Mine" -Mode content
```

此模式需要本机安装目录内版本匹配的官方ModTools。构建器会生成原生物品差异包，但是否能自然搜刮、制作、交易、穿戴或开火仍须在游戏中逐项验证。
