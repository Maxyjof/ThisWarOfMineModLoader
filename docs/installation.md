# 玩家安装指南

## 运行条件

- Windows64位和Steam版《这是我的战争》
- 当前支持已核验的SteamBuildID22193501
- 安装器会校验游戏目录及原版启动文件，不支持的版本会停止安装

## 安装加载器

1. 在Steam游戏属性中选择“已安装文件”再点“浏览”，打开游戏目录
2. 从GitHubRelease下载`MaxyModLoader-版本号-Windows-x64.zip`并解压到临时位置
3. 退出游戏，在解压目录中打开PowerShell
4. 按实际游戏目录运行安装脚本

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File ".\install-loader.ps1" -GameDirectory "D:\Steam\steamapps\common\This War of Mine"
   ```

5. 安装成功后直接从Steam启动游戏。加载器会自动处理模组并启动原版游戏

安装包自带.NET运行时。安装器把两个自包含程序放到`游戏目录\MaxyModLoader\app`，创建`游戏目录\Mods`并安装图形界面启动入口。不要删除`游戏目录\x64\MaxyModLoader.Original.exe`；游戏入口被加载器接管时，Steam通过此文件启动原版游戏。

## 安装示例模组

1. 从相同Release下载`MaxyModLoader-版本号-SampleMods.zip`
2. 解压后将需要的模组ZIP复制到`游戏目录\Mods`
3. 保持模组ZIP不解压，确保每个模组ZIP内根目录有`mod.json`
4. 从Steam启动，在主菜单打开“模组管理”并查看依赖和模组介绍
5. 启用需要的模组，点击“重启并应用”

示例玩法模组需同时启用“游戏事件与测试记录桥”。模组清单、依赖和冲突会在加载前校验；有错误时加载器会停止部署并显示原因。玩法模组会修改游戏平衡，开始前请备份存档。

## 更新和卸载

安装新版时先退出游戏，再解压新版本并用相同安装命令覆盖安装。Steam验证游戏文件会恢复游戏自启动入口；若要移除加载器，先退出游戏并执行Steam文件验证，然后删除`MaxyModLoader`、`x64\MaxyModLoader.Original.exe`以及不再需要的`Mods`文件夹。模组ZIP可先单独复制留存。

若游戏异常关闭，下一次Steam启动会先尝试恢复会话期间修改的原版资源。会话恢复备份在恢复成功后自动删除。也可从PowerShell显式恢复：

```powershell
& "D:\Steam\steamapps\common\This War of Mine\MaxyModLoader\app\MaxyModLoader.exe" restore "D:\Steam\steamapps\common\This War of Mine"
```

Steam验证会重装受Steam管理的原版文件，但不会清除`Mods`或加载器目录，也不会处理其他应用创建的存档备份。
