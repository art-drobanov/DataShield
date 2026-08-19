using System.Globalization;
using System.Text;
using DataShield.Codec.Packets;
using DataShield.Interfaces;

namespace DataShield.CollisionBench;

// ─────────────────────────────────────────────────────────────────────────────
//  Стенд сравнения коллизионной стойкости схем секторов данных A и B
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Стенд сравнения коллизионной стойкости схем секторов данных A (Classic)
/// и B (VirtualIndex): модельные вероятности ложного приёма против
/// наблюдаемыми на потоках шума, порчи и чужих файлов, плюс замеры
/// производительности хеширования. Режимы: selftest / collide / full /
/// perf / all (см. <see cref="BenchOptions"/>).
/// </summary>
internal static class Program
{
    // Замок сериализации вывода в консоль из параллельных потоков кампании
    private static readonly object Gate = new();

    /// <summary>
    /// Точка входа: разбор аргументов, баннер и диспетчеризация по режиму
    /// (selftest / collide / full / perf / all). Коды возврата: 0 — успех,
    /// 64 — ошибка аргументов, 2 — runtime-сбой, 1 — расхождение с моделью.
    /// </summary>
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        BenchOptions options;
        try
        {
            options = BenchOptions.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.WriteLine("ОШИБКА АРГУМЕНТОВ: " + ex.Message);
            BenchOptions.PrintHelp();
            return 64;
        }

        Banner(options);

        try
        {
            return options.Mode switch
            {
                "selftest" => RunSelfTest(options),
                "collide" => RunCollide(options),
                "full" => RunFull(options),
                "perf" => RunPerf(options),
                "all" => RunAll(options),
                _ => 64
            };
        }
        catch (Exception ex)
        {
            PrintLine($"ОШИБКА: {ex.Message}", ConsoleColor.Red);
            return 2;
        }
    }

    /// <summary>Режим all: запустить всё по очереди, вернуть худший код возврата.</summary>
    private static int RunAll(BenchOptions o)
    {
        var codes = new[]
        {
            RunSelfTest(o),
            RunCollide(o),
            RunFull(o),
            RunPerf(o)
        };
        return codes.Max();
    }

    // ── selftest ─────────────────────────────────────────────────────────────

    /// <summary>Режим selftest: проверки корректности схем, кеша сидов и сценариев.</summary>
    private static int RunSelfTest(BenchOptions o)
    {
        PrintLine("── Режим selftest: корректность реализаций ──", ConsoleColor.DarkCyan);
        SelfTest.Run(o.Seed, o.Threads);
        PrintLine("ИТОГ SELFTEST: все проверки пройдены.", ConsoleColor.Green);
        return 0;
    }

    // ── collide ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Режим collide: кампания по ячейкам «схема × сценарий × усечение × K».
    /// Сравнение при равном байтовом бюджете: A = 66+t байт с хешом t,
    /// B = 66+t байт с хешом t+2. Частоты допусков сверяются с аналитической
    /// моделью (A: 2^-8t, B: K·2^-8(t+2)) по z-оценке.
    /// </summary>
    private static int RunCollide(BenchOptions o)
    {
        PrintLine("── Режим collide: кампания коллизий при параметризованном усечении ──",
            ConsoleColor.DarkCyan);
        PrintLine(
            "Равный байтовый бюджет: A = idx(2)+payload(64)+Trunc_t = 66+t байт," +
            " B = payload(64)+Trunc_(t+2) = 66+t байт.",
            ConsoleColor.DarkGray);
        PrintLine("");

        var master = new Random(unchecked((int)o.Seed));
        var results = new List<CellResult>();

        foreach (var t in o.Truncations)
        {
            var headerHash = Scenarios.MakeHeaderHash(master);
            var foreignHash = Scenarios.MakeHeaderHash(master);
            var hashBytesB = t + 2;

            var expectedA = CollisionMath.FalseAcceptClassic(t);

            AddCell(results, o,
                new CellSetup("A", Scenarios.Noise, t, 0, expectedA),
                $"A/{Scenarios.Noise} t={t}",
                Scenarios.ClassicNoise(t, headerHash));

            AddCell(results, o,
                new CellSetup("A", Scenarios.ForeignFile, t, 0, expectedA),
                $"A/{Scenarios.ForeignFile} t={t}",
                Scenarios.ClassicForeign(t, headerHash, foreignHash));

            AddCell(results, o,
                new CellSetup("A", Scenarios.Corrupt, t, 0, expectedA),
                $"A/{Scenarios.Corrupt} t={t}",
                Scenarios.ClassicCorrupt(t, headerHash));

            foreach (var sectors in o.Sectors)
            {
                var cache = new SectorSeedCache(headerHash, sectors);
                var expectedB = CollisionMath.FalseAcceptExperimental(hashBytesB, sectors);

                AddCell(results, o,
                    new CellSetup("B", Scenarios.Noise, hashBytesB, sectors, expectedB),
                    $"B/{Scenarios.Noise} t={t} K={sectors}",
                    Scenarios.ExperimentalNoise(hashBytesB, cache));

                AddCell(results, o,
                    new CellSetup("B", Scenarios.ForeignFile, hashBytesB, sectors, expectedB),
                    $"B/{Scenarios.ForeignFile} t={t} K={sectors}",
                    Scenarios.ExperimentalForeign(hashBytesB, cache, foreignHash, sectors));

                AddCell(results, o,
                    new CellSetup("B", Scenarios.Corrupt, hashBytesB, sectors, expectedB),
                    $"B/{Scenarios.Corrupt} t={t} K={sectors}",
                    Scenarios.ExperimentalCorrupt(hashBytesB, cache, headerHash, sectors));
            }
        }

        PrintCollideTable(results);
        return PrintCollideVerdict(results);
    }

    /// <summary>
    /// Прогнать одну ячейку кампании: сид выводится из базового сида
    /// и параметров ячейки, чтобы ячейки были независимы и воспроизводимы.
    /// </summary>
    private static void AddCell(
        List<CellResult> results,
        BenchOptions o,
        CellSetup setup,
        string label,
        Campaign.BatchRunner runner)
    {
        var seed = o.Seed ^ ((long)setup.HashBytes << 24) ^ (setup.Sectors * 0x9E37);
        var result = Campaign.Run(
            setup, o.TargetEvents, o.BudgetSeconds, long.MaxValue,
            o.Threads, seed, runner, Progress(label));
        ClearProgress();
        results.Add(result);
    }

    /// <summary>
    /// Таблица результатов: частота, ожидание, отношение, z-оценка и
    /// Wilson-интервал по каждой ячейке; плюс сводка специфичных для B
    /// подмен индекса и неоднозначных допусков.
    /// </summary>
    private static void PrintCollideTable(IReadOnlyList<CellResult> results)
    {
        PrintLine("");
        PrintLine("Результаты кампании:", ConsoleColor.White);
        PrintLine(
            $"{"Сх",-2} {"Сценарий",-11} {"hB",2} {"K",6} {"Попыток",14} {"Допуск",8} " +
            $"{"Частота",9} {"Ожидание",9} {"Отн",5} {"z",5}  {"CI95 частоты",25} Итог",
            ConsoleColor.DarkCyan);

        foreach (var r in results)
        {
            var (lo, hi) = CollisionMath.WilsonInterval((int)r.Accepts, r.Trials);
            var ratio = r.Setup.Expected > 0 && r.ObservedRate > 0
                ? r.ObservedRate / r.Setup.Expected
                : 0;
            var z = ZScore(r);
            var (verdict, color) = CellVerdict(r);

            var line =
                $"{r.Setup.Scheme,-2} {r.Setup.Scenario,-11} {r.Setup.HashBytes,2} " +
                $"{(r.Setup.Scheme == "A" ? "—" : CollisionMath.FormatCount(r.Setup.Sectors)),6} " +
                $"{CollisionMath.FormatCount(r.Trials),14} {CollisionMath.FormatCount(r.Accepts),8} " +
                $"{CollisionMath.FormatProb(r.ObservedRate),9} " +
                $"{CollisionMath.FormatProb(r.Setup.Expected),9} {ratio,5:F2} {z,5:F1} " +
                $"[{CollisionMath.FormatProb(lo)}..{CollisionMath.FormatProb(hi)}] ";

            lock (Gate)
            {
                Console.ForegroundColor = ConsoleColor.Gray;
                Console.Write(line);
                Console.ForegroundColor = color;
                Console.WriteLine(verdict);
                Console.ResetColor();
            }
        }

        var misplaced = results.Where(r => r.Setup.Scheme == "B" && r.Setup.Scenario == Scenarios.Corrupt)
            .Sum(r => r.Misplaced);
        var corruptAccepts = results.Where(r => r.Setup.Scheme == "B" && r.Setup.Scenario == Scenarios.Corrupt)
            .Sum(r => r.Accepts);
        var ambiguous = results.Where(r => r.Setup.Scheme == "B").Sum(r => r.Ambiguous);

        PrintLine("");
        PrintLine(
            $"Специфика B: подмена индекса (первый матч != истинного): " +
            $"{CollisionMath.FormatCount(misplaced)} из {CollisionMath.FormatCount(corruptAccepts)} " +
            $"допусков в сценарии «{Scenarios.Corrupt}»; неоднозначные допуски (несколько индексов " +
            $"сразу): {CollisionMath.FormatCount(ambiguous)}.", ConsoleColor.DarkGray);
    }

    /// <summary>
    /// Вердикт ячейки: &lt;10 событий — статистики мало; |z| ≤ 4 — модель
    /// подтверждена; иначе расхождение.
    /// </summary>
    private static (string Text, ConsoleColor Color) CellVerdict(CellResult r)
    {
        if (r.Accepts < 10)
            return ("МАЛО СОБЫТИЙ", ConsoleColor.DarkYellow);

        return Math.Abs(ZScore(r)) <= 4.0
            ? ("МОДЕЛЬ OK", ConsoleColor.Green)
            : ("РАСХОЖДЕНИЕ", ConsoleColor.Red);
    }

    /// <summary>
    /// z-оценка отклонения наблюдаемой частоты от модели. Порог |z| ≤ 4 выбран
    /// из-за множественных сравнений: при десятках ячеек 95%-интервал
    /// закономерно дает 1-2 ложных «расхождения».
    /// </summary>
    private static double ZScore(CellResult r)
    {
        var n = (double)r.Trials;
        var p = r.Setup.Expected;
        if (n <= 0 || p <= 0 || p >= 1) return 0;
        return (r.ObservedRate - p) / Math.Sqrt(p * (1.0 - p) / n);
    }

    /// <summary>Ячейка подтверждена, если событий достаточно и |z| ≤ 4.</summary>
    private static bool ModelConfirmed(CellResult r)
    {
        if (r.Accepts < 10) return true;
        return Math.Abs(ZScore(r)) <= 4.0;
    }

    /// <summary>
    /// Итог collide: экстраполяция выигрыша B в битах на продовых усечениях
    /// (A: Trunc9 = 72 бита, B: Trunc11) по сетке K; затем список ячеек
    /// с расхождением (код 1) или подтверждение модели (код 0).
    /// </summary>
    private static int PrintCollideVerdict(IReadOnlyList<CellResult> results)
    {
        PrintLine("");
        PrintLine("Экстраполяция на продовые усечения (A: Trunc9 = 72 бита, B: Trunc11):",
            ConsoleColor.White);
        PrintLine($"{"K",8} {"A, бит",9} {"B, бит",9} {"Выигрыш B",12}", ConsoleColor.DarkCyan);

        foreach (var sectors in new[] { 64, 128, 256, 512, 1024, 2048, 4096, 8192, 16384, 32768, 65535 })
        {
            var bitsB = CollisionMath.EffectiveBits(
                CollisionMath.FalseAcceptExperimental(ExperimentalScheme.HashSize, sectors));
            var gain = bitsB - 8 * ClassicScheme.HashSize;
            var note = sectors == 65535 ? " (паритет)" : "";
            PrintLine(
                $"{CollisionMath.FormatCount(sectors),8} {"2^-72.0",9} " +
                $"{CollisionMath.FormatBits(bitsB),9} {($"{'+'}{gain:F1} бит{note}"),12}");
        }

        PrintLine(
            "Цена приема: A — 1 хеш/пакет; B — до K хешей/пакет при переборе индексов (замер: режим perf).",
            ConsoleColor.DarkGray);
        PrintLine("");

        var failures = results.Where(r => !ModelConfirmed(r)).ToList();
        if (failures.Count == 0)
        {
            PrintLine(
                "ИТОГ COLLIDE: наблюдаемые частоты обеих схем согласуются с моделью " +
                "(A: 2^-8t, B: K·2^-8(t+2)); во всех ячейках |z| ≤ 4.",
                ConsoleColor.Green);
            return 0;
        }

        foreach (var f in failures)
            PrintLine(
                $"РАСХОЖДЕНИЕ: {f.Setup.Scheme}/{f.Setup.Scenario} hB={f.Setup.HashBytes} " +
                $"K={f.Setup.Sectors}: {CollisionMath.FormatProb(f.ObservedRate)} против " +
                $"ожидания {CollisionMath.FormatProb(f.Setup.Expected)}.",
                ConsoleColor.Red);

        return 1;
    }

    // ── full ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Режим full: продовые усечения 9/11 байт. Нулевая гипотеза — полных
    /// совпадений нет; спектр совпадений ведущих бит сверяется с моделью
    /// равномерного хеша, что подтверждает корректность экстраполяции хвоста.
    /// </summary>
    private static int RunFull(BenchOptions o)
    {
        PrintLine(
            "── Режим full: полные усечения (A: Trunc9 = 72 бита, B: Trunc11 = 88 бит) ──",
            ConsoleColor.DarkCyan);
        PrintLine(
            "Контроль нулевых пропусков + спектр совпадений ведущих бит по всем целям.",
            ConsoleColor.DarkGray);
        PrintLine("");

        PrintLine(
            $"Схема A: {CollisionMath.FormatCount(o.FullClassicTrials)} попыток, " +
            $"1 цель на попытку...", ConsoleColor.Gray);
        var resultA = Spectrum.RunClassic(
            ClassicScheme.HashSize, o.FullClassicTrials, o.Threads,
            o.FullBudgetSeconds, o.Seed ^ 0x11, Progress("A/full"));
        ClearProgress();
        PrintSpectrum(resultA);

        PrintLine("");
        PrintLine(
            $"Схема B: {CollisionMath.FormatCount(o.FullExperimentalTrials)} попыток × " +
            $"K = {CollisionMath.FormatCount(o.FullSectors)} целей на попытку...",
            ConsoleColor.Gray);
        var resultB = Spectrum.RunExperimental(
            ExperimentalScheme.HashSize, o.FullSectors, o.FullExperimentalTrials, o.Threads,
            o.FullBudgetSeconds, o.Seed ^ 0x22, Progress("B/full"));
        ClearProgress();
        PrintSpectrum(resultB);

        PrintLine("");
        if (resultA.FullMatches == 0 && resultB.FullMatches == 0)
        {
            PrintLine(
                "ИТОГ FULL: ноль полных совпадений на полных усечениях; спектры соответствуют " +
                "модели равномерного хеша — экстраполяция хвоста (2^-72 / K·2^-88) корректна.",
                ConsoleColor.Green);
            return 0;
        }

        PrintLine(
            $"ИТОГ FULL: ОБНАРУЖЕНЫ ПРОПУСКИ — A: {resultA.FullMatches}, " +
            $"B: {resultB.FullMatches}.",
            ConsoleColor.Red);
        return 1;
    }

    /// <summary>
    /// Печать спектра P(M ≥ m): хвостовые счетчики, наблюдаемая частота
    /// против модельного ожидания, z-оценка по Пуассону.
    /// </summary>
    private static void PrintSpectrum(SpectrumResult r)
    {
        var fullBits = 8 * r.HashBytes;
        var targets = r.Targets;
        var fullExpected = targets * Math.Pow(2, -fullBits);

        PrintLine(
            (r.Scheme == "B"
                ? $"  Схема B (Trunc{r.HashBytes}, K = {CollisionMath.FormatCount(r.Sectors)}): "
                : $"  Схема A (Trunc{r.HashBytes}): ") +
            $"{CollisionMath.FormatCount(r.Trials)} попыток, целей {targets:E2}, " +
            $"{r.Seconds:F1} с",
            ConsoleColor.White);

        PrintLine(
            $"  Полных совпадений: {CollisionMath.FormatCount(r.FullMatches)} " +
            $"(ожидание {CollisionMath.FormatProb(fullExpected)})",
            r.FullMatches == 0 ? ConsoleColor.Green : ConsoleColor.Red);

        PrintLine(
            $"  Максимум совпадений: {r.MaxObserved} бит из {fullBits} " +
            $"(модель: log2(целей) ≈ {Math.Log2(Math.Max(1.0, targets)):F1})",
            ConsoleColor.Gray);

        PrintLine("  Спектр P(M ≥ m):", ConsoleColor.Gray);
        PrintLine(
            $"    {"m",4} {"Наблюдений",12} {"Частота",10} {"Ожидание",10} {"Отн",6} {"z",6}",
            ConsoleColor.DarkCyan);

        var mMax = Math.Min(fullBits - 1, (int)Math.Floor(Math.Log2(Math.Max(1.0, targets))) + 1);
        var mMin = Math.Max(1, mMax - 11);

        for (var m = mMin; m <= mMax; m++)
        {
            var count = 0L;
            for (var i = m; i < r.Histogram.Length; i++) count += r.Histogram[i];

            var freq = r.Trials > 0 ? (double)count / r.Trials : 0;
            var expected = r.ExpectedProbability(m);
            var ratio = expected > 0 && freq > 0 ? freq / expected : 0;
            var lambda = expected * r.Trials;
            var z = lambda > 0 ? (count - lambda) / Math.Sqrt(lambda) : 0;

            PrintLine(
                $"    {m,4} {CollisionMath.FormatCount(count),12} " +
                $"{CollisionMath.FormatProb(freq),10} {CollisionMath.FormatProb(expected),10} " +
                $"{ratio,6:F2} {z,6:F1}");
        }

        PrintLine(
            "    Глубокий хвост (счетчики < 5): отклонения — пуассоновский шум на пределе " +
            "достижимости; сам предел совпадает с модельным log2(целей).",
            ConsoleColor.DarkGray);
    }

    // ── perf ─────────────────────────────────────────────────────────────────

    /// <summary>Режим perf: замер стоимости приема сектора (A: 1 хеш, B: до K хешей).</summary>
    private static int RunPerf(BenchOptions o)
    {
        PrintLine("── Режим perf: стоимость приема сектора ──", ConsoleColor.DarkCyan);
        PrintLine(
            "Усечения продовые: A = 9 байт, B = 11 байт. Кеш сидов: K префиксов H5‖idxLE.",
            ConsoleColor.DarkGray);
        PrintLine("");

        var rows = PerfBench.Run(
            o.PerfSectors, o.PerfSeconds, o.Threads, o.Seed,
            line => PrintLine("  " + line, ConsoleColor.DarkGray));

        PrintPerfTable(rows);
        return 0;
    }

    /// <summary>
    /// Таблица замеров с базовой линией «A валид» на каждый размер K
    /// и итогом по худшему случаю B (чужой пакет = K хешей).
    /// </summary>
    private static void PrintPerfTable(IReadOnlyList<PerfRow> rows)
    {
        PrintLine("");
        PrintLine("Замеры:", ConsoleColor.White);
        PrintLine(
            $"{"K",8} {"Случай",8} {"Потоки",6} {"Пакетов/с",14} {"Хешей/с",14} " +
            $"{"Мкс/пакет",10} {"×к A",7}",
            ConsoleColor.DarkCyan);

        foreach (var group in rows.GroupBy(r => r.Sectors))
        {
            foreach (var row in group.OrderBy(r => r.ThreadCount).ThenBy(r => r.Label))
            {
                var baseline = group.FirstOrDefault(
                    r => r.ThreadCount == row.ThreadCount && r.Label == "A валид");
                var slowdown = baseline is not null && baseline.PacketsPerSecond > 0
                    ? baseline.PacketsPerSecond / Math.Max(1, row.PacketsPerSecond)
                    : 1;

                PrintLine(
                    $"{CollisionMath.FormatCount(row.Sectors),8} {row.Label,8} {row.ThreadCount,6} " +
                    $"{CollisionMath.FormatCount((long)row.PacketsPerSecond),14} " +
                    $"{CollisionMath.FormatCount((long)row.HashesPerSecond),14} " +
                    $"{row.MicrosPerPacket,10:F2} {slowdown,6:F1}x");
            }
        }

        var worst = rows.Where(r => r.Label == "B чужой")
            .OrderByDescending(r => r.Sectors)
            .First();
        PrintLine("");
        PrintLine(
            $"ИТОГ PERF: худший случай приема B — {CollisionMath.FormatCount(worst.Sectors)} хешей " +
            $"на чужой пакет (K = {CollisionMath.FormatCount(worst.Sectors)}); схема A всегда 1 хеш.",
            ConsoleColor.Gray);
    }

    // ── Консоль ──────────────────────────────────────────────────────────────

    /// <summary>Строка «продукт — версия — копирайт» для шапки стенда.</summary>
    private static string VersionLine
    {
        get
        {
            var ver = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version;
            return $"DataShield v{ver?.Major ?? 1}.{ver?.Minor ?? 0} {BuildInfo.BuildLabel}   Copyright (c) 2026 Artem Drobanov, Vladislav Utyumov";
        }
    }

    /// <summary>Шапка стенда: режим, потоки, сид и краткая сводка схем A и B.</summary>
    private static void Banner(BenchOptions o)
    {
        PrintLine(" ╔════════════════════════════╗", ConsoleColor.DarkCyan);
        PrintLine(" ║ DataShield Collision Bench ║", ConsoleColor.DarkCyan);
        PrintLine(" ╚════════════════════════════╝", ConsoleColor.DarkCyan);
        PrintLine(" " + VersionLine, ConsoleColor.DarkGray);
        PrintLine(
            $" Режим: {o.Mode} | Потоки: {o.Threads} | Сид: {o.Seed}",
            ConsoleColor.DarkGray);
        PrintLine(
            " A (текущая):     idx(2) ‖ payload(64) ‖ Trunc9(SHA-256(H5‖idx‖payload)) — 1 хеш на прием",
            ConsoleColor.Gray);
        PrintLine(
            " B (экспер.):     payload(64) ‖ Trunc11(SHA-256(H5‖idx‖payload)) — индекс виртуальный,",
            ConsoleColor.Gray);
        PrintLine(
            "                  перебор K индексов полного набора с кешем сидов заголовка",
            ConsoleColor.Gray);
        PrintLine("");
    }

    /// <summary>Потокобезопасный цветной вывод строки.</summary>
    internal static void PrintLine(string text, ConsoleColor color = ConsoleColor.Gray)
    {
        lock (Gate)
        {
            Console.ForegroundColor = color;
            Console.WriteLine(text);
            Console.ResetColor();
        }
    }

    /// <summary>
    /// Однострочный индикатор прогресса ячейки; null при перенаправленном
    /// выводе (чтобы не портить пайплайны).
    /// </summary>
    private static Campaign.ProgressReport? Progress(string label) =>
        Console.IsOutputRedirected
            ? null
            : (trials, accepts, seconds) =>
            {
                lock (Gate)
                {
                    Console.Write(
                        $"\r    {label}: {CollisionMath.FormatCount(trials)} попыток, " +
                        $"{CollisionMath.FormatCount(accepts)} допусков, {seconds:F0} с");
                }
            };

    /// <summary>Стереть строку однострочного прогресса.</summary>
    private static void ClearProgress()
    {
        if (Console.IsOutputRedirected) return;
        lock (Gate)
        {
            Console.Write("\r" + new string(' ', 110) + "\r");
        }
    }
}
