import argparse
import pathlib
import struct

from PIL import Image


def prepare_texture(source, destination):
    """
    <summary>
    保留生成素材的像素和透明度并写出BGRA格式DDS源纹理
    </summary>
    """
    #读取生成素材并保留透明边缘
    image = Image.open(source).convert("RGBA")
    #创建运行时纹理的目标目录
    destination = pathlib.Path(destination)
    destination.parent.mkdir(parents=True, exist_ok=True)

    #DDS像素按蓝绿红透明度排列与头部通道掩码保持一致
    raw = bytearray()
    pixels = image.tobytes()
    for index in range(0, len(pixels), 4):
        raw.extend((pixels[index + 2], pixels[index + 1], pixels[index], pixels[index + 3]))

    #构造无损32位RGBA纹理头部不需要GPU特定压缩格式
    header = [124, 0x100F, image.height, image.width, image.width * 4, 0, 0]
    header.extend([0] * 11)
    header.extend([32, 0x41, 0, 32, 0x00FF0000, 0x0000FF00, 0x000000FF, 0xFF000000])
    header.extend([0x1000, 0, 0, 0, 0])
    destination.write_bytes(b"DDS " + struct.pack("<31I", *header) + raw)


def main():
    """
    <summary>
    从生成素材命令行创建界面DDS资源和预览
    </summary>
    """
    #参数显式指定源图与两个输出路径便于从其他工作目录重建
    parser = argparse.ArgumentParser()
    parser.add_argument("source")
    parser.add_argument("dds")
    arguments = parser.parse_args()
    prepare_texture(arguments.source, arguments.dds)


if __name__ == "__main__":
    main()
