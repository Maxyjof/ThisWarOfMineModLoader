# 原生内容模组

原生内容模组在`mod.json`中填写`nativeContentFile`，指向模组目录内的`native-content.json`。构建器只读取声明，再从匹配指纹的本机游戏目录和官方`ModTools.exe`导出模板并编译差异包，不把游戏文件复制进模组或Git

## 物品

`items`数组中的每项提供全局唯一的`MML_`前缀`id`、本机模板`baseItem`、中文`name`与`description`、原创图标路径`iconTextureName`、图集编号`iconIndex`、标量属性、制作配方和可选`damageMultiplier`。`iconTextureName`只能指向`UI/MaxyModLoader/Items`中的DDS资源；`iconIndex`范围为0至63，对应1024×1024图集里的8×8格。图标文件放在模组目录的`resources`子目录下，例如`resources/UI/MaxyModLoader/Items/Example.dds`。缺少图标或路径指向原版目录时构建失败，不会回退到原版物品图标。`englishName`与`englishDescription`可选；未填写时英文语言回退为中文。标量属性只允许写入模板中已有的直接属性，字符串值使用不变区域格式的数字文本

`recipes`中的`device`和`ingredients`必须对应已导出的工作台和物品标识。每个配方指定制作小时数、材料数量及结果件数。继承配方会被清除，防止新物品意外继承原版损坏件修理配方

## 地图掉落

`loot`数组中的每项要指定原版`LootGenerator`名称、同一构建计划中的模组物品ID和`minimum`、`maximum`数量。名称必须与当前版本导出的配置完全相同，位置不会通过模糊名称推测。最小数量设为零表示不保证每次出现

## 商人货单

`trading`数组中的每项指定原版商人名称、同一构建计划中的模组物品ID、`probability`、`valueMultiplier`和单次数量范围。当前只追加到原版出售货单，不创建新商人或改写原版接收物品、每日预算及刷新逻辑

构建计划中的模组可以由一个模组定义物品，再由依赖方添加该物品的商店货单。缺少依赖、物品、工作台、地图生成器或商人时构建失败

## 安装与兼容

原生差异容器必须直接放在游戏`Mods`根目录，并使用`MaxyModLoaderNative_common.dat`、`MaxyModLoaderNative_common.idx`等以模组ID加下划线为前缀的文件名。`Mods.list`首字段填写相同的`MaxyModLoaderNative`本地模组ID；把容器放进同名子目录或省略文件名前缀，虽然模组管理器可能显示已启用，游戏物品注册表仍不会加载新增物品。安装日志记录原始`Mods.list`和新增容器指纹。恢复前会检查玩家列表与文件是否被其他程序改动，出现冲突时停止而不覆盖

构建仅接受验证记录指定的Steam游戏及官方工具指纹。当前物品通过克隆适配的原版模板获得模型、动画、行为和类别参数，再使用模组清单中明确指定的原创DDS图集覆盖背包图标。本格式不导入Blender场景或自定义模型。新增弹药可以制作和交易，但没有经验证的字段将它们与特定枪械消耗绑定。原生射击、背包图标、地图搜刮分布与商人实物库存仍须在相应玩法中继续实测
