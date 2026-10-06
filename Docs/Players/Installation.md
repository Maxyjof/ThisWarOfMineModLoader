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

安装包自带.NET运行时。安装器把两个自包含程序放到`游戏目录\MaxyModLoader\app`，创建`游戏目录\Mods`并安装图形界面启动入口。不要删除`游戏目录\x64\MaxyModLoader.Original.exe`；游戏入口被加载器接管时，Steam通过此文件启动原版游戏。首次部署后，模组资源和原版恢复点会一直保留在游戏目录，下次启动只做指纹核验并直接复用，不会每次重新构建。

## 安装示例模组

1. 从相同Release下载`MaxyModLoader-版本号-Mods.zip`
2. 解压后将需要的模组ZIP复制到`游戏目录\Mods`，或者把单个模组ZIP解压到`游戏目录\Mods\模组文件夹`
3. 确保ZIP根目录或解压文件夹根目录有`mod.json`和入口Lua文件；每个ZIP或文件夹只能放一个模组
4. 从Steam启动，在主菜单打开“模组管理”并查看依赖和模组介绍
5. 启用需要的模组，点击“重启并应用”

军火交易扩展依赖“更多枪械模组”；开局物资实测包依赖游戏事件适配器和内容模组。内容模组压缩包内含所需的原创DDS图标，按原样放入Mods目录即可；不需要另外复制贴图。模组清单、依赖和冲突会在加载前校验；有错误时加载器会停止部署并显示原因。内容模组会改变物品、配方和交易，开始前建议使用新存档。

## 更新和卸载

项目仍在内部开发，只维护当前安装格式，不迁移旧版本的目录、配置或日志。更新前先退出游戏，执行Steam文件验证，再删除游戏中的`MaxyModLoader`和`x64\MaxyModLoader.Original.exe`，然后按上面的步骤安装当前发行包。不要将旧加载器配置或缓存复制回来；模组也应使用当前规范的发行包。

若要彻底卸载，在上述步骤基础上删除不再需要的`Mods`文件夹。更新加载器时可以保留需要的模组ZIP，但须确认它们符合当前规范。

模组变更时，加载器会使用`MaxyModLoader\backups`中的原版恢复点重建部署；恢复点由`install-state.json`管理。若要主动卸载模组并恢复游戏原版资源，可从PowerShell显式恢复：

```powershell
& "D:\Steam\steamapps\common\This War of Mine\MaxyModLoader\app\MaxyModLoader.exe" restore "D:\Steam\steamapps\common\This War of Mine"
```

Steam验证会重装受Steam管理的原版文件，但不会清除`Mods`或加载器目录，也不会处理其他应用创建的存档备份。
