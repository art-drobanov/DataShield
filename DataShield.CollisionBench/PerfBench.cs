using System.Diagnostics;
using DataShield.Codec.Packets;

namespace DataShield.CollisionBench;

// ─────────────────────────────────────────────────────────────────────────────
//  Стоимость приема: одна проверка A против перебора индексов B с кешем сидов
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Строка результатов замера производительности.</summary>
/// <param name="Sectors">K — размер полного набора индексов.</param>
/// <param name="Label">Схема и тип пакета.</param>
/// <param name="ThreadCount">Число потоков замера.</param>
/// <param name="Packets">Проверено пакетов.</param>
/// <param name="Hashes">Выполнено хешей SHA-256.</param>
/// <param name="Seconds">Время замера.</param>
internal sealed record PerfRow(
    int Sectors, string Label, int ThreadCount,
    long Packets, long Hashes, double Seconds)
{
    /// <summary>Пакетов в секунду (при нулевом знаменателе — 0).</summary>
    public double PacketsPerSecond => Seconds > 0 ? Packets / Seconds : 0;

    /// <summary>Хешей SHA-256 в секунду.</summary>
    public double HashesPerSecond => Seconds > 0 ? Hashes / Seconds : 0;

    /// <summary>Микросекунд на пакет.</summary>
    public double MicrosPerPacket => Packets > 0 ? Seconds * 1e6 / Packets : 0;
}

/// <summary>
/// Замер на продовых усечениях (A: 9 байт, B: 11 байт):
///  A — 1 хеш на пакет;
///  B — перебор индексов с кешем сидов: валидный пакет в среднем K/2 хешей
///      (ранний выход), чужой пакет — полный перебор K хешей.
/// </summary>
internal static class PerfBench
{
    // Число готовых образцов пакетов на ячейку (прогрев + замер циклично)
    private const int SampleSize = 4096;

    // Длительность прогрева JIT перед замером, с
    private const double WarmupSeconds = 0.3;

    /// <summary>
    /// Пройти по сетке K: построить кеш сидов (с логом размера/времени),
    /// подготовить образцы (валидный A, валидный B, шум B) и замерить
    /// каждый случай в 1 поток и во все потоки.
    /// </summary>
    public static List<PerfRow> Run(
        int[] sectorsList,
        double secondsPerCell,
        int threads,
        long seed,
        Action<string> log)
    {
        var rows = new List<PerfRow>();

        foreach (var sectors in sectorsList)
        {
            var rng = new Random(unchecked((int)(seed ^ (sectors * 7919L))));
            var headerHash = Scenarios.MakeHeaderHash(rng);

            var buildWatch = Stopwatch.StartNew();
            var cache = new SectorSeedCache(headerHash, sectors);
            buildWatch.Stop();

            log($"K = {CollisionMath.FormatCount(sectors)}: кеш сидов " +
                $"{CollisionMath.FormatCount(sectors)}×{SectorSeedCache.PrefixSize} байт " +
                $"({sectors * (double)SectorSeedCache.PrefixSize / 1024 / 1024:F1} МБ) " +
                $"построен за {buildWatch.Elapsed.TotalMilliseconds:F1} мс");

            var classicValid = BuildClassic(headerHash, rng);
            var valid = BuildExperimental(sectors, headerHash, rng);
            var foreign = BuildNoise(ExperimentalScheme.HashSize, rng);

            foreach (var threadCount in new[] { 1, threads })
            {
                rows.Add(Measure(sectors, "A валид", threadCount, classicValid,
                    s =>
                    {
                        ClassicScheme.Verify(s, headerHash, ClassicScheme.HashSize, out _);
                        return 1;
                    },
                    checkEvery: 4096, secondsPerCell));

                rows.Add(Measure(sectors, "B валид", threadCount, valid,
                    s => ExperimentalScheme.TryVerify(
                        s, cache, ExperimentalScheme.HashSize, out var index)
                            ? index + 1
                            : cache.SectorCount,
                    checkEvery: 16, secondsPerCell));

                rows.Add(Measure(sectors, "B чужой", threadCount, foreign,
                    s => ExperimentalScheme.TryVerify(
                        s, cache, ExperimentalScheme.HashSize, out _)
                            ? 0
                            : cache.SectorCount,
                    checkEvery: 16, secondsPerCell));
            }
        }

        return rows;
    }

    /// <summary>SampleSize валидных секторов схемы A со случайными индексами.</summary>
    private static byte[][] BuildClassic(byte[] headerHash, Random rng)
    {
        var samples = new byte[SampleSize][];
        var payload = new byte[PacketFormat.PayloadSize];

        for (var i = 0; i < SampleSize; i++)
        {
            rng.NextBytes(payload);
            samples[i] = ClassicScheme.BuildSector(
                rng.Next(PacketFormat.MaxDataVolumes), payload, headerHash);
        }

        return samples;
    }

    /// <summary>SampleSize валидных секторов схемы B с индексами 0..K-1.</summary>
    private static byte[][] BuildExperimental(int sectors, byte[] headerHash, Random rng)
    {
        var samples = new byte[SampleSize][];
        var payload = new byte[PacketFormat.PayloadSize];

        for (var i = 0; i < SampleSize; i++)
        {
            rng.NextBytes(payload);
            samples[i] = ExperimentalScheme.BuildSector(
                rng.Next(sectors), payload, headerHash);
        }

        return samples;
    }

    /// <summary>SampleSize случайных пакетов-«чужаков» для худшего случая B.</summary>
    private static byte[][] BuildNoise(int hashBytes, Random rng)
    {
        var samples = new byte[SampleSize][];
        for (var i = 0; i < SampleSize; i++)
        {
            samples[i] = new byte[PacketFormat.PayloadSize + hashBytes];
            rng.NextBytes(samples[i]);
        }
        return samples;
    }

    /// <summary>
    /// Замер: прогрев — проход по подмножеству образцов (JIT-путь тот же,
    /// стоимость не зависит от числа образцов), затем цикл по образцам
    /// циклически с проверкой времени не реже чем каждые checkEvery пакетов.
    /// Многопоточный вариант: каждый воркер стартует со своего образца
    /// (сдвиг w) и ходит по ним циклически; останов по флагу stop,
    /// который выставляет первый заметивший истечение бюджета.
    /// Счётчики локальные, суммируются атомарно в конце.
    /// </summary>
    private static PerfRow Measure(
        int sectors,
        string label,
        int threadCount,
        byte[][] samples,
        Func<byte[], long> verifyOnce,
        int checkEvery,
        double seconds)
    {
        if (threadCount == 1)
        {
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed.TotalSeconds < WarmupSeconds)
                for (var i = 0; i < 64; i++)
                    verifyOnce(samples[i]);

            sw.Restart();
            long packets = 0, hashes = 0;
            var next = 0;

            while (sw.Elapsed.TotalSeconds < seconds)
            {
                var limit = Math.Min(samples.Length, next + checkEvery);
                for (; next < limit; next++)
                {
                    hashes += verifyOnce(samples[next]);
                    packets++;
                }
                if (next == samples.Length) next = 0;
            }
            sw.Stop();

            return new PerfRow(sectors, label, 1, packets, hashes, sw.Elapsed.TotalSeconds);
        }

        Parallel.For(0, 64, s => verifyOnce(samples[s]));

        var sw2 = Stopwatch.StartNew();
        long totalPackets = 0, totalHashes = 0, stop = 0;

        var tasks = new Task[threadCount];
        for (var w = 0; w < threadCount; w++)
        {
            tasks[w] = Task.Run(() =>
            {
                long localPackets = 0, localHashes = 0;
                var next = w;

                while (Volatile.Read(ref stop) == 0)
                {
                    var limit = Math.Min(samples.Length, next + checkEvery);
                    for (; next < limit; next++)
                    {
                        localHashes += verifyOnce(samples[next]);
                        localPackets++;
                    }
                    if (next == samples.Length) next = 0;
                    if (sw2.Elapsed.TotalSeconds >= seconds)
                        Interlocked.Exchange(ref stop, 1);
                }

                Interlocked.Add(ref totalPackets, localPackets);
                Interlocked.Add(ref totalHashes, localHashes);
            });
        }

        Task.WaitAll(tasks);
        sw2.Stop();

        return new PerfRow(
            sectors, label, threadCount, totalPackets, totalHashes, sw2.Elapsed.TotalSeconds);
    }
}
