using DataShield.Codec.Packets;

namespace DataShield.CollisionBench;

// ─────────────────────────────────────────────────────────────────────────────
//  Параметризуемая реплика текущей схемы A (эталон для сравнения)
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Схема A (текущая): idxLE(2) ‖ payload(64) ‖ Trunc_t(SHA-256(H5 ‖ idxLE ‖ payload)).
/// Хеш-вход совпадает с <see cref="PacketHasher"/> (H5 ‖ D1 ‖ D2); для стенда
/// усечение параметризуется: t &lt; 9 дает сокращенный сектор с тем же байтовым
/// бюджетом, что и схема B (66 + t == 64 + (t + 2)), t = 9 — точная реплика
/// продового формата.
/// </summary>
public static class ClassicScheme
{
    /// <summary>Усечение хеша схемы A: 9 байт (продовое значение).</summary>
    public const int HashSize = PacketFormat.SectorHashSize;

    /// <summary>Смещение хеша в секторе: после idx + payload.</summary>
    public const int HashOffset = PacketFormat.SectorContentSize; // 66

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
    /// Собрать сектор схемы A: idx(2) + payload(64) + Trunc(hashBytes).
    /// При hashBytes = 9 результат побитово совпадает с продовым форматом.
    /// </summary>
    public static byte[] BuildSector(
        int index,
        ReadOnlySpan<byte> payload,
        ReadOnlySpan<byte> headerHash,
        int hashBytes = HashSize)
    {
        var sector = new byte[PacketFormat.SectorContentSize + hashBytes];
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
        if (sector.Length != PacketFormat.SectorContentSize + hashBytes)
            throw new ArgumentException(
                $"Ожидалось {PacketFormat.SectorContentSize + hashBytes} байт сектора, " +
                $"получено {sector.Length}.",
                nameof(sector));

        Span<byte> hash = stackalloc byte[32];
        HashInto(index, payload, headerHash, hash);

        sector[0] = (byte)(index & 0xFF);
        sector[1] = (byte)((index >> 8) & 0xFF);
        payload.CopyTo(sector[PacketFormat.SectorNumberSize..]);
        hash[..hashBytes].CopyTo(sector[HashOffset..]);
    }

    /// <summary>
    /// Проверка сектора: единственный хеш по хранящемуся индексу (стоимость 1 хеш).
    /// </summary>
    public static bool Verify(
        ReadOnlySpan<byte> sector,
        ReadOnlySpan<byte> headerHash,
        int hashBytes,
        out int index)
    {
        if (hashBytes is < 1 or > 32)
            throw new ArgumentOutOfRangeException(nameof(hashBytes));
        if (headerHash.Length != PacketFormat.HeaderHashSize)
            throw new ArgumentException(
                $"Ожидалось {PacketFormat.HeaderHashSize} байт хеша заголовка, " +
                $"получено {headerHash.Length}.",
                nameof(headerHash));
        if (sector.Length != PacketFormat.SectorContentSize + hashBytes)
            throw new ArgumentException(
                $"Ожидалось {PacketFormat.SectorContentSize + hashBytes} байт сектора " +
                $"(66 + {hashBytes}), получено {sector.Length}.",
                nameof(sector));

        index = sector[0] | (sector[1] << 8);

        Span<byte> input = stackalloc byte[InputSize];
        headerHash.CopyTo(input);
        sector[..PacketFormat.SectorContentSize].CopyTo(input[PacketFormat.HeaderHashSize..]);

        Span<byte> hash = stackalloc byte[32];
        Sha256Compact.HashData(input, hash);

        return hash[..hashBytes].SequenceEqual(sector[HashOffset..]);
    }
}
