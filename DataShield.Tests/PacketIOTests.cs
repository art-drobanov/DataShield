using DataShield.Codec;
using DataShield.Codec.Packets;
using Xunit;

namespace DataShieldTests;

// ─────────────────────────────────────────────────────────────────────────────
//  PacketIO + OutputFormatConfig — файловый ввод-вывод FEC-потока
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Тесты PacketIO и OutputFormatConfig — файлового ввода-вывода FEC-потока:
/// текстовый Base64-вывод с декоративным обрамлением «>[ … ]<[ » и без него,
/// бинарная компоновка пакетов, полные roundtrip'ы через диск
/// (WriteFile → ScanFile) и утилиты путей/расширений
/// (GetExtension, DetectFormat, GetDefaultOutputPath, StripFecSuffix).
/// </summary>
public class PacketIOTests : IDisposable
{
    // Изолированный временный каталог для файлов теста
    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), "datashield-tests-" + Guid.NewGuid().ToString("N"));

    public PacketIOTests() => Directory.CreateDirectory(_tempDir);

    // Уборка временного каталога; сбой удаления не валит тест
    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* временный каталог — не влияет на результат теста */ }
    }

    /// <summary>Детерминированный псевдослучайный массив.</summary>
    private static byte[] RandomBytes(int len, int seed)
    {
        var b = new byte[len];
        new Random(seed).NextBytes(b);
        return b;
    }

    /// <summary>Закодировать файл (ECC 10%) в массив 75-байтных пакетов.</summary>
    private static IReadOnlyList<byte[]> EncodePackets(int size, int seed, string name) =>
        new StreamEncoder(eccPercent: 10)
            .Encode(RandomBytes(size, seed), name);

    // ────────────────────────────────────────────────────────────────────────
    //  WriteBase64Text — декоративное обрамление
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Без метаданных текст — только Base64-строки пакетов по 100 символов,
    /// без обрамления.
    /// </summary>
    [Fact]
    public void WriteBase64Text_NoMetadata_HasNoDecoration()
    {
        var packets = EncodePackets(200, 1, "plain.bin");

        var text = PacketIO.WriteBase64Text(packets);

        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(packets.Count, lines.Length);
        Assert.All(lines, l => Assert.Equal(PacketFormat.Base64Size, l.Length));
        Assert.DoesNotContain(">[", text);
    }

    /// <summary>
    /// С метаданными поток обрамляется строками «>[имя][размер][SHA-256]»
    /// и «<[», между ними — пакеты.
    /// </summary>
    [Fact]
    public void WriteBase64Text_WithMetadata_BracketsStream()
    {
        var packets = EncodePackets(200, 2, "meta.bin");
        var sha = RandomBytes(32, 3);

        var text = PacketIO.WriteBase64Text(packets, "meta.bin", sha, fileSize: 200);
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        // Обрамление + пакеты; AppendLine даёт \r\n — обрезаем концы строк
        Assert.Equal(packets.Count + 2, lines.Length);
        Assert.StartsWith(">[", lines[0]);
        Assert.EndsWith("]", lines[0].TrimEnd());
        Assert.StartsWith("<[", lines[^1].TrimEnd());
        Assert.EndsWith("]", lines[^1].TrimEnd());

        var shaHex = Convert.ToHexString(sha).ToLowerInvariant();
        Assert.Equal(
            $">[meta.bin      ][0000200][SHA-256:{shaHex}]",
            lines[0].TrimEnd());

        // Пакетные строки между обрамлением
        Assert.Equal(
            Convert.ToBase64String(packets[0]),
            lines[1]);
    }

    /// <summary>
    /// Строки обрамления выровнены до ровно 100 символов: имя упаковано
    /// в 14, размер — 7 цифр.
    /// </summary>
    [Fact]
    public void WriteBase64Text_Decoration_IsExactly100Chars()
    {
        var packets = EncodePackets(1000, 9, "documents.tar.gz");
        var sha = Sha256Compact.HashData(RandomBytes(1000, 10));

        var text = PacketIO.WriteBase64Text(
            packets, "documents.tar.gz", sha, fileSize: 1000);
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        // Имя упаковано в 14 символов, размер — 7 цифр, итого ровно 100
        Assert.Equal(100, lines[0].TrimEnd().Length);
        Assert.Equal(100, lines[^1].TrimEnd().Length);
        Assert.StartsWith(">[docume~.tar.gz][", lines[0]);
        Assert.Contains("][0001000][SHA-256:", lines[0]);
    }

    // ────────────────────────────────────────────────────────────────────────
    //  WriteBinaryBytes — компоновка сырых пакетов
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>Бинарный вывод — пакеты склеиваются вплотную, без заголовков.</summary>
    [Fact]
    public void WriteBinaryBytes_ConcatenatesPackets()
    {
        var packets = EncodePackets(300, 4, "bin.bin");

        var bytes = PacketIO.WriteBinaryBytes(packets);

        Assert.Equal(packets.Count * PacketFormat.PacketSize, bytes.Length);

        for (var i = 0; i < packets.Count; i++)
            Assert.True(packets[i].AsSpan().SequenceEqual(
                bytes.AsSpan(i * PacketFormat.PacketSize, PacketFormat.PacketSize)));
    }

    // ────────────────────────────────────────────────────────────────────────
    //  WriteFile + ScanFile — полные roundtrip'ы через диск
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>Полный дисковый roundtrip: запись Base64 → чтение → сборка.</summary>
    [Fact]
    public void WriteFile_Base64_Then_ScanFile_Roundtrip()
    {
        var content = RandomBytes(2000, 5);
        var encoder = new StreamEncoder(eccPercent: 15);
        var packets = encoder.Encode(content, "rt-b64.bin");

        var path = Path.Combine(_tempDir, "stream.DataShield.txt");
        PacketIO.WriteFile(path, packets, OutputFormat.Base64, "rt-b64.bin",
            Sha256Compact.HashData(content), (uint)content.Length);

        var decoder = new StreamDecoder();
        PacketIO.ScanFile(decoder, path);

        Assert.Single(decoder.Slots);
        var restored = decoder.TryAssemble(decoder.Slots[0].Header);
        Assert.NotNull(restored);
        Assert.Equal(content, restored);
    }

    /// <summary>Полный дисковый roundtrip: бинарная запись → чтение → сборка.</summary>
    [Fact]
    public void WriteFile_Binary_Then_ScanFile_Roundtrip()
    {
        var content = RandomBytes(1500, 6);
        var encoder = new StreamEncoder(eccPercent: 20);
        var packets = encoder.Encode(content, "rt-bin.bin");

        var path = Path.Combine(_tempDir, "stream.DataShield.bin");
        PacketIO.WriteFile(path, packets, OutputFormat.Binary);

        var decoder = new StreamDecoder();
        PacketIO.ScanFile(decoder, path);

        Assert.Single(decoder.Slots);
        var restored = decoder.TryAssemble(decoder.Slots[0].Header);
        Assert.NotNull(restored);
        Assert.Equal(content, restored);
    }

    /// <summary>
    /// Декоративное обрамление (метаданные) не мешает ScanFile:
    /// сканер пропускает посторонние строки.
    /// </summary>
    [Fact]
    public void WriteFile_Base64_Decoration_Is_Ignored_By_Scan()
    {
        var packets = EncodePackets(500, 7, "decor.bin");

        var path = Path.Combine(_tempDir, "decorated.DataShield.txt");
        PacketIO.WriteFile(path, packets, OutputFormat.Base64, "decor.bin",
            RandomBytes(32, 8), fileSize: 500);

        var decoder = new StreamDecoder();
        PacketIO.ScanFile(decoder, path);

        Assert.Equal(1, decoder.FileCount);
        Assert.True(decoder.Slots[0].HeaderReceptionCount >= 3);
    }

    // ────────────────────────────────────────────────────────────────────────
    //  OutputFormatConfig — расширения и пути
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>Расширения файлов: Base64 → .DataShield.txt, Binary → .DataShield.bin.</summary>
    [Fact]
    public void GetExtension_MapsFormats()
    {
        Assert.Equal(".DataShield.txt", OutputFormatConfig.GetExtension(OutputFormat.Base64));
        Assert.Equal(".DataShield.bin", OutputFormatConfig.GetExtension(OutputFormat.Binary));
    }

    /// <summary>
    /// Определение формата по расширению: без расширения и нераспознанные —
    /// Base64.
    /// </summary>
    [Theory]
    [InlineData("x.DataShield.txt", OutputFormat.Base64)]
    [InlineData("x.DataShield.bin", OutputFormat.Binary)]
    [InlineData("x.TXT", OutputFormat.Base64)]
    [InlineData("x.BIN", OutputFormat.Binary)]
    [InlineData("x.dat", OutputFormat.Base64)]      // нераспознанное → Base64
    [InlineData("x", OutputFormat.Base64)]          // без расширения → Base64
    public void DetectFormat_ByExtension(string path, OutputFormat expected) =>
        Assert.Equal(expected, OutputFormatConfig.DetectFormat(path));

    /// <summary>Путь вывода по умолчанию — базовое имя + расширение формата.</summary>
    [Fact]
    public void GetDefaultOutputPath_AppendsFormatExtension()
    {
        Assert.Equal(
            @"C:\dir\file.DataShield.bin",
            OutputFormatConfig.GetDefaultOutputPath(@"C:\dir\file", OutputFormat.Binary));
        Assert.Equal(
            @"C:\dir\file.DataShield.txt",
            OutputFormatConfig.GetDefaultOutputPath(@"C:\dir\file", OutputFormat.Base64));
    }

    /// <summary>
    /// StripFecSuffix удаляет известные суффиксы .DataShield.*
    /// (без учёта регистра); чужие расширения остаются.
    /// </summary>
    [Theory]
    [InlineData("data.DataShield.bin", "data")]
    [InlineData("Rar.txt.DataShield.txt", "Rar.txt")]
    [InlineData("DATA.DATASHIELD.BIN", "DATA")]     // без учёта регистра
    [InlineData("plain.bin", "plain.bin")]          // суффикс не найден
    [InlineData("", "")]
    public void StripFecSuffix_RemovesKnownSuffixes(string path, string expected) =>
        Assert.Equal(expected, OutputFormatConfig.StripFecSuffix(path));

    /// <summary>Декод-путь по умолчанию: суффикс FEC срезается.</summary>
    [Fact]
    public void GetDefaultDecodeOutputPath_StripsFecSuffix() =>
        Assert.Equal(
            @"C:\dir\Rar.txt",
            OutputFormatConfig.GetDefaultDecodeOutputPath(@"C:\dir\Rar.txt.DataShield.bin"));

    /// <summary>
    /// Для обычного входа без суффикса FEC декод-путь получает вставку
    /// «.out» перед расширением (или в конец при его отсутствии).
    /// </summary>
    [Theory]
    [InlineData(@"C:\dir\stream.txt", @"C:\dir\stream.out.txt")]   // обычный файл: .out перед расширением
    [InlineData(@"C:\dir\archive.rar", @"C:\dir\archive.out.rar")]
    [InlineData(@"C:\dir\noext", @"C:\dir\noext.out")]             // без расширения — .out в конец
    [InlineData(@"C:\dir\x.DataShield.bin", @"C:\dir\x")]          // суффикс FEC — как раньше
    public void GetDefaultDecodeOutputPath_AppendsOutWhenNoFecSuffix(string input, string expected) =>
        Assert.Equal(expected, OutputFormatConfig.GetDefaultDecodeOutputPath(input));
}
