using DataShield.Codec;
using DataShield.Codec.Packets;
using DataShield.Codec.StreamProcessor;
using Xunit;

namespace DataShield.Tests;

// ─────────────────────────────────────────────────────────────────────────────
//  Схема секторов VirtualIndex: сквозные пути кодер → декодер
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Сквозные тесты схемы секторов VirtualIndex на уровне кодер → декодер:
/// дефолты (Classic) и валидация конструкторов, бит-в-бит roundtrip
/// (бинарный и Base64-текст), гибридность (VirtualIndex-декодер читает
/// Classic-поток, обратное — нет), гейт по числу томов N+M ≤ порога
/// (512 по умолчанию) и адресная перепривязка секторов, пришедших
/// раньше заголовка.
/// </summary>
public class VirtualIndexCodecTests
{
    /// <summary>Детерминированный псевдослучайный массив.</summary>
    private static byte[] RandomBytes(int len, int seed)
    {
        var b = new byte[len];
        new Random(seed).NextBytes(b);
        return b;
    }

    /// <summary>Закодировать файл схемой VirtualIndex в бинарный поток.</summary>
    private static byte[] EncodeB(byte[] content, string name, int eccPercent = 10) =>
        PacketIO.WriteBinaryBytes(
            new StreamEncoder(eccPercent, 3, SectorScheme.VirtualIndex)
                .Encode(content, name));

    /// <summary>Сканировать бинарный поток VirtualIndex-декодером.</summary>
    private static StreamDecoder DecodeB(byte[] stream)
    {
        var decoder = new StreamDecoder(sectorScheme: SectorScheme.VirtualIndex);
        decoder.Scan(stream);
        return decoder;
    }

    // ── Конструкторы: дефолты и валидация ───────────────────────────────────

    /// <summary>Без явного указания схемы все компоненты работают в Classic.</summary>
    [Fact]
    public void Defaults_AreClassic()
    {
        Assert.Equal(SectorScheme.Classic, new StreamEncoder().SectorScheme);
        Assert.Equal(SectorScheme.Classic, new StreamDecoder().SectorScheme);
        Assert.Equal(
            VirtualIndexHasher.DefaultSectorLimit,
            new StreamEncoder().VirtualIndexSectorLimit);
    }

    /// <summary>Неизвестная схема и некорректный порог — исключения конструкторов.</summary>
    [Fact]
    public void Constructor_InvalidArguments_Throw()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new StreamEncoder(sectorScheme: (SectorScheme)5));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new StreamEncoder(sectorScheme: SectorScheme.VirtualIndex,
                virtualIndexSectorLimit: 0));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new StreamDecoder(sectorScheme: (SectorScheme)5));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new StreamDecoder(sectorScheme: SectorScheme.VirtualIndex,
                virtualIndexSectorLimit: PacketFormat.MaxDataVolumes + 1));
    }

    // ── Сквозной roundtrip ──────────────────────────────────────────────────

    /// <summary>
    /// Бинарный roundtrip: полный набор томов, карта валидности вся true,
    /// файл собирается бит-в-бит при разных размерах и процентах ECC.
    /// </summary>
    [Theory]
    [InlineData(1, 0)]      // 1 байт, без ECC
    [InlineData(64, 0)]     // ровно один сектор
    [InlineData(1000, 0)]
    [InlineData(5000, 10)]  // с ECC
    [InlineData(20000, 25)] // ~313 + 79 томов
    public void Roundtrip_Binary_AssemblesBitPerfect(int size, int eccPercent)
    {
        var content = RandomBytes(size, size);
        var stream = EncodeB(content, "vi.bin", eccPercent);

        var decoder = DecodeB(stream);

        var slot = Assert.Single(decoder.Slots);
        Assert.Equal(slot.TotalVolumeCount, slot.ReceivedSectorCount);
        Assert.All(slot.BuildValidityMap(), Assert.True);

        var restored = decoder.TryAssemble(slot.Header);
        Assert.NotNull(restored);
        Assert.Equal(content, restored);
    }

    /// <summary>Base64-текстовый roundtrip через EncodeToText/Scan(lines).</summary>
    [Fact]
    public void Roundtrip_Base64Text_AssemblesBitPerfect()
    {
        var content = RandomBytes(3000, 77);
        var text = new StreamEncoder(10, 3, SectorScheme.VirtualIndex)
            .EncodeToText(content, "vi.txt");
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        var decoder = new StreamDecoder(sectorScheme: SectorScheme.VirtualIndex);
        decoder.Scan(lines);

        var slot = Assert.Single(decoder.Slots);
        var restored = decoder.TryAssemble(slot.Header);
        Assert.NotNull(restored);
        Assert.Equal(content, restored);
    }

    // ── Гибридность и совместимость ─────────────────────────────────────────

    /// <summary>
    /// Гибридность: VirtualIndex-декодер принимает и Classic-секторы
    /// (проверяет обе схемы).
    /// </summary>
    [Fact]
    public void Hybrid_ClassicStream_ReadByVirtualIndexDecoder()
    {
        var content = RandomBytes(2000, 88);
        var stream = PacketIO.WriteBinaryBytes(
            new StreamEncoder(10).Encode(content, "classic.bin"));

        var decoder = DecodeB(stream);

        var restored = decoder.TryAssemble(decoder.Slots.Single().Header);
        Assert.NotNull(restored);
        Assert.Equal(content, restored);
    }

    /// <summary>
    /// Обратная совместимость: чистый Classic-декодер VirtualIndex-секторы
    /// не принимает (заголовок общий — читается, секторы — нет).
    /// </summary>
    [Fact]
    public void ClassicDecoder_RejectsVirtualIndexSectors()
    {
        var content = RandomBytes(1000, 99);
        var stream = EncodeB(content, "vi.bin", eccPercent: 0);

        var decoder = new StreamDecoder();
        decoder.Scan(stream);

        // Заголовок (общий формат) читается, секторы VirtualIndex — нет
        var slot = Assert.Single(decoder.Slots);
        Assert.Equal(0, slot.ReceivedSectorCount);
        Assert.Null(decoder.TryAssemble(slot.Header));
    }

    // ── Гейт по числу томов ─────────────────────────────────────────────────

    /// <summary>Гейт: N+M выше порога — кодирование схемой запрещено.</summary>
    [Fact]
    public void Encoder_AboveLimit_Throws()
    {
        // 5 секторов данных + ECC 100% = 10 томов; порог 4 — отказ
        var content = RandomBytes(5 * PacketFormat.PayloadSize, 5);

        Assert.Throws<InvalidOperationException>(
            () => new StreamEncoder(
                    100, 3,
                    SectorScheme.VirtualIndex,
                    virtualIndexSectorLimit: 4)
                .Encode(content, "gate.bin"));
    }

    /// <summary>Гейт: ровно на пороге кодирование разрешено.</summary>
    [Fact]
    public void Encoder_ExactlyAtLimit_Encodes()
    {
        // 2 сектора данных без ECC = 2 тома; порог 2 — разрешено
        var content = RandomBytes(2 * PacketFormat.PayloadSize, 6);

        var packets = new StreamEncoder(
                0, 3, SectorScheme.VirtualIndex, virtualIndexSectorLimit: 2)
            .Encode(content, "gate.bin");

        Assert.NotEmpty(packets);
    }

    /// <summary>Порог по умолчанию 512: ровно 512 томов — кодируется.</summary>
    [Fact]
    public void Encoder_DefaultLimit512_ExactlyAtLimit_Encodes()
    {
        // 512 секторов данных без ECC = 512 томов = потолок по умолчанию
        var content = RandomBytes(512 * PacketFormat.PayloadSize, 7);

        var packets = new StreamEncoder(0, 3, SectorScheme.VirtualIndex)
            .Encode(content, "gate512.bin");

        Assert.NotEmpty(packets);
    }

    /// <summary>Порог по умолчанию 512: 513 томов — InvalidOperationException.</summary>
    [Fact]
    public void Encoder_DefaultLimit512_Above_Throws()
    {
        // 513 секторов данных без ECC — выше потолка 512
        var content = RandomBytes(513 * PacketFormat.PayloadSize, 8);

        Assert.Throws<InvalidOperationException>(
            () => new StreamEncoder(0, 3, SectorScheme.VirtualIndex)
                .Encode(content, "gate512.bin"));
    }

    /// <summary>Порог выше DefaultSectorLimit (или MaxDataVolumes) — исключение.</summary>
    [Fact]
    public void Constructor_LimitAboveCeiling_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new StreamEncoder(sectorScheme: SectorScheme.VirtualIndex,
                virtualIndexSectorLimit: VirtualIndexHasher.DefaultSectorLimit + 1));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new StreamDecoder(sectorScheme: SectorScheme.VirtualIndex,
                virtualIndexSectorLimit: VirtualIndexHasher.DefaultSectorLimit + 1));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new StreamDecoder(sectorScheme: SectorScheme.VirtualIndex,
                virtualIndexSectorLimit: PacketFormat.MaxDataVolumes));
    }

    // ── Секторы раньше заголовка: адресная перепривязка ─────────────────────

    /// <summary>
    /// Адресная перепривязка: секторы, пришедшие раньше заголовка,
    /// удерживаются сканером и классифицируются после прихода H5.
    /// </summary>
    [Fact]
    public void VirtualIndexSectors_BeforeHeader_AreRebound()
    {
        var content = RandomBytes(1500, 111);
        var packets = new StreamEncoder(10, 3, SectorScheme.VirtualIndex)
            .Encode(content, "rebind.bin");

        var header = packets.First(p => PacketHasher.VerifyHeaderPacket(p));
        var sectors = packets
            .Where(p => !PacketHasher.VerifyHeaderPacket(p))
            .ToList();

        // Сначала все секторы (без заголовка — не классифицируются),
        // затем заголовок: удержанные данные перепривязываются
        var decoder = new StreamDecoder(sectorScheme: SectorScheme.VirtualIndex);
        decoder.Scan(PacketIO.WriteBinaryBytes(sectors));
        Assert.Equal(0, decoder.FileCount);

        decoder.Scan(header);
        Assert.Equal(1, decoder.FileCount);

        var slot = decoder.Slots.Single();
        Assert.Equal(slot.TotalVolumeCount, slot.ReceivedSectorCount);

        var restored = decoder.TryAssemble(slot.Header);
        Assert.NotNull(restored);
        Assert.Equal(content, restored);
    }
}
