using DataShield.Codec.Packets;
using Xunit;

namespace DataShield.Codec.Packets.Tests;

// ─────────────────────────────────────────────────────────────────────────────
//  Секторы с виртуальным индексом: Trunc11(SHA-256(H5 ‖ payload ‖ idx LE))
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Тесты VirtualIndexHasher — схемы секторов без явного номера в пакете:
/// индекс сектора не хранится, а «запекается» в хеш, поэтому проверка пакета
/// сводится к перебору индексов 0..sectorCount-1 (последовательно и параллельно).
///
/// Покрываются: формула хеша (эталон SHA-256 от H5 ‖ payload ‖ idx LE),
/// уникальность хешей по индексу, валидация аргументов, сборка и проверка
/// пакетов, включая большие sectorCount с параллельной фазой перебора.
/// </summary>
public class VirtualIndexHasherTests
{
    /// <summary>Детерминированный псевдослучайный payload (64 байта).</summary>
    private static byte[] MakePayload(int seed) =>
        RandomBytes(PacketFormat.PayloadSize, seed);

    /// <summary>Детерминированный псевдослучайный хеш заголовка (H5).</summary>
    private static byte[] MakeHeaderHash(int seed) =>
        RandomBytes(PacketFormat.HeaderHashSize, seed);

    /// <summary>Байтовый массив, заполненный Random(seed) — воспроизводимо.</summary>
    private static byte[] RandomBytes(int len, int seed)
    {
        var b = new byte[len];
        new Random(seed).NextBytes(b);
        return b;
    }

    // ── Хеш ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Формула хеша: Trunc11(SHA-256(H5 ‖ payload ‖ idx в LE)). Индекс 0x1234
    /// сериализуется как { 0x34, 0x12 } — младший байт первым.
    /// </summary>
    [Fact]
    public void Hash_Is_TruncatedSha256OfHeaderHashPayloadIndex()
    {
        var payload = MakePayload(1);
        var headerHash = MakeHeaderHash(2);
        Span<byte> hash = stackalloc byte[VirtualIndexHasher.HashSize];

        VirtualIndexHasher.ComputeHashInto(payload, 0x1234, headerHash, hash);

        // Эталон: H5(24) ‖ payload(64) ‖ idx LE(2) = 90 байт
        var input = headerHash
            .Concat(payload)
            .Concat(new byte[] { 0x34, 0x12 })
            .ToArray();

        Assert.Equal(
            Sha256Compact.HashData(input)[..VirtualIndexHasher.HashSize],
            hash.ToArray());
    }

    /// <summary>
    /// Уникальность по индексу: 64 разных индекса при одном payload
    /// дают 64 попарно различных хеша.
    /// </summary>
    [Fact]
    public void Hash_DistinctIndices_ProduceDistinctHashes()
    {
        var payload = MakePayload(3);
        var headerHash = MakeHeaderHash(4);
        var hashes = new HashSet<string>();

        Span<byte> hash = stackalloc byte[VirtualIndexHasher.HashSize];
        for (var idx = 0; idx < 64; idx++)
        {
            VirtualIndexHasher.ComputeHashInto(payload, idx, headerHash, hash);
            hashes.Add(Convert.ToHexString(hash));
        }

        Assert.Equal(64, hashes.Count);
    }

    /// <summary>
    /// Индекс вне диапазона [0..MaxDataVolumes] вызывает
    /// ArgumentOutOfRangeException.
    /// </summary>
    [Fact]
    public void Hash_IndexOutOfRange_Throws()
    {
        var payload = MakePayload(5);
        var headerHash = MakeHeaderHash(6);
        var hash = new byte[VirtualIndexHasher.HashSize];

        Assert.Throws<ArgumentOutOfRangeException>(
            () => VirtualIndexHasher.ComputeHashInto(payload, -1, headerHash, hash));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => VirtualIndexHasher.ComputeHashInto(
                payload, PacketFormat.MaxDataVolumes + 1, headerHash, hash));
    }

    /// <summary>Нестандартная длина payload, H5 или буфера хеша — исключение.</summary>
    [Fact]
    public void Hash_WrongLengths_Throw()
    {
        var payload = MakePayload(7);
        var headerHash = MakeHeaderHash(8);
        var hash = new byte[VirtualIndexHasher.HashSize];

        Assert.Throws<ArgumentException>(
            () => VirtualIndexHasher.ComputeHashInto(
                payload[..^1], 0, headerHash, hash));
        Assert.Throws<ArgumentException>(
            () => VirtualIndexHasher.ComputeHashInto(
                payload, 0, headerHash[..^1], hash));
        Assert.Throws<ArgumentException>(
            () => VirtualIndexHasher.ComputeHashInto(
                payload, 0, headerHash, hash[..^1]));
    }

    // ── Сборка и проверка пакета ────────────────────────────────────────────

    /// <summary>
    /// TryVerifyPacket находит верный индекс при любом стартовом курсоре:
    /// перебор сканирует индексы циклически, начиная с cursor.
    /// </summary>
    [Fact]
    public void BuildPacket_ThenVerify_FindsIndexFromAnyCursor()
    {
        var payload = MakePayload(9);
        var headerHash = MakeHeaderHash(10);
        const int sectorCount = 16;

        var packet = new byte[PacketFormat.PacketSize];
        VirtualIndexHasher.BuildPacketInto(payload, 11, headerHash, packet);

        for (var cursor = 0; cursor < sectorCount; cursor++)
        {
            Assert.True(VirtualIndexHasher.TryVerifyPacket(
                packet, headerHash, sectorCount, cursor, out var index));
            Assert.Equal(11, index);
        }
    }

    /// <summary>
    /// Round-trip: пакет каждого индекса 0..sectorCount-1 собирается
    /// и успешно распознаётся с возвращением того же индекса.
    /// </summary>
    [Fact]
    public void BuildAndVerify_AllIndices_RoundTrip()
    {
        var headerHash = MakeHeaderHash(12);
        const int sectorCount = 32;

        var packet = new byte[PacketFormat.PacketSize];
        for (var idx = 0; idx < sectorCount; idx++)
        {
            var payload = MakePayload(100 + idx);
            VirtualIndexHasher.BuildPacketInto(payload, idx, headerHash, packet);

            Assert.True(VirtualIndexHasher.TryVerifyPacket(
                packet, headerHash, sectorCount, 0, out var found));
            Assert.Equal(idx, found);
        }
    }

    /// <summary>Пакет чужого файла (иной H5) не проходит проверку.</summary>
    [Fact]
    public void Verify_ForeignHeaderHash_False()
    {
        var payload = MakePayload(13);
        var headerHash = MakeHeaderHash(14);
        var foreign = MakeHeaderHash(15);

        var packet = new byte[PacketFormat.PacketSize];
        VirtualIndexHasher.BuildPacketInto(payload, 3, headerHash, packet);

        Assert.False(VirtualIndexHasher.TryVerifyPacket(
            packet, foreign, 8, 0, out _));
    }

    /// <summary>Порча бита payload — ни один индекс не даёт совпадения.</summary>
    [Fact]
    public void Verify_DamagedPayload_False()
    {
        var payload = MakePayload(16);
        var headerHash = MakeHeaderHash(17);

        var packet = new byte[PacketFormat.PacketSize];
        VirtualIndexHasher.BuildPacketInto(payload, 2, headerHash, packet);
        packet[40] ^= 0x80;

        Assert.False(VirtualIndexHasher.TryVerifyPacket(
            packet, headerHash, 8, 0, out _));
    }

    /// <summary>Порча поля хеша внутри пакета — проверка не проходит.</summary>
    [Fact]
    public void Verify_DamagedHashField_False()
    {
        var payload = MakePayload(18);
        var headerHash = MakeHeaderHash(19);

        var packet = new byte[PacketFormat.PacketSize];
        VirtualIndexHasher.BuildPacketInto(payload, 2, headerHash, packet);
        packet[VirtualIndexHasher.HashOffset] ^= 0x01;

        Assert.False(VirtualIndexHasher.TryVerifyPacket(
            packet, headerHash, 8, 0, out _));
    }

    /// <summary>
    /// Индекс, выходящий за пределы sectorCount, не принимается:
    /// пакет сектора 7 при всём 7 секторов (0..6) отвергается.
    /// </summary>
    [Fact]
    public void Verify_IndexBeyondSectorCount_False()
    {
        var payload = MakePayload(20);
        var headerHash = MakeHeaderHash(21);

        // Пакет сектора 7, но в файле всего 7 секторов (0..6)
        var packet = new byte[PacketFormat.PacketSize];
        VirtualIndexHasher.BuildPacketInto(payload, 7, headerHash, packet);

        Assert.False(VirtualIndexHasher.TryVerifyPacket(
            packet, headerHash, 7, 0, out _));
    }

    /// <summary>
    /// Схемы не взаимозаменяемы: корректный классический секторный пакет
    /// (с явным номером сектора) VirtualIndex-проверкой не принимается.
    /// </summary>
    [Fact]
    public void Verify_ClassicSectorPacket_NotAccepted()
    {
        var headerHash = MakeHeaderHash(22);

        // Классический пакет: idx(2) ‖ payload(64) ‖ Trunc9(9)
        var classic = new byte[PacketFormat.PacketSize];
        classic[0] = 5;
        classic[1] = 0;
        MakePayload(23).CopyTo(classic, PacketFormat.SectorNumberSize);
        PacketHasher.ComputeSectorHash(classic.AsSpan(0, PacketFormat.SectorContentSize), headerHash)
            .CopyTo(classic, PacketFormat.SectorHashOffset);

        Assert.False(VirtualIndexHasher.TryVerifyPacket(
            classic, headerHash, 16, 0, out _));
    }

    // ── Параллельная фаза перебора (длинный остаток) ───────────────────────

    /// <summary>
    /// Большой sectorCount: индекс 4000 лежит в параллельной фазе перебора
    /// (за пределами короткого префикса) и находится с любого курсора.
    /// </summary>
    [Fact]
    public void Verify_LargeK_FarIndexBeyondParallelThreshold_Found()
    {
        var payload = MakePayload(30);
        var headerHash = MakeHeaderHash(31);
        const int sectorCount = 4096;

        // Шаг 4000 попадает в параллельную фазу (после префикса 64)
        var packet = new byte[PacketFormat.PacketSize];
        VirtualIndexHasher.BuildPacketInto(payload, 4000, headerHash, packet);

        for (var cursor = 0; cursor < sectorCount; cursor += 517)
        {
            Assert.True(VirtualIndexHasher.TryVerifyPacket(
                packet, headerHash, sectorCount, cursor, out var index));
            Assert.Equal(4000, index);
        }
    }

    /// <summary>Крайний случай: последний индекс sectorCount-1 находится корректно.</summary>
    [Fact]
    public void Verify_LargeK_LastIndex_Found()
    {
        var payload = MakePayload(32);
        var headerHash = MakeHeaderHash(33);
        const int sectorCount = 3000;

        var packet = new byte[PacketFormat.PacketSize];
        VirtualIndexHasher.BuildPacketInto(payload, sectorCount - 1, headerHash, packet);

        Assert.True(VirtualIndexHasher.TryVerifyPacket(
            packet, headerHash, sectorCount, 0, out var index));
        Assert.Equal(sectorCount - 1, index);
    }

    /// <summary>
    /// Отрицательный контроль параллельной фазы: пакет, собранный с чужим H5,
    /// не совпадает ни с одним индексом при полном сканировании.
    /// </summary>
    [Fact]
    public void Verify_LargeK_NoMatch_FullParallelScan_False()
    {
        var payload = MakePayload(34);
        var headerHash = MakeHeaderHash(35);
        var foreign = MakeHeaderHash(36);
        const int sectorCount = 4096;

        var packet = new byte[PacketFormat.PacketSize];
        VirtualIndexHasher.BuildPacketInto(payload, 0, foreign, packet);

        Assert.False(VirtualIndexHasher.TryVerifyPacket(
            packet, headerHash, sectorCount, 0, out _));
    }

    /// <summary>
    /// Согласованность фаз: на границе порога параллельной фазы
    /// (sectorCount = 1100) все проверяемые индексы находятся корректно.
    /// </summary>
    [Fact]
    public void Verify_LargeK_MatchesSequentialResult_ForAllIndices()
    {
        var headerHash = MakeHeaderHash(37);
        const int sectorCount = 1100; // остаток чуть выше порога фазы 2

        var packet = new byte[PacketFormat.PacketSize];
        for (var idx = 0; idx < sectorCount; idx += 97)
        {
            var payload = MakePayload(200 + idx);
            VirtualIndexHasher.BuildPacketInto(payload, idx, headerHash, packet);

            Assert.True(VirtualIndexHasher.TryVerifyPacket(
                packet, headerHash, sectorCount, 0, out var found));
            Assert.Equal(idx, found);
        }
    }

    /// <summary>
    /// Некорректные аргументы TryVerifyPacket (длины, sectorCount ≤ 0,
    /// sectorCount сверх лимита) возвращают false без исключений.
    /// </summary>
    [Fact]
    public void Verify_InvalidArguments_False()
    {
        var payload = MakePayload(24);
        var headerHash = MakeHeaderHash(25);

        var packet = new byte[PacketFormat.PacketSize];
        VirtualIndexHasher.BuildPacketInto(payload, 0, headerHash, packet);

        Assert.False(VirtualIndexHasher.TryVerifyPacket(
            packet[..^1], headerHash, 8, 0, out _));
        Assert.False(VirtualIndexHasher.TryVerifyPacket(
            packet, headerHash[..^1], 8, 0, out _));
        Assert.False(VirtualIndexHasher.TryVerifyPacket(
            packet, headerHash, 0, 0, out _));
        Assert.False(VirtualIndexHasher.TryVerifyPacket(
            packet, headerHash, -1, 0, out _));
        Assert.False(VirtualIndexHasher.TryVerifyPacket(
            packet, headerHash,
            PacketFormat.MaxDataVolumes + 1, 0, out _));
    }

    /// <summary>Сборка в буфер нестандартной длины — ArgumentException.</summary>
    [Fact]
    public void BuildPacket_WrongPacketLength_Throws()
    {
        var payload = MakePayload(26);
        var headerHash = MakeHeaderHash(27);

        Assert.Throws<ArgumentException>(
            () => VirtualIndexHasher.BuildPacketInto(
                payload, 0, headerHash,
                new byte[PacketFormat.PacketSize - 1]));
    }
}
