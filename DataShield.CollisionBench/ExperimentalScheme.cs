using DataShield.Codec.Packets;

namespace DataShield.CollisionBench;

// ─────────────────────────────────────────────────────────────────────────────
//  Экспериментальная схема B: сектор без физического поля индекса
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Схема B (экспериментальная): payload(64) ‖ Trunc11(SHA-256(H5 ‖ idxLE ‖ payload)).
///
/// Два байта индекса не хранятся в секторе — они «виртуальные»: значение индекса
/// входит в хеш-вход, вставая в голову блока перед payload. Освободившиеся байты
/// уходят в хеш-блок (9 → 11 байт усечения). Прием сектора — перебор всех
/// индексов полного набора (K = data + ECC) с кешем сидов
/// (<see cref="SectorSeedCache"/>): сектор принят, если хотя бы один индекс
/// дает совпадение усеченного хеша.
/// </summary>
public static class ExperimentalScheme
{
    /// <summary>Усечение хеша схемы B: 9 + 2 = 11 байт.</summary>
    public const int HashSize = PacketFormat.SectorHashSize + PacketFormat.SectorNumberSize;

    /// <summary>Полный размер сектора: payload(64) + hash(11) = 75.</summary>
    public const int SectorSize = PacketFormat.PacketSize;

    /// <summary>Смещение хеша в секторе: сразу после payload.</summary>
    public const int HashOffset = PacketFormat.PayloadSize; // 64

    /// <summary>Размер хеш-входа: H5(24) + idx(2) + payload(64) = 90.</summary>
    private const int InputSize =
        PacketFormat.HeaderHashSize + PacketFormat.SectorNumberSize + PacketFormat.PayloadSize;

    /// <summary>Полный SHA-256 сектора (32 байта) в буфер hash.</summary>
    public static void HashInto(
        int index,
        ReadOnlySpan<byte> payload,
        ReadOnlySpan<byte> headerHash,
        Span<byte> hash)
    {
        if (payload.Length != PacketFormat.PayloadSize)
            throw new ArgumentException(
                $"Ожидалось {PacketFormat.PayloadSize} байт payload, " +
                $"получено {payload.Length}.",
                nameof(payload));

        if (headerHash.Length != PacketFormat.HeaderHashSize)
            throw new ArgumentException(
                $"Ожидалось {PacketFormat.HeaderHashSize} байт хеша заголовка, " +
                $"получено {headerHash.Length}.",
                nameof(headerHash));

        Span<byte> input = stackalloc byte[InputSize];
        headerHash.CopyTo(input);
        input[PacketFormat.HeaderHashSize] = (byte)(index & 0xFF);
        input[PacketFormat.HeaderHashSize + 1] = (byte)((index >> 8) & 0xFF);
        payload.CopyTo(input[(PacketFormat.HeaderHashSize + PacketFormat.SectorNumberSize)..]);

        Sha256Compact.HashData(input, hash);
    }

    /// <summary>
    /// Собрать сектор схемы B: payload(64) + Trunc(hashBytes)(SHA-256).
    /// Для стенда усечение параметризуется (реальное значение — <see cref="HashSize"/>).
    /// </summary>
    public static byte[] BuildSector(
        int index,
        ReadOnlySpan<byte> payload,
        ReadOnlySpan<byte> headerHash,
        int hashBytes = HashSize)
    {
        var sector = new byte[PacketFormat.PayloadSize + hashBytes];
        BuildSectorInto(sector, index, payload, headerHash, hashBytes);
        return sector;
    }

    /// <summary>Бесаллокационная сборка сектора в готовый буфер.</summary>
    public static void BuildSectorInto(
        Span<byte> sector,
        int index,
        ReadOnlySpan<byte> payload,
        ReadOnlySpan<byte> headerHash,
        int hashBytes)
    {
        if (hashBytes is < 1 or > 32)
            throw new ArgumentOutOfRangeException(nameof(hashBytes));
        if (index is < 0 or > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(index));
        if (sector.Length != PacketFormat.PayloadSize + hashBytes)
            throw new ArgumentException(
                $"Ожидалось {PacketFormat.PayloadSize + hashBytes} байт сектора " +
                $"(64 + {hashBytes}), получено {sector.Length}.",
                nameof(sector));

        Span<byte> hash = stackalloc byte[32];
        HashInto(index, payload, headerHash, hash);

        payload.CopyTo(sector);
        hash[..hashBytes].CopyTo(sector[HashOffset..]);
    }

    /// <summary>
    /// Полный перебор индексов (худший случай приема): сектор допущен, если
    /// хотя бы один индекс дает совпадение. Считаются все совпадения —
    /// неоднозначность означает, что один пакет претендует на несколько индексов.
    /// Стоимость — ровно K хешей.
    /// </summary>
    public static bool VerifyAll(
        ReadOnlySpan<byte> sector,
        SectorSeedCache cache,
        int hashBytes,
        out int firstIndex,
        out int matchCount)
    {
        ValidateSector(sector, cache, hashBytes);

        Span<byte> input = stackalloc byte[InputSize];
        sector[..PacketFormat.PayloadSize].CopyTo(
            input[(PacketFormat.HeaderHashSize + PacketFormat.SectorNumberSize)..]);
        Span<byte> hash = stackalloc byte[32];

        firstIndex = -1;
        matchCount = 0;

        for (var i = 0; i < cache.SectorCount; i++)
        {
            cache.Prefix(i).CopyTo(input);
            Sha256Compact.HashData(input, hash);

            if (hash[..hashBytes].SequenceEqual(sector[HashOffset..]))
            {
                if (firstIndex < 0) firstIndex = i;
                matchCount++;
            }
        }

        return matchCount > 0;
    }

    /// <summary>
    /// Перебор с ранним выходом (типовой прием валидного сектора): при
    /// равномерном индексе в среднем K/2 хешей. Возвращает matchedIndex = -1,
    /// если сектор не принят (стоимость — полный перебор K хешей).
    /// </summary>
    public static bool TryVerify(
        ReadOnlySpan<byte> sector,
        SectorSeedCache cache,
        int hashBytes,
        out int index)
    {
        ValidateSector(sector, cache, hashBytes);

        Span<byte> input = stackalloc byte[InputSize];
        sector[..PacketFormat.PayloadSize].CopyTo(
            input[(PacketFormat.HeaderHashSize + PacketFormat.SectorNumberSize)..]);
        Span<byte> hash = stackalloc byte[32];

        for (var i = 0; i < cache.SectorCount; i++)
        {
            cache.Prefix(i).CopyTo(input);
            Sha256Compact.HashData(input, hash);

            if (hash[..hashBytes].SequenceEqual(sector[HashOffset..]))
            {
                index = i;
                return true;
            }
        }

        index = -1;
        return false;
    }

    /// <summary>Проверка аргументов сектора перед перебором (длина и усечение).</summary>
    private static void ValidateSector(ReadOnlySpan<byte> sector, SectorSeedCache cache, int hashBytes)
    {
        if (hashBytes is < 1 or > 32)
            throw new ArgumentOutOfRangeException(nameof(hashBytes));
        if (sector.Length != PacketFormat.PayloadSize + hashBytes)
            throw new ArgumentException(
                $"Ожидалось {PacketFormat.PayloadSize + hashBytes} байт сектора " +
                $"(64 + {hashBytes}), получено {sector.Length}.",
                nameof(sector));
    }
}
