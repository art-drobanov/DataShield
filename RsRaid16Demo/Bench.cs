using System.Diagnostics;

// ─────────────────────────────────────────────────────────────────────────────
//  Детерминированный бенчмарк RS-декодера (режим «bench»)
//
//  Калибровка стадии подбора подмножества томов: сколько стоит одна попытка
//  RS-восстановления при заданном числе используемых ECC-томов E
//  (E = число стёртых data-томов, определяет размер обращаемой матрицы).
//
//  Стоимость попытки = Init (построение + гауссова инверсия, ~O(E²·N))
//  + 32 × Process (восстановление 32 символов, O(E·N) на символ)
//  + SHA-256 буфера N·64 (финальная проверка сборки).
// ─────────────────────────────────────────────────────────────────────────────

internal static class Bench
{
    // Фиксированные размеры data-линейки N: практические файлы 1 КБ .. 256 КБ
    // (payload тома = 64 байта).
    private static readonly int[] FixedDataCounts = { 16, 64, 256, 1024, 4096 };

    // Доли избыточности ECC, %.
    private static readonly int[] EccPercents = { 5, 10, 25, 50 };

    // Сетка числа стёртых data-томов E (по смыслу — сколько ECC-томов
    // расходуется на восстановление).
    private static readonly int[] ErasedDataCounts = { 1, 2, 4, 8, 16, 32, 64, 128, 256 };

    // Предел поля GF(2^16): N + M <= 65535.
    private const int GfLimit = 65535;

    // Payload тома 64 байта = 32 GF-символа.
    private const int SymbolsPerVolume = 32;
    private const int PayloadSize = 64;

    // Бюджет стадии подбора подмножеств, на который нормируется «попыток».
    private const int BudgetMilliseconds = 30_000;

    // Методика замера: минимум 200 мс или 5 повторов; вызов длиннее 200 мс
    // не перемеряется (первый замер = результат).
    private const double MinMeasureMs = 200.0;
    private const int MaxReps = 5;

    // Итог одной ячейки сетки.
    private sealed class Cell
    {
        public int DataCount { get; set; }
        public int EccCount { get; set; }
        public int ErasedCount { get; set; }
        public double InitMs { get; set; }
        public double ProcessMs { get; set; }
        public double ShaMs { get; set; }
        public double TotalMs => InitMs + ProcessMs + ShaMs;
    }

    /// <summary>Запустить сеточный бенчмарк: (N × ECC% × E) → стоимость попытки.</summary>
    /// <param name="csv">true — машиночитаемый CSV вместо таблицы.</param>
    public static void Run(bool csv)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        if (csv)
        {
            Console.WriteLine("N;M;E;InitMs;Process32Ms;ShaMs;AttemptMs;AttemptsPer30s");
        }
        else
        {
            Console.WriteLine(" RS DECODER CALIBRATION GRID (RsRaid16, GF(2^16))");
            Console.WriteLine(" Attempt = Init (matrix build + Gauss inversion) + 32 x Process + SHA-256(N*64)");
            Console.WriteLine();
            Console.WriteLine(
                " {0,6} | {1,5} | {2,4} | {3,9} | {4,9} | {5,9} | {6,10} | {7,9} ",
                "N", "M", "E", "Init,ms", "Proc32,ms", "SHA,ms", "Att.,ms", "Att/30s");
        }

        var results = new List<Cell>();

        foreach (var (dataCount, eccCount) in BuildGrid())
            foreach (var erased in ErasedDataCounts)
            {
                if (erased > eccCount || erased > dataCount)
                    continue;

                var cell = MeasureCell(dataCount, eccCount, erased);
                results.Add(cell);
                PrintCell(cell, csv);
            }

        if (!csv)
            PrintSummary(results);
    }

    // ── Геометрия сетки ─────────────────────────────────────────────────────

    /// <summary>
    /// Фиксированные N для всех долей ECC + серия максимальных файлов
    /// (N + M у предела поля) для каждой доли.
    /// </summary>
    private static IEnumerable<(int DataCount, int EccCount)> BuildGrid()
    {
        foreach (var dataCount in FixedDataCounts)
            foreach (var percent in EccPercents)
                yield return (dataCount, ComputeEccCount(dataCount, percent));

        foreach (var percent in EccPercents)
        {
            // Наибольшее N, при котором N + M укладывается в предел поля.
            var dataCount = GfLimit * 100 / (100 + percent);

            while (dataCount > 1 &&
                   dataCount + ComputeEccCount(dataCount, percent) > GfLimit)
                dataCount--;

            yield return (dataCount, ComputeEccCount(dataCount, percent));
        }
    }

    /// <summary>M = max(1, ⌈N · percent / 100⌉) — как в FileEncoder.</summary>
    private static int ComputeEccCount(int dataCount, int percent) =>
        Math.Max(1, (int)(((long)dataCount * percent + 99) / 100));

    // ── Замер одной ячейки ──────────────────────────────────────────────────

    private static Cell MeasureCell(int dataCount, int eccCount, int erased)
    {
        var totalCount = dataCount + eccCount;

        // Карта стираний: первые E data-томов стёрты, все ECC на месте.
        // Положение стёртых не влияет на стоимость (структура Коши).
        var map = new bool[totalCount];
        for (var i = 0; i < totalCount; i++)
            map[i] = i >= erased;

        GC.Collect();
        GC.WaitForPendingFinalizers();

        // 1) Init: построение матрицы декодирования и гауссова инверсия.
        var initMs = MeasureMs(() =>
        {
            var rs = new RsRaid16();
            if (!rs.Init(dataCount, eccCount, map))
                throw new InvalidOperationException(
                    $"RsRaid16 init failed: N={dataCount}, M={eccCount}, E={erased}.");
        });

        // 2) 32 × Process: восстановление всех символов томов. Копия столбца
        // внутри замера имитирует загрузку символов адаптером.
        var rsDec = new RsRaid16();
        if (!rsDec.Init(dataCount, eccCount, map))
            throw new InvalidOperationException("RsRaid16 init failed.");

        var rng = new Mt19937(20260901u);
        var saved = new int[totalCount];
        for (var i = 0; i < totalCount; i++)
            saved[i] = (int)(rng.Genrand() & 0xFFFF);

        var work = new int[totalCount];
        var processMs = MeasureMs(() =>
        {
            for (var s = 0; s < SymbolsPerVolume; s++)
            {
                Array.Copy(saved, work, totalCount);
                rsDec.Process(work);
            }
        });

        // 3) SHA-256 буфера N·64 — финальная проверка одной попытки сборки.
        var buffer = new byte[(long)dataCount * PayloadSize];
        for (var i = 0; i < buffer.Length; i++)
            buffer[i] = (byte)(rng.Genrand() & 0xFF);
        var hash = new byte[32];

        var shaMs = MeasureMs(() => Sha256Compact.HashData(buffer, hash));

        return new Cell
        {
            DataCount = dataCount,
            EccCount = eccCount,
            ErasedCount = erased,
            InitMs = initMs,
            ProcessMs = processMs,
            ShaMs = shaMs
        };
    }

    /// <summary>
    /// Средняя длительность вызова: первый вызов длиннее 200 мс засчитывается
    /// как есть; иначе первый — прогрев, затем повторы до 200 мс суммарно
    /// (не более 5).
    /// </summary>
    private static double MeasureMs(Action action)
    {
        var watch = Stopwatch.StartNew();
        action();
        var first = watch.Elapsed.TotalMilliseconds;

        if (first >= MinMeasureMs)
            return first;

        watch.Restart();
        var reps = 0;

        while (reps < MaxReps)
        {
            action();
            reps++;

            if (watch.Elapsed.TotalMilliseconds >= MinMeasureMs)
                break;
        }

        return watch.Elapsed.TotalMilliseconds / reps;
    }

    // ── Вывод ───────────────────────────────────────────────────────────────

    private static void PrintCell(Cell cell, bool csv)
    {
        var attempts = BudgetMilliseconds / Math.Max(cell.TotalMs, 0.0001);

        if (csv)
        {
            Console.WriteLine(
                "{0};{1};{2};{3:F3};{4:F3};{5:F3};{6:F3};{7:F0}",
                cell.DataCount, cell.EccCount, cell.ErasedCount,
                cell.InitMs, cell.ProcessMs, cell.ShaMs, cell.TotalMs, attempts);
            return;
        }

        Console.WriteLine(
            " {0,6} | {1,5} | {2,4} | {3,9:F2} | {4,9:F2} | {5,9:F2} | {6,10:F2} | {7,9:F0} ",
            cell.DataCount, cell.EccCount, cell.ErasedCount,
            cell.InitMs, cell.ProcessMs, cell.ShaMs, cell.TotalMs, attempts);
    }

    /// <summary>
    /// Калибровочный итог: для каждой пары (N, M) — наибольшее E, при котором
    /// одна попытка укладывается в 1 с и в 100 мс (30 и 300 попыток за бюджет
    /// стадии соответственно).
    /// </summary>
    private static void PrintSummary(IReadOnlyList<Cell> results)
    {
        Console.WriteLine();
        Console.WriteLine(" CALIBRATION SUMMARY (max affordable E per attempt budget)");
        Console.WriteLine(
            " {0,6} | {1,5} | {2,9} | {3,10} | {4,9} ",
            "N", "M", "E @<=1s", "E @<=100ms", "M used");

        foreach (var group in results.GroupBy(c => (c.DataCount, c.EccCount)))
        {
            var affordable1s = group
                .Where(c => c.TotalMs <= 1000.0)
                .Select(c => c.ErasedCount)
                .DefaultIfEmpty(0)
                .Max();
            var affordable100ms = group
                .Where(c => c.TotalMs <= 100.0)
                .Select(c => c.ErasedCount)
                .DefaultIfEmpty(0)
                .Max();

            Console.WriteLine(
                " {0,6} | {1,5} | {2,9} | {3,10} | {4,9} ",
                group.Key.DataCount, group.Key.EccCount,
                affordable1s, affordable100ms,
                group.Key.EccCount);
        }
    }
}
