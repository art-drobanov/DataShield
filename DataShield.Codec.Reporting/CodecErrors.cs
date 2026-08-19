namespace DataShield.Codec.Reporting;

// ─────────────────────────────────────────────────────────────────────────────
//  Локализация сообщений исключений кодека
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Локализованные сообщения исключений кодека.
///
/// Язык определяется глобальным свойством <see cref="CodecStrings.Language"/>
/// (по умолчанию английский) и читается в момент выброса исключения.
/// </summary>
public static class CodecErrors
{
    private static bool Ru => CodecStrings.Language == CodecLanguage.Russian;

    /// <summary>Порог VirtualIndex вне диапазона 1..<paramref name="limit"/>.</summary>
    public static string VirtualIndexLimitRange(int limit) =>
        Ru ? $"Порог должен быть в диапазоне 1..{limit}."
           : $"The threshold must be in the range 1..{limit}.";

    /// <summary>Размер файла превышает 3-байтное поле заголовка.</summary>
    public static string FileSizeExceedsMax(long size, long max) =>
        Ru ? $"Размер файла ({size:N0}) превышает максимум {max:N0} байт (3-байтное поле)."
           : $"File size ({size:N0}) exceeds the maximum of {max:N0} bytes (3-byte field).";

    /// <summary>Размер потока превышает 3-байтное поле заголовка.</summary>
    public static string StreamSizeExceedsMax(long size, long max) =>
        Ru ? $"Размер потока ({size:N0}) превышает максимум {max:N0} байт (3-байтное поле)."
           : $"Stream size ({size:N0}) exceeds the maximum of {max:N0} bytes (3-byte field).";

    /// <summary>Общее число томов N+M превышает предел поля GF(2¹⁶).</summary>
    public static string TotalVolumesExceedMax(int total, int max) =>
        Ru ? $"N+M = {total:N0} превышает предел {max:N0} томов (GF(2¹⁶)). " +
             "Уменьшите размер файла или процент избыточности."
           : $"N+M = {total:N0} exceeds the limit of {max:N0} volumes (GF(2¹⁶)). " +
             "Reduce the file size or the redundancy percentage.";

    /// <summary>Файл не помещается в лимит томов схемы VirtualIndex.</summary>
    public static string VirtualIndexVolumeLimit(int total, int limit) =>
        Ru ? $"Схема VirtualIndex применима максимум к {limit:N0} томам, " +
             $"у файла N+M = {total:N0}. Используйте схему Classic или уменьшите размер файла."
           : $"The VirtualIndex scheme supports at most {limit:N0} volumes, " +
             $"but this file has N+M = {total:N0}. Use the Classic scheme or reduce the file size.";

    /// <summary>Поток не поддерживает чтение.</summary>
    public static string StreamNotReadable =>
        Ru ? "Поток не поддерживает чтение." : "The stream does not support reading.";

    /// <summary>Поток не поддерживает запись.</summary>
    public static string StreamNotWritable =>
        Ru ? "Поток не поддерживает запись." : "The stream does not support writing.";

    /// <summary>Путь к файлу не задан.</summary>
    public static string FilePathNotSet =>
        Ru ? "Путь к файлу не задан." : "The file path is not set.";

    /// <summary>Буфер назначения меньше заголовка.</summary>
    public static string DestinationBufferTooSmall =>
        Ru ? "Буфер назначения слишком мал." : "The destination buffer is too small.";

    /// <summary>Источник меньше заголовка.</summary>
    public static string SourceTooSmall =>
        Ru ? "Источник слишком мал." : "The source buffer is too small.";

    /// <summary>Имя файла в ASCII длиннее поля H1.</summary>
    public static string FileNameAsciiTooLong(int length, int limit) =>
        Ru ? $"Имя файла в ASCII занимает {length} байт, превышает лимит {limit}."
           : $"The file name takes {length} bytes in ASCII, exceeding the limit of {limit}.";

    /// <summary>Имя файла не представимо в поле H1.</summary>
    public static string FileNameNotRepresentable(string? fileName, int fieldSize) =>
        Ru ? $"Имя файла «{fileName}» не представимо в поле {fieldSize} байт."
           : $"The file name \"{fileName}\" cannot be represented in a {fieldSize}-byte field.";

    /// <summary>Расширение имени файла не оставляет базе минимального бюджета.</summary>
    public static string FileNameExtensionTooLong(string? fileName, int fieldSize) =>
        Ru ? $"Имя файла «{fileName}» не представимо в поле {fieldSize} байт: " +
             "расширение слишком длинное."
           : $"The file name \"{fileName}\" cannot be represented in a {fieldSize}-byte field: " +
             "the extension is too long.";

    /// <summary>Неверная длина payload сектора.</summary>
    public static string ExpectedPayloadBytes(int actual, int expected) =>
        Ru ? $"Ожидалось {expected} байт payload, получено {actual}."
           : $"Expected {expected} payload bytes, got {actual}.";

    /// <summary>Неверная длина хеша заголовка.</summary>
    public static string ExpectedHeaderHashBytes(int actual, int expected) =>
        Ru ? $"Ожидалось {expected} байт хеша заголовка, получено {actual}."
           : $"Expected {expected} header hash bytes, got {actual}.";

    /// <summary>Неверная длина содержимого заголовка.</summary>
    public static string ExpectedHeaderContentBytes(int actual, int expected) =>
        Ru ? $"Ожидалось {expected} байт содержимого заголовка, получено {actual}."
           : $"Expected {expected} bytes of header content, got {actual}.";

    /// <summary>Неверная длина содержимого сектора.</summary>
    public static string ExpectedSectorContentBytes(int actual, int expected) =>
        Ru ? $"Ожидалось {expected} байт содержимого сектора, получено {actual}."
           : $"Expected {expected} bytes of sector content, got {actual}.";

    /// <summary>Неверный размер буфера.</summary>
    public static string ExpectedBufferBytes(int actual, int expected) =>
        Ru ? $"Ожидался буфер в {expected} байт, получено {actual}."
           : $"Expected a buffer of {expected} bytes, got {actual}.";

    /// <summary>Номер сектора вне диапазона поля.</summary>
    public static string SectorIndexOutOfRange(int index, int max) =>
        Ru ? $"Номер сектора {index} вне диапазона 0..{max}."
           : $"Sector index {index} is out of the range 0..{max}.";

    /// <summary>K + M превышает предел поля GF(2¹⁶).</summary>
    public static string Gf16LimitExceeded(int total, int max) =>
        Ru ? $"K + M = {total} превышает предел GF(16) = {max}."
           : $"K + M = {total} exceeds the GF(16) limit of {max}.";

    /// <summary>Сбой инициализации RsRaid16 для кодирования.</summary>
    public static string RsInitEncodingFailed =>
        Ru ? "Не удалось инициализировать RsRaid16 для кодирования."
           : "RsRaid16 init failed for encoding.";

    /// <summary>Переполнение буфера приёмника.</summary>
    public static string ReceiverBufferOverflow(int position, int length, int capacity) =>
        Ru ? $"Буфер приёмника переполнен: {position} + {length} > {capacity}."
           : $"Receiver buffer overflow: {position} + {length} > {capacity}.";

    /// <summary>Диапазон байт задан неверно (начало больше конца).</summary>
    public static string InvalidByteRange(int from, int to) =>
        Ru ? $"Некорректный диапазон: {from} > {to}."
           : $"Invalid byte range: {from} > {to}.";

    /// <summary>Длина снимка томов не равна N+M.</summary>
    public static string VolumeSnapshotLengthMismatch(int count, int total) =>
        Ru ? $"Длина снимка томов ({count}) не равна N+M ({total})."
           : $"The volume snapshot length ({count}) does not equal N+M ({total}).";

    /// <summary>Длины массивов индексов и модулей не совпадают.</summary>
    public static string IndexModuliLengthMismatch =>
        Ru ? "Длины массивов индексов и модулей не совпадают."
           : "The lengths of the index and modulus arrays do not match.";

    /// <summary>Самотест Sha256Compact провален: инструмент не верифицирован.</summary>
    public static string Sha256SelfTestFailed =>
        Ru ? "Самотест SHA-256 провален: реализация хеша повреждена, операция прервана."
           : "SHA-256 self-test failed: the hash implementation is corrupted, operation aborted.";
}
