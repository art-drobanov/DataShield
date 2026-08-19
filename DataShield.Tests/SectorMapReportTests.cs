using DataShield.Codec;
using DataShield.Codec.Packets;
using DataShield.Codec.Reporting;
using DataShield.Codec.StreamProcessor;
using Xunit;

namespace DataShield.Tests;

/// <summary>
/// Тесты MAP-отчёта кодека: путь файла карты, запись отчёта (заголовок,
/// сводка приёма, строки карты) на языке CodecStrings.Language
/// и флаги коллизий слота.
/// </summary>
public sealed class SectorMapReportTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), $"dshield-map-{Guid.NewGuid():N}");

    public SectorMapReportTests() =>
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

    /// <summary>Слот приёма после полного roundtrip с ECC.</summary>
    private static ReceptionSlot DecodeSlot(byte[] content, int seed)
    {
        var encoder = new StreamEncoder(eccPercent: 20);
        var text = encoder.EncodeToText(content, $"test-{seed}.bin");

        var decoder = new StreamDecoder();
        decoder.Scan(text.Split('\n', StringSplitOptions.RemoveEmptyEntries));

        return decoder.Slots.Single();
    }

    /// <summary>Путь карты: имя выходного файла + «.MAP.txt» в его каталоге.</summary>
    [Fact]
    public void BuildPath_Appends_Map_Suffix()
    {
        Assert.Equal("out.bin.MAP.txt", SectorMapReport.BuildPath("out.bin"));

        var dir = Path.Combine("x", "y");
        Assert.Equal(
            Path.Combine(dir, "a.dat.MAP.txt"),
            SectorMapReport.BuildPath(Path.Combine(dir, "a.dat")));
    }

    /// <summary>
    /// Отчёт содержит заголовок, сводку приёма и строки карты; язык следует
    /// за CodecStrings.Language (восстанавливается после теста).
    /// </summary>
    [Fact]
    public void Write_Produces_Report_In_Selected_Language()
    {
        var content = RandomBytes(5000, 42);
        var slot = DecodeSlot(content, 42);
        var mapPath = Path.Combine(_dir, "restored.bin.MAP.txt");
        var originalLanguage = CodecStrings.Language;

        try
        {
            CodecStrings.Language = CodecLanguage.English;
            SectorMapReport.Write(mapPath, slot);

            Assert.True(File.Exists(mapPath));
            var lines = File.ReadAllLines(mapPath);
            Assert.Contains("         DataShield : Sector validity map", lines);
            Assert.Contains($"         {"File name".PadRight(10)} : test-42.bin", lines);
            Assert.Contains("         Legend: '█' — intact, '▓' — collision, '░' — corrupted/missing", lines);

            var total = slot.TotalVolumeCount;
            var remainder = total % 64;
            var lastRowCount = remainder == 0 ? 64 : remainder;
            var last = lines[^1];
            Assert.EndsWith($"│ {lastRowCount}/{lastRowCount}", last);
            Assert.Contains('█', last);

            CodecStrings.Language = CodecLanguage.Russian;
            SectorMapReport.Write(mapPath, slot);
            lines = File.ReadAllLines(mapPath);
            Assert.Contains("         DataShield : Карта валидности секторов", lines);
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
        var slot = DecodeSlot(RandomBytes(300, 7), 7);
        Assert.Null(slot.BuildCollisionFlags());
    }
}
