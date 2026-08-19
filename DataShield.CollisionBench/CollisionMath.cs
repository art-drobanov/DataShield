using System.Globalization;

namespace DataShield.CollisionBench;

// ─────────────────────────────────────────────────────────────────────────────
//  Теория коллизионной стойкости и статистика отчетов
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Модель ложного пропуска «плохого» пакета (шум, чужой файл, порча):
///
///  A: P = 2^-8t          — один хеш по хранящемуся индексу;
///  B: P = 1-(1-2^-8h)^K  — перебор K индексов, h = t + 2 (байты ушли в хеш).
///
/// Эффективная стойкость: A = 8t бит; B = 8h - log2(K) бит. При K = 65536
/// схемы эквивалентны, при меньших K схема B выигрывает log2(65536/K) бит,
/// платя K хешей на прием пакета.
/// </summary>
internal static class CollisionMath
{
    /// <summary>Вероятность ложного пропуска схемы A при усечении hashBytes.</summary>
    public static double FalseAcceptClassic(int hashBytes) =>
        Math.Pow(2.0, -8.0 * hashBytes);

    /// <summary>Вероятность ложного пропуска схемы B: хотя бы одно совпадение из K.</summary>
    public static double FalseAcceptExperimental(int hashBytes, int sectors)
    {
        var p = Math.Pow(2.0, -8.0 * hashBytes);
        var mean = p * sectors;

        // При K·p << 1 формула 1-(1-p)^K вырождается в double ((1-p) rounds to 1):
        // используем прямое произведение, относительная погрешность < K·p/2.
        if (mean < 1e-9)
            return mean;

        return 1.0 - Math.Pow(1.0 - p, sectors);
    }

    /// <summary>Эффективная стойкость, бит: -log2(P).</summary>
    public static double EffectiveBits(double probability) =>
        probability <= 0 ? double.PositiveInfinity : -Math.Log2(probability);

    /// <summary>Доверительный интервал Вильсона (95%) для доли accepts/trials.</summary>
    public static (double Lo, double Hi) WilsonInterval(int accepts, long trials)
    {
        if (trials <= 0) return (0.0, 1.0);

        const double z = 1.959963984540054; // 97.5%-квантиль нормального
        var n = (double)trials;
        var phat = accepts / n;
        var denom = 1.0 + z * z / n;
        var centre = (phat + z * z / (2 * n)) / denom;
        var spread = z * Math.Sqrt(
            phat * (1.0 - phat) / n + z * z / (4 * n * n)) / denom;

        return (Math.Max(0.0, centre - spread), Math.Min(1.0, centre + spread));
    }

    /// <summary>Вероятность в экспоненциальной форме: 3.91E-03.</summary>
    public static string FormatProb(double p) =>
        p <= 0 ? "0" : p.ToString("E2", CultureInfo.InvariantCulture);

    /// <summary>Стойкость в форме 2^-72.0.</summary>
    public static string FormatBits(double bits) =>
        double.IsPositiveInfinity(bits)
            ? "inf"
            : $"2^-{bits.ToString("F1", CultureInfo.InvariantCulture)}";

    /// <summary>Компактное целое с разделителями: 1 234 567.</summary>
    public static string FormatCount(long value) =>
        value.ToString("N0", CultureInfo.InvariantCulture)
            .Replace(",", " ");
}
