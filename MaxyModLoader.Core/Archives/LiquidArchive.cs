using System.Buffers.Binary;
using System.IO.Compression;

namespace MaxyModLoader.Archives;

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
        //保留原始头部只允许公开旧版本与本机实测版本包含官方编译器的未压缩输出
        var header = new byte[11];
        index.ReadExactly(header);
        if (header[0] != 0 || !(header[1] == 3 && header[2] is 0 or 1 || header[1] == 6 && header[2] == 1))
            throw new InvalidDataException("未知容器版本：只接受000300、000301和000601");
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
    public void WriteReplacement(uint hash, byte[] replacement, string outputBase,
        IReadOnlyDictionary<uint, byte[]>? additions = null)
    {
        //脚本替换入口保持原有调用方式新增资源由同一个写入流程处理
        WriteContents(hash, replacement, outputBase, additions);
    }

    /// <summary>
    /// 仅追加新资源保留纹理容器内全部原版条目
    /// </summary>
    public void WriteAdditions(IReadOnlyDictionary<uint, byte[]> additions, string outputBase)
    {
        //纹理追加不需要选择或重新压缩任何原版资源
        WriteContents(null, [], outputBase, additions);
    }

    /// <summary>
    /// 统一写入可选替换资源和新增资源并复读验证
    /// </summary>
    private void WriteContents(uint? hash, byte[] replacement, string outputBase,
        IReadOnlyDictionary<uint, byte[]>? additions)
    {
        //拒绝原路径和已有输出避免覆盖原版或先前实验结果
        if (hash.HasValue && !Entries.Any(e => e.Hash == hash)) throw new InvalidDataException("只能替换存在的资源哈希。");
        //新资源必须使用未占用哈希并限制每项解压后大小
        var extra = (additions ?? new Dictionary<uint, byte[]>()).OrderBy(item => item.Key).ToArray();
        if (Entries.Count + extra.Length > 1_000_000) throw new InvalidDataException("自定义资源数量超出容器上限。");
        foreach (var (addedHash, bytes) in extra)
            if (addedHash == hash || Entries.Any(entry => entry.Hash == addedHash) || bytes.Length is 0 or > 64 * 1024 * 1024)
                throw new InvalidDataException($"自定义资源哈希冲突或文件长度无效：{addedHash:x8}");
        outputBase = Path.GetFullPath(outputBase);
        if (string.Equals(outputBase, BasePath, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("输出不能覆盖原容器。");
        if (File.Exists(outputBase + ".idx") || File.Exists(outputBase + ".dat")) throw new IOException("输出容器已存在，请使用新的输出目录。");
        //生成符合游戏已测格式的gzip压缩数据
        var packed = new List<(uint Hash, uint Size, byte[] Bytes)>();
        if (hash.HasValue) packed.Add((hash.Value, checked((uint)replacement.Length), Compress(replacement)));
        packed.AddRange(extra.Select(item => (item.Key, checked((uint)item.Value.Length), Compress(item.Value))));
        var resourceOffsets = new Dictionary<uint, uint>();
        Directory.CreateDirectory(Path.GetDirectoryName(outputBase)!);
        var createdData = false; var createdIndex = false;
        try
        {
            //复制源数据后逐项追加压缩资源并验证32位偏移边界
            using (var output = new FileStream(outputBase + ".dat", FileMode.CreateNew))
            {
                createdData = true;
                using var source = File.OpenRead(BasePath + ".dat");
                source.CopyTo(output);
                foreach (var resource in packed)
                {
                    if (output.Position + resource.Bytes.Length > uint.MaxValue) throw new InvalidDataException("输出超出 32 位容器范围。");
                    resourceOffsets.Add(resource.Hash, checked((uint)output.Position));
                    output.Write(resource.Bytes);
                }
            }
            //原版索引按哈希递增供引擎二分查找新增条目必须插入正确顺序
            using (var output = new FileStream(outputBase + ".idx", FileMode.CreateNew))
            {
                createdIndex = true;
                using var writer = new BinaryWriter(output);
                var header = Header.ToArray();
                BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(3), checked((uint)(Entries.Count + extra.Length)));
                writer.Write(header);
                var entries = Entries.Select(entry => entry.Hash == hash
                    ? new ArchiveEntry(entry.Hash, checked((uint)packed[0].Bytes.Length), packed[0].Size,
                        resourceOffsets[entry.Hash], true) : entry)
                    .Concat(packed.Skip(hash.HasValue ? 1 : 0).Select(resource => new ArchiveEntry(resource.Hash,
                        checked((uint)resource.Bytes.Length), resource.Size, resourceOffsets[resource.Hash], true)))
                    .OrderBy(entry => entry.Hash);
                foreach (var entry in entries)
                {
                    writer.Write(entry.Hash);
                    writer.Write(entry.StoredSize);
                    writer.Write(entry.Size);
                    writer.Write(entry.Offset);
                    writer.Write(entry.Compressed ? (byte)1 : (byte)0);
                }
            }
            //复读输出验证替换资源与每个新增资源的完整内容
            var archive = Open(outputBase);
            if (hash.HasValue && !archive.Read(hash.Value).AsSpan().SequenceEqual(replacement))
                throw new InvalidDataException("输出复读验证失败。");
            foreach (var (addedHash, bytes) in extra)
                if (!archive.Read(addedHash).AsSpan().SequenceEqual(bytes)) throw new InvalidDataException($"新增资源复读验证失败：{addedHash:x8}");
        }
        catch
        {
            //只删除本次已创建的输出文件保留任何原有文件
            if (createdIndex) File.Delete(outputBase + ".idx");
            if (createdData) File.Delete(outputBase + ".dat");
            throw;
        }
    }

    /// <summary>
    /// 将单个资源压缩为容器使用的gzip数据
    /// </summary>
    private static byte[] Compress(byte[] resource)
    {
        //压缩缓冲区独立于输入数组以便后续统一写入索引
        using var packed = new MemoryStream();
        using (var gzip = new GZipStream(packed, CompressionLevel.Optimal, leaveOpen: true)) gzip.Write(resource);
        return packed.ToArray();
    }
}
