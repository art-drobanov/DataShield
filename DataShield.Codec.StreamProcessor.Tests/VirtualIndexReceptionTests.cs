using DataShield.Codec;
using DataShield.Codec.Packets;
using DataShield.Codec.StreamProcessor;
using DataShield.Codec.StreamProcessor.Versions;
using DataShield.Interfaces;
using Xunit;

namespace DataShield.Codec.StreamProcessor.Tests;

// ─────────────────────────────────────────────────────────────────────────────
//  Приём секторов с виртуальным индексом на уровне накопителя
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Тесты приёма VirtualIndex-секторов на уровне StreamProcessor:
/// порядок прихода не важен (индекс «запечён» в хеш), схема включается
/// только в пределах порога virtualIndexSectorLimit и несовместима
/// с Classic-процессором; гибридный режим допускает оба вида пакетов
/// в одном слоте. Чужой H5, как обычно, отбрасывается.
/// </summary>
public class VirtualIndexReceptionTests
{
    /// <summary>
    /// Синхронный источник с ручной прокачкой: Pump() однократно
    /// «выдаёт» весь массив как одну порцию данных.
    /// </summary>
    private sealed class ManualSource : IDataSource
    {
        private readonly byte[] _data;

        public ManualSource(byte[] data) => _data = data;

        public int BufferSize => Math.Max(1, _data.Length);
        public bool IsRunning { get; private set; }
        public Task Completion => Task.CompletedTask;
        public Exception? Error => null;
        public event DataReadyHandler? DataReady;

        // Повторный вызов take() после первого отдаёт пустой массив
        public void Pump()
        {
            IsRunning = true;
            var taken = false;
            DataReady?.Invoke(() =>
            {
                if (taken) return Array.Empty<byte>();
                taken = true;
                return _data;
            });
            IsRunning = false;
        }

        public void Start() => Pump();
        public void Stop() => IsRunning = false;
    }

    /// <summary>Детерминированный псевдослучайный файл длины length.</summary>
    private static byte[] MakeFile(int length, int seed)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    /// <summary>Корректное содержимое заголовка для файла с eccCount ECC-томами.</summary>
    private static HeaderContent MakeHeader(byte[] file, int eccCount) => new()
    {
        FileName = "test.bin",
        FileSize = (uint)file.Length,
        Sha256 = Sha256Compact.HashData(file),
        EccCount = (ushort)eccCount,
    };

    /// <summary>Собрать валидный заголовочный пакет: содержимое + хеш H5.</summary>
    private static byte[] MakeHeaderPacket(HeaderContent header)
    {
        var packet = new byte[PacketFormat.PacketSize];
        header.WriteTo(packet);
        PacketHasher.ComputeHeaderHash(
                packet.AsSpan(0, PacketFormat.HeaderContentSize))
            .CopyTo(packet, PacketFormat.HeaderHashOffset);
        return packet;
    }

    /// <summary>Отличимый от других вариантов payload фиксированной длины.</summary>
    private static byte[] Payload(int variant) =>
        Enumerable.Range(0, PacketFormat.PayloadSize)
            .Select(i => unchecked((byte)(variant * 31 + i * 17)))
            .ToArray();

    /// <summary>Пакет схемы VirtualIndex: payload ‖ Trunc11(H5 ‖ payload ‖ idx).</summary>
    private static byte[] MakeVirtualSectorPacket(
        int sectorIndex, byte[] payload, byte[] headerHash)
    {
        var packet = new byte[PacketFormat.PacketSize];
        VirtualIndexHasher.BuildPacketInto(payload, sectorIndex, headerHash, packet);
        return packet;
    }

    /// <summary>
    /// Прогнать пакеты через процессор с заданной схемой и порогом,
    /// затем завершить приём.
    /// </summary>
    private static StreamProcessor Run(
        SectorScheme scheme, int limit, params byte[][] packets)
    {
        var processor = new StreamProcessor(
            sectorScheme: scheme,
            virtualIndexSectorLimit: limit);
        var source = new ManualSource(packets.SelectMany(p => p).ToArray());
        processor.Attach(source);
        source.Pump();
        processor.Complete();
        processor.Detach();
        return processor;
    }

    // ── Приём потока схемы VirtualIndex ─────────────────────────────────────

    /// <summary>
    /// Секторы в порядке следования накапливаются в слоте; payload
    /// оказывается под своим виртуальным индексом.
    /// </summary>
    [Fact]
    public void VirtualIndexSectors_InStreamOrder_AccumulateInSlot()
    {
        var file = MakeFile(PacketFormat.PayloadSize * 3, 1);
        var headerPacket = MakeHeaderPacket(MakeHeader(file, eccCount: 0));
        var h5 = headerPacket[PacketFormat.HeaderHashOffset..].ToArray();

        var packets = new List<byte[]> { headerPacket };
        for (var i = 0; i < 3; i++)
            packets.Add(MakeVirtualSectorPacket(i, Payload(i), h5));

        var processor = Run(
            SectorScheme.VirtualIndex, VirtualIndexHasher.DefaultSectorLimit,
            packets.ToArray());

        var slot = processor.Slots.Single();
        Assert.Equal(3, slot.ReceivedSectorCount);
        Assert.Equal(3, slot.ReceivedSectorCopyCount);
        Assert.Equal(new[] { true, true, true }, slot.BuildValidityMap());

        // Payload сектора 2 — под индексом 2
        var versions = slot.GetSectorVersions(2);
        Assert.Single(versions);
        Assert.Equal(Payload(2), versions[0].Payload);
    }

    /// <summary>
    /// Обратный порядок прихода не мешает: индекс определяется перебором
    /// из хеша, а не позицией в потоке.
    /// </summary>
    [Fact]
    public void VirtualIndexSectors_ReverseOrder_StillAccepted()
    {
        var file = MakeFile(PacketFormat.PayloadSize * 3, 2);
        var headerPacket = MakeHeaderPacket(MakeHeader(file, eccCount: 0));
        var h5 = headerPacket[PacketFormat.HeaderHashOffset..].ToArray();

        var packets = new List<byte[]> { headerPacket };
        for (var i = 2; i >= 0; i--)
            packets.Add(MakeVirtualSectorPacket(i, Payload(i), h5));

        var processor = Run(
            SectorScheme.VirtualIndex, VirtualIndexHasher.DefaultSectorLimit,
            packets.ToArray());

        var slot = processor.Slots.Single();
        Assert.Equal(3, slot.ReceivedSectorCount);
        Assert.Equal(new[] { true, true, true }, slot.BuildValidityMap());
    }

    /// <summary>
    /// Несовместимость схем: процессор в режиме Classic читает заголовок
    /// (формат общий), но VirtualIndex-секторы не принимает.
    /// </summary>
    [Fact]
    public void VirtualIndexSectors_ClassicProcessor_Rejects()
    {
        var file = MakeFile(PacketFormat.PayloadSize * 2, 3);
        var headerPacket = MakeHeaderPacket(MakeHeader(file, eccCount: 0));
        var h5 = headerPacket[PacketFormat.HeaderHashOffset..].ToArray();

        var packets = new List<byte[]> { headerPacket };
        for (var i = 0; i < 2; i++)
            packets.Add(MakeVirtualSectorPacket(i, Payload(i), h5));

        var processor = Run(SectorScheme.Classic, VirtualIndexHasher.DefaultSectorLimit,
            packets.ToArray());

        // Заголовок читается (формат общий), секторы — нет
        var slot = processor.Slots.Single();
        Assert.Equal(1, slot.HeaderReceptionCount);
        Assert.Equal(0, slot.ReceivedSectorCount);
    }

    /// <summary>
    /// Порог применимости: N+M выше virtualIndexSectorLimit — слот
    /// проверяется только по схеме Classic, VirtualIndex-секторы не приняты.
    /// </summary>
    [Fact]
    public void VirtualIndexSectors_SlotAboveLimit_Rejected()
    {
        var file = MakeFile(PacketFormat.PayloadSize * 3, 4);
        var headerPacket = MakeHeaderPacket(MakeHeader(file, eccCount: 0));
        var h5 = headerPacket[PacketFormat.HeaderHashOffset..].ToArray();

        var packets = new List<byte[]> { headerPacket };
        for (var i = 0; i < 3; i++)
            packets.Add(MakeVirtualSectorPacket(i, Payload(i), h5));

        // Порог 2 < N+M = 3: слот выше порога — проверка только Classic
        var processor = Run(SectorScheme.VirtualIndex, 2, packets.ToArray());

        Assert.Equal(0, processor.Slots.Single().ReceivedSectorCount);
    }

    /// <summary>
    /// Гибридный приём: классический пакет и VirtualIndex-пакет
    /// накапливаются в одном слоте (двойная проверка хешей).
    /// </summary>
    [Fact]
    public void Hybrid_ClassicAndVirtualSectors_AccumulateInSameSlot()
    {
        var file = MakeFile(PacketFormat.PayloadSize * 2, 5);
        var headerPacket = MakeHeaderPacket(MakeHeader(file, eccCount: 0));
        var h5 = headerPacket[PacketFormat.HeaderHashOffset..].ToArray();

        // Сектор 0 — классический пакет, сектор 1 — виртуальный индекс
        var classic = new byte[PacketFormat.PacketSize];
        classic[0] = 0;
        classic[1] = 0;
        Payload(0).CopyTo(classic, PacketFormat.SectorNumberSize);
        PacketHasher.ComputeSectorHash(
                classic.AsSpan(0, PacketFormat.SectorContentSize), h5)
            .CopyTo(classic, PacketFormat.SectorHashOffset);

        var virtualPacket = MakeVirtualSectorPacket(1, Payload(1), h5);

        var processor = Run(
            SectorScheme.VirtualIndex, VirtualIndexHasher.DefaultSectorLimit,
            headerPacket, classic, virtualPacket);

        var slot = processor.Slots.Single();
        Assert.Equal(2, slot.ReceivedSectorCount);
        Assert.Equal(new[] { true, true }, slot.BuildValidityMap());
    }

    /// <summary>VirtualIndex-пакет с чужим H5 не прикрепляется к слоту.</summary>
    [Fact]
    public void VirtualIndexSectors_ForeignHeader_Rejected()
    {
        var file = MakeFile(PacketFormat.PayloadSize * 2, 6);
        var headerPacket = MakeHeaderPacket(MakeHeader(file, eccCount: 0));
        var foreignH5 = MakeHeaderPacket(MakeHeader(MakeFile(100, 7), eccCount: 0))
            [PacketFormat.HeaderHashOffset..].ToArray();

        var foreign = MakeVirtualSectorPacket(0, Payload(0), foreignH5);

        var processor = Run(
            SectorScheme.VirtualIndex, VirtualIndexHasher.DefaultSectorLimit,
            headerPacket, foreign);

        Assert.Equal(0, processor.Slots.Single().ReceivedSectorCount);
    }

    /// <summary>
    /// Валидация конструктора: неизвестная схема, нулевой или превышающий
    /// DefaultSectorLimit порог — ArgumentOutOfRangeException.
    /// </summary>
    [Fact]
    public void Constructor_InvalidArguments_Throw()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new StreamProcessor(
                searchOptions: new SectorVersionSearchOptions(),
                sectorScheme: (SectorScheme)99));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new StreamProcessor(sectorScheme: SectorScheme.VirtualIndex,
                virtualIndexSectorLimit: 0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new StreamProcessor(sectorScheme: SectorScheme.VirtualIndex,
                virtualIndexSectorLimit: VirtualIndexHasher.DefaultSectorLimit + 1));
    }
}
