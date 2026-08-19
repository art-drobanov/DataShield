using DataShield.Codec.Packets;

namespace DataShield.CollisionBench;

// ─────────────────────────────────────────────────────────────────────────────
//  Аргументы командной строки стенда
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Режимы: selftest (корректность), collide (кампания коллизий при усечении t),
/// full (полные усечения + спектр), perf (стоимость приема), all.
/// </summary>
internal sealed class BenchOptions
{
    /// <summary>Допустимые имена режимов.</summary>
    public static readonly string[] Modes = ["selftest", "collide", "full", "perf", "all"];

    // ── Общие параметры ──────────────────────────────────────────────────────

    /// <summary>Активный режим запуска.</summary>
    public string Mode { get; private set; } = "collide";

    /// <summary>Список усечений t схемы A (схема B использует t+2).</summary>
    public int[] Truncations { get; private set; } = [1];

    /// <summary>Размеры полного набора секторов K для схемы B в режиме collide.</summary>
    public int[] Sectors { get; private set; } = [64, 128, 256, 512];

    /// <summary>Целевое число событий на ячейку collide (ранняя остановка).</summary>
    public int TargetEvents { get; private set; } = 200;

    /// <summary>Бюджет времени на ячейку collide, с.</summary>
    public double BudgetSeconds { get; private set; } = 30;

    /// <summary>Число рабочих потоков.</summary>
    public int Threads { get; private set; } = Environment.ProcessorCount;

    /// <summary>Базовый сид всех экспериментов (воспроизводимость).</summary>
    public long Seed { get; private set; } = 20260906;

    // ── Режим perf ───────────────────────────────────────────────────────────

    /// <summary>Значения K для замера стоимости приема.</summary>
    public int[] PerfSectors { get; private set; } = [100, 1000, 10000, 65535];

    /// <summary>Длительность одного замера perf, с.</summary>
    public double PerfSeconds { get; private set; } = 2.5;

    // ── Режим full ───────────────────────────────────────────────────────────

    /// <summary>Число попыток схемы A (1 цель на попытку).</summary>
    public long FullClassicTrials { get; private set; } = 200_000_000;

    /// <summary>Число попыток схемы B (K целей на попытку).</summary>
    public long FullExperimentalTrials { get; private set; } = 2_000_000;

    /// <summary>Число целей K на попытку схемы B.</summary>
    public int FullSectors { get; private set; } = 1024;

    /// <summary>Суммарный бюджет времени режима full, с.</summary>
    public double FullBudgetSeconds { get; private set; } = 240;

    /// <summary>
    /// Разбор командной строки: первый аргумент без дефиса — режим,
    /// далее именованные ключи. Бросает ArgumentException при ошибке.
    /// </summary>
    public static BenchOptions Parse(string[] args)
    {
        var options = new BenchOptions();
        var i = 0;

        if (args.Length > 0 && !args[0].StartsWith('-'))
        {
            var mode = args[0].ToLowerInvariant();
            if (!Modes.Contains(mode))
                throw new ArgumentException(
                    $"Неизвестный режим: {args[0]}. Доступны: {string.Join(", ", Modes)}.");
            options.Mode = mode;
            i = 1;
        }

        for (; i < args.Length; i++)
        {
            string Take() =>
                i + 1 < args.Length
                    ? args[++i]
                    : throw new ArgumentException($"Нет значения для ключа {args[i]}.");

            switch (args[i].ToLowerInvariant())
            {
                case "-t":
                    options.Truncations = ParseList(Take(), 1, 7);
                    break;
                case "-k":
                    options.Sectors = ParseList(Take(), 1, PacketFormat.MaxDataVolumes);
                    break;
                case "-events":
                    options.TargetEvents = ParseInt(Take(), 1, int.MaxValue);
                    break;
                case "-budget":
                    options.BudgetSeconds = ParseDouble(Take(), 1, 3600);
                    break;
                case "-threads":
                    options.Threads = ParseInt(Take(), 1, 128);
                    break;
                case "-seed":
                    options.Seed = ParseLong(Take(), 1, long.MaxValue);
                    break;
                case "-perf-k":
                    options.PerfSectors = ParseList(Take(), 1, PacketFormat.MaxDataVolumes);
                    break;
                case "-perf-sec":
                    options.PerfSeconds = ParseDouble(Take(), 0.2, 600);
                    break;
                case "-fa":
                    options.FullClassicTrials = ParseLong(Take(), 1, long.MaxValue);
                    break;
                case "-fb":
                    options.FullExperimentalTrials = ParseLong(Take(), 1, long.MaxValue);
                    break;
                case "-full-k":
                    options.FullSectors = ParseInt(Take(), 1, PacketFormat.MaxDataVolumes);
                    break;
                case "-full-budget":
                    options.FullBudgetSeconds = ParseDouble(Take(), 1, 7200);
                    break;
                case "-h":
                case "-help":
                case "--help":
                    PrintHelp();
                    Environment.Exit(0);
                    break;
                default:
                    throw new ArgumentException($"Неизвестный ключ: {args[i]}.");
            }
        }

        return options;
    }

    /// <summary>Печать справки по использованию стенда.</summary>
    public static void PrintHelp()
    {
        Console.WriteLine(
            """
            Использование: DataShield.CollisionBench [режим] [ключи]

            Режимы:
              selftest  корректность реализаций (схемы, кеш сидов, сценарии)
              collide   кампания коллизий при параметризованном усечении t
              full      полные усечения 9/11 байт: контроль нуля + спектр совпадений
              perf      стоимость приема: 1 хеш (A) против перебора K (B)
              all       все режимы по очереди (по умолчанию t = 1)

            Ключи:
              -t список        усечение схемы A в байтах: A = t, B = t+2 (1..7), по умолчанию 1.
                               Стоимость одного события B ≈ 2^(8·(t+2)) хешей — t>1 для B
                               требует экспоненциально большего времени (глубокий хвост
                               дополнительно проверяет режим full)
              -k список        размеры полного набора K для схемы B (64,128,256,512)
              -events N        целевое число событий на ячейку collide (200)
              -budget сек      бюджет времени на ячейку collide (30)
              -threads N       число потоков (все ядра)
              -seed N          сид ГПСЧ для воспроизводимости
              -perf-k список   значения K в режиме perf (100,1000,10000,65535)
              -perf-sec сек    длительность замера одной ячейки perf (2.5)
              -fa N            попыток схемы A в режиме full (200000000)
              -fb N            попыток схемы B в режиме full (2000000)
              -full-k N        K в режиме full (1024)
              -full-budget сек бюджет времени режима full (240)

            Списки задаются через запятую: -k 64,1024.
            """);
    }

    /// <summary>Разбор списка вида «64,1024» в массив int с проверкой диапазона.</summary>
    private static int[] ParseList(string text, int min, int max)
    {
        var parts = text.Split(',', StringSplitOptions.RemoveEmptyEntries |
            StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
            throw new ArgumentException($"Пустой список: {text}.");

        var values = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
            values[i] = ParseInt(parts[i], min, max);
        return values;
    }

    /// <summary>int.TryParse с проверкой границ.</summary>
    private static int ParseInt(string text, int min, int max)
    {
        if (!int.TryParse(text, out var value) || value < min || value > max)
            throw new ArgumentException($"Ожидалось целое {min}..{max}: {text}.");
        return value;
    }

    /// <summary>long.TryParse с проверкой границ.</summary>
    private static long ParseLong(string text, long min, long max)
    {
        if (!long.TryParse(text, out var value) || value < min || value > max)
            throw new ArgumentException($"Ожидалось целое {min}..{max}: {text}.");
        return value;
    }

    /// <summary>double.TryParse в инвариантной культуре с проверкой границ.</summary>
    private static double ParseDouble(string text, double min, double max)
    {
        if (!double.TryParse(text, System.Globalization.CultureInfo.InvariantCulture,
                out var value) || value < min || value > max)
            throw new ArgumentException($"Ожидалось число {min}..{max}: {text}.");
        return value;
    }
}
