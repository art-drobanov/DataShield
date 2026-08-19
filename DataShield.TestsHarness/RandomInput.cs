namespace DataShield.TestsHarness;

// ─────────────────────────────────────────────────────────────────────────────
//  Генерация случайных входных данных и вспомогательные рандом-операции
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Случайные входные данные для тестов и демо-стенда.
/// </summary>
public static class RandomInput
{
    /// <summary>Массив случайных байтов заданной длины.</summary>
    public static byte[] Bytes(int length, Random rng)
    {
        var result = new byte[length];
        rng.NextBytes(result);
        return result;
    }

    /// <summary>Массив случайных байтов с детерминированным сидом.</summary>
    public static byte[] Bytes(int length, int seed)
    {
        var result = new byte[length];
        new Random(seed).NextBytes(result);
        return result;
    }

    /// <summary>Случайная перестановка (Фишер—Йетс).</summary>
    public static void Shuffle<T>(IList<T> items, Random rng)
    {
        for (var i = items.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }
    }
}
