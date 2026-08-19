namespace DataShield.Codec.StreamProcessor.Versions;

using DataShield.Codec.StreamProcessor.Subsets;

// ─────────────────────────────────────────────────────────────────────────────
//  Настройки обхода коллизий версий секторов
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Настройки обхода коллизий версий секторов при сборке файла.
/// Включает настройки стадии подбора подмножества томов (<see cref="SubsetSearch"/>),
/// которая запускается после исчерпания прямых механизмов.
/// </summary>
public sealed class SectorVersionSearchOptions
{
    /// <summary>
    /// Максимальное число комбинаций, для которого допускается полный перебор.
    /// Дополнительно учитывается оценка времени по первой попытке.
    /// </summary>
    public long MaxExhaustiveCombinations { get; init; } = 100_000;

    /// <summary>
    /// Максимальное число состояний при эвристической прокрутке,
    /// включая исходное состояние.
    /// </summary>
    public int MaxHeuristicAttempts { get; init; } = 100_000;

    /// <summary>
    /// Общий мягкий бюджет поиска. Проверяется между попытками сборки.
    /// </summary>
    public TimeSpan TimeBudget { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Настройки стадии подбора подмножества томов (исключение подозрительных
    /// томов с восстановлением по RS). Стадия имеет собственные лимиты попыток
    /// и времени.
    /// </summary>
    public VolumeSubsetSearchOptions SubsetSearch { get; init; } =
        VolumeSubsetSearchOptions.Default;

    /// <summary>Настройки по умолчанию (полный перебор ≤ 100 000 комбинаций).</summary>
    public static SectorVersionSearchOptions Default { get; } = new();

    /// <summary>Проверить допустимость значений; выбрасывает исключение при ошибке.</summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Любое значение вне допустимого диапазона.
    /// </exception>
    public void Validate()
    {
        if (MaxExhaustiveCombinations < 1)
            throw new ArgumentOutOfRangeException(
                nameof(MaxExhaustiveCombinations));

        if (MaxHeuristicAttempts < 1)
            throw new ArgumentOutOfRangeException(
                nameof(MaxHeuristicAttempts));

        if (TimeBudget <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(TimeBudget));

        SubsetSearch.Validate();
    }
}
