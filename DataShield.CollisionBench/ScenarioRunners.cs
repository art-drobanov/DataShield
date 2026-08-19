using System.Diagnostics;
using DataShield.Codec.Packets;

namespace DataShield.CollisionBench;

// ─────────────────────────────────────────────────────────────────────────────
//  Кампания коллизий: сценарии порождения «плохих» пакетов и движок замера
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Параметры одной ячейки кампании.</summary>
/// <param name="Scheme">Схема: A (текущая) или B (экспериментальная).</param>
/// <param name="Scenario">Сценарий порождения плохого пакета.</param>
/// <param name="HashBytes">Фактическое усечение хеша схемы.</param>
/// <param name="Sectors">K — размер полного набора индексов.</param>
/// <param name="Expected">Ожидаемая частота ложного пропуска по модели.</param>
internal sealed record CellSetup(
    string Scheme,
    string Scenario,
    int HashBytes,
    int Sectors,
    double Expected);

/// <summary>Итог ячейки кампании.</summary>
internal sealed class CellResult
{
    /// <summary>Параметры ячейки.</summary>
    public required CellSetup Setup { get; init; }

    /// <summary>Целевое число событий (для признака BudgetCapped).</summary>
    public required int TargetEvents { get; init; }

    /// <summary>Выполнено попыток (пополняется воркерами атомарно).</summary>
    public long Trials;

    /// <summary>Ложных пропусков.</summary>
    public long Accepts;

    /// <summary>B: допуск на чужом индексе (первый матч != истинного).</summary>
    public long Misplaced;

    /// <summary>B: пакет претендует сразу на несколько индексов.</summary>
    public long Ambiguous;

    /// <summary>Время прогона ячейки, с.</summary>
    public double Seconds;

    /// <summary>Останов по бюджету, а не по числу событий.</summary>
    public bool BudgetCapped;

    /// <summary>Наблюдаемая частота ложного пропуска = Accepts / Trials.</summary>
    public double ObservedRate => Trials > 0 ? (double)Accepts / Trials : 0.0;
}

/// <summary>Локальная статистика пачки попыток.</summary>
/// <param name="Trials">Количество попыток.</param>
/// <param name="Accepts">Ложных пропусков.</param>
/// <param name="Misplaced">Допусков на чужом индексе.</param>
/// <param name="Ambiguous">Неоднозначных допусков (несколько индексов).</param>
internal readonly record struct BatchStats(
    long Trials, long Accepts, long Misplaced, long Ambiguous);

/// <summary>
/// Движок кампании: параллельное накопление статистики ячейки до
/// целевого числа событий, бюджета времени или предела попыток.
/// </summary>
internal static class Campaign
{
    /// <summary>Пачка попыток над личным ГПСЧ воркера; возвращает локальную статистику.</summary>
    public delegate BatchStats BatchRunner(Random rng, int count);

    /// <summary>Прогресс ячейки: (попыток, допусков, секунд).</summary>
    public delegate void ProgressReport(long trials, long accepts, double seconds);

    /// <summary>
    /// Прогнать ячейку: параллельные воркеры с сеяными ГПСЧ выполняют пачки
    /// попыток, пока не набрано целевое число событий, не исчерпан бюджет
    /// времени или предел попыток.
    /// </summary>
    public static CellResult Run(
        CellSetup setup,
        int targetEvents,
        double budgetSeconds,
        long maxTrials,
        int threads,
        long seed,
        BatchRunner batch,
        ProgressReport? progress = null)
    {
        var result = new CellResult { Setup = setup, TargetEvents = targetEvents };

        // Размер пачки: отчет воркера не чаще ~50 мс; попытка схемы B стоит
        // K хешей, поэтому пачка ужимается пропорционально K.
        var batchTrials = setup.Scheme == "B"
            ? Math.Clamp(2_000_000 / Math.Max(1, setup.Sectors), 1, 1 << 16)
            : 1 << 16;

        var sw = Stopwatch.StartNew();
        long trials = 0, accepts = 0, misplaced = 0, ambiguous = 0;
        long stop = 0;

        var tasks = new Task[threads];
        for (var w = 0; w < threads; w++)
        {
            var worker = w;
            tasks[worker] = Task.Run(() =>
            {
                var rng = new Random(MixSeed(seed, worker));
                var lastReport = 0.0;

                while (Volatile.Read(ref stop) == 0)
                {
                    var stats = batch(rng, batchTrials);
                    Interlocked.Add(ref trials, stats.Trials);
                    Interlocked.Add(ref accepts, stats.Accepts);
                    Interlocked.Add(ref misplaced, stats.Misplaced);
                    Interlocked.Add(ref ambiguous, stats.Ambiguous);

                    var elapsed = sw.Elapsed.TotalSeconds;
                    if (elapsed >= budgetSeconds ||
                        Volatile.Read(ref accepts) >= targetEvents ||
                        Volatile.Read(ref trials) >= maxTrials)
                    {
                        Interlocked.Exchange(ref stop, 1);
                    }

                    if (progress is not null && elapsed - lastReport >= 0.25)
                    {
                        lastReport = elapsed;
                        progress(Volatile.Read(ref trials), Volatile.Read(ref accepts), elapsed);
                    }
                }
            });
        }

        Task.WaitAll(tasks);
        sw.Stop();

        result.Trials = trials;
        result.Accepts = accepts;
        result.Misplaced = misplaced;
        result.Ambiguous = ambiguous;
        result.Seconds = sw.Elapsed.TotalSeconds;
        result.BudgetCapped = accepts < targetEvents;

        return result;
    }

    /// <summary>Перемешивание (seed, worker) в уникальный сид воркера.</summary>
    private static int MixSeed(long seed, int worker) =>
        unchecked((int)(0x9E3779B9U * (uint)seed ^ 0x85EBCA6BU * (uint)(worker + 1)));
}

/// <summary>
/// Сценарии порождения «плохого» пакета. Общий H5 файла и (для B) кеш сидов
/// создаются один раз на ячейку; каждая попытка независима.
/// </summary>
internal static class Scenarios
{
    /// <summary>Сценарий «шум» (одинаков для схем A и B).</summary>
    public const string Noise = "шум";

    /// <summary>Сценарий «чужой файл».</summary>
    public const string ForeignFile = "чужой файл";

    /// <summary>Сценарий «порча».</summary>
    public const string Corrupt = "порча";

    /// <summary>H5 как в проде: Trunc24(SHA-256(случайное содержимое заголовка)).</summary>
    public static byte[] MakeHeaderHash(Random rng)
    {
        Span<byte> header = stackalloc byte[PacketFormat.HeaderContentSize];
        rng.NextBytes(header);
        return PacketHasher.ComputeHeaderHash(header);
    }

    /// <summary>Схема A, шум: случайные байты против проверяющего H5. Стоимость 1 хеш.</summary>
    public static Campaign.BatchRunner ClassicNoise(int hashBytes, byte[] headerHash)
    {
        return (rng, count) =>
        {
            var sector = new byte[PacketFormat.SectorContentSize + hashBytes];
            long accepts = 0;

            for (var i = 0; i < count; i++)
            {
                rng.NextBytes(sector);
                if (ClassicScheme.Verify(sector, headerHash, hashBytes, out _))
                    accepts++;
            }

            return new BatchStats(count, accepts, 0, 0);
        };
    }

    /// <summary>
    /// Схема A, чужой файл: валидный сектор постороннего H5 (случайный индекс,
    /// случайный payload) против проверяющего H5. Стоимость 2 хеша.
    /// </summary>
    public static Campaign.BatchRunner ClassicForeign(
        int hashBytes, byte[] headerHash, byte[] foreignHeaderHash)
    {
        return (rng, count) =>
        {
            var sector = new byte[PacketFormat.SectorContentSize + hashBytes];
            var payload = new byte[PacketFormat.PayloadSize];
            long accepts = 0;

            for (var i = 0; i < count; i++)
            {
                rng.NextBytes(payload);
                ClassicScheme.BuildSectorInto(
                    sector, rng.Next(PacketFormat.MaxDataVolumes), payload, foreignHeaderHash, hashBytes);

                if (ClassicScheme.Verify(sector, headerHash, hashBytes, out _))
                    accepts++;
            }

            return new BatchStats(count, accepts, 0, 0);
        };
    }

    /// <summary>
    /// Схема A, порча своего сектора: битовые инверсии по всему пакету
    /// (включая поле индекса) с последующей штатной проверкой. Стоимость 2 хеша.
    /// </summary>
    public static Campaign.BatchRunner ClassicCorrupt(
        int hashBytes, byte[] headerHash)
    {
        return (rng, count) =>
        {
            var sector = new byte[PacketFormat.SectorContentSize + hashBytes];
            var payload = new byte[PacketFormat.PayloadSize];
            long accepts = 0;

            for (var i = 0; i < count; i++)
            {
                rng.NextBytes(payload);
                ClassicScheme.BuildSectorInto(
                    sector, rng.Next(PacketFormat.MaxDataVolumes), payload, headerHash, hashBytes);

                CorruptSector(sector, rng);

                if (ClassicScheme.Verify(sector, headerHash, hashBytes, out _))
                    accepts++;
            }

            return new BatchStats(count, accepts, 0, 0);
        };
    }

    /// <summary>Схема B, шум: случайные байты, полный перебор K индексов.</summary>
    public static Campaign.BatchRunner ExperimentalNoise(int hashBytes, SectorSeedCache cache)
    {
        return (rng, count) =>
        {
            var sector = new byte[PacketFormat.PayloadSize + hashBytes];
            long accepts = 0, ambiguous = 0;

            for (var i = 0; i < count; i++)
            {
                rng.NextBytes(sector);
                ExperimentalScheme.VerifyAll(sector, cache, hashBytes, out _, out var matches);
                if (matches > 0)
                {
                    accepts++;
                    if (matches > 1) ambiguous++;
                }
            }

            return new BatchStats(count, accepts, 0, ambiguous);
        };
    }

    /// <summary>
    /// Схема B, чужой файл: валидный сектор постороннего H5 (случайный истинный
    /// индекс), проверка против собственного кеша сидов.
    /// </summary>
    public static Campaign.BatchRunner ExperimentalForeign(
        int hashBytes, SectorSeedCache cache, byte[] foreignHeaderHash, int sectors)
    {
        return (rng, count) =>
        {
            var sector = new byte[PacketFormat.PayloadSize + hashBytes];
            var payload = new byte[PacketFormat.PayloadSize];
            long accepts = 0, ambiguous = 0;

            for (var i = 0; i < count; i++)
            {
                rng.NextBytes(payload);
                var index = rng.Next(sectors);
                ExperimentalScheme.BuildSectorInto(
                    sector, index, payload, foreignHeaderHash, hashBytes);

                ExperimentalScheme.VerifyAll(sector, cache, hashBytes, out _, out var matches);
                if (matches > 0)
                {
                    accepts++;
                    if (matches > 1) ambiguous++;
                }
            }

            return new BatchStats(count, accepts, accepts, ambiguous);
        };
    }

    /// <summary>
    /// Схема B, порча своего сектора: битовые инверсии по всему пакету с
    /// последующим полным перебором. Дополнительно различаются пропуск на
    /// «истинном» индексе и подмена индекса (первый матч != истинного).
    /// </summary>
    public static Campaign.BatchRunner ExperimentalCorrupt(
        int hashBytes, SectorSeedCache cache, byte[] headerHash, int sectors)
    {
        return (rng, count) =>
        {
            var sector = new byte[PacketFormat.PayloadSize + hashBytes];
            var payload = new byte[PacketFormat.PayloadSize];
            long accepts = 0, misplaced = 0, ambiguous = 0;

            for (var i = 0; i < count; i++)
            {
                rng.NextBytes(payload);
                var index = rng.Next(sectors);
                ExperimentalScheme.BuildSectorInto(
                    sector, index, payload, headerHash, hashBytes);

                CorruptSector(sector, rng);

                var accepted = ExperimentalScheme.VerifyAll(
                    sector, cache, hashBytes, out var first, out var matches);
                if (accepted)
                {
                    accepts++;
                    if (first != index) misplaced++;
                    if (matches > 1) ambiguous++;
                }
            }

            return new BatchStats(count, accepts, misplaced, ambiguous);
        };
    }

    /// <summary>
    /// Порча: 1..4 уникальных позиций с ненулевыми масками XOR — пакет
    /// гарантированно отличается от собранного (по образцу PacketProbe).
    /// </summary>
    private static void CorruptSector(Span<byte> sector, Random rng)
    {
        var flips = 1 + rng.Next(4);
        var flipped = new HashSet<int>();

        while (flipped.Count < flips)
        {
            var position = rng.Next(sector.Length);
            if (flipped.Add(position))
                sector[position] ^= (byte)(1 + rng.Next(255));
        }
    }
}
