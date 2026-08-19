using System.Globalization;
using DataShield.Codec.Reporting;
using DataShield.Console;

namespace DataShield.Console.Ru;

// ─────────────────────────────────────────────────────────────────────────────
//  Русская локализация консольного приложения
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Русские строки консольного приложения и настройки локали: язык фаз
/// прогресса кодека и культура форматирования чисел (ru-RU).
/// </summary>
internal sealed class RuStrings : IConsoleStrings
{
    /// <summary>Единственный экземпляр русской локализации.</summary>
    public static RuStrings Instance { get; } = new();

    private RuStrings()
    {
    }

    /// <inheritdoc/>
    public CodecLanguage CodecLanguage => CodecLanguage.Russian;

    /// <inheritdoc/>
    public CultureInfo Culture => CultureInfo.GetCultureInfo("ru-RU");

    /// <inheritdoc/>
    public string HelpText => """
        Использование:
          DataShield.Console.RU encode <вход> [параметры]
          DataShield.Console.RU decode <вход> [параметры]

        Команды:
          encode    Кодировать файл в FEC-поток (75-байтные пакеты, строка = пакет)
          decode    Сканировать FEC-поток и восстановить файл(ы)

        Параметры encode:
          -o, --output <путь>              Выходной файл (по умолчанию: вход + .DataShield.txt|.bin)
          -f, --format <text|bin>          Формат: Base64-текст (по умолчанию) или бинарный
          -e, --ecc <0-1000>               ECC-избыточность, % (по умолчанию 10)
          -h, --header <0-50>              Копии заголовка, % (по умолчанию 3)
          -s, --scheme <classic|virtual>   Схема секторов (по умолчанию classic)
          -q, --quiet                      Отключить индикацию прогресса

        Параметры decode:
          -o, --output <путь>              Выходной файл или каталог
                                           (по умолчанию: имя входа без суффикса .DataShield.*)
          -a, --all                        Восстановить все файлы потока, а не только лучший
          -n, --use-name                   Именовать выходные файлы по заголовкам потока
          -s, --scheme <classic|virtual>   Схема приёма секторов (по умолчанию classic)
          -q, --quiet                      Отключить индикацию прогресса

        Формат входа при decode определяется по расширению: .txt — Base64, .bin — бинарный.
        Порядок пакетов произволен; дубликаты, шум и потери допускаются.

        Примеры:
          DataShield.Console.RU encode doc.zip -e 20 -f bin
          DataShield.Console.RU decode doc.zip.DataShield.bin -o out -n
        """;

    /// <inheritdoc/>
    public string UsageHint => "Вызовите с параметром --help для справки.";

    /// <inheritdoc/>
    public string UnknownCommandFormat => "Неизвестная команда: {0}";

    /// <inheritdoc/>
    public string UnknownOptionFormat => "Неизвестный параметр: {0}";

    /// <inheritdoc/>
    public string OptionValueMissingFormat => "Для параметра {0} не указано значение.";

    /// <inheritdoc/>
    public string IntExpectedFormat => "--{0}: ожидалось целое число, получено «{1}».";

    /// <inheritdoc/>
    public string IntRangeFormat => "--{0}: значение {1} вне диапазона {2}…{3}.";

    /// <inheritdoc/>
    public string BadFormatValueFormat => "--format: неизвестное значение «{0}» (доступно: text, bin).";

    /// <inheritdoc/>
    public string BadSchemeValueFormat => "--scheme: неизвестное значение «{0}» (доступно: classic, virtual).";

    /// <inheritdoc/>
    public string CanceledByUser => "Отменено пользователем.";

    /// <inheritdoc/>
    public string ErrorFormat => "Ошибка: {0}";

    /// <inheritdoc/>
    public string EncodeSingleInputError => "encode: укажите ровно один входной файл.";

    /// <inheritdoc/>
    public string DecodeSingleInputError => "decode: укажите ровно один входной FEC-файл.";

    /// <inheritdoc/>
    public string EncodeFileNotFoundFormat => "encode: файл не найден: {0}";

    /// <inheritdoc/>
    public string DecodeFileNotFoundFormat => "decode: файл не найден: {0}";

    /// <inheritdoc/>
    public string NoHeadersError => "Заголовки DataShield в потоке не обнаружены.";

    /// <inheritdoc/>
    public string AssemblyFailedError => "Сборка не удалась: потери превышают возможности ECC.";

    /// <inheritdoc/>
    public string NoFilesRestored => "Ни один файл не восстановлен.";

    /// <inheritdoc/>
    public string DoneSummaryFormat => "Готово: восстановлено файлов: {0} из {1}.";

    /// <inheritdoc/>
    public string FileLabel => "Файл:";

    /// <inheritdoc/>
    public string SizeLabel => "Размер:";

    /// <inheritdoc/>
    public string Sha256Label => "SHA-256:";

    /// <inheritdoc/>
    public string VolumesLabel => "Тома:";

    /// <inheritdoc/>
    public string PacketsLabel => "Пакеты:";

    /// <inheritdoc/>
    public string SchemeLabel => "Схема:";

    /// <inheritdoc/>
    public string OutputLabel => "Выход:";

    /// <inheritdoc/>
    public string InputLabel => "Вход:";

    /// <inheritdoc/>
    public string FilesLabel => "Файлы:";

    /// <inheritdoc/>
    public string SizeFormat => "{0:N0} Б";

    /// <inheritdoc/>
    public string VolumesFormat => "{0:F1}% избыточности ({1} data + {2} ECC)";

    /// <inheritdoc/>
    public string PacketsFormat => "{0:F1}% заголовков (всего пакетов {1}, заголовков: {2})";

    /// <inheritdoc/>
    public string FilesInStreamFormat => "{0} в потоке, обработка: {1}";

    /// <inheritdoc/>
    public string SlotHeaderFormat => "[{0}] {1} — {2:N0} Б, покрытие {3:F1}% ({4}/{5} томов, копий заголовка: {6})";

    /// <inheritdoc/>
    public string CollisionsFormat => "Коллизии версий: {0} секторов";

    /// <inheritdoc/>
    public string RestoredLineFormat => "Восстановлен: {0} ({1:N0} Б)";

    /// <inheritdoc/>
    public string MapLineFormat => "Карта: {0}";

    /// <inheritdoc/>
    public string Base64FormatName => "Base64-текст";

    /// <inheritdoc/>
    public string BinaryFormatName => "бинарный";
}
