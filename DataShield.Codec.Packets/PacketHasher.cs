namespace DataShield.Codec.Packets;

// ─────────────────────────────────────────────────────────────────────────────
//  Усечённые хеши SHA-256 для пакетов DataShield
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Хеши целостности пакетов на основе SHA-256 с усечением.
///
/// H5 заголовка — Trunc24(SHA-256(H1–H4)): 24 байта (192 бита), ложное
/// срабатывание классификации 2⁻¹⁹². D3 сектора — Trunc9(SHA-256(H5 ‖ D1 ‖ D2)):
/// привязка к заголовку файла, без него проверка невозможна.
/// </summary>
public static class PacketHasher
{
    /// <summary>H5: Trunc24(SHA-256(содержимое заголовка)). Вход — ровно 51 байт.</summary>
    public static byte[] ComputeHeaderHash(ReadOnlySpan<byte> headerContent)
    {
        if (headerContent.Length != PacketFormat.HeaderContentSize)
            throw new ArgumentException(
                $"Ожидалось {PacketFormat.HeaderContentSize} байт содержимого заголовка, " +
                $"получено {headerContent.Length}.",
                nameof(headerContent));

        Span<byte> hash = stackalloc byte[32];
        Sha256Compact.HashData(headerContent, hash);
        return hash[..PacketFormat.HeaderHashSize].ToArray();
    }

    /// <summary>D3: Trunc9(SHA-256(headerHash ‖ содержимое сектора)). Входы — 24 и 66 байт.</summary>
    public static byte[] ComputeSectorHash(
        ReadOnlySpan<byte> sectorContent,
        ReadOnlySpan<byte> headerHash)
    {
        if (sectorContent.Length != PacketFormat.SectorContentSize)
            throw new ArgumentException(
                $"Ожидалось {PacketFormat.SectorContentSize} байт содержимого сектора, " +
                $"получено {sectorContent.Length}.",
                nameof(sectorContent));

        if (headerHash.Length != PacketFormat.HeaderHashSize)
            throw new ArgumentException(
                $"Ожидалось {PacketFormat.HeaderHashSize} байт хеша заголовка, " +
                $"получено {headerHash.Length}.",
                nameof(headerHash));

        Span<byte> hash = stackalloc byte[32];
        ComputeSectorHashInto(sectorContent, headerHash, hash);
        return hash[..PacketFormat.SectorHashSize].ToArray();
    }

    /// <summary>
    /// Полный пакет (75 байт) является заголовком: H5 совпадает с
    /// Trunc24(SHA-256(H1–H4)).
    /// </summary>
    public static bool VerifyHeaderPacket(ReadOnlySpan<byte> packet)
    {
        if (packet.Length != PacketFormat.PacketSize)
            return false;

        Span<byte> hash = stackalloc byte[32];
        Sha256Compact.HashData(
            packet[..PacketFormat.HeaderContentSize],
            hash);

        return hash[..PacketFormat.HeaderHashSize].SequenceEqual(
            packet[PacketFormat.HeaderHashOffset..]);
    }

    /// <summary>
    /// Полный пакет (75 байт) является корректным сектором данного файла:
    /// D3 совпадает с Trunc9(SHA-256(H5 ‖ D1 ‖ D2)).
    /// </summary>
    public static bool VerifySectorPacket(
        ReadOnlySpan<byte> packet,
        ReadOnlySpan<byte> headerHash)
    {
        if (packet.Length != PacketFormat.PacketSize ||
            headerHash.Length != PacketFormat.HeaderHashSize)
        {
            return false;
        }

        Span<byte> hash = stackalloc byte[32];
        ComputeSectorHashInto(
            packet[..PacketFormat.SectorContentSize],
            headerHash,
            hash);

        return hash[..PacketFormat.SectorHashSize].SequenceEqual(
            packet[PacketFormat.SectorHashOffset..]);
    }

    /// <summary>Хеш сектора в 32-байтный буфер (используются первые 9 байт).</summary>
    private static void ComputeSectorHashInto(
        ReadOnlySpan<byte> sectorContent,
        ReadOnlySpan<byte> headerHash,
        Span<byte> hash)
    {
        Span<byte> input = stackalloc byte[
            PacketFormat.HeaderHashSize + PacketFormat.SectorContentSize]; // 90

        headerHash.CopyTo(input);
        sectorContent.CopyTo(input[PacketFormat.HeaderHashSize..]);
        Sha256Compact.HashData(input, hash);
    }
}
