import json
import pathlib

import bpy
from mathutils import Vector


def material(name, color, metallic=0.0):
    """
    <summary>
    创建箱体使用的纯色材质
    </summary>
    """
    #使用独立材质名称避免修改用户已有的材质
    result = bpy.data.materials.new(name)
    result.diffuse_color = (*color, 1.0)
    result.use_nodes = True
    #节点名称会随界面语言变化使用稳定类型查找着色节点
    shader = next(node for node in result.node_tree.nodes if node.type == "BSDF_PRINCIPLED")
    shader.inputs["Base Color"].default_value = (*color, 1.0)
    shader.inputs["Roughness"].default_value = 0.85
    shader.inputs["Metallic"].default_value = metallic
    return result


def box(scene, name, center, size, surface):
    """
    <summary>
    用八个顶点生成可独立导出的箱体部件
    </summary>
    """
    #直接创建网格数据避免依赖用户当前选择或编辑模式
    vertices = [(center[0] + x * size[0] / 2, center[1] + y * size[1] / 2,
                 center[2] + z * size[2] / 2)
                for x, y, z in [(-1, -1, -1), (-1, -1, 1), (-1, 1, -1), (-1, 1, 1),
                                (1, -1, -1), (1, -1, 1), (1, 1, -1), (1, 1, 1)]]
    faces = [(0, 2, 6, 4), (1, 5, 7, 3), (0, 4, 5, 1),
             (2, 3, 7, 6), (0, 1, 3, 2), (4, 6, 7, 5)]
    mesh = bpy.data.meshes.new(name)
    mesh.from_pydata(vertices, [], faces)
    mesh.update()
    result = bpy.data.objects.new(name, mesh)
    scene.collection.objects.link(result)
    result.data.materials.append(surface)
    return result


def create(output_directory):
    """
    <summary>
    创建低面数补给箱并导出建模探针及预览
    </summary>
    """
    #拒绝覆盖已有结果允许重复执行时选择不同输出目录
    output = pathlib.Path(output_directory).resolve()
    output.mkdir(parents=True, exist_ok=False)
    scene = bpy.data.scenes.new("TWOM_Model_Probe")
    scene.unit_settings.system = "METRIC"
    wood = material("TWOM_Probe_Wood", (0.22, 0.14, 0.065))
    metal = material("TWOM_Probe_Steel", (0.08, 0.085, 0.08), 0.65)
    pale = material("TWOM_Probe_Mark", (0.65, 0.59, 0.43))

    #分块木板和金属边框保持约一米宽的静态物件尺度
    for index in range(5):
        x = -0.4 + index * 0.2
        box(scene, "底板" + str(index), (x, 0, 0.04), (0.19, 0.7, 0.08), wood)
        box(scene, "盖板" + str(index), (x, 0, 0.66), (0.19, 0.7, 0.08), wood)
    for side in (-1, 1):
        for index in range(4):
            z = 0.14 + index * 0.15
            box(scene, "长侧板", (0, side * 0.32, z), (1, 0.065, 0.14), wood)
            box(scene, "短侧板", (side * 0.465, 0, z), (0.065, 0.58, 0.14), wood)
        for x in (-0.46, 0.46):
            box(scene, "边框", (x, side * 0.355, 0.35), (0.065, 0.035, 0.7), metal)
    box(scene, "补给标记横", (0, -0.357, 0.35), (0.3, 0.014, 0.075), pale)
    box(scene, "补给标记竖", (0, -0.357, 0.35), (0.075, 0.014, 0.3), pale)
    meshes = list(scene.objects)

    #相机和光源仅用于预览不包含在模型导出中
    camera = bpy.data.objects.new("ProbeCamera", bpy.data.cameras.new("ProbeCamera"))
    scene.collection.objects.link(camera)
    camera.location = (1.8, -2.5, 1.7)
    camera.rotation_euler = (Vector((0, 0, 0.33)) - camera.location).to_track_quat("-Z", "Y").to_euler()
    camera.data.type = "ORTHO"
    camera.data.ortho_scale = 1.65
    scene.camera = camera
    light = bpy.data.objects.new("ProbeLight", bpy.data.lights.new("ProbeLight", "AREA"))
    scene.collection.objects.link(light)
    light.location = (0.5, -1.5, 3)
    light.rotation_euler = (Vector((0, 0, 0.3)) - light.location).to_track_quat("-Z", "Y").to_euler()
    light.data.energy = 450
    light.data.shape = "DISK"
    light.data.size = 3
    scene.world = bpy.data.worlds.new("ProbeWorld")
    scene.world.color = (0.13, 0.13, 0.13)

    #临时切换到独立场景执行导出完成后恢复用户场景
    window = bpy.context.window
    original = window.scene
    try:
        window.scene = scene
        for item in meshes:
            item.select_set(True)
        bpy.context.view_layer.objects.active = meshes[0]
        bpy.ops.export_scene.gltf(filepath=str(output / "supply-crate.glb"), export_format="GLB",
                                  use_selection=True, use_active_scene=True,
                                  export_animations=False, export_apply=True)
        #只保存新建场景及其依赖不会把用户其他场景写入实验文件
        bpy.data.libraries.write(str(output / "supply-crate.blend"), {scene})
        #不同版本的渲染器标识不同通过运行时枚举选择可用的实时渲染器
        engines = {entry.identifier for entry in scene.render.bl_rna.properties["engine"].enum_items}
        scene.render.engine = "BLENDER_EEVEE" if "BLENDER_EEVEE" in engines else "BLENDER_EEVEE_NEXT"
        scene.render.resolution_x = 640
        scene.render.resolution_y = 640
        scene.render.resolution_percentage = 100
        scene.render.image_settings.file_format = "PNG"
        scene.render.film_transparent = True
        scene.render.filepath = str(output / "supply-crate.png")
        bpy.ops.render.render(write_still=True, scene=scene.name)
    finally:
        window.scene = original

    #明确记录这是通用模型探针不能把成功导出当作游戏导入成功
    report = {"blender": bpy.app.version_string, "mesh_parts": len(meshes),
              "triangles": sum(len(item.data.polygons) * 2 for item in meshes),
              "game_import_verified": False, "format": "glTF2.0"}
    (output / "report.json").write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps(report, ensure_ascii=False))
