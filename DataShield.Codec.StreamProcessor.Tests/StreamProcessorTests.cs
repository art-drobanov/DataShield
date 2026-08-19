using DataShield.Codec.Packets;
using DataShield.Codec.StreamProcessor;
using DataShield.Codec.StreamProcessor.Versions;
using DataShield.Interfaces;

namespace DataShield.Codec.StreamProcessor.Tests;

// ─────────────────────────────────────────────────────────────────────────────
//  Накопитель приёма: классификация и оценка содержимого
// ─────────────────────────────────────────────────────────────────────────────

public sealed class StreamProcessorTests
{
    // ── Тестовые двойники и конструкторы пакетов ────────────────────────────

    /// <summary>Синхронный источник с ручной прокачкой.</summary>
    private sealed class ManualSource : IDataSource
    {
        private readonly byte[] _data;

        public ManualSource(byte[] data) => _data = data;

        public int BufferSize => Math.Max(1, _data.Length);
        public bool IsRunning { get; private set; }
        public Task Completion => Task.CompletedTask;
        public Exception? Error => null;
        public event DataReadyHandler? DataReady;

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

    private static byte[] MakeFile(int length, int seed)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    private static HeaderContent MakeHeader(byte[] file, int eccCount) => new()
    {
        FileName = "test.bin",
        FileSize = (uint)file.Length,
        Sha256 = Sha256Compact.HashData(file),
        EccCount = (ushort)eccCount,
    };

    private static byte[] MakeHeaderPacket(HeaderContent header)
    {
        var packet = new byte[PacketFormat.PacketSize];
        header.WriteTo(packet);
        PacketHasher.ComputeHeaderHash(
                packet.AsSpan(0, PacketFormat.HeaderContentSize))
            .CopyTo(packet, PacketFormat.HeaderHashOffset);
        return packet;
    }

    private static byte[] MakeSectorPacket(int sectorNum, byte[] payload, byte[] headerHash)
    {
        var packet = new byte[PacketFormat.PacketSize];
        packet[0] = (byte)(sectorNum & 0xFF);
        packet[1] = (byte)((sectorNum >> 8) & 0xFF);
        payload.AsSpan().CopyTo(packet.AsSpan(
            PacketFormat.SectorNumberSize, PacketFormat.PayloadSize));
        PacketHasher.ComputeSectorHash(
                packet.AsSpan(0, PacketFormat.SectorContentSize), headerHash)
            .CopyTo(packet, PacketFormat.SectorHashOffset);
        return packet;
    }

    private static StreamProcessor Run(params byte[][] packets)
    {
        var processor = new StreamProcessor();
        var source = new ManualSource(packets.SelectMany(p => p).ToArray());
        processor.Attach(source);
        source.Pump();
        processor.Complete();
        processor.Detach();
        return processor;
    }

    private static byte[] Payload(int variant) =>
        Enumerable.Range(0, PacketFormat.PayloadSize)
            .Select(i => unchecked((byte)(variant * 31 + i * 17)))
            .ToArray();

    // ── Заголовки ───────────────────────────────────────────────────────────

    [Fact]
    public void HeaderPacket_CreatesSlotAndFiresEvent()
    {
        var file = MakeFile(200, 1);
        var header = MakeHeader(file, eccCount: 3);
        var headerPacket = MakeHeaderPacket(header);

        HeaderContent? acceptedHeader = null;
        byte[]? acceptedHash = null;

        var processor = new StreamProcessor();
        processor.HeaderAccepted += (h, hash) => { acceptedHeader = h; acceptedHash = hash; };
        var source = new ManualSource(headerPacket);
        processor.Attach(source);
        source.Pump();
        processor.Complete();

        Assert.Equal(1, processor.FileCount);
        Assert.NotNull(acceptedHeader);
        Assert.Equal("test.bin", acceptedHeader.GetValueOrDefault().FileName);
        Assert.Equal(
            headerPacket.AsSpan(PacketFormat.HeaderHashOffset).ToArray(),
            acceptedHash);

        var slot = processor.Slots.Single();
        Assert.Equal(1, slot.HeaderReceptionCount);
        Assert.Equal(4, slot.DataVolumeCount);   // ceil(200/64)
        Assert.Equal(3, slot.EccCount);
        Assert.Equal(7, slot.TotalVolumeCount);
    }

    [Fact]
    public void HeaderPacketCopies_IncrementReceptionCount_SingleEvent()
    {
        var file = MakeFile(100, 2);
        var headerPacket = MakeHeaderPacket(MakeHeader(file, eccCount: 0));

        var events = 0;
        var processor = new StreamProcessor();
        processor.HeaderAccepted += (_, _) => events++;

        var source = new ManualSource(headerPacket.Concat(headerPacket).Concat(headerPacket).ToArray());
        processor.Attach(source);
        source.Pump();
        processor.Complete();

        Assert.Equal(1, processor.FileCount);
        Assert.Equal(3, processor.Slots.Single().HeaderReceptionCount);
        Assert.Equal(1, events);
    }

    [Fact]
    public void CorruptedHeader_Rejected()
    {
        var file = MakeFile(100, 3);
        var headerPacket = MakeHeaderPacket(MakeHeader(file, eccCount: 0));
        headerPacket[10] ^= 0xFF; // ломаем содержимое — хеш не сойдётся

        var processor = Run(headerPacket);

        Assert.Equal(0, processor.FileCount);
    }

    // ── Секторы данных ──────────────────────────────────────────────────────

    [Fact]
    public void SectorPockets_AccumulateInSlot()
    {
        var file = MakeFile(PacketFormat.PayloadSize * 3, 4);
        var header = MakeHeader(file, eccCount: 0);
        var headerPacket = MakeHeaderPacket(header);
        var h5 = headerPacket[PacketFormat.HeaderHashOffset..].ToArray();

        var sectors = Enumerable.Range(0, 3)
            .Select(i => MakeSectorPacket(i, Payload(i), h5))
            .ToArray();

        var processor = Run(
            headerPacket.Concat(sectors.SelectMany(p => p)).ToArray());

        var slot = processor.Slots.Single();
        Assert.Equal(3, slot.ReceivedSectorCount);
        Assert.Equal(3, processor.TotalReceivedSectorCount);
        Assert.Equal(0, processor.TotalCollisionSectorCount);
        Assert.Equal(new[] { true, true, true }, slot.BuildValidityMap());
    }

    [Fact]
    public void SectorBeforeHeader_RejectedByForwardPass()
    {
        // Сектор приходит раньше заголовка: без слота хеш не проверяется
        var file = MakeFile(PacketFormat.PayloadSize, 5);
        var header = MakeHeader(file, eccCount: 0);
        var headerPacket = MakeHeaderPacket(header);
        var h5 = headerPacket[PacketFormat.HeaderHashOffset..].ToArray();

        var processor = Run(MakeSectorPacket(0, Payload(0), h5));

        Assert.Equal(0, processor.FileCount);
    }

    [Fact]
    public void SectorWithWrongHash_Rejected()
    {
        var file = MakeFile(PacketFormat.PayloadSize, 6);
        var headerPacket = MakeHeaderPacket(MakeHeader(file, eccCount: 0));
        var h5 = headerPacket[PacketFormat.HeaderHashOffset..].ToArray();

        var sector = MakeSectorPacket(0, Payload(0), h5);
        sector[5] ^= 0xFF; // ломаем payload без пересчёта хеша

        var processor = Run(headerPacket, sector);

        Assert.Equal(0, processor.Slots.Single().ReceivedSectorCount);
    }

    [Fact]
    public void SectorNumberOutOfRange_Rejected()
    {
        var file = MakeFile(PacketFormat.PayloadSize, 7);
        var headerPacket = MakeHeaderPacket(MakeHeader(file, eccCount: 0));
        var h5 = headerPacket[PacketFormat.HeaderHashOffset..].ToArray();

        // Файл из одного сектора; номер 1 вне диапазона
        var sector = MakeSectorPacket(1, Payload(1), h5);

        var processor = Run(headerPacket, sector);

        Assert.Equal(0, processor.Slots.Single().ReceivedSectorCount);
    }

    [Fact]
    public void SectorOfOtherHeader_Rejected()
    {
        var file = MakeFile(PacketFormat.PayloadSize, 8);
        var otherFile = MakeFile(PacketFormat.PayloadSize, 9);
        var headerPacket = MakeHeaderPacket(MakeHeader(file, eccCount: 0));
        var otherH5 = MakeHeaderPacket(MakeHeader(otherFile, eccCount: 0))
            [PacketFormat.HeaderHashOffset..].ToArray();

        var foreignSector = MakeSectorPacket(0, Payload(0), otherH5);

        var processor = Run(headerPacket, foreignSector);

        Assert.Equal(0, processor.Slots.Single().ReceivedSectorCount);
    }

    [Fact]
    public void PacketSplitAcrossChunks_IsAssembled()
    {
        var file = MakeFile(PacketFormat.PayloadSize * 2, 10);
        var header = MakeHeader(file, eccCount: 0);
        var headerPacket = MakeHeaderPacket(header);
        var h5 = headerPacket[PacketFormat.HeaderHashOffset..].ToArray();

        var stream = headerPacket
            .Concat(MakeSectorPacket(0, Payload(0), h5))
            .Concat(MakeSectorPacket(1, Payload(1), h5))
            .ToArray();

        var processor = new StreamProcessor();
        processor.DataReady += _ => { };

        // Пакеты режутся произвольными кусками по 17 байт
        for (var offset = 0; offset < stream.Length; offset += 17)
        {
            var slice = stream.AsSpan(offset,
                Math.Min(17, stream.Length - offset)).ToArray();
            var source = new ManualSource(slice);
            processor.Attach(source);
            source.Pump();
        }
        processor.Complete();

        Assert.Equal(2, processor.Slots.Single().ReceivedSectorCount);
    }

    // ── Предикат распознавания ──────────────────────────────────────────────

    [Fact]
    public void Recognizes_HeadersAndKnownSectors()
    {
        var file = MakeFile(PacketFormat.PayloadSize, 11);
        var header = MakeHeader(file, eccCount: 0);
        var headerPacket = MakeHeaderPacket(header);
        var h5 = headerPacket[PacketFormat.HeaderHashOffset..].ToArray();
        var sector = MakeSectorPacket(0, Payload(0), h5);

        var processor = new StreamProcessor();
        var source = new ManualSource(headerPacket);
        processor.Attach(source);
        source.Pump();

        Assert.True(processor.Recognizes(headerPacket));
        Assert.True(processor.Recognizes(sector));
        Assert.False(processor.Recognizes(MakeSectorPacket(5, Payload(5), h5)));
        Assert.False(processor.Recognizes(new byte[10]));
    }

    // ── Оценка содержимого ──────────────────────────────────────────────────

    [Fact]
    public void CollisionMap_ReportsMultiplicity()
    {
        var file = MakeFile(PacketFormat.PayloadSize * 2, 12);
        var headerPacket = MakeHeaderPacket(MakeHeader(file, eccCount: 0));
        var h5 = headerPacket[PacketFormat.HeaderHashOffset..].ToArray();

        var packets = new List<byte[]> { headerPacket };
        // Сектор 0: три конкурирующие версии
        for (var v = 0; v < 3; v++)
            packets.Add(MakeSectorPacket(0, Payload(v), h5));
        // Сектор 1: одна версия, дважды
        packets.Add(MakeSectorPacket(1, Payload(9), h5));
        packets.Add(MakeSectorPacket(1, Payload(9), h5));

        var processor = Run(packets.ToArray());

        var slot = processor.Slots.Single();
        Assert.Equal(1, slot.CollisionSectorCount);
        Assert.Equal(1, processor.TotalCollisionSectorCount);
        Assert.Equal(2, slot.ReceivedSectorCount);
        Assert.Equal(5, slot.ReceivedSectorCopyCount);
        Assert.Equal(5, processor.TotalReceivedSectorCopyCount);

        var collisionMap = slot.BuildCollisionMap();
        var entry = Assert.Single(collisionMap);
        Assert.Equal(0, entry.Key);
        Assert.Equal(3, entry.Value);

        // Кратность подтверждений каждой версии
        var versions = slot.GetSectorVersions(0);
        Assert.Equal(3, versions.Count);
        Assert.All(versions, v => Assert.Equal(1, v.ConfirmationCount));
    }
}
