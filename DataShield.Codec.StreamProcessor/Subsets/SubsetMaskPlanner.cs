namespace DataShield.Codec.StreamProcessor.Subsets;

// ─────────────────────────────────────────────────────────────────────────────
//  Планировщик подмножеств томов: чистая комбинаторика без доступа к слоту
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Построение последовательности масок исключения томов для стадии подбора
/// подмножества (стиль <c>SectorCombinationMath</c>: чистые функции,
/// полная детерминированность, юнит-тестируемость).
///
/// Модель suspicions и уровней:
/// <list type="bullet">
///   <item><b>База</b> — все коллизионные слоты: их версии не перебираются,
///         тома исключаются и восстанавливаются RS («осознанное избегание
///         коллизий»). Если база не влезает в бюджет ECC или кап E — план
///         пуст: остаточные коллизии стадией не покрываются.</item>
///   <item><b>Уровень 1</b> — одиночные исключения присутствующих
///         неколлизионных томов в порядке подозрительности (менее
///         подтверждённые — раньше; внутри равных счётчиков — псевдослучайный
///         порядок с сидом от H5).</item>
///   <item><b>Уровни 2..MaxExtraExclusionLevel</b> — сочетания из шорт-листа
///         наиболее подозрительных кандидатов.</item>
/// </list>
///
/// Маска осуществима, когда суммарное число стёртых data-томов (пропущенные +
/// исключённые) не превышает доступных ECC-томов и капа E; неосуществимые
/// маски пропускаются до обращения матрицы. Маска без стёртых data-томов
/// бессмысленна (RS-passthrough повторяет прямую сборку) и также пропускается.
/// </summary>
public static class SubsetMaskPlanner
{
    /// <summary>
    /// Построить план подбора подмножества по снимку приёма томов.
    /// </summary>
    /// <param name="volumes">Снимки томов; длина = dataCount + eccCount.</param>
    /// <param name="dataCount">N — число data-томов.</param>
    /// <param name="eccCount">M — число ECC-томов.</param>
    /// <param name="options">Настройки стадии.</param>
    /// <param name="randomSeed">Сид псевдослучайного перемешивания (от H5).</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="volumes"/> или <paramref name="options"/> — null.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Длина <paramref name="volumes"/> не равна dataCount + eccCount.
    /// </exception>
    public static SubsetMaskPlan Plan(
        IReadOnlyList<VolumeReception> volumes,
        int dataCount,
        int eccCount,
        VolumeSubsetSearchOptions options,
        uint randomSeed)
    {
        ArgumentNullException.ThrowIfNull(volumes);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        var total = dataCount + eccCount;

        if (dataCount < 0 || eccCount < 0 || volumes.Count != total)
            throw new ArgumentException(
                $"Длина снимка томов ({volumes.Count}) не равна N+M ({total}).",
                nameof(volumes));

        // Без ECC подбор бессмыслен: восстанавливать не из чего.
        if (dataCount == 0 || eccCount == 0)
            return EmptyPlan();

        var missingData = 0;
        var missingEcc = 0;

        for (var i = 0; i < total; i++)
        {
            if (!volumes[i].Present)
            {
                if (i < dataCount) missingData++;
                else missingEcc++;
            }
        }

        var availEcc = eccCount - missingEcc;

        // База: все коллизионные слоты. База не влезает в бюджет ECC или кап E —
        // план пуст (частично исключённые коллизии стадия не покрывает).
        var baseExclusions = BuildCollisionBase(volumes);

        var (baseData, baseEcc) = CountByRange(baseExclusions, dataCount);
        var erasedDataBase = missingData + baseData;

        if (erasedDataBase > availEcc - baseEcc ||
            erasedDataBase > options.MaxErasedDataVolumes)
            return EmptyPlan();

        // Кандидаты уровней: присутствующие неколлизионные тома в порядке
        // подозрительности.
        var candidates = OrderCandidates(volumes, randomSeed);

        // Оценка числа попыток для прогресса: база + уровень 1 + шорт-лист.
        var upperBound = CountUpperBound(
            baseExclusions, candidates, dataCount, missingData, availEcc, options);

        return new SubsetMaskPlan(
            upperBound,
            EnumerateMasks(
                baseExclusions, candidates,
                dataCount, missingData, availEcc, options));
    }

    // ── База и кандидаты ────────────────────────────────────────────────────

    /// <summary>
    /// База: номера присутствующих коллизионных томов по возрастанию.
    /// Порядок не влияет на попытки (база целиком входит в каждую маску),
    /// сортировка даёт детерминизм.
    /// </summary>
    private static int[] BuildCollisionBase(IReadOnlyList<VolumeReception> volumes)
    {
        var result = new List<int>();

        for (var i = 0; i < volumes.Count; i++)
            if (volumes[i].HasCollision)
                result.Add(i);

        return result.ToArray();
    }

    /// <summary>
    /// Кандидаты дополнительных исключений: присутствующие неколлизионные
    /// тома. Псевдослучайное перемешивание (Mt19937, фиксированный сид)
    /// стабильно сортируется по возрастанию подтверждений головы:
    /// менее подтверждённые — подозрительнее и проверяются раньше,
    /// равновероятные идут в перемешанном порядке.
    /// </summary>
    private static int[] OrderCandidates(
        IReadOnlyList<VolumeReception> volumes, uint randomSeed)
    {
        var candidates = new List<int>();

        for (var i = 0; i < volumes.Count; i++)
        {
            var volume = volumes[i];
            if (volume.Present && volume.VariantCount == 1)
                candidates.Add(i);
        }

        Shuffle(candidates, randomSeed);

        return candidates
            .OrderBy(i => volumes[i].HeadConfirmationCount)
            .ToArray();
    }

    /// <summary>Тасование Фишера—Йетса на встроенном Mt19937.</summary>
    private static void Shuffle(List<int> items, uint randomSeed)
    {
        if (items.Count < 2)
            return;

        var rng = new Mt19937(randomSeed);

        for (var i = items.Count - 1; i > 0; i--)
        {
            var j = (int)(rng.Genrand() % (uint)(i + 1));
            (items[i], items[j]) = (items[j], items[i]);
        }
    }

    // ── Поток масок ─────────────────────────────────────────────────────────

    /// <summary>
    /// Ленивое перечисление масок: базовая маска, одиночные исключения,
    /// затем сочетания из шорт-листа по уровням. Неосуществимые маски
    /// пропускаются.
    /// </summary>
    private static IEnumerable<int[]> EnumerateMasks(
        int[] baseExclusions,
        int[] candidates,
        int dataCount,
        int missingData,
        int availEcc,
        VolumeSubsetSearchOptions options)
    {
        // Маска 0: база без дополнительных исключений — конфигурация,
        // которой не было в предыдущих стадиях (коллизионные слоты стёрты).
        if (baseExclusions.Length > 0)
        {
            var baseMask = (int[])baseExclusions.Clone();

            if (IsFeasible(baseMask, dataCount, missingData, availEcc,
                    options.MaxErasedDataVolumes))
                yield return baseMask;
        }

        // Уровень 1: одиночные исключения по всем кандидатам.
        foreach (var candidate in candidates)
        {
            var mask = new int[baseExclusions.Length + 1];
            baseExclusions.CopyTo(mask, 0);
            mask[^1] = candidate;
            Array.Sort(mask);

            if (IsFeasible(mask, dataCount, missingData, availEcc,
                    options.MaxErasedDataVolumes))
                yield return mask;
        }

        // Уровни 2..MaxExtraExclusionLevel: сочетания из шорт-листа.
        var shortlistLength = Math.Min(options.ShortlistSize, candidates.Length);

        for (var level = 2;
             level <= options.MaxExtraExclusionLevel && level <= shortlistLength;
             level++)
        {
            foreach (var combination in Combinations(shortlistLength, level))
            {
                var mask = new int[baseExclusions.Length + level];
                baseExclusions.CopyTo(mask, 0);

                for (var i = 0; i < level; i++)
                    mask[baseExclusions.Length + i] = candidates[combination[i]];

                Array.Sort(mask);

                if (IsFeasible(mask, dataCount, missingData, availEcc,
                        options.MaxErasedDataVolumes))
                    yield return mask;
            }
        }
    }

    /// <summary>
    /// Осуществимость маски: есть стёртые data-тома (иначе RS-passthrough
    /// повторяет прямую сборку), стёртых data не больше доступных ECC и не
    /// больше капа E.
    /// </summary>
    private static bool IsFeasible(
        int[] mask, int dataCount, int missingData, int availEcc, int maxErasedData)
    {
        var erasedData = missingData;
        var excludedEcc = 0;

        foreach (var volume in mask)
        {
            if (volume < dataCount)
                erasedData++;
            else
                excludedEcc++;
        }

        if (erasedData == 0)
            return false;

        if (erasedData > maxErasedData)
            return false;

        return erasedData <= availEcc - excludedEcc;
    }

    // ── Оценка и комбинаторика ──────────────────────────────────────────────

    /// <summary>Верхняя оценка числа попыток плана для отображения прогресса.</summary>
    private static long CountUpperBound(
        int[] baseExclusions,
        int[] candidates,
        int dataCount,
        int missingData,
        int availEcc,
        VolumeSubsetSearchOptions options)
    {
        long result = 0;

        if (baseExclusions.Length > 0 &&
            IsFeasible(baseExclusions, dataCount, missingData, availEcc,
                options.MaxErasedDataVolumes))
            result++;

        result += candidates.Length;

        var shortlistLength = Math.Min(options.ShortlistSize, candidates.Length);

        for (var level = 2;
             level <= options.MaxExtraExclusionLevel && level <= shortlistLength;
             level++)
        {
            result = SaturatingAdd(
                result, BinomialCoefficient(shortlistLength, level));
        }

        return result;
    }

    /// <summary>Биномиальный коэффициент с насыщением на long.MaxValue.</summary>
    private static long BinomialCoefficient(int n, int k)
    {
        if (k < 0 || k > n)
            return 0;

        k = Math.Min(k, n - k);
        long result = 1;

        for (var i = 1; i <= k; i++)
        {
            if (result > long.MaxValue / (n - k + i))
                return long.MaxValue;

            result = result * (n - k + i) / i;
        }

        return result;
    }

    /// <summary>Сложение с насыщением на long.MaxValue.</summary>
    private static long SaturatingAdd(long left, long right) =>
        left > long.MaxValue - right ? long.MaxValue : left + right;

    /// <summary>
    /// Сочетания k из n индексов в лексикографическом порядке.
    /// </summary>
    private static IEnumerable<int[]> Combinations(int n, int k)
    {
        var indexes = new int[k];

        for (var i = 0; i < k; i++)
            indexes[i] = i;

        while (true)
        {
            yield return (int[])indexes.Clone();

            var pivot = k - 1;

            while (pivot >= 0 && indexes[pivot] == n - k + pivot)
                pivot--;

            if (pivot < 0)
                yield break;

            indexes[pivot]++;

            for (var i = pivot + 1; i < k; i++)
                indexes[i] = indexes[i - 1] + 1;
        }
    }

    private static SubsetMaskPlan EmptyPlan() =>
        new(0, Enumerable.Empty<int[]>());

    /// <summary>Число исключённых data- и ECC-томов в наборе номеров.</summary>
    private static (int Data, int Ecc) CountByRange(int[] volumes, int dataCount)
    {
        var data = 0;

        foreach (var volume in volumes)
            if (volume < dataCount)
                data++;

        return (data, volumes.Length - data);
    }
}
