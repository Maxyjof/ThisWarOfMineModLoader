using System.Buffers.Binary;
using System.IO.Compression;

namespace ThisWarOfMineModLoader.Archives;

/// <summary>
/// 表示容器索引中的资源标识、长度、偏移和压缩状态
/// </summary>
public sealed record ArchiveEntry(uint Hash, uint StoredSize, uint Size, uint Offset, bool Compressed);

/// <summary>
/// 读取已知版本的Liquid Engine容器并生成不覆盖原文件的替代容器
/// </summary>
public sealed class LiquidArchive
{
    public string BasePath { get; }
    public byte[] Header { get; }
    public IReadOnlyList<ArchiveEntry> Entries { get; }

    /// <summary>
    /// 保存经过完整索引校验的容器信息
    /// </summary>
    private LiquidArchive(string basePath, byte[] header, IReadOnlyList<ArchiveEntry> entries)
    {
        //保存已校验索引避免后续重复扫描
        BasePath = basePath;
        Header = header;
        Entries = entries;
    }

    /// <summary>
    /// 打开索引和数据并校验版本、条目数、重复哈希和资源边界
    /// </summary>
    public static LiquidArchive Open(string basePath)
    {
        //先限制索引长度以避免未知格式或超大索引消耗内存
        basePath = Path.GetFullPath(basePath);
        using var index = File.OpenRead(basePath + ".idx");
        using var data = File.OpenRead(basePath + ".dat");
        if (index.Length < 11 || (index.Length - 11) % 17 != 0) throw new InvalidDataException("索引长度不符合 11 + n × 17。");
        if (index.Length > 11 + 17L * 1_000_000) throw new InvalidDataException("索引条目过多。");
        //保留原始头部只允许公开旧版本与本机实测版本
        var header = new byte[11];
        index.ReadExactly(header);
        if (header[0] != 0 || header[2] != 1 || header[1] is not (3 or 6))
            throw new InvalidDataException("未知容器版本：当前只接受 00 03 01 和 00 06 01，需先分析该游戏版本。");
        var count = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(3));
        if (count != (index.Length - 11) / 17) throw new InvalidDataException("索引声明的条目数与长度不符。");
        //逐条读取小端索引不解压数据即可验证所有资源的存储范围
        var entries = new List<ArchiveEntry>();
        var hashes = new HashSet<uint>();
        using var reader = new BinaryReader(index);
        for (var i = 0; i < count; i++)
        {
            var hash = reader.ReadUInt32(); var stored = reader.ReadUInt32();
            var size = reader.ReadUInt32(); var offset = reader.ReadUInt32(); var flag = reader.ReadByte();
            if (flag > 1 || (long)offset + stored > data.Length || (flag == 0 && stored != size))
                throw new InvalidDataException($"索引条目 {hash:x8} 的压缩标志、长度或边界无效。");
            if (!hashes.Add(hash)) throw new InvalidDataException($"重复资源哈希：{hash:x8}");
            entries.Add(new(hash, stored, size, offset, flag == 1));
        }
        return new(basePath, header, entries);
    }

    /// <summary>
    /// 按哈希读取资源并限制压缩前后长度
    /// </summary>
    public byte[] Read(uint hash, int maxBytes = 64 * 1024 * 1024)
    {
        //读取前校验资源存在与长度上限
        var entry = Entries.SingleOrDefault(e => e.Hash == hash)
            ?? throw new InvalidDataException($"容器中没有资源 {hash:x8}。");
        if (entry.Size > maxBytes || entry.StoredSize > maxBytes) throw new InvalidDataException("资源超出读取上限。");
        //只读取目标资源区间不载入整个数据容器
        using var data = File.OpenRead(BasePath + ".dat");
        data.Position = entry.Offset;
        var stored = new byte[checked((int)entry.StoredSize)];
        data.ReadExactly(stored);
        if (!entry.Compressed) return stored;
        //索引声明的解压长度必须与实际内容严格一致
        using var source = new MemoryStream(stored);
        using var gzip = new GZipStream(source, CompressionMode.Decompress);
        var result = new byte[checked((int)entry.Size)];
        gzip.ReadExactly(result);
        if (gzip.ReadByte() != -1) throw new InvalidDataException("资源解压长度超出索引声明。");
        return result;
    }

    /// <summary>
    /// 保留源容器字节和索引顺序并在末尾追加替代资源
    /// </summary>
    public void WriteReplacement(uint hash, byte[] replacement, string outputBase)
    {
        //拒绝原路径和已有输出避免覆盖原版或先前实验结果
        if (!Entries.Any(e => e.Hash == hash)) throw new InvalidDataException("只能替换存在的资源哈希。");
        outputBase = Path.GetFullPath(outputBase);
        if (string.Equals(outputBase, BasePath, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("输出不能覆盖原容器。");
        if (File.Exists(outputBase + ".idx") || File.Exists(outputBase + ".dat")) throw new IOException("输出容器已存在，请使用新的输出目录。");
        //生成符合游戏已测格式的gzip压缩数据
        using var packed = new MemoryStream();
        using (var gzip = new GZipStream(packed, CompressionLevel.Optimal, leaveOpen: true)) gzip.Write(replacement);
        var bytes = packed.ToArray();
        Directory.CreateDirectory(Path.GetDirectoryName(outputBase)!);
        var createdData = false; var createdIndex = false;
        try
        {
            //复制源数据后追加新内容32位偏移必须能够表示完整结果
            uint offset;
            using (var output = new FileStream(outputBase + ".dat", FileMode.CreateNew))
            {
                createdData = true;
                using var source = File.OpenRead(BasePath + ".dat");
                if (source.Length + bytes.Length > uint.MaxValue) throw new InvalidDataException("输出超出 32 位容器范围。");
                offset = checked((uint)source.Length);
                source.CopyTo(output);
                output.Write(bytes);
            }
            //保持原头部和全部非目标条目只改变目标资源的存储信息
            using (var output = new FileStream(outputBase + ".idx", FileMode.CreateNew))
            {
                createdIndex = true;
                using var writer = new BinaryWriter(output);
                writer.Write(Header);
                foreach (var entry in Entries)
                {
                    writer.Write(entry.Hash);
                    writer.Write(entry.Hash == hash ? checked((uint)bytes.Length) : entry.StoredSize);
                    writer.Write(entry.Hash == hash ? checked((uint)replacement.Length) : entry.Size);
                    writer.Write(entry.Hash == hash ? offset : entry.Offset);
                    writer.Write(entry.Hash == hash || entry.Compressed ? (byte)1 : (byte)0);
                }
            }
            //复读输出验证容器结构和目标资源的完整内容
            var verify = Open(outputBase).Read(hash);
            if (!verify.AsSpan().SequenceEqual(replacement)) throw new InvalidDataException("输出复读验证失败。");
        }
        catch
        {
            //只删除本次已创建的输出文件保留任何原有文件
            if (createdIndex) File.Delete(outputBase + ".idx");
            if (createdData) File.Delete(outputBase + ".dat");
            throw;
        }
    }
}
