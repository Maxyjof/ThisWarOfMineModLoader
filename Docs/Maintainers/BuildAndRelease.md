# 构建和发行

开发机需要.NET10SDK、Python3.12及`tests/requirements.txt`中的Lua5.1测试依赖。玩家包使用Windows x64自包含单文件程序，玩家无需安装.NET。

## 回归检查

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

离线测试通过后，继续执行[游戏实测矩阵](../Testing/ModTestMatrix.md)，特别是新增物品实际获取、制作、使用及禁用恢复。不要将虚拟Lua对象测试当作实际游戏证据。

## 生成玩家包

使用尚未发布的新版本号：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/package-release.ps1 -Version "<主版本.次版本.修订号>"
```

输出位于忽略的`artifacts/release`，包含加载器ZIP、示例模组总ZIP及SHA256SUMS。不要提交EXE、DLL、解包资源、日志、本机配置或发行ZIP。安装脚本和安装说明来自当前源码；说明路径变动时同步调整打包脚本。

手动安装构建时，先按[安装指南](../Players/Installation.md)完整卸载非当前契约，再将发布目录传给`tools/install-loader.ps1`的`PublishDirectory`和`BootstrapDirectory`参数。不要编写旧记录迁移或放宽指纹来调试。

## GitHub工作流

常规推送工作流运行回归，版本标签工作流额外构建并发布Release。完成实际验证、同步代码版本和CHANGELOG后，再创建未使用的`v主版本.次版本.修订号`标签并推送，不能覆盖已有标签或发布资产。

Release描述说明支持的SteamBuildID、安装步骤、可下载示例、已验证范围和已知限制。尚未完成实测的内容包不包装成“全部可用”。每个阶段使用中文Git标题和正文，并推送便于回退。
