# 构建和发行

开发机需要.NET10SDK。玩家包使用Windows x64自包含单文件程序，玩家无需安装.NET。

## 构建

```powershell
dotnet build -c Release
```

本仓库不维护自动化测试套件。构建成功只说明源码可编译，不代表游戏内玩法已经验证；新增物品获取、制作、使用和禁用恢复仍须在目标游戏版本中实际检查。

正常启动会复用已部署容器。完整恢复和安装仍校验SHA256；运行时的`container-integrity.json`只缓存已核验文件的长度与时间信息，文件元数据改变时重新计算SHA256。它是可删除的派生缓存，不属于部署身份或恢复数据。

## 生成玩家包

使用尚未发布的新版本号：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/package-release.ps1 -Version "<主版本.次版本.修订号>"
```

输出位于忽略的`artifacts/release`，包含加载器ZIP、只收录正式内容的`Mods`模组总ZIP及SHA256SUMS。模组包包含枪械、弹药、野战装备、交易、开局物资实测包和必要的游戏事件适配器，不收录玩法演示或故意失败探针。不要提交EXE、DLL、解包资源、日志、本机配置或发行ZIP。安装脚本和安装说明来自当前源码；说明路径变动时同步调整打包脚本。

手动安装构建时，先按[安装指南](../Players/Installation.md)完整卸载非当前契约，再将发布目录传给`tools/install-loader.ps1`的`PublishDirectory`和`BootstrapDirectory`参数。不要编写旧记录迁移或放宽指纹来调试。

## GitHub工作流

常规推送工作流构建解决方案，版本标签工作流构建并发布Release。完成实际验证、同步代码版本和CHANGELOG后，再创建未使用的`v主版本.次版本.修订号`标签并推送，不能覆盖已有标签或发布资产。

Release描述说明支持的SteamBuildID、安装步骤、可下载示例、已验证范围和已知限制。尚未完成实测的内容包不包装成“全部可用”。每个阶段使用中文Git标题和正文，并推送便于回退。
