using DataShield.Codec.Packets;

namespace DataShield.TestsHarness;

// ─────────────────────────────────────────────────────────────────────────────
//  Классификация и подделка 75-байтных пакетов DataShield
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Инструменты анализа пакетов: распознавание заголовков и корректных секторов,
/// извлечение H5, подделка сектора с пересчётом хеша (коллизия версий).
/// </summary>
public static class PacketProbe
{
    /// <summary>Пакет является заголовком: H5 совпадает с Trunc24(SHA-256(H1–H4)).</summary>
    public static bool IsHeader(ReadOnlySpan<byte> packet) =>
        PacketHasher.VerifyHeaderPacket(packet);

    /// <summary>Пакет является корректным сектором: D3 сходится с сидом H5.</summary>
    public static bool IsValidSector(ReadOnlySpan<byte> packet, byte[] headerHash) =>
        PacketHasher.VerifySectorPacket(packet, headerHash);

    /// <summary>Найти первый пакет заголовка в потоке пакетов.</summary>
    public static byte[] FindHeader(IReadOnlyList<byte[]> packets)
    {
        foreach (var packet in packets)
            if (IsHeader(packet))
                return packet;
        throw new InvalidOperationException("В потоке нет пакета заголовка.");
    }

    /// <summary>H5 — хеш заголовка, сид для хеша секторов данных.</summary>
    public static byte[] HeaderHash(ReadOnlySpan<byte> headerPacket) =>
        PacketHasher.ComputeHeaderHash(
            headerPacket[..PacketFormat.HeaderContentSize]);

    /// <summary>Номер сектора (D1, 2 байта LE).</summary>
    public static int SectorNumber(ReadOnlySpan<byte> packet) =>
        packet[0] | (packet[1] << 8);

    /// <summary>Безопасно декодировать Base64-строку в 75-байтный пакет.</summary>
    public static bool TryGetPacket(string line, out byte[] packet)
    {
        packet = Array.Empty<byte>();
        try
        {
            packet = Convert.FromBase64String(line);
        }
        catch (FormatException)
        {
            return false;
        }
        return packet.Length == PacketFormat.PacketSize;
    }

    /// <summary>
    /// Подделка сектора: тот же номер, искажённый payload, пересчитанный хеш D3.
    /// Полученный пакет проходит проверку хеша, но несёт неверные данные —
    /// это создаёт коллизию версий сектора в приёмнике.
    /// </summary>
    public static byte[] ForgeSectorVariant(
        ReadOnlySpan<byte> sector, byte[] headerHash, Random rng)
    {
        var forged = sector.ToArray();

        // Различные позиции с ненулевыми масками гарантируют отличие от оригинала.
        var flipped = new HashSet<int>();
        var flips = 1 + rng.Next(4);
        while (flipped.Count < flips)
        {
            var position = PacketFormat.SectorNumberSize + rng.Next(PacketFormat.PayloadSize);
            if (flipped.Add(position))
                forged[position] ^= (byte)(1 + rng.Next(255));
        }

        var hash = PacketHasher.ComputeSectorHash(
            forged.AsSpan(0, PacketFormat.SectorContentSize), headerHash);
        hash.AsSpan().CopyTo(forged.AsSpan(PacketFormat.SectorHashOffset));
        return forged;
    }
}
