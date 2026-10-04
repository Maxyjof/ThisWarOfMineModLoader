using System.Buffers.Binary;
using System.Text;

namespace ThisWarOfMineModLoader.Archives;

/// <summary>
/// 计算Liquid Engine资源路径使用的MurmurHash2标识
/// </summary>
public static class ResourceHash
{
    /// <summary>
    /// 规范化容器内相对路径并使用零种子计算32位哈希
    /// </summary>
    public static uint Compute(string relativePath)
    {
        //游戏路径使用小写正斜杠并去除开头斜杠
        var bytes = Encoding.UTF8.GetBytes(relativePath.Replace('\\', '/').TrimStart('/').ToLowerInvariant());
        const uint multiplier = 0x5bd1e995;
        var hash = (uint)bytes.Length;
        var offset = 0;
        unchecked
        {
            //按小端32位块混合并保留无符号整数自然溢出
            while (bytes.Length - offset >= 4)
            {
                var k = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
                k *= multiplier; k ^= k >> 24; k *= multiplier;
                hash = hash * multiplier ^ k;
                offset += 4;
            }
            //处理末尾不足四字节的剩余内容
            var remainder = bytes.Length - offset;
            if (remainder == 3) hash ^= (uint)bytes[offset + 2] << 16;
            if (remainder >= 2) hash ^= (uint)bytes[offset + 1] << 8;
            if (remainder >= 1) { hash ^= bytes[offset]; hash *= multiplier; }
            //通过末次混合扩散输入字节对最终结果的影响
            hash ^= hash >> 13; hash *= multiplier; hash ^= hash >> 15;
        }
        return hash;
    }
}
