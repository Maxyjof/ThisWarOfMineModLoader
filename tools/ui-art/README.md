# 模组管理界面炭笔素材

左侧列表使用`brush-list.png`，右侧介绍使用`brush-detail.png`。两张素材通过内置ImageGen工具生成，背景保留透明度，中央保留深灰纸张颗粒和炭笔纹理。它们属于项目自制素材，不包含游戏解包图片。

`resources/UI/MaxyModLoader/BrushList.dds`和`BrushDetail.dds`是对应的无损BGRA32位源纹理。核心程序集嵌入这两张素材，普通`build`命令自动携带，不要求使用者另行指定素材目录。

## 游戏资源转换

原版`UIPicture:SetTexture`会将`.dds`路径转换成`.texture`路径，从纹理挂载点读取。直接把DDS字节或纹理放入`common`脚本容器，实际截图显示为纯黑矩形。

`LiquidTexture.FromDds`只接受单层无损BGRA32位DDS，将其转换为本机原版已核验的封装：144字节头部、格式21、单层、十六项层级表、原始BGRA像素。透明度不丢弃，不改变图片设计。

构建器保留全部原版纹理条目，将两张素材追加到`textures-s3`容器。当前部署包必须同时包含脚本和纹理容器。安装器预先校验两个容器，全部备份后再替换，恢复时同样先核验全部指纹。

## 重建源纹理

需要Python和Pillow，编码脚本不重绘或裁剪素材：

```powershell
python tools/ui-art/build_brush_texture.py tools/ui-art/brush-list.png tools/ui-art/brush-list.png tools/ui-art/resources/UI/MaxyModLoader/BrushList.dds
python tools/ui-art/build_brush_texture.py tools/ui-art/brush-detail.png tools/ui-art/brush-detail.png tools/ui-art/resources/UI/MaxyModLoader/BrushDetail.dds
```

## 生成提示词

使用内置ImageGen编辑工具，以此前生成的长条炭笔板为风格参考，分别生成两张适合面板比例的图片

列表素材提示词：

> Use case: style-transfer. Edit target: attached long charcoal brush panel asset. Create a production game UI background for This War of Mine mod manager LEFT scrollable list. Change the shape to a near-square slightly portrait panel, aspect ratio 5:6, filling 94% of canvas with only small transparent border. Retain hand-drawn war journal charcoal-on-paper aesthetic, subtle dark grey graphite grain, uneven dry-brush edges. Central 85% must be dark charcoal (#171918 approximately) and nearly opaque, calm, readable for white text. No pure black flat rectangle. No text, no symbols, no buttons, no bevels, no glow, no white background. Outer few pixels have organic softly feathered dry charcoal transparency; avoid protruding spikes. Keep visibly subtle paper grain through the entire center, edges gently lighter smoky grey. Genuine alpha transparency outside brush panel. Front-facing flat asset, not a screenshot.

介绍素材提示词：

> Use case: style-transfer. Edit target: attached long charcoal brush panel asset. Make production game UI background for This War of Mine mod manager RIGHT scrollable description panel. Change shape to landscape 8:5 aspect ratio, filling 94% of canvas, small transparent margins. Retain hand-drawn wartime journal charcoal-on-paper style. Dark neutral charcoal center around #171918, subtle clearly visible graphite paper grain throughout, central 85% calm and nearly opaque so white text is legible. Dry brush uneven weathered smoky edges with organic alpha transparency, not a straight black rectangle. No text, no icons, no bevel, no glow, no decorative objects, no white background. Corners irregular but restrained, small transparent border. Flat front-facing bitmap UI asset, not interface screenshot.
