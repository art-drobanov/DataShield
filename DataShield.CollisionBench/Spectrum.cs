using System.Diagnostics;
using System.Numerics;
using DataShield.Codec.Packets;

namespace DataShield.CollisionBench;

// ─────────────────────────────────────────────────────────────────────────────
//  Спектр совпадений префиксов на полных усечениях (9/11 байт)
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>Итог спектра: гистограмма максимальных совпадений ведущих бит.</summary>
internal sealed class SpectrumResult
{
    /// <summary>Схема («A»/«B»).</summary>
    public required string Scheme { get; init; }

    /// <summary>Размер усечённого хеша в байтах.</summary>
    public required int HashBytes { get; init; }

    /// <summary>Число целей K на попытку (для B; A всегда 1).</summary>
    public required int Sectors { get; init; }

    /// <summary>Выполнено попыток.</summary>
    public required long Trials { get; init; }

    /// <summary>Гистограмма максимальных совпадений ведущих бит (индекс = биты).</summary>
    public required long[] Histogram { get; init; }

    /// <summary>Затраченное время, с.</summary>
    public required double Seconds { get; init; }

    /// <summary>Число целей: trials для A, trials·K для B.</summary>
    public double Targets => Scheme == "B" ? (double)Trials * Sectors : Trials;

    /// <summary>Полных совпадений (ложных пропусков на полных усечениях).</summary>
    public long FullMatches => Histogram[^1];

    /// <summary>
    /// Модельная вероятность P(M ≥ m) на попытку: 2⁻ᵐ для A,
    /// 1-(1-2⁻ᵐ)^K для B (максимум по K целям).
    /// </summary>
    public double ExpectedProbability(int m)
    {
        if (m <= 0) return 1.0;
        var p = Math.Pow(2.0, -m);
        return Scheme == "B" ? 1.0 - Math.Pow(1.0 - p, Sectors) : p;
    }

    /// <summary>Максимальное наблюдаемое число совпадающих бит.</summary>
    public int MaxObserved
    {
        get
        {
            for (var i = Histogram.Length - 1; i >= 0; i--)
                if (Histogram[i] > 0) return i;
            return 0;
        }
    }
}

/// <summary>
/// Непрерывная проверка равномерности усеченного хеша без ожидания реальных
/// коллизий (2⁻⁷²/2⁻⁸⁸ недостижимы перебором). Для каждой попытки-«шума»
/// измеряется максимальное по всем целям число совпавших ведущих бит M;
/// модель: P(M ≥ m) = targets·2⁻ᵐ. Совпадение наблюдаемого спектра с моделью
/// до максимально достижимой глубины + ноль полных совпадений подтверждает
/// корректность экстраполяции на полный хвост.
/// </summary>
internal static class Spectrum
{
    /// <summary>
    /// Схема A: одна цель на попытку — индекс берется из самого пакета,
    /// как при сканировании потока шума.
    /// </summary>
    public static SpectrumResult RunClassic(
        int hashBytes,
        long trials,
        int threads,
        double budgetSeconds,
        long seed,
        Campaign.ProgressReport? progress = null)
    {
        var headerHash = Scenarios.MakeHeaderHash(
            new Random(unchecked((int)seed)));

        return Run(
            scheme: "A", hashBytes, sectors: 1, trials, threads, budgetSeconds, seed, progress,
            (Random rng, byte[] sector, byte[] h5) =>
            {
                rng.NextBytes(sector);

                Span<byte> input = stackalloc byte[
                    PacketFormat.HeaderHashSize + PacketFormat.SectorContentSize];
                h5.CopyTo(input);
                sector.AsSpan(0, PacketFormat.SectorContentSize).CopyTo(
                    input[PacketFormat.HeaderHashSize..]);

                Span<byte> hash = stackalloc byte[32];
                Sha256Compact.HashData(input, hash);

                return (
                    MatchBits(sector.AsSpan(ClassicScheme.HashOffset), hash[..hashBytes]),
                    false);
            },
            headerHash);
    }

    /// <summary>Схема B: максимум совпадений по всем K целям (полный перебор).</summary>
    public static SpectrumResult RunExperimental(
        int hashBytes,
        int sectors,
        long trials,
        int threads,
        double budgetSeconds,
        long seed,
        Campaign.ProgressReport? progress = null)
    {
        var cache = new SectorSeedCache(
            Scenarios.MakeHeaderHash(new Random(unchecked((int)seed))), sectors);

        return Run(
            scheme: "B", hashBytes, sectors, trials, threads, budgetSeconds, seed, progress,
            (Random rng, byte[] sector, SectorSeedCache cacheIn) =>
            {
                rng.NextBytes(sector);

                Span<byte> input = stackalloc byte[
                    PacketFormat.HeaderHashSize + PacketFormat.SectorNumberSize +
                    PacketFormat.PayloadSize];
                sector.AsSpan(0, PacketFormat.PayloadSize).CopyTo(
                    input[(PacketFormat.HeaderHashSize + PacketFormat.SectorNumberSize)..]);

                Span<byte> hash = stackalloc byte[32];
                var best = 0;

                for (var i = 0; i < cacheIn.SectorCount; i++)
                {
                    cacheIn.Prefix(i).CopyTo(input);
                    Sha256Compact.HashData(input, hash);

                    var bits = MatchBits(
                        sector.AsSpan(ExperimentalScheme.HashOffset), hash[..hashBytes]);
                    if (bits > best) best = bits;
                    if (best == 8 * hashBytes) break;
                }

                return (best, best == 8 * hashBytes);
            },
            cache);
    }

    /// <summary>
    /// Одна попытка: вернуть (число совпавших бит, полное совпадение?).
    /// </summary>
    private delegate (int Bits, bool Full) Attempt<in TSeed>(
        Random rng, byte[] sector, TSeed seed);

    /// <summary>
    /// Общий движок: воркеры с личными ГПСЧ и локальными гистограммами
    /// работают чанками до общей остановки (бюджет или лимит попыток);
    /// результат — слитая гистограмма и счётчик полных совпадений.
    /// </summary>
    private static SpectrumResult Run<TSeed>(
        string scheme,
        int hashBytes,
        int sectors,
        long trials,
        int threads,
        double budgetSeconds,
        long seed,
        Campaign.ProgressReport? progress,
        Attempt<TSeed> attempt,
        TSeed attemptSeed)
    {
        var sectorSize = scheme == "B"
            ? PacketFormat.PayloadSize + hashBytes
            : PacketFormat.SectorContentSize + hashBytes;

        var chunkTrials = scheme == "B"
            ? Math.Clamp(2_000_000 / Math.Max(1, sectors), 1, 1 << 16)
            : 1 << 16;

        var histogram = new long[8 * hashBytes + 1];
        long done = 0, fullMatches = 0, stop = 0;
        var sw = Stopwatch.StartNew();

        var tasks = new Task[threads];
        for (var w = 0; w < threads; w++)
        {
            var worker = w;
            tasks[worker] = Task.Run(() =>
            {
                var rng = new Random(unchecked((int)(seed * 31 + worker)));
                var sector = new byte[sectorSize];
                var local = new long[8 * hashBytes + 1];
                long localFull = 0;
                var lastReport = 0.0;

                while (Volatile.Read(ref stop) == 0)
                {
                    var executed = 0;
                    for (var i = 0; i < chunkTrials && Volatile.Read(ref stop) == 0; i++)
                    {
                        var (bits, full) = attempt(rng, sector, attemptSeed);
                        local[bits]++;
                        if (full) localFull++;
                        executed++;
                    }

                    Interlocked.Add(ref done, executed);

                    var elapsed = sw.Elapsed.TotalSeconds;
                    if (elapsed >= budgetSeconds || Volatile.Read(ref done) >= trials)
                        Interlocked.Exchange(ref stop, 1);

                    if (progress is not null && elapsed - lastReport >= 0.25)
                    {
                        lastReport = elapsed;
                        progress(Volatile.Read(ref done), Volatile.Read(ref fullMatches), elapsed);
                    }
                }

                for (var i = 0; i < local.Length; i++)
                    if (local[i] > 0) Interlocked.Add(ref histogram[i], local[i]);
                if (localFull > 0) Interlocked.Add(ref fullMatches, localFull);
            });
        }

        Task.WaitAll(tasks);
        sw.Stop();

        return new SpectrumResult
        {
            Scheme = scheme,
            HashBytes = hashBytes,
            Sectors = sectors,
            Trials = done,
            Histogram = histogram,
            Seconds = sw.Elapsed.TotalSeconds,
        };
    }

    /// <summary>Число совпавших ведущих бит двух усеченных хешей (0..8·len).</summary>
    private static int MatchBits(ReadOnlySpan<byte> stored, ReadOnlySpan<byte> computed)
    {
        var bits = 0;
        for (var i = 0; i < stored.Length; i++)
        {
            int diff = stored[i] ^ computed[i];
            if (diff == 0)
            {
                bits += 8;
                continue;
            }

            bits += BitOperations.LeadingZeroCount((uint)diff) - 24;
            break;
        }
        return bits;
    }
}
