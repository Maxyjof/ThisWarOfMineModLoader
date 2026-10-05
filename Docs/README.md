# 文档导航

文档与仓库当前源码同步；下载发行包的玩家应阅读该Release附带的安装说明。内部开发只维护当前契约，不迁移旧版API、清单、配置和安装记录。

## 玩家

- [安装、更新和卸载](Players/Installation.md)：运行条件、安装脚本、目录布局、存档注意事项
- [模组使用](Players/Usage.md)：ZIP放置、依赖、启用禁用、重启应用、窗口模式
- [故障排查](Players/Troubleshooting.md)：启动失败、版本核验、恢复与诊断

## 开发者

- [制作第一个模组](Developers/GettingStarted.md)：Lua入口、打包与实测流程
- [能力总览](Developers/CapabilityOverview.md)：官方原生内容之外的运行时扩展和当前限制
- [清单字段](Developers/Manifest.md)：身份、依赖、模块、配置、Markdown和能力
- [运行时API参考](Developers/ModdingAPI.md)：事件、服务、函数包装、共享规则、持久存储和MCP动作
- [原生内容](Developers/NativeContent.md)：物品、配方、掉落和交易声明
- [内置MCP](Developers/MCP.md)：客户端配置、工具、测试方式及权限边界
- [可运行示例](../examples)：入口、游戏函数观察、共享规则、持久数据和MCP动作

## 维护与测试

- [构建和发行](Maintainers/BuildAndRelease.md)：回归、玩家包与GitHub工作流
- [游戏实测矩阵](Testing/ModTestMatrix.md)：当前模组的实际验证结果和未完成项
- [历史验证记录](Testing/ValidationHistory.md)：先前阶段的证据；不能替代当前构建的测试
- [实机测试准备](Testing/PlaytestPreparation.md)：构建隔离的基线与组合测试部署包
