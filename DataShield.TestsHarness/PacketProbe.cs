using DataShield.Codec.Packets;

namespace DataShield.TestsHarness;

// ─────────────────────────────────────────────────────────────────────────────
//  Классификация и подделка 75-байтных пакетов DataShield
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Инструменты анализа пакетов: распознавание заголовков и корректных секторов
/// (обе схемы хеширования), извлечение H5, подделка сектора с пересчётом хеша
/// (коллизия версий).
/// </summary>
public static class PacketProbe
{
    /// <summary>Пакет является заголовком: H5 совпадает с Trunc24(SHA-256(H1–H4)).</summary>
    public static bool IsHeader(ReadOnlySpan<byte> packet) =>
        PacketHasher.VerifyHeaderPacket(packet);

    /// <summary>Пакет является корректным сектором: D3 сходится с сидом H5.</summary>
    public static bool IsValidSector(ReadOnlySpan<byte> packet, byte[] headerHash) =>
        PacketHasher.VerifySectorPacket(packet, headerHash);

    /// <summary>
    /// Пакет является корректным сектором заданной схемы: Classic — D3
    /// сходится с сидом H5; VirtualIndex — перебор индексов 0..K−1
    /// находит совпадение Trunc11 (K = <paramref name="sectorCount"/>).
    /// </summary>
    public static bool IsValidSector(
        ReadOnlySpan<byte> packet, byte[] headerHash,
        SectorScheme scheme, int sectorCount) =>
        scheme == SectorScheme.VirtualIndex
            ? sectorCount > 0 &&
              VirtualIndexHasher.TryVerifyPacket(packet, headerHash, sectorCount, 0, out _)
            : PacketHasher.VerifySectorPacket(packet, headerHash);

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
        ReadOnlySpan<byte> sector, byte[] headerHash, Random rng) =>
        ForgeSectorVariant(sector, headerHash, rng, SectorScheme.Classic, 0);

    /// <summary>
    /// Подделка сектора заданной схемы: тот же номер тома, искажённый payload,
    /// пересчитанный хеш (Classic — D3, VirtualIndex — Trunc11 для того же
    /// виртуального индекса). Полученный пакет проходит проверку хеша, но несёт
    /// неверные данные — это создаёт коллизию версий сектора в приёмнике
    /// (для VirtualIndex — ровно на исходном виртуальном индексе).
    /// </summary>
    public static byte[] ForgeSectorVariant(
        ReadOnlySpan<byte> sector, byte[] headerHash, Random rng,
        SectorScheme scheme, int sectorCount)
    {
        if (scheme == SectorScheme.VirtualIndex)
        {
            // Истинный индекс восстанавливается перебором; подделка пересобирается
            // с тем же индексом — приёмник примет её вместо оригинала.
            if (!VirtualIndexHasher.TryVerifyPacket(
                    sector, headerHash, sectorCount, 0, out var index))
                throw new InvalidOperationException(
                    "Подделка VirtualIndex: пакет не является корректным сектором.");

            var forged = sector.ToArray();

            // Различные позиции payload с ненулевыми масками гарантируют
            // отличие от оригинала (номера в пакете схемы B нет).
            var flipped = new HashSet<int>();
            var flips = 1 + rng.Next(4);
            while (flipped.Count < flips)
            {
                var position = rng.Next(PacketFormat.PayloadSize);
                if (flipped.Add(position))
                    forged[position] ^= (byte)(1 + rng.Next(255));
            }

            VirtualIndexHasher.BuildPacketInto(
                forged.AsSpan(0, PacketFormat.PayloadSize), index, headerHash, forged);
            return forged;
        }

        var classic = sector.ToArray();

        // Различные позиции с ненулевыми масками гарантируют отличие от оригинала.
        var flippedClassic = new HashSet<int>();
        var flipsClassic = 1 + rng.Next(4);
        while (flippedClassic.Count < flipsClassic)
        {
            var position = PacketFormat.SectorNumberSize + rng.Next(PacketFormat.PayloadSize);
            if (flippedClassic.Add(position))
                classic[position] ^= (byte)(1 + rng.Next(255));
        }

        var hash = PacketHasher.ComputeSectorHash(
            classic.AsSpan(0, PacketFormat.SectorContentSize), headerHash);
        hash.AsSpan().CopyTo(classic.AsSpan(PacketFormat.SectorHashOffset));
        return classic;
    }
}
