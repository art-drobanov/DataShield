namespace DataShield.Codec.StreamProcessor.Subsets;

// ─────────────────────────────────────────────────────────────────────────────
//  План подбора подмножества томов
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Готовый план подбора подмножества томов: оценка числа попыток (для прогресса)
/// и ленивый поток масок исключения. Каждая маска — отсортированный массив
/// номеров томов, которые в попытке помечаются стёртыми (RS восстанавливает
/// их по оставшимся).
/// </summary>
public sealed class SubsetMaskPlan
{
    internal SubsetMaskPlan(long attemptUpperBound, IEnumerable<int[]> exclusions)
    {
        AttemptUpperBound = attemptUpperBound;
        Exclusions = exclusions;
    }

    /// <summary>
    /// Верхняя оценка числа попыток плана (до фильтра осуществимости и лимитов
    /// выполнения); для отображения прогресса.
    /// </summary>
    public long AttemptUpperBound { get; }

    /// <summary>Ленивый поток масок исключения в порядке выполнения.</summary>
    public IEnumerable<int[]> Exclusions { get; }
}
