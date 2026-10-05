using System.Text;

namespace MaxyModLoader.Archives;

/// <summary>
/// 写入已核验的原生语言字典长度前缀格式
/// </summary>
public static class LiquidLanguage
{
    /// <summary>
    /// 将自有翻译编码为UTF8键和UTF16文字的独立字典
    /// </summary>
    public static byte[] Encode(IReadOnlyDictionary<string, string> entries)
    {
        //英文与中文原版语言文件实测使用总长度减四以及32位条目数
        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true);
        writer.Write(0u); writer.Write(checked((uint)entries.Count));
        foreach (var (key, value) in entries.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var name = Encoding.UTF8.GetBytes(key);
            if (name.Length is 0 or > ushort.MaxValue || value.Length > ushort.MaxValue)
                throw new InvalidDataException("语言字典键或文字长度无效");
            writer.Write(checked((ushort)name.Length)); writer.Write(name);
            writer.Write(checked((ushort)value.Length)); writer.Write(Encoding.Unicode.GetBytes(value));
        }
        //在全部文字写入后回填长度不接受外部二进制头部
        buffer.Position = 0; writer.Write(checked((uint)buffer.Length - 4));
        return buffer.ToArray();
    }
}
