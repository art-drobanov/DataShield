using System.IO;
using System.Text;
using DataShield.Codec.Packets;
using DataShield.Codec.Reporting;
using DataShield.Codec.StreamProcessor;

namespace DataShield.Codec;

// ─────────────────────────────────────────────────────────────────────────────
//  Отчёт (.report): кодирование и декодирование
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Формирование файла отчёта «выходной файл» + «.report». Отчёт
/// кодирования — сводка операции без карты блоков; отчёт декодирования —
/// сводка приёма с построчной картой валидности секторов.
///
/// Локализация — <see cref="CodecStrings.Language"/>, поэтому отчёт
/// одинаково формируется GUI и консольными приложениями.
///
/// Сигнатуры в отчёте — реальные (без секрета), пересчитанные по потоку
/// в ОЗУ: значения «на проводе» в режиме «с секретом» защищены и для
/// вывода непригодны. Если в отчёт всё же попадает значение «с секретом»
/// (fallback статистики либо H3 заголовка без собранного содержимого),
/// строка помечается «*» в конце. SHA-512 выводится двумя строками,
/// выровненными под расположенным выше значением SHA-256, при активной
/// расширенной проверке.
/// </summary>
public static class CodecReport
{
    /// <summary>Секторов в одной строке карты.</summary>
    private const int ColumnsPerRow = 64;

    /// <summary>Ширина колонки меток в отчёте.</summary>
    private const int LabelWidth = 10;

    /// <summary>
    /// Путь отчёта: полное имя выходного файла на декодере
    /// + «.report.txt», в каталоге выходного файла.
    /// </summary>
    public static string BuildPath(string output) => output + ".report.txt";

    // ── Отчёт кодирования ───────────────────────────────────────────────────

    /// <summary>
    /// Записать отчёт кодирования (без карты блоков): имя, размер,
    /// SHA-256/SHA-512, состав томов и пакетов на языке
    /// <see cref="CodecStrings.Language"/>.
    /// </summary>
    /// <param name="path">Путь файла отчёта.</param>
    /// <param name="stats">Статистика кодирования (реальные хеши — Real*).</param>
    /// <param name="fileName">Имя закодированного файла.</param>
    public static void WriteEncoding(string path, EncodeStats stats, string fileName)
    {
        using var sw = new StreamWriter(path, false, Encoding.UTF8);

        WriteHeader(sw, CodecStrings.EncodingReportTitle);
        sw.WriteLine(MapField(CodecStrings.MapFileName, fileName));
        sw.WriteLine(MapField(CodecStrings.MapSize,
            $"{stats.FileSize:N0} {CodecStrings.MapBytesUnit}"));
        // Real* — реальные хеши; fallback на значение «на проводе»
        // помечается «*» (в режиме «с секретом» оно защищено)
        sw.WriteLine(MapField(CodecStrings.MapSha256,
            Hex(stats.RealSha256 ?? stats.Sha256) + (stats.RealSha256 is null ? "*" : "")));

        if (stats.SignaturePackets512)
            WriteSha512(sw, stats.RealSha512 ?? stats.Sha512!, stats.RealSha512 is null);

        sw.WriteLine(MapField("N (data)", $"{stats.DataCount}"));
        sw.WriteLine(MapField("M (ECC)", $"{stats.EccCount}"));
        sw.WriteLine(MapField(CodecStrings.MapHeadersCount, $"{stats.HeaderCopies}"));
        sw.WriteLine(MapField(CodecStrings.MapSignaturesCount, $"{stats.SignatureCopies}"));
        sw.WriteLine(MapField(CodecStrings.MapPackets, $"{stats.TotalPackets}"));
    }

    // ── Отчёт декодирования ─────────────────────────────────────────────────

    /// <summary>
    /// Записать отчёт декодирования: сводку приёма (имя, размер, SHA-256,
    /// при активной расширенной проверке — SHA-512 в две строки, статистика
    /// секторов) и построчную карту валидности ('█' — принят, '▓' —
    /// коллизия версий, '░' — пропущен) на языке
    /// <see cref="CodecStrings.Language"/>.
    /// </summary>
    /// <param name="path">Путь файла отчёта.</param>
    /// <param name="slot">Слот приёма.</param>
    /// <param name="content">
    /// Собранное содержимое файла (для пересчёта реальных сигнатур в ОЗУ)
    /// либо null, если сборка не выполнена.
    /// </param>
    /// <param name="signatureCheckActive">Активна ли расширенная проверка SHA-512.</param>
    /// <param name="signaturePacketCount">Принято сигнатурных пакетов (с учётом копий).</param>
    /// <param name="hashSecretUsed">
    /// Использовался ли секрет хеширования: без собранного содержимого
    /// в отчёт попадает H3 заголовка — в режиме «с секретом» это
    /// «двойной хеш», и строка помечается «*».
    /// </param>
    public static void WriteDecoding(
        string path,
        ReceptionSlot slot,
        byte[]? content = null,
        bool signatureCheckActive = false,
        int signaturePacketCount = 0,
        bool hashSecretUsed = false)
    {
        var map = slot.BuildValidityMap();
        var collisions = slot.BuildCollisionFlags();
        var total = map.Length;
        var present = 0;
        for (var i = 0; i < total; i++) if (map[i]) present++;
        var missing = total - present;
        var coverage = total == 0 ? 0.0 : present * 100.0 / total;

        using var sw = new StreamWriter(path, false, Encoding.UTF8);

        WriteHeader(sw, CodecStrings.DecodingReportTitle);
        sw.WriteLine(MapField(CodecStrings.MapFileName, slot.Header.FileName));
        sw.WriteLine(MapField(CodecStrings.MapSize,
            $"{slot.Header.FileSize:N0} {CodecStrings.MapBytesUnit}"));

        // Реальные сигнатуры пересчитываются по потоку в ОЗУ: значения
        // заголовка в режиме «с секретом» защищены и не годятся для вывода;
        // без содержимого выводится H3 — при секрете он «двойной» («*»)
        var realSha256 = content is not null
            ? Sha256Compact.HashData(content)
            : slot.Header.Sha256;
        var sha256Mark = content is null && hashSecretUsed ? "*" : "";
        sw.WriteLine(MapField(CodecStrings.MapSha256, Hex(realSha256) + sha256Mark));

        if (signatureCheckActive && content is not null)
            WriteSha512(sw, Sha512Compact.HashData(content));

        sw.WriteLine(MapField(CodecStrings.MapHeadersCount, $"{slot.HeaderReceptionCount}"));
        sw.WriteLine(MapField(CodecStrings.MapSignaturesCount, $"{signaturePacketCount}"));
        sw.WriteLine(MapField("N (data)", $"{slot.DataVolumeCount}"));
        sw.WriteLine(MapField("M (ECC)", $"{slot.EccCount}"));
        sw.WriteLine(MapField(CodecStrings.MapTotal, $"{total}"));
        sw.WriteLine(MapField(CodecStrings.MapPresent, $"{present}"));
        sw.WriteLine(MapField(CodecStrings.MapMissing, $"{missing}"));
        sw.WriteLine(MapField(CodecStrings.MapCoverage, $"{coverage:F2}%"));
        sw.WriteLine("         ────────────────────────────────────────────────────────────────");
        sw.WriteLine($"         {CodecStrings.MapLegend}");
        sw.WriteLine("         ────────────────────────────────────────────────────────────────");
        sw.WriteLine();

        if (total == 0)
        {
            sw.WriteLine("  " + CodecStrings.MapEmpty);
            return;
        }

        var idxWidth = Math.Max(7, total.ToString().Length);

        for (var row = 0; row * ColumnsPerRow < total; row++)
        {
            var start = row * ColumnsPerRow;
            var end = Math.Min(start + ColumnsPerRow, total);

            sw.Write(start.ToString().PadLeft(idxWidth));
            sw.Write(" │");

            for (var i = start; i < end; i++)
                sw.Write(!map[i] ? '░'
                    : collisions is not null && i < collisions.Length && collisions[i] ? '▓'
                    : '█');

            sw.Write(new string('─', ColumnsPerRow - (end - start)));

            var rowPresent = 0;
            for (var i = start; i < end; i++) if (map[i]) rowPresent++;
            sw.WriteLine($"│ {rowPresent}/{end - start}");
        }
    }

    // ── Служебные ───────────────────────────────────────────────────────────

    // Шапка отчёта: титул в рамке из двойных линий.
    private static void WriteHeader(StreamWriter sw, string title)
    {
        sw.WriteLine("         ════════════════════════════════════════════════════════════════");
        sw.WriteLine($"         {title}");
        sw.WriteLine("         ════════════════════════════════════════════════════════════════");
    }

    // SHA-512 двумя строками по 64 hex-символа: продолжение выровнено
    // под значением SHA-256, расположенным выше. Значение «с секретом»
    // помечается «*» в конце второй строки.
    private static void WriteSha512(StreamWriter sw, byte[] sha512, bool marked = false)
    {
        var hex = Hex(sha512);
        sw.WriteLine(MapField(CodecStrings.MapSha512, hex[..64]));
        sw.WriteLine("         " + new string(' ', LabelWidth + 3) + hex[64..] + (marked ? "*" : ""));
    }

    /// <summary>
    /// Контрольная сумма потока в формате sha256sum: файл
    /// «путь потока» + «.sha256» со строкой
    /// «hex(SHA-256)␣␣имя файла». Пишется после декодирования рядом
    /// с восстановленным файлом.
    /// </summary>
    /// <param name="streamPath">Путь файла потока.</param>
    public static void WriteSha256Sidecar(string streamPath)
    {
        var hash = Sha256Compact.HashData(File.ReadAllBytes(streamPath));
        File.WriteAllText(
            streamPath + ".sha256",
            Hex(hash) + "  " + Path.GetFileName(streamPath) + "\n");
    }

    /// <summary>Строка отчёта «Метка : значение» с выравниванием меток.</summary>
    private static string MapField(string label, string value) =>
        "         " + label.PadRight(LabelWidth) + " : " + value;

    /// <summary>Байты → hex-строка нижнего регистра.</summary>
    private static string Hex(byte[] bytes) =>
        BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
}
