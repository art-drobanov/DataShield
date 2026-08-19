using System.IO;
using System.Text;
using DataShield.Codec.Packets;
using DataShield.Codec.Reporting;

namespace DataShield.Codec;

// ─────────────────────────────────────────────────────────────────────────────
//  Файловый ввод-вывод FEC-потока
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Файловый ввод-вывод FEC-потока:
/// <list type="bullet">
///   <item><b>Base64</b> — текст, по строке на пакет (.DataShield.txt).</item>
///   <item><b>Binary</b> — сырые 75-байтные пакеты подряд (.DataShield.bin).</item>
/// </list>
/// Чтение автоматически определяет формат по расширению файла.
/// </summary>
public static class PacketIO
{
    // ── Запись ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Записать пакеты в файл в заданном формате.
    /// </summary>
    /// <param name="outputPath">Путь к выходному файлу.</param>
    /// <param name="packets">Список 75-байтных пакетов.</param>
    /// <param name="format">Формат вывода.</param>
    /// <param name="sourceFileName">Имя исходного файла (для декорации Base64).</param>
    /// <param name="sha256">SHA-256 исходного файла (для декорации Base64).</param>
    /// <param name="fileSize">Размер исходного файла в байтах (для декорации Base64).</param>
    public static void WriteFile(
        string outputPath,
        IReadOnlyList<byte[]> packets,
        OutputFormat format,
        string? sourceFileName = null,
        byte[]? sha256 = null,
        uint? fileSize = null)
    {
        switch (format)
        {
            case OutputFormat.Base64:
                File.WriteAllText(outputPath,
                    WriteBase64Text(packets, sourceFileName, sha256, fileSize), Encoding.UTF8);
                break;
            case OutputFormat.Binary:
                File.WriteAllBytes(outputPath, WriteBinaryBytes(packets));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(format));
        }
    }

    /// <summary>
    /// Кодировать пакеты в Base64-текст. При указании <paramref name="sourceFileName"/>
    /// добавляются декоративные строки-обрамления (ровно 100 символов):
    /// <code>&gt;[имя 14][размер 7][SHA-256:hex 64]</code>
    /// </summary>
    public static string WriteBase64Text(
        IReadOnlyList<byte[]> packets,
        string? sourceFileName = null,
        byte[]? sha256 = null,
        uint? fileSize = null)
    {
        var sb = new StringBuilder(packets.Count * (PacketFormat.Base64Size + 2));

        if (sourceFileName is not null)
        {
            var (nameField, sizeField, shaHex) = FormatMetadata(sourceFileName, sha256, fileSize);
            sb.AppendLine($">[{nameField}][{sizeField}][SHA-256:{shaHex}]");
        }

        foreach (var p in packets)
            sb.Append(Convert.ToBase64String(p)).Append('\n');

        if (sourceFileName is not null)
        {
            var (nameField, sizeField, shaHex) = FormatMetadata(sourceFileName, sha256, fileSize);
            sb.AppendLine($"<[{nameField}][{sizeField}][SHA-256:{shaHex}]");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Записать пакеты в поток в заданном формате (поток не закрывается).
    /// </summary>
    /// <param name="output">Выходной поток.</param>
    /// <param name="packets">Список 75-байтных пакетов.</param>
    /// <param name="format">Формат вывода.</param>
    /// <param name="sourceFileName">Имя исходного файла (для декорации Base64).</param>
    /// <param name="sha256">SHA-256 исходного файла (для декорации Base64).</param>
    /// <param name="fileSize">Размер исходного файла в байтах (для декорации Base64).</param>
    public static void WriteFile(
        Stream output,
        IReadOnlyList<byte[]> packets,
        OutputFormat format,
        string? sourceFileName = null,
        byte[]? sha256 = null,
        uint? fileSize = null)
    {
        ArgumentNullException.ThrowIfNull(output);

        switch (format)
        {
            case OutputFormat.Base64:
                var bytes = Encoding.UTF8.GetBytes(
                    WriteBase64Text(packets, sourceFileName, sha256, fileSize));
                output.Write(bytes, 0, bytes.Length);
                break;
            case OutputFormat.Binary:
                foreach (var p in packets)
                    output.Write(p, 0, PacketFormat.PacketSize);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(format));
        }
    }

    /// <summary>
    /// Собрать пакеты в один двоичный массив
    /// (75 байт x N пакетов, подряд без разделителей).
    /// </summary>
    public static byte[] WriteBinaryBytes(IReadOnlyList<byte[]> packets)
    {
        var result = new byte[(long)packets.Count * PacketFormat.PacketSize];
        var offset = 0;
        foreach (var p in packets)
        {
            p.CopyTo(result, offset);
            offset += PacketFormat.PacketSize;
        }
        return result;
    }

    // ── Чтение / сканирование ───────────────────────────────────────────────

    /// <summary>
    /// Прочитать FEC-файл любого формата и сканировать его декодером.
    /// Формат определяется по расширению:
    /// <c>.txt</c> -> Base64, <c>.bin</c> -> Binary
    /// </summary>
    /// <param name="decoder">Декодер для сканирования.</param>
    /// <param name="inputPath">Путь к входному файлу.</param>
    /// <param name="progress">Приёмник прогресса (глобальная шкала 0..100).</param>
    /// <param name="ct">Токен отмены.</param>
    public static void ScanFile(
        FileDecoder decoder,
        string inputPath,
        IProgress<CodecProgress>? progress = null,
        CancellationToken ct = default)
    {
        var format = OutputFormatConfig.DetectFormat(inputPath);

        using var input = File.OpenRead(inputPath);
        ScanStream(decoder, input, format, progress, ct);
    }

    /// <summary>
    /// Прочитать FEC-поток заданного формата из <paramref name="input"/>
    /// и сканировать его декодером. Формат указывается явно:
    /// у потока нет расширения для автоопределения.
    /// </summary>
    /// <param name="decoder">Декодер для сканирования.</param>
    /// <param name="input">Входной поток FEC-данных (не закрывается).</param>
    /// <param name="format">Формат входного потока.</param>
    /// <param name="progress">Приёмник прогресса (глобальная шкала 0..100).</param>
    /// <param name="ct">Токен отмены.</param>
    public static void ScanStream(
        FileDecoder decoder,
        Stream input,
        OutputFormat format,
        IProgress<CodecProgress>? progress = null,
        CancellationToken ct = default) =>
        decoder.Scan(input, format, progress, ct);

    // ── Служебные ───────────────────────────────────────────────────────────

    /// <summary>
    /// Подготовить поля декоративной строки Base64-формата: имя, упакованное
    /// в 14 символов (<see cref="FileNameCodec.Pack"/>), размер (7 десятичных
    /// цифр с ведущими нулями) и SHA-256 hex-строку.
    /// </summary>
    private static (string nameField, string sizeField, string shaHex) FormatMetadata(
        string sourceFileName, byte[]? sha256, uint? fileSize)
    {
        string packed;

        try
        {
            packed = FileNameCodec.Pack(sourceFileName);
        }
        catch (InvalidOperationException)
        {
            // Имя не представимо политикой упаковки — декорация не должна
            // ломать запись, ограничиваем простым усечением.
            packed = sourceFileName.Length <= PacketFormat.FileNameSize
                ? sourceFileName
                : sourceFileName[..(PacketFormat.FileNameSize - 1)] +
                  FileNameCodec.TruncationMarker;
        }

        var nameField = packed.PadRight(PacketFormat.FileNameSize);
        var sizeField = fileSize is { } size
            ? size.ToString().PadLeft(7, '0')
            : new string(' ', 7);

        var shaHex = sha256 is not null
            ? BitConverter.ToString(sha256).Replace("-", "").ToLowerInvariant()
            : "";

        return (nameField, sizeField, shaHex);
    }
}