using System.Buffers.Binary;

namespace MaxyModLoader.Archives;

/// <summary>
/// 将无损DDS转换为本机已核验的Liquid Engine纹理封装
/// </summary>
public static class LiquidTexture
{
    /// <summary>
    /// 校验单层BGRA纹理并生成带十六项层级表的原生资源
    /// </summary>
    public static byte[] FromDds(byte[] dds)
    {
        //只接受实测可映射到D3DFMT_A8R8G8B8的单层格式避免猜测压缩和层级布局
        if (dds.Length < 128 || !dds.AsSpan(0, 4).SequenceEqual("DDS "u8) || Read(dds, 4) != 124 ||
            Read(dds, 76) != 32 || Read(dds, 80) != 0x41 || Read(dds, 84) != 0 || Read(dds, 88) != 32 ||
            Read(dds, 92) != 0x00FF0000 || Read(dds, 96) != 0x0000FF00 ||
            Read(dds, 100) != 0x000000FF || Read(dds, 104) != 0xFF000000 ||
            Read(dds, 28) > 1 || Read(dds, 112) != 0)
            throw new InvalidDataException("只支持单层无损BGRA32位DDS纹理");
        var height = Read(dds, 12);
        var width = Read(dds, 16);
        if (width is 0 or > 8192 || height is 0 or > 8192)
            throw new InvalidDataException("纹理尺寸超出有效范围");
        var pixelBytes = checked((int)(width * height * 4));
        if (pixelBytes > 64 * 1024 * 1024 || dds.Length != 128 + pixelBytes || Read(dds, 20) != width * 4)
            throw new InvalidDataException("DDS像素长度或行步长与尺寸不符");

        //本机原版未压缩UI纹理采用144字节头部和BGRA像素没有DDS签名
        var texture = new byte[144 + pixelBytes];
        Write(texture, 0, width);
        Write(texture, 4, height);
        Write(texture, 8, 21);
        Write(texture, 12, 1);
        BinaryPrimitives.WriteUInt16LittleEndian(texture.AsSpan(16), checked((ushort)width));
        BinaryPrimitives.WriteUInt16LittleEndian(texture.AsSpan(18), checked((ushort)height));
        Write(texture, 20, (uint)pixelBytes);
        dds.AsSpan(128).CopyTo(texture.AsSpan(144));
        return texture;
    }

    /// <summary>
    /// 从已经校验长度的纹理头部读取小端字段
    /// </summary>
    private static uint Read(byte[] bytes, int offset)
    {
        //字段偏移来自固定DDS头部布局
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
    }

    /// <summary>
    /// 将原生纹理的固定字段写入小端头部
    /// </summary>
    private static void Write(byte[] bytes, int offset, uint value)
    {
        //保留未使用层级表为零与本机单层原版纹理一致
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), value);
    }
}
