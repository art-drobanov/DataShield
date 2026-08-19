namespace DataShield.Codec.Reporting;

// ─────────────────────────────────────────────────────────────────────────────
//  Локализация служебных строк кодека (названия фаз прогресса)
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Локализованные служебные строки кодека: названия фаз прогресса
/// (<see cref="CodecProgress.Phase"/>) и сопутствующие шаблоны.
///
/// По умолчанию — английский. Свойство <see cref="Language"/> меняет язык
/// глобально: все последующие операции сообщают фазы на выбранном языке.
/// Запись атомарна (enum), чтение из фоновых потоков безопасно.
///
/// <para>
/// Внимание: <see cref="Language"/> — глобальное статическое состояние процесса.
/// Юнит-тесты, меняющие язык, обязаны восстанавливать исходное значение
/// (например, в finally); такие тесты нельзя запускать параллельно
/// с другими тестами, зависящими от языка сообщений.
/// </para>
/// </summary>
public static class CodecStrings
{
    private static volatile CodecLanguage _language = CodecLanguage.English;

    /// <summary>Текущий язык сообщений кодека. По умолчанию English.</summary>
    public static CodecLanguage Language
    {
        get => _language;
        set => _language = value;
    }

    private static bool Ru => _language == CodecLanguage.Russian;

    /// <summary>Фаза: подготовка данных кодирования.</summary>
    public static string DataPreparation => Ru ? "Подготовка данных" : "Preparing data";

    /// <summary>Фаза: вычисление ECC-томов.</summary>
    public static string EccEncoding => Ru ? "ECC-кодирование" : "ECC encoding";

    /// <summary>Фаза: формирование пакетов из секторов.</summary>
    public static string PacketBuilding => Ru ? "Формирование пакетов" : "Building packets";

    /// <summary>Фаза: операция завершена.</summary>
    public static string Done => Ru ? "Готово" : "Done";

    /// <summary>Фаза: проход поиска заголовков.</summary>
    public static string HeaderSearch => Ru ? "Поиск заголовков" : "Searching for headers";

    /// <summary>Фаза: проход поиска секторов данных.</summary>
    public static string SectorSearch => Ru ? "Поиск секторов" : "Searching for sectors";

    /// <summary>Фаза: RS-восстановление пропущенных томов.</summary>
    public static string RsRecovery => Ru ? "RS-восстановление" : "RS recovery";

    /// <summary>Фаза: файл успешно собран.</summary>
    public static string AssemblyFinished => Ru ? "Сборка завершена" : "Assembly finished";

    /// <summary>Фаза: полный перебор равновероятных версий секторов.</summary>
    public static string ExhaustiveSearch =>
        Ru ? "Полный перебор версий секторов" : "Exhaustive search of sector versions";

    /// <summary>Фаза: эвристическая прокрутка равновероятных версий.</summary>
    public static string HeuristicRotation =>
        Ru ? "Эвристическая прокрутка версий секторов" : "Heuristic rotation of sector versions";

    /// <summary>Фаза: подбор подмножества томов с RS-восстановлением исключённых.</summary>
    public static string VolumeSubsetSearch =>
        Ru ? "Подбор подмножества томов" : "Volume subset search";

    /// <summary>Замещение числа, превышающего long (переполнение счётчика комбинаций).</summary>
    public static string MoreThanLongMax => Ru ? "больше 9.22e18" : "over 9.22e18";

    /// <summary>Шаблон «Проверка версий секторов, комбинаций: {0}».</summary>
    public static string SectorVersionSearchFormat =>
        Ru ? "Проверка версий секторов, комбинаций: {0}" : "Checking sector versions, combinations: {0}";

    /// <summary>Шаблон «Подбор томов, попытка: {0} / {1}».</summary>
    public static string VolumeSubsetSearchFormat =>
        Ru ? "Подбор томов, попытка: {0} / {1}" : "Volume subset search, attempt: {0} / {1}";
}
