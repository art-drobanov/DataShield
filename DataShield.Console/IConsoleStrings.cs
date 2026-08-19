using System.Globalization;
using DataShield.Codec.Reporting;

namespace DataShield.Console;

// ─────────────────────────────────────────────────────────────────────────────
//  Контракт локализации консольного приложения
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Контракт строк консольного приложения и сопутствующих настроек локали:
/// язык фаз прогресса кодека (<see cref="CodecStrings.Language"/>) и культура
/// форматирования чисел. Реализации живут в локализованных консолях
/// (DataShield.Console.RU / DataShield.Console.EN).
/// </summary>
public interface IConsoleStrings
{
    /// <summary>Язык фаз прогресса кодека (<see cref="CodecStrings.Language"/>).</summary>
    CodecLanguage CodecLanguage { get; }

    /// <summary>Культура форматирования чисел.</summary>
    CultureInfo Culture { get; }

    /// <summary>Текст справки.</summary>
    string HelpText { get; }

    /// <summary>Подсказка «как получить справку» после ошибки разбора аргументов.</summary>
    string UsageHint { get; }

    /// <summary>«Неизвестная команда: {0}».</summary>
    string UnknownCommandFormat { get; }

    /// <summary>«Неизвестный параметр: {0}».</summary>
    string UnknownOptionFormat { get; }

    /// <summary>«Для параметра {0} не указано значение».</summary>
    string OptionValueMissingFormat { get; }

    /// <summary>«--{0}: ожидалось целое, получено «{1}»».</summary>
    string IntExpectedFormat { get; }

    /// <summary>«--{0}: значение {1} вне диапазона {2}…{3}».</summary>
    string IntRangeFormat { get; }

    /// <summary>«--format: неизвестное значение {0}».</summary>
    string BadFormatValueFormat { get; }

    /// <summary>«--scheme: неизвестное значение {0}».</summary>
    string BadSchemeValueFormat { get; }

    /// <summary>Сообщение при отмене по Ctrl+C.</summary>
    string CanceledByUser { get; }

    /// <summary>Общий формат непредвиденной ошибки: «Ошибка: {0}».</summary>
    string ErrorFormat { get; }

    /// <summary>encode: требуется ровно один входной файл.</summary>
    string EncodeSingleInputError { get; }

    /// <summary>decode: требуется ровно один входной поток.</summary>
    string DecodeSingleInputError { get; }

    /// <summary>encode: входной файл не найден ({0} — путь).</summary>
    string EncodeFileNotFoundFormat { get; }

    /// <summary>decode: входной файл не найден ({0} — путь).</summary>
    string DecodeFileNotFoundFormat { get; }

    /// <summary>Поток не содержит распознаваемых заголовков DataShield.</summary>
    string NoHeadersError { get; }

    /// <summary>Данные слота не восстановлены (потери сверх ECC).</summary>
    string AssemblyFailedError { get; }

    /// <summary>Итог decode: не восстановлено ни одного файла.</summary>
    string NoFilesRestored { get; }

    /// <summary>Итог decode: «восстановлено {0} из {1}».</summary>
    string DoneSummaryFormat { get; }

    /// <summary>Метка сводки encode: входной файл.</summary>
    string FileLabel { get; }

    /// <summary>Метка сводки: размер файла.</summary>
    string SizeLabel { get; }

    /// <summary>Метка сводки: SHA-256 содержимого.</summary>
    string Sha256Label { get; }

    /// <summary>Метка сводки encode: тома (data + ECC).</summary>
    string VolumesLabel { get; }

    /// <summary>Метка сводки encode: пакеты потока.</summary>
    string PacketsLabel { get; }

    /// <summary>Метка сводки: схема секторов.</summary>
    string SchemeLabel { get; }

    /// <summary>Метка сводки encode: выходной файл.</summary>
    string OutputLabel { get; }

    /// <summary>Метка сводки decode: входной поток.</summary>
    string InputLabel { get; }

    /// <summary>Метка сводки decode: файлы в потоке.</summary>
    string FilesLabel { get; }

    /// <summary>Размер в байтах с разделителями групп («{0:N0} Б»).</summary>
    string SizeFormat { get; }

    /// <summary>Сводка томов: «{0}% избыточности ({1} data + {2} ECC)».</summary>
    string VolumesFormat { get; }

    /// <summary>Сводка пакетов: «{0}% заголовков (всего {1}, заголовков {2})».</summary>
    string PacketsFormat { get; }

    /// <summary>Сводка decode: «{0} в потоке, обработка: {1}».</summary>
    string FilesInStreamFormat { get; }

    /// <summary>Заголовок слота при decode: индекс, имя, размер, покрытие.</summary>
    string SlotHeaderFormat { get; }

    /// <summary>Примечание о коллизиях версий секторов слота.</summary>
    string CollisionsFormat { get; }

    /// <summary>Строка успешного восстановления: путь и размер.</summary>
    string RestoredLineFormat { get; }

    /// <summary>Строка decode: путь к файлу MAP-отчёта.</summary>
    string MapLineFormat { get; }

    /// <summary>Название текстового формата (Base64).</summary>
    string Base64FormatName { get; }

    /// <summary>Название бинарного формата.</summary>
    string BinaryFormatName { get; }
}
