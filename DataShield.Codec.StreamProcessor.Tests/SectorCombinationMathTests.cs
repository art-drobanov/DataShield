using DataShield.Codec.StreamProcessor.Versions;
using Xunit;

namespace DataShield.Codec.StreamProcessor.Tests;

// ─────────────────────────────────────────────────────────────────────────────
//  Комбинаторная математика перебора версий секторов
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Тесты SectorCombinationMath — чистой комбинаторики поиска версий:
/// НОД/НОК с ограничением сверху, число комбинаций как произведение
/// числа версий (с насыщением в long.MaxValue), число состояний
/// синхронной прокрутки (НОК длин циклов), одометр индексов и решение
/// «полный перебор или эвристика» по лимиту комбинаций и бюджету времени.
/// </summary>
public sealed class SectorCombinationMathTests
{
    // ────────────────────────────────────────────────────────────────────────
    //  НОД и НОК
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// НОД по алгоритму Евклида: классические пары, взаимно простые,
    /// равные, нули и длинные на краях диапазона long.
    /// </summary>
    [Theory]
    [InlineData(12, 18, 6)]
    [InlineData(18, 12, 6)]
    [InlineData(1071, 462, 21)]
    [InlineData(7, 13, 1)]
    [InlineData(1, 9, 1)]
    [InlineData(5, 5, 5)]
    [InlineData(0, 5, 5)]
    [InlineData(5, 0, 5)]
    [InlineData(0, 0, 0)]
    [InlineData(long.MaxValue, long.MaxValue, long.MaxValue)]
    [InlineData(long.MaxValue, long.MaxValue - 1, 1)]
    public void GreatestCommonDivisor_ComputesEuclidGcd(
        long left, long right, long expected)
    {
        Assert.Equal(
            expected,
            SectorCombinationMath.GreatestCommonDivisor(left, right));
    }

    /// <summary>
    /// НОК с потолком: если истинный НОК превышает limit, возвращается
    /// limit (защита от переполнения и от бесконечных циклов прокрутки).
    /// </summary>
    [Theory]
    [InlineData(2, 3, 100, 6)]
    [InlineData(4, 6, 100, 12)]
    [InlineData(2, 2, 100, 2)]
    [InlineData(5, 5, 100, 5)]
    [InlineData(1, 999_999_999, long.MaxValue, 999_999_999)]
    [InlineData(2, 7, 13, 13)]
    [InlineData(100, 7, 150, 150)]
    [InlineData(3037000499, 3037000501, long.MaxValue, long.MaxValue)]
    public void LeastCommonMultipleLimited_CapsAtLimit(
        long left, long right, long limit, long expected)
    {
        Assert.Equal(
            expected,
            SectorCombinationMath.LeastCommonMultipleLimited(left, right, limit));
    }

    /// <summary>Любой аргумент < 1 — ArgumentOutOfRangeException.</summary>
    [Theory]
    [InlineData(0, 5, 10)]
    [InlineData(5, 0, 10)]
    [InlineData(5, 5, 0)]
    [InlineData(-1, 5, 10)]
    [InlineData(5, -1, 10)]
    [InlineData(5, 5, -1)]
    public void LeastCommonMultipleLimited_ArgumentBelowOne_Throws(
        long left, long right, long limit)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => SectorCombinationMath.LeastCommonMultipleLimited(left, right, limit));
    }

    // ────────────────────────────────────────────────────────────────────────
    //  Число комбинаций
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Число комбинаций — произведение числа версий каждого сектора
    /// (правило произведения); пустой набор и единичные версии дают 1.
    /// </summary>
    [Theory]
    [InlineData(new int[0], 1)]
    [InlineData(new[] { 1 }, 1)]
    [InlineData(new[] { 1, 1, 1 }, 1)]
    [InlineData(new[] { 2 }, 2)]
    [InlineData(new[] { 2, 3 }, 6)]
    [InlineData(new[] { 2, 2, 3 }, 12)]
    [InlineData(new[] { 3, 3, 3 }, 27)]
    [InlineData(new[] { 2, 3, 5, 7 }, 210)]
    public void CountCombinations_IsProductOfFactors(
        int[] factors, long expected)
    {
        Assert.Equal(expected, SectorCombinationMath.CountCombinations(factors));
    }

    /// <summary>
    /// Точность у границы: произведение 3^39 ещё помещается в long
    /// и считается без искажений.
    /// </summary>
    [Fact]
    public void CountCombinations_ProductBelowLongMax_IsExact()
    {
        // 3^39 < long.MaxValue.
        var factors = Enumerable.Repeat(3, 39).ToArray();

        Assert.Equal(
            4_052_555_153_018_976_267L,
            SectorCombinationMath.CountCombinations(factors));
    }

    /// <summary>
    /// Насыщение: произведение, превышающее long.MaxValue, возвращается
    /// как long.MaxValue (никакого переполнения/отката в минус).
    /// </summary>
    [Fact]
    public void CountCombinations_ProductAboveLongMax_Saturates()
    {
        // 3^40 > long.MaxValue.
        Assert.Equal(
            long.MaxValue,
            SectorCombinationMath.CountCombinations(Enumerable.Repeat(3, 40).ToArray()));

        // 4 × int.MaxValue × int.MaxValue > long.MaxValue.
        Assert.Equal(
            long.MaxValue,
            SectorCombinationMath.CountCombinations(
                new[] { 2, 2, 2147483647, 2147483647 }));
    }

    /// <summary>Множитель < 1 не имеет смысла — исключение.</summary>
    [Theory]
    [InlineData(new[] { 0 })]
    [InlineData(new[] { 2, -1 })]
    public void CountCombinations_FactorBelowOne_Throws(int[] factors)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => SectorCombinationMath.CountCombinations(factors));
    }

    /// <summary>null-набор множителей — ArgumentNullException.</summary>
    [Fact]
    public void CountCombinations_NullFactors_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => SectorCombinationMath.CountCombinations(null!));
    }

    // ────────────────────────────────────────────────────────────────────────
    //  Число состояний синхронной прокрутки
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Число состояний синхронной прокрутки списков равно НОК их длин.
    /// </summary>
    [Theory]
    [InlineData(new int[0], 100, 1)]
    [InlineData(new[] { 1, 1 }, 100, 1)]
    [InlineData(new[] { 2 }, 100, 2)]
    [InlineData(new[] { 2, 3 }, 100, 6)]
    [InlineData(new[] { 4, 6 }, 100, 12)]
    [InlineData(new[] { 2, 3, 5, 7 }, 210, 210)]
    public void CountRotationStates_IsLeastCommonMultiple(
        int[] cycleLengths, long limit, long expected)
    {
        Assert.Equal(
            expected,
            SectorCombinationMath.CountRotationStates(cycleLengths, limit));
    }

    /// <summary>
    /// НОК длин выше лимита — результат зажимается лимитом
    /// (перебор состояний ограничен сверху).
    /// </summary>
    [Theory]
    [InlineData(new[] { 2, 3, 5, 7 }, 100, 100)]
    [InlineData(new[] { 6, 10 }, 15, 15)]
    [InlineData(new[] { 5 }, 3, 3)]
    public void CountRotationStates_AboveLimit_IsCapped(
        int[] cycleLengths, long limit, long expected)
    {
        Assert.Equal(
            expected,
            SectorCombinationMath.CountRotationStates(cycleLengths, limit));
    }

    /// <summary>Нулевая/отрицательная длина цикла или лимит < 1 — исключение.</summary>
    [Theory]
    [InlineData(new[] { 0 }, 10)]
    [InlineData(new[] { 2 }, 0)]
    [InlineData(new[] { 2 }, -1)]
    public void CountRotationStates_InvalidArguments_Throw(
        int[] cycleLengths, long limit)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => SectorCombinationMath.CountRotationStates(cycleLengths, limit));
    }

    /// <summary>null вместо набора длин — ArgumentNullException.</summary>
    [Fact]
    public void CountRotationStates_NullCycleLengths_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => SectorCombinationMath.CountRotationStates(null!, 10));
    }

    // ────────────────────────────────────────────────────────────────────────
    //  Одометр индексов
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// AdvanceIndexes — одометр с переносом: инкрементируется крайний
    /// правый разряд, при переполнении — перенос влево; после последней
    /// комбинации возвращает false и сбрасывается в нули.
    /// </summary>
    [Fact]
    public void AdvanceIndexes_WalksOdometerFromLeastSignificantPosition()
    {
        var indexes = new[] { 0, 0 };
        var moduli = new[] { 2, 3 };

        // Полный цикл: 2 × 3 = 6 комбинаций, нулевая уже была пройдена.
        // Младший разряд — крайний правый.
        Assert.True(SectorCombinationMath.AdvanceIndexes(indexes, moduli));
        Assert.Equal(new[] { 0, 1 }, indexes);

        Assert.True(SectorCombinationMath.AdvanceIndexes(indexes, moduli));
        Assert.Equal(new[] { 0, 2 }, indexes);

        Assert.True(SectorCombinationMath.AdvanceIndexes(indexes, moduli));
        Assert.Equal(new[] { 1, 0 }, indexes);

        Assert.True(SectorCombinationMath.AdvanceIndexes(indexes, moduli));
        Assert.Equal(new[] { 1, 1 }, indexes);

        Assert.True(SectorCombinationMath.AdvanceIndexes(indexes, moduli));
        Assert.Equal(new[] { 1, 2 }, indexes);

        // Комбинации исчерпаны: одометр обнуляется.
        Assert.False(SectorCombinationMath.AdvanceIndexes(indexes, moduli));
        Assert.Equal(new[] { 0, 0 }, indexes);
    }

    /// <summary>
    /// Единичный модуль: единственная комбинация уже выбрана, одометр
    /// исчерпан немедленно.
    /// </summary>
    [Fact]
    public void AdvanceIndexes_SingleModulusOfOne_IsImmediatelyExhausted()
    {
        var indexes = new[] { 0 };

        Assert.False(SectorCombinationMath.AdvanceIndexes(indexes, new[] { 1 }));
        Assert.Equal(new[] { 0 }, indexes);
    }

    /// <summary>Один разряд с модулем 2: один шаг вперёд, затем исчерпание.</summary>
    [Fact]
    public void AdvanceIndexes_SingleModulus_TogglesAndExhausts()
    {
        var indexes = new[] { 0 };

        Assert.True(SectorCombinationMath.AdvanceIndexes(indexes, new[] { 2 }));
        Assert.Equal(new[] { 1 }, indexes);

        Assert.False(SectorCombinationMath.AdvanceIndexes(indexes, new[] { 2 }));
        Assert.Equal(new[] { 0 }, indexes);
    }

    /// <summary>Разные длины массивов индексов и модулей — ArgumentException.</summary>
    [Fact]
    public void AdvanceIndexes_MismatchedLengths_Throws()
    {
        Assert.Throws<ArgumentException>(
            () => SectorCombinationMath.AdvanceIndexes(
                new[] { 0,0 }, new[] { 1 }));
    }

    /// <summary>Модуль < 1 — ArgumentOutOfRangeException.</summary>
    [Fact]
    public void AdvanceIndexes_ModulusBelowOne_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => SectorCombinationMath.AdvanceIndexes(
                new[] { 0, 0 }, new[] { 1, 0 }));
    }

    // ────────────────────────────────────────────────────────────────────────
    //  Решение о полном переборе
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Опции поиска: лимит комбинаций 100 и бюджет 10 секунд.
    /// </summary>
    private static SectorVersionSearchOptions MakeOptions() => new()
    {
        MaxExhaustiveCombinations = 100,
        TimeBudget = TimeSpan.FromSeconds(10)
    };

    /// <summary>
    /// Полный перебор разрешён, только если число комбинаций ≤ лимита
    /// и оценка времени ≤ бюджета.
    /// </summary>
    [Theory]
    [InlineData(101, 0.0, false)]
    [InlineData(100, 10.5, false)]
    [InlineData(100, 10.0, true)]
    [InlineData(100, 0.001, true)]
    [InlineData(1, 9.99, true)]
    [InlineData(1, 1e9, false)]
    public void ShouldUseExhaustiveSearch_RespectsLimitAndBudget(
        long combinationCount, double estimatedSeconds, bool expected)
    {
        Assert.Equal(
            expected,
            SectorCombinationMath.ShouldUseExhaustiveSearch(
                combinationCount, estimatedSeconds, MakeOptions()));
    }

    /// <summary>null вместо опций — ArgumentNullException.</summary>
    [Fact]
    public void ShouldUseExhaustiveSearch_NullOptions_Throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => SectorCombinationMath.ShouldUseExhaustiveSearch(
                1, 0.0, null!));
    }
}
