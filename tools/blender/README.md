# 建模探针

`create_crate.py`通过Blender的`bpy`接口生成独立场景中的低面数补给箱

在已经连接的MCP插件中执行脚本，然后调用`create(绝对输出目录)`，输出目录必须尚不存在。也可以在Blender的脚本工作区执行，按实际路径调用该方法

脚本通过节点类型识别材质，避免中文节点名称导致查找失败，通过实时枚举兼容渲染器名称。导出GLB时仅包含新建的当前场景，BLEND使用数据块写出，仅保存新建场景及其依赖，结束后恢复用户原场景

输出包含`供给箱`的GLB、BLEND、PNG预览和JSON报告，实际文件名为`supply-crate`。报告明确记录`game_import_verified: false`

2026年10月4日已通过本机MCP for Blender1.8连接Blender5.2.2 LTS完成生成和渲染，导出GLB为32个网格部件、384个三角面、3个材质，验证未混入用户默认立方体。模型输出保存在Git忽略的`artifacts/model-probe/`，源码可用于重建

游戏安装附带的`MetaData.xml`包含`MeshTemplate`，其中`Imported from file`为只读属性，附带的模组说明没有模型转换步骤。当前尚未实现Liquid Engine模型编译或加载入口。GLB成功导出不代表游戏支持该格式，这个资产目前是导入研究探针，不作为已经实装的模型模组

导出参数依据[Blender官方glTF导出接口](https://docs.blender.org/api/main/bpy.ops.export_scene.html)，实际参数和渲染器选择以本机Blender运行结果为准
