using System.Text;
using DataShield.Codec;
using DataShield.Codec.Packets;
using DataShield.Codec.Reporting;
using DataShield.Codec.StreamProcessor;
using Xunit;

namespace DataShield.Tests;

/// <summary>
/// Тесты отчёта (.report) кодека: путь файла отчёта, запись отчётов
/// кодирования и декодирования (шапка, сводка, реальные сигнатуры,
/// SHA-512 двумя строками, строки карты) на языке CodecStrings.Language
/// и флаги коллизий слота.
/// </summary>
public sealed class CodecReportTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), $"dshield-report-{Guid.NewGuid():N}");

    public CodecReportTests() =>
        Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Детерминированный псевдослучайный массив.</summary>
    private static byte[] RandomBytes(int len, int seed)
    {
        var r = new Random(seed);
        var b = new byte[len];
        r.NextBytes(b);
        return b;
    }

    /// <summary>Слот приёма и содержимое после roundtrip с ECC и сигнатурами.</summary>
    private static (ReceptionSlot slot, byte[] content) DecodeSlot(byte[] content, int seed)
    {
        var encoder = new StreamEncoder(eccPercent: 20, signaturePackets512: true);
        var text = encoder.EncodeToText(content, $"test-{seed}.bin");

        var decoder = new StreamDecoder();
        decoder.Scan(text.Split('\n', StringSplitOptions.RemoveEmptyEntries));

        var slot = decoder.Slots.Single();
        return (slot, decoder.TryAssemble(slot.Header)!);
    }

    /// <summary>Путь отчёта: полное имя + «.report.txt».</summary>
    [Fact]
    public void BuildPath_Appends_Report_Suffix()
    {
        Assert.Equal("out.bin.report.txt", CodecReport.BuildPath("out.bin"));

        var dir = Path.Combine("x", "y");
        Assert.Equal(
            Path.Combine(dir, "a.dat.report.txt"),
            CodecReport.BuildPath(Path.Combine(dir, "a.dat")));
    }

    /// <summary>
    /// Отчёт кодирования: шапка, имя, реальные сигнатуры (SHA-256 и SHA-512
    /// двумя строками), число копий заголовков и сигнатурных пакетов.
    /// </summary>
    [Fact]
    public void WriteEncoding_Produces_Report_With_Real_Hashes()
    {
        var content = RandomBytes(5000, 42);
        var encoder = new StreamEncoder(eccPercent: 20, signaturePackets512: true);
        var (_, stats) = encoder.EncodeWithStats(content, "test-42.bin");
        var reportPath = Path.Combine(_dir, "out.report.txt");

        CodecReport.WriteEncoding(reportPath, stats, "test-42.bin");

        var lines = File.ReadAllLines(reportPath);
        Assert.Contains("         DataShield : Encoding report", lines);
        Assert.Contains($"         {"File name".PadRight(10)} : test-42.bin", lines);
        Assert.Contains($"         {"SHA-256".PadRight(10)} : {Hex(stats.RealSha256 ?? stats.Sha256)}", lines);

        var sha512 = Hex(Sha512Compact.HashData(content));
        Assert.Contains($"         {"SHA-512".PadRight(10)} : {sha512[..64]}", lines);
        Assert.Contains(new string(' ', 9 + 10 + 3) + sha512[64..], lines);

        Assert.Contains($"         {"Headers".PadRight(10)} : {stats.HeaderCopies}", lines);
        Assert.Contains($"         {"Signatures".PadRight(10)} : {stats.SignatureCopies}", lines);
        Assert.Contains($"         {"Packets".PadRight(10)} : {stats.TotalPackets}", lines);
    }

    /// <summary>
    /// Отчёт декодирования: шапка, легенда, построчная карта, SHA-512 при
    /// активной расширенной проверке; язык следует за CodecStrings.Language
    /// (восстанавливается после теста).
    /// </summary>
    [Fact]
    public void WriteDecoding_Produces_Report_In_Selected_Language()
    {
        var content = RandomBytes(5000, 42);
        var (slot, restored) = DecodeSlot(content, 42);
        var reportPath = Path.Combine(_dir, "restored.report.txt");
        var originalLanguage = CodecStrings.Language;

        try
        {
            CodecStrings.Language = CodecLanguage.English;
            CodecReport.WriteDecoding(reportPath, slot, restored,
                signatureCheckActive: true, signaturePacketCount: 3);

            Assert.True(File.Exists(reportPath));
            var lines = File.ReadAllLines(reportPath);
            Assert.Contains("         DataShield : Decoding report", lines);
            Assert.Contains($"         {"File name".PadRight(10)} : test-42.bin", lines);
            Assert.Contains($"         {"Signatures".PadRight(10)} : 3", lines);
            Assert.Contains("         Legend: '█' — intact, '▓' — collision, '░' — corrupted/missing", lines);

            var sha512 = Hex(Sha512Compact.HashData(content));
            Assert.Contains($"         {"SHA-512".PadRight(10)} : {sha512[..64]}", lines);
            Assert.Contains(new string(' ', 9 + 10 + 3) + sha512[64..], lines);

            var total = slot.TotalVolumeCount;
            var remainder = total % 64;
            var lastRowCount = remainder == 0 ? 64 : remainder;
            var last = lines[^1];
            Assert.EndsWith($"│ {lastRowCount}/{lastRowCount}", last);
            Assert.Contains('█', last);

            CodecStrings.Language = CodecLanguage.Russian;
            CodecReport.WriteDecoding(reportPath, slot, restored);
            lines = File.ReadAllLines(reportPath);
            Assert.Contains("         DataShield : Отчет по декодированию", lines);
            Assert.Contains($"         {"Имя файла".PadRight(10)} : test-42.bin", lines);
            Assert.Contains("         Легенда: '█' — целостный, '▓' — коллизия, '░' — битый/отсутствует", lines);
        }
        finally
        {
            CodecStrings.Language = originalLanguage;
        }
    }

    /// <summary>Чистый приём без коллизий: флаги коллизий — null.</summary>
    [Fact]
    public void BuildCollisionFlags_CleanReception_Returns_Null()
    {
        var content = RandomBytes(300, 7);
        var encoder = new StreamEncoder(eccPercent: 20, signaturePackets512: true);
        var text = encoder.EncodeToText(content, "test-7.bin");

        var decoder = new StreamDecoder();
        decoder.Scan(text.Split('\n', StringSplitOptions.RemoveEmptyEntries));

        Assert.Null(decoder.Slots.Single().BuildCollisionFlags());
    }

    /// <summary>
    /// Fallback на значения «на проводе» (Real* не заданы): строки
    /// SHA-256 и SHA-512 помечаются «*» в конце.
    /// </summary>
    [Fact]
    public void WriteEncoding_Marks_OnWire_Hash_Fallback()
    {
        var content = RandomBytes(500, 9);
        var sha256 = Sha256Compact.HashData(content);
        var sha512 = Sha512Compact.HashData(content);
        var stats = new EncodeStats((uint)content.Length, sha256, 8, 1, 12, 3, sha512, 2);
        var reportPath = Path.Combine(_dir, "secret.report.txt");

        CodecReport.WriteEncoding(reportPath, stats, "test-9.bin");

        var lines = File.ReadAllLines(reportPath);
        Assert.Contains($"         {"SHA-256".PadRight(10)} : {Hex(sha256)}*", lines);
        var hex512 = Hex(sha512);
        Assert.Contains(new string(' ', 9 + 10 + 3) + hex512[64..] + "*", lines);
    }

    /// <summary>
    /// Отчёт декодирования без содержимого: H3 заголовка помечается «*»
    /// только при использованном секрете хеширования.
    /// </summary>
    [Fact]
    public void WriteDecoding_Marks_Header_Hash_Only_With_Secret()
    {
        var content = RandomBytes(500, 11);
        var (slot, _) = DecodeSlot(content, 11);
        var reportPath = Path.Combine(_dir, "hdr.report.txt");

        CodecReport.WriteDecoding(reportPath, slot, content: null, hashSecretUsed: true);
        var lines = File.ReadAllLines(reportPath);
        Assert.Contains($"         {"SHA-256".PadRight(10)} : {Hex(slot.Header.Sha256)}*", lines);

        CodecReport.WriteDecoding(reportPath, slot, content: null, hashSecretUsed: false);
        lines = File.ReadAllLines(reportPath);
        Assert.Contains($"         {"SHA-256".PadRight(10)} : {Hex(slot.Header.Sha256)}", lines);
        Assert.DoesNotContain(lines, l => l.TrimEnd().EndsWith("*"));
    }

    /// <summary>
    /// Контрольная сумма потока после декодирования: файл «поток».sha256
    /// в формате sha256sum («hex␣␣имя» + перевод строки).
    /// </summary>
    [Fact]
    public void WriteSha256Sidecar_Writes_Checksum_Line()
    {
        var content = RandomBytes(700, 13);
        var streamPath = Path.Combine(_dir, "Rar-short.txt.DataShield.bin");
        File.WriteAllBytes(streamPath, content);

        CodecReport.WriteSha256Sidecar(streamPath);

        var sidecar = streamPath + ".sha256";
        Assert.True(File.Exists(sidecar));
        Assert.Equal(
            Hex(Sha256Compact.HashData(content)) + "  Rar-short.txt.DataShield.bin\n",
            File.ReadAllText(sidecar));
    }

    /// <summary>Байты → hex-строка нижнего регистра.</summary>
    private static string Hex(byte[] bytes) =>
        BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
}
