using System.Buffers.Binary;
using System.Text;

namespace DataShield.Codec.Packets;

// ─────────────────────────────────────────────────────────────────────────────
//  Содержимое заголовка файла (51 байт на проводе)
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Содержимое заголовка файла (51 байт на проводе).
/// Используется для сериализации/десериализации и как источник
/// сид-значения (H5) для хеша секторов данных.
/// </summary>
public readonly record struct HeaderContent
{
    /// <summary>H1: имя файла (упакованное <see cref="FileNameCodec.Pack"/>, ASCII, ≤14 байт).</summary>
    public string FileName { get; init; }

    /// <summary>H2: размер файла в байтах.</summary>
    public uint FileSize { get; init; }

    /// <summary>H3: SHA-256 содержимого файла (32 байта).</summary>
    public byte[] Sha256 { get; init; }

    /// <summary>H4: количество ECC-томов.</summary>
    public ushort EccCount { get; init; }

    /// <summary>Количество data-томов = ceil(FileSize / 64), минимум 1.</summary>
    public int DataVolumeCount =>
        FileSize == 0 ? 1 :
        (int)((FileSize + (uint)PacketFormat.PayloadSize - 1) / PacketFormat.PayloadSize);

    /// <summary>Общее количество томов (data + ECC).</summary>
    public int TotalVolumeCount => DataVolumeCount + EccCount;

    /// <summary>Записать в 51-байтный буфер (little-endian).</summary>
    public void WriteTo(Span<byte> dst)
    {
        if (dst.Length < PacketFormat.HeaderContentSize)
            throw new ArgumentException("Буфер назначения слишком мал.", nameof(dst));

        // H1: FileName — ASCII, space-padded
        var nameSpan = dst[..PacketFormat.FileNameSize];
        nameSpan.Fill((byte)' ');
        var name = FileName ?? "";
        var nameBytes = Encoding.ASCII.GetBytes(name);
        if (nameBytes.Length > PacketFormat.FileNameSize)
            throw new InvalidOperationException(
                $"Имя файла в ASCII занимает {nameBytes.Length} байт, " +
                $"превышает лимит {PacketFormat.FileNameSize}.");
        nameBytes.CopyTo(nameSpan);

        // H2: FileSize — 3 байта LE
        WriteUInt24LE(dst[PacketFormat.FileSizeOffset..], FileSize);

        // H3: SHA-256 — 32 байта
        var sha = Sha256;
        if (sha is null || sha.Length < PacketFormat.Sha256Size)
        {
            dst[PacketFormat.Sha256Offset..(PacketFormat.Sha256Offset + PacketFormat.Sha256Size)].Clear();
            sha?.AsSpan().CopyTo(dst[PacketFormat.Sha256Offset..]);
        }
        else
        {
            sha.AsSpan(0, PacketFormat.Sha256Size)
               .CopyTo(dst[PacketFormat.Sha256Offset..]);
        }

        // H4: EccCount — UInt16 LE
        BinaryPrimitives.WriteUInt16LittleEndian(
            dst[PacketFormat.EccCountOffset..], EccCount);
    }

    /// <summary>Сериализовать в новый массив 51 байт.</summary>
    public byte[] ToBytes()
    {
        var bytes = new byte[PacketFormat.HeaderContentSize];
        WriteTo(bytes);
        return bytes;
    }

    /// <summary>Десериализовать из 51+ байт.</summary>
    public static HeaderContent ReadFrom(ReadOnlySpan<byte> src)
    {
        if (src.Length < PacketFormat.HeaderContentSize)
            throw new ArgumentException("Источник слишком мал.", nameof(src));

        var name = Encoding.ASCII.GetString(src[..PacketFormat.FileNameSize])
            .TrimEnd(' ');
        var fileSize = ReadUInt24LE(src[PacketFormat.FileSizeOffset..]);
        var sha256 = src[PacketFormat.Sha256Offset..
            (PacketFormat.Sha256Offset + PacketFormat.Sha256Size)].ToArray();
        var eccCount = BinaryPrimitives.ReadUInt16LittleEndian(
            src[PacketFormat.EccCountOffset..]);

        return new HeaderContent
        {
            FileName = name,
            FileSize = fileSize,
            Sha256 = sha256,
            EccCount = eccCount,
        };
    }

    /// <summary>Записать UInt32 младшими 3 байтами (LE).</summary>
    internal static void WriteUInt24LE(Span<byte> dst, uint value)
    {
        dst[0] = (byte)(value & 0xFF);
        dst[1] = (byte)((value >> 8) & 0xFF);
        dst[2] = (byte)((value >> 16) & 0xFF);
    }

    /// <summary>Прочитать 3 байта как UInt32 (LE).</summary>
    internal static uint ReadUInt24LE(ReadOnlySpan<byte> src) =>
        (uint)(src[0] | (src[1] << 8) | (src[2] << 16));
}
