using System.Text;
using DataShield.Codec.Reporting;

namespace DataShield.Codec.StreamProcessor;

// ─────────────────────────────────────────────────────────────────────────────
//  MAP-отчёт: файл карты валидности секторов
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Формирование MAP-отчёта по слоту приёма: путь одноимённого файла карты
/// («выходной файл» + «.MAP.txt») и запись сводки приёма с построчной
/// картой валидности секторов. Локализация — <see cref="CodecStrings.Language"/>,
/// поэтому отчёт одинаково формируется GUI и консольными приложениями.
/// </summary>
public static class SectorMapReport
{
    /// <summary>Секторов в одной строке карты.</summary>
    private const int ColumnsPerRow = 64;

    /// <summary>Ширина колонки меток в MAP-отчёте.</summary>
    private const int LabelWidth = 10;

    /// <summary>
    /// Путь MAP-отчёта: имя выходного файла + «.MAP.txt»,
    /// в каталоге выходного файла.
    /// </summary>
    public static string BuildPath(string output)
    {
        var dir = Path.GetDirectoryName(output);
        var name = Path.GetFileName(output);
        var mapName = $"{name}.MAP.txt";
        return string.IsNullOrEmpty(dir) ? mapName : Path.Combine(dir, mapName);
    }

    /// <summary>
    /// Записать MAP-отчёт: сводку приёма (имя, размер, SHA-256, статистика
    /// секторов) и построчную карту валидности ('█' — принят, '▓' — коллизия
    /// версий, '░' — пропущен) на языке <see cref="CodecStrings.Language"/>.
    /// </summary>
    public static void Write(string path, ReceptionSlot slot)
    {
        var map = slot.BuildValidityMap();
        var collisions = slot.BuildCollisionFlags();
        var total = map.Length;
        var present = 0;
        for (var i = 0; i < total; i++) if (map[i]) present++;
        var missing = total - present;
        var coverage = total == 0 ? 0.0 : present * 100.0 / total;

        using var sw = new StreamWriter(path, false, Encoding.UTF8);

        sw.WriteLine("         ════════════════════════════════════════════════════════════════");
        sw.WriteLine($"         {CodecStrings.MapReportTitle}");
        sw.WriteLine("         ════════════════════════════════════════════════════════════════");
        sw.WriteLine(MapField(CodecStrings.MapFileName, slot.Header.FileName));
        sw.WriteLine(MapField(CodecStrings.MapSize, $"{slot.Header.FileSize:N0} {CodecStrings.MapBytesUnit}"));
        sw.WriteLine(MapField(CodecStrings.MapSha256, Hex(slot.Header.Sha256)));
        sw.WriteLine(MapField(CodecStrings.MapHeadersCount, $"{slot.HeaderReceptionCount}"));
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

    /// <summary>Строка MAP-отчёта «Метка : значение» с выравниванием меток.</summary>
    private static string MapField(string label, string value) =>
        "         " + label.PadRight(LabelWidth) + " : " + value;

    /// <summary>Байты → hex-строка нижнего регистра.</summary>
    private static string Hex(byte[] bytes) =>
        BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
}
