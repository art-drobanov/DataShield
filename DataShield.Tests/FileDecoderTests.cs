using System.Text;
using DataShield.Codec;
using DataShield.Codec.Packets;
using DataShield.Codec.Reporting;
using DataShield.Codec.StreamProcessor;
using DataShield.Codec.StreamProcessor.Versions;
using Xunit;

namespace DataShield.Tests;

// ─────────────────────────────────────────────────────────────────────────────
//  FileDecoder — binary-скан с шумом, накопительные проходы, служебные пути
// ─────────────────────────────────────────────────────────────────────────────

public class FileDecoderTests
{
    private static byte[] RandomBytes(int len, int seed)
    {
        var b = new byte[len];
        new Random(seed).NextBytes(b);
        return b;
    }

    private static List<string> EncodeToLines(byte[] content, string name, int eccPercent = 0)
    {
        var text = new FileEncoder(eccPercent).EncodeToText(content, name);
        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    private static byte[] EncodeToBinary(byte[] content, string name, int eccPercent = 0) =>
        PacketIO.WriteBinaryBytes(
            new FileEncoder(eccPercent).Encode(content, name));

    // ────────────────────────────────────────────────────────────────────────
    //  Конструктор — валидация настроек поиска
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Constructor_InvalidSearchOptions_Throws()
    {
        var options = new SectorVersionSearchOptions
        {
            MaxExhaustiveCombinations = 0,
        };

        Assert.Throws<ArgumentOutOfRangeException>(() => new FileDecoder(options));
    }

    [Fact]
    public void Constructor_NullOptions_UsesDefaults()
    {
        var decoder = new FileDecoder(null);

        Assert.Empty(decoder.Slots);
        Assert.Equal(0, decoder.FileCount);
    }

    // ────────────────────────────────────────────────────────────────────────
    //  Пустой и короткий вход
    // ────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(99)]
    public void Scan_Text_TooShort_IsIgnored(int lineCount)
    {
        var decoder = new FileDecoder();
        var lines = Enumerable.Repeat("QUJD", lineCount);

        decoder.Scan(lines);

        Assert.Equal(0, decoder.FileCount);
    }

    [Fact]
    public void Scan_EmptyText_NoSlots()
    {
        var decoder = new FileDecoder();

        decoder.Scan(Array.Empty<string>());

        Assert.Equal(0, decoder.FileCount);
    }

    [Fact]
    public void Scan_EmptyBinary_NoSlots()
    {
        var decoder = new FileDecoder();

        decoder.Scan(Array.Empty<byte>());

        Assert.Equal(0, decoder.FileCount);
    }

    [Fact]
    public void Scan_Binary_TooShort_IsIgnored()
    {
        var decoder = new FileDecoder();

        decoder.Scan(new byte[PacketFormat.PacketSize - 1]);

        Assert.Equal(0, decoder.FileCount);
    }

    // ────────────────────────────────────────────────────────────────────────
    //  Binary-скан: шум и рассинхронизация пакетов
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Scan_Binary_WithPrefixAndSuffixNoise_StillDecodes()
    {
        var content = RandomBytes(3000, 11);
        var stream = EncodeToBinary(content, "noise.bin", eccPercent: 10);

        var noisy = new List<byte>();
        noisy.AddRange(RandomBytes(37, 12));          // мусор в начале
        noisy.AddRange(stream);
        noisy.AddRange(RandomBytes(53, 13));          // мусор в конце

        var decoder = new FileDecoder();
        decoder.Scan(noisy.ToArray());

        Assert.Single(decoder.Slots);
        Assert.Equal(content, decoder.TryAssemble(decoder.Slots[0].Header));
    }

    [Fact]
    public void Scan_Binary_WithDesyncInsideStream_StillDecodes()
    {
        var content = RandomBytes(3000, 14);
        var stream = EncodeToBinary(content, "desync.bin", eccPercent: 20).ToList();

        // Вставка посторонних байтов в середину потока сбивает выравнивание:
        // далее сканер обязан снова синхронизироваться скользящим окном.
        stream.InsertRange(stream.Count / 2, RandomBytes(3, 15));

        var decoder = new FileDecoder();
        decoder.Scan(stream.ToArray());

        Assert.Single(decoder.Slots);
        var slot = decoder.Slots[0];
        Assert.True(slot.ReceivedSectorCount >= slot.DataVolumeCount);
        Assert.Equal(content, decoder.TryAssemble(slot.Header));
    }

    [Fact]
    public void Scan_Binary_Roundtrip_WithEccRecovery()
    {
        var content = RandomBytes(5000, 16);
        var stream = EncodeToBinary(content, "bin-ecc.bin", eccPercent: 30).ToList();

        // Затираем пару пакетов в середине — ECC должен восстановить пропуски.
        var mid = (stream.Count / PacketFormat.PacketSize / 2) * PacketFormat.PacketSize;
        for (var i = 0; i < 2 * PacketFormat.PacketSize; i++)
            stream[mid + i] ^= 0xFF;

        var decoder = new FileDecoder();
        decoder.Scan(stream.ToArray());

        Assert.Single(decoder.Slots);
        Assert.Equal(content, decoder.TryAssemble(decoder.Slots[0].Header));
    }

    [Fact]
    public void Scan_Binary_MixedWithTextNoise_StillDecodes()
    {
        var content = RandomBytes(1000, 17);
        var stream = EncodeToBinary(content, "mixed.bin");

        // Любые байты между пакетами допускаются: формат самосинхронизируется.
        var mixed = new List<byte>();
        mixed.AddRange(stream.Take(300));
        mixed.AddRange("мусор между пакетами"u8.ToArray());
        mixed.AddRange(stream.Skip(300));

        var decoder = new FileDecoder();
        decoder.Scan(mixed.ToArray());

        Assert.Single(decoder.Slots);
        Assert.Equal(content, decoder.TryAssemble(decoder.Slots[0].Header));
    }

    // ────────────────────────────────────────────────────────────────────────
    //  Накопительное сканирование (несколько вызовов Scan)
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Scan_SecondPass_AccumulatesSectorsAndHeaderCount()
    {
        var content = RandomBytes(3000, 18);
        var lines = EncodeToLines(content, "twopass.bin", eccPercent: 10);

        var half = lines.Count / 2;
        var decoder = new FileDecoder();

        decoder.Scan(lines.Take(half));
        var firstHeaderCount = decoder.Slots[0].HeaderReceptionCount;
        var firstSectors = decoder.Slots[0].ReceivedSectorCount;

        decoder.Scan(lines.Skip(half));

        Assert.Single(decoder.Slots);
        Assert.True(decoder.Slots[0].HeaderReceptionCount > firstHeaderCount);
        Assert.True(decoder.Slots[0].ReceivedSectorCount > firstSectors);
        Assert.Equal(content, decoder.TryAssemble(decoder.Slots[0].Header));
    }

    // ────────────────────────────────────────────────────────────────────────
    //  Кусок без заголовка: секторы не теряются, а перепривязываются,
    //  когда заголовок приходит в другом куске
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Scan_Text_SectorsBeforeHeader_AreRecoveredWhenHeaderArrives()
    {
        var content = RandomBytes(3000, 24);
        var lines = EncodeToLines(content, "late-header.bin", eccPercent: 10);

        var sectors = lines.Where(l => !IsHeaderLine(l)).ToList();
        var headers = lines.Where(IsHeaderLine).ToList();

        var decoder = new FileDecoder();

        // Первый кусок — только секторы, заголовков нет
        decoder.Scan(sectors);
        Assert.Equal(0, decoder.FileCount);

        // Заголовок приходит позже — накопленные секторы привязываются
        decoder.Scan(headers);
        Assert.Single(decoder.Slots);
        var restored = decoder.TryAssemble(decoder.Slots[0].Header);
        Assert.NotNull(restored);
        Assert.Equal(content, restored);
    }

    [Fact]
    public void Scan_Binary_SectorsBeforeHeader_AreRecoveredWhenHeaderArrives()
    {
        var content = RandomBytes(2000, 25);
        var packets = new FileEncoder(eccPercent: 10).Encode(content, "late.bin");

        var sectorPackets = packets.Where(p => !IsHeaderPacket(p)).ToList();
        var headerPackets = packets.Where(IsHeaderPacket).ToList();

        var decoder = new FileDecoder();

        // Первый кусок — только секторы
        decoder.Scan(PacketIO.WriteBinaryBytes(sectorPackets));
        Assert.Equal(0, decoder.FileCount);

        // Заголовки приходят вторым куском
        decoder.Scan(PacketIO.WriteBinaryBytes(headerPackets));
        Assert.Single(decoder.Slots);
        var restored = decoder.TryAssemble(decoder.Slots[0].Header);
        Assert.NotNull(restored);
        Assert.Equal(content, restored);
    }

    // ────────────────────────────────────────────────────────────────────────
    //  Многофайловый поток: секторы файла приходят раньше его заголовка
    //  (в одном куске с заголовком другого файла)
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Scan_MultiFile_Binary_HeaderOfSecondFileInLaterChunk_BindsEarlySectors()
    {
        var contentA = RandomBytes(1500, 26);
        var contentB = RandomBytes(2000, 27);
        var packetsA = new FileEncoder(eccPercent: 10).Encode(contentA, "multiA.bin");
        var packetsB = new FileEncoder(eccPercent: 10).Encode(contentB, "multiB.bin");

        // Кусок 1: заголовок A + секторы A + секторы B (заголовка B ещё нет)
        var chunk1 = packetsA
            .Concat(packetsB.Where(p => !IsHeaderPacket(p)))
            .ToList();

        // Кусок 2: заголовки B
        var chunk2 = packetsB.Where(IsHeaderPacket).ToList();

        var decoder = new FileDecoder();
        decoder.Scan(PacketIO.WriteBinaryBytes(chunk1));
        decoder.Scan(PacketIO.WriteBinaryBytes(chunk2));

        Assert.Equal(2, decoder.FileCount);

        foreach (var slot in decoder.Slots)
        {
            var restored = decoder.TryAssemble(slot.Header);
            Assert.NotNull(restored);
            Assert.Equal(
                slot.Header.FileName == "multiA.bin" ? contentA : contentB,
                restored);
        }
    }

    [Fact]
    public void Scan_MultiFile_Text_HeaderOfSecondFileInLaterChunk_BindsEarlySectors()
    {
        var contentA = RandomBytes(1200, 28);
        var contentB = RandomBytes(1800, 29);

        var linesA = EncodeToLines(contentA, "mtA.bin", eccPercent: 10);
        var linesB = EncodeToLines(contentB, "mtB.bin", eccPercent: 10);

        // Кусок 1: строки A вперемешку с секторами B (заголовков B нет)
        var chunk1 = linesA
            .Concat(linesB.Where(l => !IsHeaderLine(l)))
            .ToList();

        // Кусок 2: заголовки B
        var chunk2 = linesB.Where(IsHeaderLine).ToList();

        var decoder = new FileDecoder();
        decoder.Scan(chunk1);
        decoder.Scan(chunk2);

        Assert.Equal(2, decoder.FileCount);

        foreach (var slot in decoder.Slots)
        {
            var restored = decoder.TryAssemble(slot.Header);
            Assert.NotNull(restored);
            Assert.Equal(
                slot.Header.FileName == "mtA.bin" ? contentA : contentB,
                restored);
        }
    }

    [Fact]
    public void Scan_TextAndBinary_PacketsAccumulateInOneSlot()
    {
        var content = RandomBytes(2000, 19);
        var lines = EncodeToLines(content, "mix-io.bin");
        var binary = EncodeToBinary(content, "mix-io.bin");

        var decoder = new FileDecoder();
        decoder.Scan(lines);
        decoder.Scan(binary);

        Assert.Single(decoder.Slots);
        Assert.Equal(content, decoder.TryAssemble(decoder.Slots[0].Header));
    }

    [Fact]
    public void Scan_Mixed_TextSectorsBeforeBinaryHeader_AreRecoveredWhenHeaderArrives()
    {
        var content = RandomBytes(3000, 41);
        var lines = EncodeToLines(content, "late-mix1.bin", eccPercent: 10);

        var sectors = lines.Where(l => !IsHeaderLine(l)).ToList();
        var headers = lines.Where(IsHeaderLine).ToList();

        var decoder = new FileDecoder();

        // Первый кусок — секторы текстом, заголовков нет
        decoder.Scan(sectors);
        Assert.Equal(0, decoder.FileCount);

        // Заголовок приходит позже бинарным куском другого формата
        var headerPackets = headers
            .Select(l => Convert.FromBase64String(l))
            .ToList();
        decoder.Scan(PacketIO.WriteBinaryBytes(headerPackets));

        Assert.Single(decoder.Slots);
        var restored = decoder.TryAssemble(decoder.Slots[0].Header);
        Assert.NotNull(restored);
        Assert.Equal(content, restored);
    }

    [Fact]
    public void Scan_Mixed_BinarySectorsBeforeTextHeader_AreRecoveredWhenHeaderArrives()
    {
        var content = RandomBytes(2500, 42);
        var packets = new FileEncoder(eccPercent: 10).Encode(content, "late-mix2.bin");

        var sectorPackets = packets.Where(p => !IsHeaderPacket(p)).ToList();
        var headerPackets = packets.Where(IsHeaderPacket).ToList();

        var decoder = new FileDecoder();

        // Первый кусок — секторы бинарно, заголовков нет
        decoder.Scan(PacketIO.WriteBinaryBytes(sectorPackets));
        Assert.Equal(0, decoder.FileCount);

        // Заголовок приходит позже текстовым куском другого формата
        var headerLines = headerPackets
            .Select(p => Convert.ToBase64String(p))
            .ToList();
        decoder.Scan(headerLines);

        Assert.Single(decoder.Slots);
        var restored = decoder.TryAssemble(decoder.Slots[0].Header);
        Assert.NotNull(restored);
        Assert.Equal(content, restored);
    }

    [Fact]
    public void TryAssemble_UnknownHeader_ReturnsNull()
    {
        var content = RandomBytes(500, 20);
        var decoder = new FileDecoder();
        decoder.Scan(EncodeToLines(content, "known.bin"));

        var stranger = new HeaderContent
        {
            FileName = "stranger.bin",
            FileSize = (uint)content.Length,
            Sha256 = Sha256Compact.HashData(content),
            EccCount = 0,
        };

        Assert.Null(decoder.TryAssemble(stranger));
    }

    [Fact]
    public void TryAssemble_OnEmptyDecoder_ReturnsNull()
    {
        var decoder = new FileDecoder();
        var header = new HeaderContent
        {
            FileName = "none.bin",
            FileSize = 1,
            Sha256 = new byte[32],
            EccCount = 0,
        };

        Assert.Null(decoder.TryAssemble(header));
    }

    // ────────────────────────────────────────────────────────────────────────
    //  Base64-скан: отбраковка посторонних символов
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Scan_Base64_IgnoresPaddingAndWhitespace()
    {
        var content = RandomBytes(1000, 21);
        var lines = EncodeToLines(content, "pad.bin");

        // Паддинг '=', пробелы и переносы не должны мешать извлечению символов.
        var decorated = lines
            .Select(l => "  " + l[..50] + " = " + l[50..] + " =")
            .ToList();

        var decoder = new FileDecoder();
        decoder.Scan(decorated);

        Assert.Single(decoder.Slots);
        Assert.Equal(content, decoder.TryAssemble(decoder.Slots[0].Header));
    }

    [Fact]
    public void Scan_Base64_PrefixFragmentBeforeLastLine_StillReceivesLastSector()
    {
        var (damaged, content) = BuildPrefixFragmentStream(seed: 27, tailLines: 0);

        var decoder = new FileDecoder();
        decoder.Scan(damaged);

        Assert.Single(decoder.Slots);
        var slot = decoder.Slots[0];
        Assert.Equal(slot.TotalVolumeCount, slot.ReceivedSectorCount);
        Assert.NotNull(decoder.TryAssemble(slot.Header));
        Assert.Equal(content, decoder.TryAssemble(slot.Header));
    }

    [Fact]
    public void Scan_Base64_PrefixFragmentMidStream_StillReceivesEatenSector()
    {
        var (damaged, content) = BuildPrefixFragmentStream(seed: 28, tailLines: 2);

        var decoder = new FileDecoder();
        decoder.Scan(damaged);

        Assert.Single(decoder.Slots);
        var slot = decoder.Slots[0];
        Assert.Equal(slot.TotalVolumeCount, slot.ReceivedSectorCount);
        Assert.NotNull(decoder.TryAssemble(slot.Header));
        Assert.Equal(content, decoder.TryAssemble(slot.Header));
    }

    private static (List<string> Damaged, byte[] Content) BuildPrefixFragmentStream(
        int seed, int tailLines)
    {
        var content = RandomBytes(2048, seed);
        var lines = EncodeToLines(content, "fragment.bin");
        var packets = lines.Where(IsPacketLine).ToList();

        string? fragmentSource = null;
        string? eaten = null;

        for (var i = 0; i < packets.Count && fragmentSource is null; i++)
        {
            if (IsHeaderLine(packets[i]))
                continue;

            for (var j = 0; j < packets.Count; j++)
            {
                if (i == j || IsHeaderLine(packets[j]) ||
                    packets[i][^1] != packets[j][0])
                    continue;

                fragmentSource = packets[i];
                eaten = packets[j];
                break;
            }
        }

        Assert.NotNull(fragmentSource);
        Assert.NotNull(eaten);

        var rest = packets.Where(p => p != eaten).ToList();
        var tail = rest.TakeLast(tailLines).ToList();
        rest = rest.SkipLast(tailLines).ToList();

        var damaged = new List<string>(rest)
        {
            fragmentSource![..(PacketFormat.Base64Size - 1)],
            eaten!
        };
        damaged.AddRange(tail);

        return (damaged, content);
    }

    private static bool IsPacketLine(string line)
    {
        try
        {
            return Convert.FromBase64String(line).Length == PacketFormat.PacketSize;
        }
        catch
        {
            return false;
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    //  Прогресс и отмена
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Scan_Text_ReportsProgressWithin0To100()
    {
        var content = RandomBytes(8000, 22);
        var lines = EncodeToLines(content, "prog.bin", eccPercent: 10);
        var reported = new List<CodecProgress>();

        var decoder = new FileDecoder();
        decoder.Scan(lines, new ProgressCollector(reported), default);

        Assert.NotEmpty(reported);
        Assert.All(reported, p => Assert.InRange(p.Percent, 0, 100));
        Assert.Equal(100, reported[^1].Percent);
    }

    [Fact]
    public void Scan_PreCancelledToken_Throws()
    {
        var content = RandomBytes(500, 23);
        var lines = EncodeToLines(content, "cancel.bin");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => new FileDecoder().Scan(lines, progress: null, cts.Token));
    }

    // ────────────────────────────────────────────────────────────────────────
    //  Непозиционируемый поток и подделка-победитель
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Scan_Stream_NonSeekable_DecodesWithoutSeek()
    {
        var content = RandomBytes(1500, 31);
        var text = new FileEncoder(eccPercent: 10).EncodeToText(content, "nonseek.txt");
        var bytes = Encoding.UTF8.GetBytes(text);

        var decoder = new FileDecoder();
        var reported = new List<CodecProgress>();

        decoder.Scan(new FileEncoderTests.NonSeekableStream(bytes), OutputFormat.Base64,
            new ProgressCollector(reported), default);

        Assert.Equal(1, decoder.FileCount);

        // Непозиционируемый поток не знает длины: процентов сканирования нет,
        // финальный отчёт — единственный и равен 100.
        var report = Assert.Single(reported);
        Assert.Equal(100, report.Percent);

        Assert.Equal(content, decoder.TryAssemble(decoder.Slots[0].Header));
    }

    [Fact]
    public void WinnerForgery_RefusedWhileMinority_AssembledAfterTruthLeads()
    {
        // Сценарий «подделка-победитель»: хеш-валидный сектор с чужим payload
        // продублирован чаще правды. Пока истина в меньшинстве подтверждений,
        // она вне пространства поиска и сборка обязана отказать; после добора
        // копий правды сборка завершается успехом с первой попытки.
        var content = RandomBytes(PacketFormat.PayloadSize, 32);
        var packets = new FileEncoder(eccPercent: 0).Encode(content, "forged.bin");
        var headerPacket = packets[0];
        var trueSector = packets[1];
        var h5 = headerPacket[PacketFormat.HeaderHashOffset..].ToArray();

        var forgedPayload = trueSector
            .Skip(PacketFormat.SectorNumberSize)
            .Take(PacketFormat.PayloadSize)
            .ToArray();
        forgedPayload[0] ^= 0x5A;
        var forgedSector = MakeSectorPacket(0, forgedPayload, h5);

        var decoder = new FileDecoder();

        // Фаза 1: подделка 3 копии против одной правды.
        var phase1 = new List<byte[]> { headerPacket };
        for (var i = 0; i < 3; i++)
            phase1.Add(forgedSector);
        phase1.Add(trueSector);

        decoder.Scan(PacketIO.WriteBinaryBytes(phase1));

        var slot = decoder.Slots.Single();
        var versions = slot.GetSectorVersions(0);

        Assert.Equal(2, versions.Count);
        Assert.Equal(3, versions[0].ConfirmationCount);
        Assert.Equal(1, versions[1].ConfirmationCount);

        Assert.Null(decoder.TryAssemble(slot.Header));

        // Фаза 2: три копии правды — счётчик 4 против 3, истина возглавила список.
        decoder.Scan(PacketIO.WriteBinaryBytes(
            new List<byte[]> { trueSector, trueSector, trueSector }));

        Assert.Equal(4, slot.GetSectorVersions(0)[0].ConfirmationCount);

        var result = decoder.TryAssemble(slot.Header);

        Assert.NotNull(result);
        Assert.Equal(content, result);
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

    /// <summary>Синхронный сборщик прогресса (без диспетчера UI).</summary>
    private sealed class ProgressCollector : IProgress<CodecProgress>
    {
        private readonly List<CodecProgress> _list;

        public ProgressCollector(List<CodecProgress> list) => _list = list;

        public void Report(CodecProgress value) => _list.Add(value);
    }

    private static bool IsHeaderLine(string base64)
    {
        try
        {
            var bytes = Convert.FromBase64String(base64);
            return bytes.Length == PacketFormat.PacketSize && IsHeaderPacket(bytes);
        }
        catch { return false; }
    }

    private static bool IsHeaderPacket(byte[] packet) =>
        PacketHasher.VerifyHeaderPacket(packet);
}
