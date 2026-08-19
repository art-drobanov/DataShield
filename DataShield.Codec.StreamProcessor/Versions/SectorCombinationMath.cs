namespace DataShield.Codec.StreamProcessor.Versions;

// ─────────────────────────────────────────────────────────────────────────────
//  Комбинаторика обхода коллизий версий секторов
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Чистая комбинаторика поиска правильной комбинации версий секторов:
/// подсчёт числа комбинаций и состояний прокрутки, пошаговое продвижение
/// индексов и решение о выборе стратегии поиска.
/// </summary>
public static class SectorCombinationMath
{
    /// <summary>НОД двух чисел (алгоритм Евклида).</summary>
    public static long GreatestCommonDivisor(long left, long right)
    {
        while (right != 0)
        {
            var remainder = left % right;
            left = right;
            right = remainder;
        }

        return left;
    }

    /// <summary>
    /// НОК с ограничением сверху: при переполнении произведения
    /// возвращает <paramref name="limit"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Любой аргумент меньше 1.
    /// </exception>
    public static long LeastCommonMultipleLimited(
        long left, long right, long limit)
    {
        if (left < 1)
            throw new ArgumentOutOfRangeException(nameof(left));
        if (right < 1)
            throw new ArgumentOutOfRangeException(nameof(right));
        if (limit < 1)
            throw new ArgumentOutOfRangeException(nameof(limit));

        var gcd = GreatestCommonDivisor(left, right);
        var divided = left / gcd;

        if (divided > limit / right)
            return limit;

        return Math.Min(limit, divided * right);
    }

    /// <summary>
    /// Произведение сомножителей — общее число комбинаций равновероятных
    /// версий, с насыщением на long.MaxValue при переполнении.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Хотя бы один сомножитель меньше 1.
    /// </exception>
    public static long CountCombinations(IReadOnlyList<int> factors)
    {
        ArgumentNullException.ThrowIfNull(factors);

        long result = 1;

        foreach (var factor in factors)
        {
            if (factor < 1)
                throw new ArgumentOutOfRangeException(nameof(factors));

            if (result > long.MaxValue / factor)
                return long.MaxValue;

            result *= factor;
        }

        return result;
    }

    /// <summary>
    /// Число уникальных состояний синхронной прокрутки: НОК длин циклов,
    /// ограниченный сверху <paramref name="limit"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Хотя бы одна длина цикла или лимит меньше 1.
    /// </exception>
    public static long CountRotationStates(
        IReadOnlyList<int> cycleLengths, long limit)
    {
        ArgumentNullException.ThrowIfNull(cycleLengths);

        if (limit < 1)
            throw new ArgumentOutOfRangeException(nameof(limit));

        long result = 1;

        foreach (var cycleLength in cycleLengths)
        {
            if (cycleLength < 1)
                throw new ArgumentOutOfRangeException(nameof(cycleLengths));

            result = LeastCommonMultipleLimited(result, cycleLength, limit);

            if (result >= limit)
                return limit;
        }

        return Math.Max(1, result);
    }

    /// <summary>
    /// Перейти к следующей комбинации индексов (одометр, младшая позиция
    /// справа). Возвращает false, когда комбинации исчерпаны; при этом
    /// все индексы обнуляются.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Длины <paramref name="indexes"/> и <paramref name="moduli"/> не совпадают.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Хотя бы один модуль меньше 1.
    /// </exception>
    public static bool AdvanceIndexes(
        int[] indexes, IReadOnlyList<int> moduli)
    {
        ArgumentNullException.ThrowIfNull(indexes);
        ArgumentNullException.ThrowIfNull(moduli);

        if (indexes.Length != moduli.Count)
            throw new ArgumentException(
                "Длины массивов индексов и модулей не совпадают.",
                nameof(moduli));

        for (var i = 0; i < moduli.Count; i++)
            if (moduli[i] < 1)
                throw new ArgumentOutOfRangeException(nameof(moduli));

        for (var i = indexes.Length - 1; i >= 0; i--)
        {
            indexes[i]++;

            if (indexes[i] < moduli[i])
                return true;

            indexes[i] = 0;
        }

        return false;
    }

    /// <summary>
    /// Допустим ли полный перебор: комбинаций не больше жёсткого лимита,
    /// а оценка времени укладывается в бюджет.
    /// </summary>
    public static bool ShouldUseExhaustiveSearch(
        long combinationCount,
        double estimatedSeconds,
        SectorVersionSearchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (combinationCount > options.MaxExhaustiveCombinations)
            return false;

        return estimatedSeconds <= options.TimeBudget.TotalSeconds;
    }
}
