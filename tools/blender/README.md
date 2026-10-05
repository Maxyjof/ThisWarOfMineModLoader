# Blender建模探针

`create_crate.py`通过Blender的`bpy`接口生成独立场景中的低面数补给箱。生成的Blender工程和交付文件保存在本目录的`models/`，不是只留在忽略目录中的临时文件：

- [supply-crate.blend](models/supply-crate.blend)：可继续编辑的Blender工程
- [supply-crate.glb](models/supply-crate.glb)：通用glTF2.0交换文件
- [supply-crate.png](models/supply-crate.png)：透明背景预览
- [report.json](models/report.json)：面数、材质和验证状态

2026年10月4日通过MCP for Blender1.8连接Blender5.2.2 LTS完成生成和渲染。模型含32个网格部件、384个三角面和3个材质，未混入用户默认立方体。重新生成时，在Blender脚本工作区运行`create_crate.py`并调用`create(绝对输出目录)`；输出目录必须尚不存在。

脚本通过节点类型识别材质，按当前Blender版本枚举实时渲染器。导出GLB时仅包含新建场景，BLEND仅保存新建场景及其依赖，结束后恢复原场景。

游戏安装附带的`MetaData.xml`包含`MeshTemplate`，其中`Imported from file`为只读属性；随游戏的模组说明没有提供外部模型转换步骤。Liquid Engine模型编译和游戏加载入口尚未验证，因此这个模型是可编辑的建模交付物和格式研究探针，不能作为已实装的游戏模型模组。

导出参数参考[Blender官方glTF导出接口](https://docs.blender.org/api/main/bpy.ops.export_scene.html)，实际参数和渲染器以本机Blender运行结果为准。
