using DataShield.Codec;
using DataShield.Codec.Packets;
using DataShield.Codec.Reporting;
using Xunit;

namespace DataShield.Tests;

// ─────────────────────────────────────────────────────────────────────────────
//  FileEncoder + CodecProgress — валидация аргументов, статистика, форма потока
// ─────────────────────────────────────────────────────────────────────────────

public class FileEncoderTests
{
    private const int Payload = PacketFormat.PayloadSize; // 64

    private static byte[] RandomBytes(int len, int seed)
    {
        var b = new byte[len];
        new Random(seed).NextBytes(b);
        return b;
    }

    private static bool IsHeaderPacket(byte[] packet) =>
        PacketHasher.VerifyHeaderPacket(packet);

    // ────────────────────────────────────────────────────────────────────────
    //  Конструктор — валидация процентов
    // ────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(-1, 3)]
    [InlineData(10, -1)]
    [InlineData(-100, -100)]
    public void Constructor_NegativePercent_Throws(int eccPercent, int headerPercent) =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new FileEncoder(eccPercent, headerPercent));

    [Fact]
    public void Constructor_StoresPercents()
    {
        var encoder = new FileEncoder(eccPercent: 25, headerPercent: 7);

        Assert.Equal(25, encoder.EccPercent);
        Assert.Equal(7, encoder.HeaderPercent);
        Assert.Equal(3, FileEncoder.DefaultHeaderPercent);
    }

    // ────────────────────────────────────────────────────────────────────────
    //  Encode — валидация входа
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Encode_OversizedFile_Throws()
    {
        // На 1 байт больше максимума 3-байтного поля FileSize.
        var content = new byte[PacketFormat.MaxFileSizeField + 1];
        var encoder = new FileEncoder(eccPercent: 0);

        Assert.Throws<InvalidOperationException>(
            () => encoder.Encode(content, "big.bin"));
    }

    [Fact]
    public void Encode_LongName_IsPackedWithTilde()
    {
        var content = RandomBytes(100, 1);
        var encoder = new FileEncoder(eccPercent: 0);

        var text = encoder.EncodeToText(content, "documents.tar.gz");

        var decoder = new FileDecoder();
        decoder.Scan(text.Split('\n', StringSplitOptions.RemoveEmptyEntries));

        Assert.Single(decoder.Slots);
        Assert.Equal("docume~.tar.gz", decoder.Slots[0].Header.FileName);
    }

    [Fact]
    public void Encode_UnrepresentableName_Throws()
    {
        // Расширение 21 байт не оставляет базе минимального бюджета (1+~).
        var content = RandomBytes(100, 11);
        var encoder = new FileEncoder(eccPercent: 0);

        Assert.Throws<InvalidOperationException>(
            () => encoder.Encode(content, "file.verylongextension123"));
    }

    [Theory]
    [InlineData("подпапка\\name.bin")]
    [InlineData("/var/log/name.bin")]
    [InlineData("name.bin")]
    public void Encode_NameIsReducedToFileName(string path)
    {
        var content = RandomBytes(100, 2);
        var encoder = new FileEncoder(eccPercent: 0);

        var text = encoder.EncodeToText(content, path);

        var decoder = new FileDecoder();
        decoder.Scan(text.Split('\n', StringSplitOptions.RemoveEmptyEntries));

        Assert.Single(decoder.Slots);
        Assert.Equal("name.bin", decoder.Slots[0].Header.FileName);
    }

    [Fact]
    public void Encode_TotalVolumesOverGf16_Throws()
    {
        // N = 65536 data-томов (4 МБ), M >= 1 → N+M > 65535.
        var content = RandomBytes(PacketFormat.MaxDataVolumes * Payload, 3);
        var encoder = new FileEncoder(eccPercent: 1);

        Assert.Throws<InvalidOperationException>(
            () => encoder.Encode(content, "over.bin"));
    }

    // ────────────────────────────────────────────────────────────────────────
    //  Форма выходного потока
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Encode_AllPackets_AreExactly75Bytes()
    {
        var content = RandomBytes(5000, 4);
        var packets = new FileEncoder(eccPercent: 10).Encode(content, "form.bin");

        Assert.NotEmpty(packets);
        Assert.All(packets, p => Assert.Equal(PacketFormat.PacketSize, p.Length));
    }

    [Fact]
    public void EncodeToText_AllLines_AreExactly100Base64Chars()
    {
        var content = RandomBytes(5000, 5);
        var text = new FileEncoder(eccPercent: 10).EncodeToText(content, "form.bin");

        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.All(lines, l => Assert.Equal(PacketFormat.Base64Size, l.Length));
    }

    [Fact]
    public void Encode_StreamStartsAndEndsWithHeaderPacket()
    {
        var content = RandomBytes(2000, 6);
        var packets = new FileEncoder(eccPercent: 0).Encode(content, "ends.bin");

        Assert.True(IsHeaderPacket(packets[0]), "Первый пакет должен быть заголовком");
        Assert.True(IsHeaderPacket(packets[^1]), "Последний пакет должен быть заголовком");
    }

    [Fact]
    public void Encode_Stream_NonSeekable_ReadsWholeContent()
    {
        // Непозиционируемый поток читается через CopyTo без обращения
        // к Length/Position.
        var content = RandomBytes(2000, 21);
        var packets = new FileEncoder(eccPercent: 10)
            .Encode(new NonSeekableStream(content), "nonseek.bin");

        Assert.Equal(content, DecodeRoundtrip(packets));
    }

    [Theory]
    [InlineData(200 * Payload, 0, 3, new[] { 0, 41, 82, 123, 164, 205 })]
    [InlineData(100 * Payload, 10, 3, new[] { 0, 37, 74, 113 })]
    [InlineData(100 * Payload, 0, 5, new[] { 0, 26, 52, 78, 104 })]
    public void Encode_HeaderPlacement_ExactCopyCountAndEvenSpacing(
        int size, int eccPercent, int headerPercent, int[] expectedHeaderIndices)
    {
        var content = RandomBytes(size, 30 + size + eccPercent);
        var packets = new FileEncoder(eccPercent, headerPercent).Encode(content, "layout.bin");

        var dataCount = Math.Max(1, (size + Payload - 1) / Payload);
        var eccCount = FileEncoder.ComputeEccCount(dataCount, eccPercent);
        var totalCount = dataCount + eccCount;
        var headerCount = FileEncoder.ComputeHeaderCount(totalCount, headerPercent);

        // Ровно H копий заголовка и ровно H + (N+M) пакетов всего.
        Assert.Equal(totalCount + headerCount, packets.Count);
        Assert.Equal(headerCount, packets.Count(IsHeaderPacket));

        // Копии — первый пакет, равномерные промежуточные, последний пакет.
        var actualIndices = packets
            .Select((packet, index) => (Packet: packet, Index: index))
            .Where(x => IsHeaderPacket(x.Packet))
            .Select(x => x.Index)
            .ToArray();

        Assert.Equal(expectedHeaderIndices, actualIndices);

        // Между заголовками секторы идут в естественном порядке номеров
        // (сначала N data-томов, затем M ECC-томов).
        var sectorNumbers = packets
            .Where(p => !IsHeaderPacket(p))
            .Select(p => p[0] | (p[1] << 8))
            .ToArray();

        Assert.Equal(Enumerable.Range(0, totalCount), sectorNumbers);
    }

    // ────────────────────────────────────────────────────────────────────────
    //  EncodeWithStats — корректность статистики
    // ────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1000, 0)]
    [InlineData(5000, 10)]
    [InlineData(5000, 25)]
    public void EncodeWithStats_ReportsCorrectCounts(int size, int eccPercent)
    {
        var content = RandomBytes(size, 100 + size + eccPercent);
        var (packets, stats) = new FileEncoder(eccPercent).EncodeWithStats(content, "stats.bin");

        var expectedDataCount = Math.Max(1, (size + Payload - 1) / Payload);
        var expectedEccCount = FileEncoder.ComputeEccCount(expectedDataCount, eccPercent);

        Assert.Equal((uint)size, stats.FileSize);
        Assert.Equal(Sha256Compact.HashData(content), stats.Sha256);
        Assert.Equal(expectedDataCount, stats.DataCount);
        Assert.Equal(expectedEccCount, stats.EccCount);
        Assert.Equal(packets.Count, stats.TotalPackets);
        Assert.Equal(packets.Count - expectedDataCount - expectedEccCount, stats.HeaderCopies);
        Assert.True(stats.HeaderCopies >= 3);
    }

    // ────────────────────────────────────────────────────────────────────────
    //  CodecProgress.Create — ограничение диапазона
    // ────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(-1000, 0)]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(50, 50)]
    [InlineData(100, 100)]
    [InlineData(101, 100)]
    [InlineData(int.MaxValue, 100)]
    public void CodecProgress_Create_ClampsPercent(int input, int expected)
    {
        var p = CodecProgress.Create(input, "фаза");

        Assert.Equal(expected, p.Percent);
        Assert.Equal("фаза", p.Phase);
    }

    [Fact]
    public void CodecProgress_Create_NullPhase_BecomesEmpty()
    {
        var p = CodecProgress.Create(10, null!);

        Assert.Equal(string.Empty, p.Phase);
    }

    // ────────────────────────────────────────────────────────────────────────
    //  Прогресс и отмена — проход через Encode
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Encode_ReportsProgressWithin0To100()
    {
        var content = RandomBytes(20_000, 7);
        var reported = new List<CodecProgress>();

        new FileEncoder(eccPercent: 10).Encode(
            content, "prog.bin",
            new ProgressCollector(reported), default);

        Assert.NotEmpty(reported);
        Assert.All(reported, p => Assert.InRange(p.Percent, 0, 100));
        Assert.Equal(100, reported[^1].Percent);
    }

    [Fact]
    public void Encode_PreCancelledToken_Throws()
    {
        var content = RandomBytes(100, 8);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => new FileEncoder(eccPercent: 0).Encode(content, "cancel.bin", progress: null, cts.Token));
    }

    /// <summary>Синхронный сборщик прогресса (без диспетчера UI).</summary>
    internal sealed class ProgressCollector : IProgress<CodecProgress>
    {
        private readonly List<CodecProgress> _list;

        public ProgressCollector(List<CodecProgress> list) => _list = list;

        public void Report(CodecProgress value) => _list.Add(value);
    }

    /// <summary>
    /// Поток только для чтения без позиционирования: CanSeek = false,
    /// чтение делегируется внутреннему MemoryStream.
    /// </summary>
    internal sealed class NonSeekableStream : Stream
    {
        private readonly MemoryStream _inner;

        public NonSeekableStream(byte[] data) => _inner = new MemoryStream(data);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count) =>
            _inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    /// <summary>Сквозная проверка пакета: бинарный скан + сборка.</summary>
    private static byte[] DecodeRoundtrip(List<byte[]> packets)
    {
        var decoder = new FileDecoder();
        decoder.Scan(PacketIO.WriteBinaryBytes(packets));

        return decoder.TryAssemble(decoder.Slots.Single().Header)!;
    }
}
