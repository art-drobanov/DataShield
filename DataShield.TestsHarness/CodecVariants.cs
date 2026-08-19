using System.Security.Cryptography;
using System.Text;
using DataShield.Codec;
using DataShield.Codec.Packets;
using DataShield.Console;

namespace DataShield.TestsHarness;

// ─────────────────────────────────────────────────────────────────────────────
//  Случайные реализации и перегрузки контракта IDataShieldCodec
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Случайные реализации и перегрузки контракта IDataShieldCodec для стендов:
/// библиотечная DataShieldCodec (A) либо консольная DataShieldConsole (C) на
/// каждую операцию, случайная перегрузка кодирования (S/P/B/W/I/T) и
/// декодирования (D/T/N/A/M), а также вспомогательный разбор пакетов.
/// </summary>
public static class CodecVariants
{
    /// <summary>
    /// Создать случайную реализацию контракта IDataShieldCodec:
    /// библиотечную DataShieldCodec (A) либо консольную DataShieldConsole (C,
    /// quiet — стенд рисует собственную таблицу). Экземпляр освобождается
    /// вызывающим кодом через IDisposable.
    /// </summary>
    public static (IDataShieldCodec Codec, char Tag) CreateCodec(
        int eccPercent, int headerPercent, SectorScheme scheme,
        int virtualIndexSectorLimit, Random rng) =>
        rng.Next(2) == 0
            ? (new DataShieldCodec(
                    eccPercent, headerPercent, scheme, virtualIndexSectorLimit),
                'A')
            : (new DataShieldConsole(
                    quiet: true, eccPercent: eccPercent,
                    headerPercent: headerPercent, sectorScheme: scheme,
                    virtualIndexSectorLimit: virtualIndexSectorLimit),
                'C');

    // ── Вариативное кодирование: каждая операция выбирает случайную
    // ── перегрузку контракта и возвращает пакеты + статистику.
    // S = EncodePacketsWithStats, P = EncodeToPackets, B = EncodeToBytes,
    // W = Encode(Stream→Stream), I = EncodeInto, T = EncodeToText.

    /// <summary>
    /// Закодировать содержимое случайной перегрузкой контракта. Для
    /// перегрузок, не возвращающих статистику, она выводится из конфигурации
    /// и полученных пакетов.
    /// </summary>
    public static (List<byte[]> Packets, EncodeStats Stats, char Via) EncodeVariants(
        IDataShieldCodec codec, byte[] content, string fileName, int eccPercent, Random rng)
    {
        using var contentStream = new MemoryStream(content, writable: false);

        switch (rng.Next(6))
        {
            case 0:
            {
                var (packets, stats) = codec.EncodePacketsWithStats(
                    contentStream, fileName, progress: null, default);
                return (packets, stats, 'S');
            }
            case 1:
            {
                var packets = codec.EncodeToPackets(
                    contentStream, fileName, progress: null, default).ToList();
                return (packets, DeriveStats(content, packets, eccPercent), 'P');
            }
            case 2:
            {
                var blob = codec.EncodeToBytes(
                    contentStream, fileName, progress: null, default);
                var packets = SplitBlob(blob);
                return (packets, DeriveStats(content, packets, eccPercent), 'B');
            }
            case 3:
            {
                using var output = new MemoryStream();
                codec.Encode(contentStream, output, OutputFormat.Binary, fileName,
                    progress: null, default);
                var packets = SplitBlob(output.ToArray());
                return (packets, DeriveStats(content, packets, eccPercent), 'W');
            }
            case 4:
            {
                var packets = new List<byte[]>();
                codec.EncodeInto(contentStream, fileName, packets,
                    progress: null, default);
                return (packets, DeriveStats(content, packets, eccPercent), 'I');
            }
            default:
            {
                var text = codec.EncodeToText(
                    contentStream, fileName, progress: null, default);
                var packets = ParseTextPackets(text);
                return (packets, DeriveStats(content, packets, eccPercent), 'T');
            }
        }
    }

    // ── Вариативное декодирование одиночного файла.
    // D = Decode(Stream), T = DecodeText, N = ScanText + DecodeAll,
    // A = DecodeAll(Stream), M = накопительный Scan + DecodeAll (смешанные куски).

    /// <summary>
    /// Декодировать повреждённый поток случайной перегрузкой контракта:
    /// одноходовой Decode/DecodeText/DecodeAll либо накопительный приём.
    /// Возвращает содержимое (null — отказ) и метку использованной перегрузки.
    /// </summary>
    public static (byte[]? Restored, char Via) DecodeVariants(
        IDataShieldCodec codec, DamageResult damage, Random rng)
    {
        // Смешанные txt+bin куски — только накопительный приём
        if (!IsUniform(damage))
        {
            for (var i = 0; i < damage.Chunks.Count; i++)
            {
                using var chunkStream = new MemoryStream(damage.Chunks[i], writable: false);
                codec.Scan(chunkStream, damage.ChunkFormats[i], progress: null, default);
            }

            var mixed = codec.DecodeAll(progress: null, default);
            return (mixed.Count == 1 ? mixed[0].Content : null, 'M');
        }

        var format = damage.ChunkFormats[0];
        var bytes = ConcatChunks(damage);

        if (format == OutputFormat.Base64)
        {
            switch (rng.Next(4))
            {
                case 0:
                    using (var stream = new MemoryStream(bytes, writable: false))
                        return (codec.Decode(stream, format, progress: null, default), 'D');
                case 1:
                    return (codec.DecodeText(
                        TextLines(bytes), progress: null, default), 'T');
                case 2:
                    codec.ScanText(TextLines(bytes), progress: null, default);
                    var scanned = codec.DecodeAll(progress: null, default);
                    return (scanned.Count == 1 ? scanned[0].Content : null, 'N');
                default:
                    using (var stream = new MemoryStream(bytes, writable: false))
                    {
                        var results = codec.DecodeAll(stream, format, progress: null, default);
                        return (results.Count == 1 ? results[0].Content : null, 'A');
                    }
            }
        }

        // Бинарный поток: Decode либо DecodeAll
        if (rng.Next(2) == 0)
        {
            using var stream = new MemoryStream(bytes, writable: false);
            return (codec.Decode(stream, format, progress: null, default), 'D');
        }

        using (var stream = new MemoryStream(bytes, writable: false))
        {
            var results = codec.DecodeAll(stream, format, progress: null, default);
            return (results.Count == 1 ? results[0].Content : null, 'A');
        }
    }

    /// <summary>
    /// Декодировать куски повреждённого потока через контракт: куски
    /// одного формата — одноходовой DecodeAll по конкатенации; куски разных
    /// форматов — накопительный Scan по кускам + финальная сборка.
    /// </summary>
    public static List<DecodeResult> DecodeChunks(
        IDataShieldCodec codec, DamageResult damage)
    {
        if (IsUniform(damage))
        {
            using var stream = new MemoryStream(ConcatChunks(damage), writable: false);
            return codec.DecodeAll(stream, damage.ChunkFormats[0], progress: null, default);
        }

        for (var i = 0; i < damage.Chunks.Count; i++)
        {
            using var chunkStream = new MemoryStream(damage.Chunks[i], writable: false);
            codec.Scan(chunkStream, damage.ChunkFormats[i], progress: null, default);
        }

        return codec.DecodeAll(progress: null, default);
    }

    /// <summary>Куски потока одного формата (без смешивания txt+bin).</summary>
    public static bool IsUniform(DamageResult damage)
    {
        for (var i = 1; i < damage.ChunkFormats.Count; i++)
            if (damage.ChunkFormats[i] != damage.ChunkFormats[0])
                return false;
        return true;
    }

    /// <summary>Разобрать двоичный блоб обратно в список 75-байтных пакетов.</summary>
    public static List<byte[]> SplitBlob(byte[] blob)
    {
        var packets = new List<byte[]>(blob.Length / PacketFormat.PacketSize);
        for (var off = 0; off + PacketFormat.PacketSize <= blob.Length;
             off += PacketFormat.PacketSize)
            packets.Add(blob[off..(off + PacketFormat.PacketSize)]);
        return packets;
    }

    /// <summary>
    /// Разобрать Base64-текст в пакеты: строки длиной 100 символов,
    /// декоративные строки обрамления (начинаются с &gt; / &lt;) пропускаются.
    /// </summary>
    public static List<byte[]> ParseTextPackets(string text)
    {
        var packets = new List<byte[]>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length != PacketFormat.Base64Size || line[0] is '>' or '<')
                continue;
            try
            {
                packets.Add(Convert.FromBase64String(line));
            }
            catch (FormatException)
            {
                // Мусорная строка совпавшей длины — пропускается
            }
        }
        return packets;
    }

    /// <summary>
    /// Вывести статистику кодирования из конфигурации и полученных пакетов
    /// (для перегрузок, не возвращающих <see cref="EncodeStats"/>).
    /// </summary>
    public static EncodeStats DeriveStats(
        byte[] content, List<byte[]> packets, int eccPercent)
    {
        var dataCount = Math.Max(1,
            (content.Length + PacketFormat.PayloadSize - 1) / PacketFormat.PayloadSize);
        var eccCount = StreamEncoder.ComputeEccCount(dataCount, eccPercent);
        return new EncodeStats(
            (uint)content.Length,
            SHA256.HashData(content),
            dataCount,
            eccCount,
            packets.Count,
            packets.Count - dataCount - eccCount);
    }

    /// <summary>Байты → строки текста (мусор и декорации сохраняются).</summary>
    public static string[] TextLines(byte[] bytes) =>
        Encoding.UTF8.GetString(bytes).Split('\n');

    /// <summary>Конкатенация кусков потока в один массив.</summary>
    public static byte[] ConcatChunks(DamageResult damage)
    {
        long total = 0;
        foreach (var c in damage.Chunks) total += c.Length;

        var result = new byte[total];
        var offset = 0;
        foreach (var c in damage.Chunks)
        {
            Buffer.BlockCopy(c, 0, result, offset, c.Length);
            offset += c.Length;
        }
        return result;
    }
}
