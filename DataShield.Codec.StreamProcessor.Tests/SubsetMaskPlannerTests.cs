using DataShield.Codec.StreamProcessor.Subsets;
using DataShield.Codec.StreamProcessor.Versions;
using Xunit;

namespace DataShield.Codec.StreamProcessor.Tests;

// ─────────────────────────────────────────────────────────────────────────────
//  Чистая комбинаторика планировщика подбора подмножества томов
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Тесты SubsetMaskPlanner — планировщика масок исключения томов
/// для стадии 3 (подбор подмножества при RS-восстановлении).
///
/// Стратегия: база из коллизионных томов (исключаются всегда), затем
/// одиночные исключения по «подозрительности» (меньше подтверждений —
/// раньше), затем комбинации шорт-листа по уровням; осуществимость
/// каждой маски ограничена бюджетом ECC и капом стираний.
/// </summary>
public sealed class SubsetMaskPlannerTests
{
    // ── Валидация и базовые случаи ──────────────────────────────────────────

    /// <summary>Без ECC исключать нечего — пустой план.</summary>
    [Fact]
    public void Plan_WithoutEcc_ReturnsEmptyPlan()
    {
        var volumes = PresentVolumes(dataCount: 3, eccCount: 0);

        var plan = Plan(volumes, dataCount: 3, eccCount: 0);

        Assert.Equal(0, plan.AttemptUpperBound);
        Assert.Empty(plan.Exclusions);
    }

    /// <summary>Без data-томов восстановление бессмысленно — пустой план.</summary>
    [Fact]
    public void Plan_WithoutData_ReturnsEmptyPlan()
    {
        var volumes = PresentVolumes(dataCount: 0, eccCount: 2);

        var plan = Plan(volumes, dataCount: 0, eccCount: 2);

        Assert.Equal(0, plan.AttemptUpperBound);
        Assert.Empty(plan.Exclusions);
    }

    /// <summary>Отрицательные счётчики томов — ArgumentException.</summary>
    [Theory]
    [InlineData(-1, 2)]
    [InlineData(3, -1)]
    public void Plan_NegativeCounts_ThrowArgumentException(int dataCount, int eccCount)
    {
        var volumes = PresentVolumes(dataCount: 2, eccCount: 2);

        Assert.Throws<ArgumentException>(
            () => Plan(volumes, dataCount, eccCount));
    }

    /// <summary>Длина карты приёма ≠ N+M — ArgumentException.</summary>
    [Fact]
    public void Plan_LengthMismatch_ThrowsArgumentException()
    {
        var volumes = PresentVolumes(dataCount: 2, eccCount: 2);

        Assert.Throws<ArgumentException>(
            () => Plan(volumes, dataCount: 3, eccCount: 2));
    }

    /// <summary>null-карта приёма — ArgumentNullException.</summary>
    [Fact]
    public void Plan_NullVolumes_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(
            () => SubsetMaskPlanner.Plan(
                null!, dataCount: 1, eccCount: 1,
                VolumeSubsetSearchOptions.Default, randomSeed: 1));
    }

    /// <summary>null-опции — ArgumentNullException.</summary>
    [Fact]
    public void Plan_NullOptions_ThrowsArgumentNullException()
    {
        var volumes = PresentVolumes(dataCount: 1, eccCount: 1);

        Assert.Throws<ArgumentNullException>(
            () => SubsetMaskPlanner.Plan(
                volumes, dataCount: 1, eccCount: 1, null!, randomSeed: 1));
    }

    /// <summary>
    /// Валидация VolumeSubsetSearchOptions: нулевые/отрицательные лимиты
    /// попыток, бюджета, капа стираний, шорт-листа и уровня — исключение.
    /// </summary>
    [Theory]
    [InlineData(0, 30, 32, 64, 3)]
    [InlineData(100, 0, 32, 64, 3)]
    [InlineData(100, 30, 0, 64, 3)]
    [InlineData(100, 30, 32, 1, 3)]
    [InlineData(100, 30, 32, 64, 0)]
    public void Validate_InvalidValues_Throw(
        long maxAttempts, int timeBudgetMs, int maxErased, int shortlist, int level)
    {
        var options = new VolumeSubsetSearchOptions
        {
            MaxAttempts = maxAttempts,
            TimeBudget = TimeSpan.FromMilliseconds(timeBudgetMs),
            MaxErasedDataVolumes = maxErased,
            ShortlistSize = shortlist,
            MaxExtraExclusionLevel = level
        };

        Assert.Throws<ArgumentOutOfRangeException>(options.Validate);
    }

    /// <summary>
    /// Вложенные опции стадии 3 валидируются вместе с родительскими
    /// SectorVersionSearchOptions.
    /// </summary>
    [Fact]
    public void SectorVersionSearchOptions_InvalidSubsetSearch_ThrowsOnValidate()
    {
        var options = new SectorVersionSearchOptions
        {
            SubsetSearch = new VolumeSubsetSearchOptions { MaxAttempts = 0 }
        };

        Assert.Throws<ArgumentOutOfRangeException>(options.Validate);
    }

    /// <summary>
    /// Коллизия тома = присутствие И более одной версии; дыра или
    /// единственная версия коллизией не считаются.
    /// </summary>
    [Fact]
    public void VolumeReception_CollisionRequiresPresenceAndMultiplicity()
    {
        Assert.True(new VolumeReception(true, 2, 1).HasCollision);
        Assert.False(new VolumeReception(true, 1, 1).HasCollision);
        Assert.False(new VolumeReception(false, 2, 0).HasCollision);
    }

    // ── База: коллизионные слоты ────────────────────────────────────────────

    /// <summary>
    /// База (коллизионные тома) влезает в бюджет ECC: каждая маска
    /// включает её целиком; первая маска — база без дополнений.
    /// </summary>
    [Fact]
    public void Plan_CollisionsFit_EveryMaskIncludesWholeBase()
    {
        // N=2, M=2; коллизия на data-томе 1. База влезает: E=1 <= 2.
        var volumes = PresentVolumes(dataCount: 2, eccCount: 2);
        SetCollision(volumes, 1);

        var masks = Plan(volumes, dataCount: 2, eccCount: 2).Exclusions.ToArray();

        Assert.NotEmpty(masks);
        Assert.All(masks, mask => Assert.Contains(1, mask));

        // Маска 0 — база без дополнений: конфигурация, которой не было
        // в предыдущих стадиях (коллизионный слот стёрт).
        Assert.Equal(new[] { "1" }, Keys(masks.Take(1)));
    }

    /// <summary>
    /// База коллизий больше бюджета ECC — исключить все спорные тома
    /// невозможно, план пуст.
    /// </summary>
    [Fact]
    public void Plan_CollisionBaseExceedsEccBudget_ReturnsEmptyPlan()
    {
        // N=2, M=1; обе data с коллизиями — исключить обе нельзя (E=2 > 1).
        var volumes = PresentVolumes(dataCount: 2, eccCount: 1);
        SetCollision(volumes, 0);
        SetCollision(volumes, 1);

        var plan = Plan(volumes, dataCount: 2, eccCount: 1);

        Assert.Equal(0, plan.AttemptUpperBound);
        Assert.Empty(plan.Exclusions);
    }

    /// <summary>
    /// База больше капа MaxErasedDataVolumes — план пуст, даже если
    /// бюджет ECC позволил бы.
    /// </summary>
    [Fact]
    public void Plan_CollisionBaseExceedsErasedCap_ReturnsEmptyPlan()
    {
        // N=2, M=4; обе data с коллизиями, кап E=1 — база (E=2) не влезает.
        var volumes = PresentVolumes(dataCount: 2, eccCount: 4);
        SetCollision(volumes, 0);
        SetCollision(volumes, 1);

        var plan = Plan(
            volumes, dataCount: 2, eccCount: 4,
            new VolumeSubsetSearchOptions { MaxErasedDataVolumes = 1 },
            randomSeed: 1);

        Assert.Equal(0, plan.AttemptUpperBound);
        Assert.Empty(plan.Exclusions);
    }

    // ── Уровень 1: одиночные исключения ─────────────────────────────────────

    /// <summary>
    /// Уровень 1: data-тома исключаются в порядке подозрительности
    /// (меньше подтверждений — раньше); ECC-исключения при полном
    /// комплекте data бессмысленны и пропускаются.
    /// </summary>
    [Fact]
    public void Plan_Level1_ExcludesDataVolumesInSuspicionOrder()
    {
        // Уникальные счётчики головы → порядок детерминирован без перемешивания:
        // чем меньше подтверждений, тем раньше том проверяется. Уровень
        // ограничен первым, чтобы проверить именно одиночные исключения.
        var volumes = new VolumeReception[5];
        volumes[0] = new VolumeReception(true, 1, 5); // data
        volumes[1] = new VolumeReception(true, 1, 1); // data
        volumes[2] = new VolumeReception(true, 1, 3); // data
        volumes[3] = new VolumeReception(true, 1, 4); // ecc
        volumes[4] = new VolumeReception(true, 1, 2); // ecc

        var masks = Plan(
            volumes, dataCount: 3, eccCount: 2,
            new VolumeSubsetSearchOptions { MaxExtraExclusionLevel = 1 },
            randomSeed: 1).Exclusions.ToArray();

        // Только исключения data-томов осмысленны (E > 0); ECC-исключения при
        // полном комплекте data — passthrough — пропускаются.
        Assert.Equal(new[] { "1", "2", "0" }, Keys(masks));
    }

    /// <summary>
    /// При дыре в data исключение ECC-тома меняет набор уравнений
    /// восстановления — такие маски уровня 1 генерируются.
    /// </summary>
    [Fact]
    public void Plan_Level1_EccExclusionEmitted_WhenDataIsMissing()
    {
        // N=2, M=2; data-том 1 не принят. Исключение ECC-тома меняет набор
        // уравнений восстановления — такие маски нужны.
        var volumes = new VolumeReception[4];
        volumes[0] = new VolumeReception(true, 1, 2);  // data
        volumes[1] = new VolumeReception(false, 0, 0); // data — дыра
        volumes[2] = new VolumeReception(true, 1, 1);  // ecc
        volumes[3] = new VolumeReception(true, 1, 3);  // ecc

        var masks = Plan(
            volumes, dataCount: 2, eccCount: 2,
            new VolumeSubsetSearchOptions { MaxExtraExclusionLevel = 1 },
            randomSeed: 1).Exclusions.ToArray();

        Assert.Equal(new[] { "2", "0", "3" }, Keys(masks));
    }

    /// <summary>
    /// Осуществимость: маски за пределами бюджета ECC отбрасываются
    /// (при M=1 пары исключений невозможны, остаются только одиночные).
    /// </summary>
    [Fact]
    public void Plan_Level1_Feasibility_SkipsMasksBeyondEccBudget()
    {
        // N=3, M=1: исключить один data-том можно (1 <= 1), пару — нельзя.
        var volumes = PresentVolumes(dataCount: 3, eccCount: 1);

        var masks = Plan(
            volumes, dataCount: 3, eccCount: 1,
            new VolumeSubsetSearchOptions { MaxExtraExclusionLevel = 2 },
            randomSeed: 1).Exclusions.ToArray();

        Assert.Equal(3, masks.Length);
        Assert.All(masks, mask => Assert.Single(mask));
    }

    /// <summary>
    /// Дыра в data уже съедает весь ECC-бюджет — дополнительные
    /// исключения невозможны, масок нет.
    /// </summary>
    [Fact]
    public void Plan_MissingDataBeyondEcc_NoMasks()
    {
        // N=3, M=1, один data потерян: единственный ECC уже занят восстановлением
        // дыры — никаких дополнительных исключений не осталось.
        var volumes = PresentVolumes(dataCount: 3, eccCount: 1);
        SetMissing(volumes, 2);

        var plan = Plan(volumes, dataCount: 3, eccCount: 1);

        Assert.Empty(plan.Exclusions);
    }

    // ── Уровни 2+: шорт-лист ────────────────────────────────────────────────

    /// <summary>
    /// Уровень 2: пары строятся только из шорт-листа самых подозрительных
    /// кандидатов (ShortlistSize = 3 → пары {0,1}, {0,2}, {1,2}).
    /// </summary>
    [Fact]
    public void Plan_Level2_UsesShortlistCombinationsOnly()
    {
        // 4 кандидата с уникальными счётчиками, шорт-лист 3, уровни до 2.
        var volumes = new VolumeReception[4];
        volumes[0] = new VolumeReception(true, 1, 1);
        volumes[1] = new VolumeReception(true, 1, 2);
        volumes[2] = new VolumeReception(true, 1, 3);
        volumes[3] = new VolumeReception(true, 1, 4);

        var options = new VolumeSubsetSearchOptions
        {
            ShortlistSize = 3,
            MaxExtraExclusionLevel = 2
        };

        var masks = Plan(volumes, dataCount: 2, eccCount: 2, options, 1)
            .Exclusions.ToArray();

        // Уровень 1: data-исключения ({0}, {1}); ECC-исключения — passthrough.
        // Уровень 2: лексикографические пары шорт-листа {0,1,2} с data-томом.
        Assert.Equal(new[] { "0", "1", "0,1", "0,2", "1,2" }, Keys(masks));
    }

    /// <summary>
    /// AttemptUpperBound — верхняя оценка ДО фильтра осуществимости
    /// (все уровни целиком), сами маски могут быть её подмножеством.
    /// </summary>
    [Fact]
    public void Plan_AttemptUpperBound_CountsAllLevelsBeforeFeasibility()
    {
        // N=1, M=2: верхняя оценка = 3 кандидата + C(3,2) + C(3,3) = 7;
        // осуществимы только маски со стёртым data-томом 0.
        var volumes = PresentVolumes(dataCount: 1, eccCount: 2);

        var options = new VolumeSubsetSearchOptions
        {
            ShortlistSize = 64,
            MaxExtraExclusionLevel = 3
        };

        var plan = Plan(volumes, dataCount: 1, eccCount: 2, options, 1);

        Assert.Equal(7, plan.AttemptUpperBound);

        var masks = plan.Exclusions.ToArray();

        // Порядок перемешанных равноподтверждённых кандидатов зависит от сида,
        // поэтому сравниваем множество, а не последовательность.
        Assert.True(masks.Length <= plan.AttemptUpperBound);
        Assert.Equal(
            new HashSet<string> { "0", "0,1", "0,2" },
            Keys(masks).ToHashSet());
    }

    // ── Детерминизм перемешивания ───────────────────────────────────────────

    /// <summary>
    /// Детерминизм: один randomSeed — одна и та же последовательность
    /// масок (перемешивание равноподтверждённых кандидатов воспроизводимо).
    /// </summary>
    [Fact]
    public void Plan_SameSeed_ProducesSameSequence()
    {
        // Все счётчики равны — порядок кандидатов задаёт только перемешивание.
        var volumes = PresentVolumes(dataCount: 4, eccCount: 2);

        var first = Plan(volumes, dataCount: 4, eccCount: 2, randomSeed: 42)
            .Exclusions.ToArray();
        var second = Plan(volumes, dataCount: 4, eccCount: 2, randomSeed: 42)
            .Exclusions.ToArray();

        Assert.Equal(Keys(first), Keys(second));

        // Уровень 1: каждый data-том — ровно одним одиночным исключением
        // (ECC-исключения при полном комплекте data пропускаются).
        var singles = first.Where(mask => mask.Length == 1).ToArray();

        Assert.Equal(4, singles.Length);
        Assert.Equal(
            new HashSet<int> { 0, 1, 2, 3 },
            singles.Select(mask => mask[0]).ToHashSet());

        // Уровень 2 присутствует: пары с data-томами осуществимы (E <= 2).
        Assert.Contains(first, mask => mask.Length == 2);
    }

    /// <summary>
    /// Полнота покрытия: каждый присутствующий том попадает хотя бы
    /// в одну маску перебора.
    /// </summary>
    [Fact]
    public void Plan_FullEnumeration_CoversEveryPresentVolume()
    {
        var volumes = PresentVolumes(dataCount: 4, eccCount: 3);
        SetMissing(volumes, 2);

        var masks = Plan(
            volumes, dataCount: 4, eccCount: 3,
            new VolumeSubsetSearchOptions { MaxExtraExclusionLevel = 2 },
            randomSeed: 7).Exclusions.ToArray();

        var covered = masks.SelectMany(mask => mask).ToHashSet();

        // Каждый присутствующий том попадает хотя бы в одну маску.
        for (var volume = 0; volume < volumes.Length; volume++)
        {
            if (volumes[volume].Present)
                Assert.Contains(volume, covered);
        }
    }

    // ── Вспомогательные методы ──────────────────────────────────────────────

    /// <summary>Обёртка над Plan с опциями по умолчанию и сидом 1.</summary>
    private static SubsetMaskPlan Plan(
        IReadOnlyList<VolumeReception> volumes,
        int dataCount,
        int eccCount,
        VolumeSubsetSearchOptions? options = null,
        uint randomSeed = 1) =>
        SubsetMaskPlanner.Plan(
            volumes,
            dataCount,
            eccCount,
            options ?? VolumeSubsetSearchOptions.Default,
            randomSeed);

    /// <summary>Строковые ключи масок: надёжное сравнение последовательностей.</summary>
    private static string[] Keys(IEnumerable<int[]> masks) =>
        masks.Select(mask => string.Join(",", mask)).ToArray();

    /// <summary>Все тома присутствуют, по одной версии, одинаковая надёжность.</summary>
    private static VolumeReception[] PresentVolumes(int dataCount, int eccCount)
    {
        var volumes = new VolumeReception[dataCount + eccCount];

        for (var i = 0; i < volumes.Length; i++)
            volumes[i] = new VolumeReception(true, 1, 1);

        return volumes;
    }

    /// <summary>Пометить том как не принятый (дыра).</summary>
    private static void SetMissing(VolumeReception[] volumes, int index) =>
        volumes[index] = new VolumeReception(false, 0, 0);

    /// <summary>Пометить том как коллизионный (две версии).</summary>
    private static void SetCollision(VolumeReception[] volumes, int index) =>
        volumes[index] = volumes[index] with { VariantCount = 2 };
}
