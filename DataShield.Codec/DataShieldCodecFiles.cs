using System.IO;
using DataShield.Codec.Packets;
using DataShield.Codec.Reporting;

namespace DataShield.Codec;

// ─────────────────────────────────────────────────────────────────────────────
//  Файловые операции единого кодека
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Файловые операции единого контракта <see cref="IDataShieldCodec"/>:
/// открытие файловых потоков по путям, автоопределение формата FEC-потока
/// по расширению (.txt → Base64, .bin → Binary). Ядро кодека работает
/// только со <see cref="Stream"/> — файловые перегрузки вынесены в этот
/// отдельный класс сборки.
///
/// Работает с любой реализацией контракта (библиотечной
/// <see cref="DataShieldCodec"/> и консольной DataShieldConsole).
/// </summary>
public static class DataShieldCodecFiles
{
    /// <summary>
    /// Кодировать файл в FEC-файл заданного формата.
    /// </summary>
    /// <param name="codec">Кодек (любая реализация контракта).</param>
    /// <param name="inputPath">Путь к исходному файлу.</param>
    /// <param name="outputPath">Путь к выходному FEC-файлу.</param>
    /// <param name="format">Формат выходного FEC-потока.</param>
    /// <param name="progress">Приёмник прогресса (глобальная шкала 0..100).</param>
    /// <param name="ct">Токен отмены.</param>
    /// <returns>Статистика кодирования (SHA-256, счётчики томов и пакетов).</returns>
    public static EncodeStats EncodeFile(
        IDataShieldCodec codec,
        string inputPath,
        string outputPath,
        OutputFormat format,
        IProgress<CodecProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(codec);
        if (string.IsNullOrWhiteSpace(inputPath))
            throw new ArgumentException(CodecErrors.FilePathNotSet, nameof(inputPath));
        if (string.IsNullOrWhiteSpace(outputPath))
            throw new ArgumentException(CodecErrors.FilePathNotSet, nameof(outputPath));

        using var input = File.OpenRead(inputPath);
        using var output = File.Create(outputPath);
        return codec.Encode(input, output, format, Path.GetFileName(inputPath), progress, ct);
    }

    /// <summary>
    /// Декодировать FEC-файл (формат определяется по расширению) и вернуть
    /// результаты по всем обнаруженным файлам, упорядоченные по числу
    /// принятых секторов по убыванию.
    /// </summary>
    /// <param name="codec">Кодек (любая реализация контракта).</param>
    /// <param name="inputPath">Путь к входному FEC-файлу (.txt или .bin).</param>
    /// <param name="progress">Приёмник прогресса (глобальная шкала 0..100).</param>
    /// <param name="ct">Токен отмены.</param>
    public static List<DecodeResult> DecodeFile(
        IDataShieldCodec codec,
        string inputPath,
        IProgress<CodecProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(codec);
        if (string.IsNullOrWhiteSpace(inputPath))
            throw new ArgumentException(CodecErrors.FilePathNotSet, nameof(inputPath));

        var format = OutputFormatConfig.DetectFormat(inputPath);

        using var input = File.OpenRead(inputPath);
        return codec.DecodeAll(input, format, progress, ct);
    }
}
