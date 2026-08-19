using System.IO;

namespace DataShield.Codec.Packets;

// ─────────────────────────────────────────────────────────────────────────────
//  Конфигурирование формата вывода FEC-потока
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Конфигурирование формата вывода FEC-потока пакетов.
/// </summary>
public static class OutputFormatConfig
{
    // ── Расширения выходных файлов ──────────────────────────────────────────

    /// <summary>Расширение файла для Base64-режима.</summary>
    public const string ExtensionBase64 = ".DataShield.txt";

    /// <summary>Расширение файла для Binary-режима.</summary>
    public const string ExtensionBinary = ".DataShield.bin";

    // ── Маппинг формат ↔ расширение ─────────────────────────────────────────

    /// <summary>Получить расширение выходного файла для заданного формата.</summary>
    public static string GetExtension(OutputFormat format) => format switch
    {
        OutputFormat.Base64 => ExtensionBase64,
        OutputFormat.Binary => ExtensionBinary,
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    /// <summary>
    /// Построить путь к выходному файлу по умолчанию:
    /// <c>inputPath</c> + расширение формата.
    /// </summary>
    public static string GetDefaultOutputPath(string inputPath, OutputFormat format) =>
        inputPath + GetExtension(format);

    // ── Auto-detect формата по расширению входного файла ────────────────────

    /// <summary>
    /// Определить формат входного файла по расширению.
    /// Нераспознанные расширения трактуются как Base64.
    /// </summary>
    public static OutputFormat DetectFormat(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        return ext.ToLowerInvariant() switch
        {
            ".txt" => OutputFormat.Base64,
            ".bin" => OutputFormat.Binary,
            _ => OutputFormat.Base64,
        };
    }

    // ── Восстановление имени исходного файла из имени FEC-потока ─────────────

    /// <summary>
    /// Суффиксы FEC-потока (без учёта регистра), которые отбрасываются
    /// при восстановлении имени исходного файла.
    /// </summary>
    private static readonly string[] _fecSuffixes =
    {
        ExtensionBase64,  // .DataShield.txt
        ExtensionBinary,  // .DataShield.bin
    };

    /// <summary>
    /// Отбросить суффикс FEC-потока (.DataShield.txt/.bin) из пути,
    /// восстанавливая имя исходного файла.
    /// Если суффикс не найден — возвращает путь без изменений.
    /// </summary>
    /// <example>
    /// <c>data.DataShield.bin</c> → <c>data</c><br/>
    /// <c>stream.txt</c> → <c>stream.txt</c> (без изменений)
    /// </example>
    public static string StripFecSuffix(string filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return filePath;

        foreach (var suffix in _fecSuffixes)
        {
            if (filePath.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return filePath[..^suffix.Length];
        }
        return filePath;
    }

    /// <summary>
    /// Построить путь к выходному файлу декодера по умолчанию:
    /// имя входного FEC-файла без суффикса (.DataShield.*).
    /// Если суффикс отсутствует (вход не является типичным FEC-потоком),
    /// перед расширением вставляется <c>.out</c>, чтобы результат
    /// не совпал с исходным файлом:
    /// <example>
    /// <c>Rar.txt.DataShield.bin</c> → <c>Rar.txt</c><br/>
    /// <c>stream.txt</c> → <c>stream.out.txt</c>
    /// </example>
    /// </summary>
    public static string GetDefaultDecodeOutputPath(string inputPath)
    {
        var stripped = StripFecSuffix(inputPath);
        if (!ReferenceEquals(stripped, inputPath))
            return stripped;

        var dir = Path.GetDirectoryName(inputPath) ?? "";
        var name = Path.GetFileName(inputPath);
        var ext = Path.GetExtension(name);
        var stem = ext.Length > 0 ? name[..^ext.Length] : name;
        var outName = stem + ".out" + ext;
        return string.IsNullOrEmpty(dir) ? outName : Path.Combine(dir, outName);
    }
}
