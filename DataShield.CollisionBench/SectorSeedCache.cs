using DataShield.Codec.Packets;

namespace DataShield.CollisionBench;

// ─────────────────────────────────────────────────────────────────────────────
//  Кеш сидов перебора индексов (схема B)
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Заголовок файла (H5) постоянен для всего набора секторов, поэтому префиксы
/// хеш-входа H5(24) ‖ idxLE(2) вычисляются один раз и кешируются для всех
/// индексов полного набора. При проверке сектора остается дописать payload
/// и выполнить SHA-256.
/// </summary>
public sealed class SectorSeedCache
{
    /// <summary>Размер префикса хеш-входа одного индекса: H5 + idxLE = 26 байт.</summary>
    public const int PrefixSize =
        PacketFormat.HeaderHashSize + PacketFormat.SectorNumberSize; // 26

    private readonly byte[] _prefixes;

    /// <summary>Количество индексов в полном наборе (data + ECC тома).</summary>
    public int SectorCount { get; }

    /// <summary>
    /// Построить кеш сидов для заданного H5 и количества секторов.
    /// </summary>
    public SectorSeedCache(ReadOnlySpan<byte> headerHash, int sectorCount)
    {
        if (headerHash.Length != PacketFormat.HeaderHashSize)
            throw new ArgumentException(
                $"Ожидалось {PacketFormat.HeaderHashSize} байт хеша заголовка, " +
                $"получено {headerHash.Length}.",
                nameof(headerHash));

        if (sectorCount < 1 || sectorCount > PacketFormat.MaxDataVolumes)
            throw new ArgumentOutOfRangeException(
                nameof(sectorCount),
                $"Количество секторов должно быть в диапазоне 1..{PacketFormat.MaxDataVolumes}.");

        SectorCount = sectorCount;
        _prefixes = new byte[sectorCount * PrefixSize];

        for (var i = 0; i < sectorCount; i++)
        {
            var prefix = _prefixes.AsSpan(i * PrefixSize, PrefixSize);
            headerHash.CopyTo(prefix);
            prefix[PacketFormat.HeaderHashSize] = (byte)(i & 0xFF);
            prefix[PacketFormat.HeaderHashSize + 1] = (byte)((i >> 8) & 0xFF);
        }
    }

    /// <summary>Префикс хеш-входа (26 байт) для индекса сектора.</summary>
    public ReadOnlySpan<byte> Prefix(int index) =>
        _prefixes.AsSpan(index * PrefixSize, PrefixSize);
}
