using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using DataShield.Codec;
using DataShield.Codec.Packets;
using DataShield.Console;
using DataShield.Interfaces;
using DataShield.TestsHarness;

// ─────────────────────────────────────────────────────────────────────────────
//  Консольный стенд непрерывного взаимодействия с пользователем
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Консольный стенд непрерывного взаимодействия с пользователем: в бесконечном
/// цикле запускает реальные консольные приложения DataShield.Console.EN и
/// DataShield.Console.RU как дочерние процессы и проверяет пользовательский
/// контракт: коды возврата, шапку с рамкой и тексты вывода, справку без
/// аргументов, локализованные сообщения об ошибках, побитовые roundtrip'ы
/// encode → decode и декодирование повреждённых потоков (одиночные и
/// многофайловые, обе схемы секторов, случайные проценты ECC/заголовков,
/// обе локали, длинные и короткие опции).
///
/// PASS — ожидание выполнено (roundtrip побитово точен, справка/шапка на месте),
/// WARN — корректный отказ по замыслу (ошибки использования, поток без
/// заголовков), FAIL — сломанное ожидание (останавливает стенд). Таблица
/// последних итераций и средние времена процессов рисуются на месте
/// (при перенапределённом выводе — потоком). Сид ПСЧ ротируется по интервалу
/// либо по числу итераций (см. <see cref="SeedRotator"/>).
/// </summary>
internal static class Program
{
    // Диапазон размера входного файла: 1 байт .. 256 Кб (лог-равномерно).
    private const int MinFileSize = 1;
    private const int MaxSizeLog2 = 18;
    private const int MaxFileSize = 1 << MaxSizeLog2;

    // Режимы итерации, %.
    private const int HelpChancePercent = 8;    // справка: без аргументов + help
    private const int MultiChancePercent = 12;  // многофайловый поток
    private const int DamageChancePercent = 20; // повреждённый поток через CLI
    private const int ErrChancePercent = 15;    // ошибки использования (ожидаемый отказ)
    private const int NoHeadersChancePercent = 10; // поток без заголовков (ожидаемый отказ)
    private const int EmptyChancePercent = 3;   // пустой файл внутри roundtrip
    private const int VirtualIndexChancePercent = 12; // схема virtual (с откатом на classic)
    private const int QuietChancePercent = 25;  // флаг --quiet

    // Строк таблицы в кольцевом буфере.
    private const int HistorySize = 20;

    // Таймаут одного дочернего процесса, мс (с запасом на RS-инициализацию
    // и медленный приём схемы virtual: перебор индексов на каждом окне).
    private const int ChildTimeoutMs = 1_800_000;

    // Схема virtual применима к компактным файлам: приём brute-force
    // (до 512 индексов × каждое окно потока) квадратично дорожает от объёма.
    private const int VirtualMaxSize = 16 * 1024;

    // Параметры повреждённых сценариев (dmg): всегда одиночный файл (один
    // CLI decode; многофайловые потоки покрывает mlt), избыточность ECC до
    // 100% — RS-кодирование in-process остаётся быстрым; остальное —
    // распределения кодек-стенда.
    private static readonly ScenarioOptions DamageOptions =
        new(MaxEccPercent: 100, MultiChancePercent: 0);

    /// <summary>Шапка таблицы результатов (общая для отрисовки и вычисления ширины).</summary>
    private static readonly string HeaderLine = string.Format(
        " {0,5} | {1,8} | {2,3} | {3,3} | {4,3} | {5,4} | {6,3} | {7,13} | {8,7} | {9,7} | {10,4} ",
        "N", "Size", "Lng", "Scn", "Fmt", "Ecc", "Hdr", "Via", "EncMs", "DecMs", "Stat");

    // Формат строки таблицы (общий для отрисовки и журнала).
    private const string RowFormat =
        " {0,5} | {1,8} | {2,3} | {3,3} | {4,3} | {5,4} | {6,3} | {7,13} | {8,7:F0} | {9,7:F0} | {10,4} ";

    // Реальная ширина таблицы: длина отформатированной строки-образца.
    private static readonly int TableWidth = string.Format(
        RowFormat, 0, 0, "", "", "", 0, 0, "", 0.0, 0.0, "").Length;

    // Последняя применённая ширина консоли (кэш, чтобы не дёргать
    // resize на каждом кадре; растёт по фактической длине строк).
    private static int _lastEnsuredWidth;

    // Последняя применённая высота окна консоли (кэш; уточняется после
    // отрисовки шапки — по фактической высоте раскладки экрана).
    private static int _lastEnsuredHeight = 40;

    // ── Пути к консолям и рабочему каталогу ──────────────────────────────────

    // Корень решения (ищется вверх от каталога сборки по DataShield.slnx).
    private static readonly string RepoRoot = FindRepoRoot();

    // DLL локализованных консолей (Debug приоритетнее, затем Release).
    private static readonly string EnDll =
        FindConsoleDll("DataShield.Console.EN");
    private static readonly string RuDll =
        FindConsoleDll("DataShield.Console.RU");

    // Рабочий каталог итераций (очищается на каждой итерации); включает PID
    // процесса, чтобы параллельные экземпляры стенда не конфликтовали
    // на общих файлах артефактов (m-000001.DataShield.*).
    private static readonly string WorkDir = Path.Combine(
        Path.GetTempPath(), "DataShield-logs", "DataShield.Console.Demo", $"p{Environment.ProcessId}");

    /// <summary>Итог одной итерации стенда.</summary>
    private sealed class TestResult
    {
        public int N { get; set; }               // номер итерации
        public int SizeBytes { get; set; }       // размер входа (сумма для multi)
        public string Lang { get; set; } = "";   // локаль дочерних процессов: E / R / ER
        public string Scenario { get; set; } = ""; // rt / mlt / dmg / hlp / err / nhc
        public string Format { get; set; } = ""; // txt / bin / -
        public int EccPercent { get; set; }      // избыточность, % (средняя для multi)
        public int HeaderPercent { get; set; }   // избыточность заголовков, %
        public string Via { get; set; } = "";    // набор опций вызовов
        public double EncMs { get; set; }        // суммарное время encode-процессов
        public double DecMs { get; set; }        // время decode-процесса
        public bool Passed { get; set; }         // ожидание итерации выполнено
        public bool Stress { get; set; }         // режим ожидаемого отказа
        public int TotalSuccess { get; set; }    // накопленный успех (PASS+WARN)
        public int TotalFail { get; set; }       // накопленные неудачи

        /// <summary>Строка статуса: FAIL / WARN (ожидаемый отказ) / PASS.</summary>
        public string Status => !Passed ? "FAIL" : Stress ? "WARN" : "PASS";

        // Краткая причина неудачи (какое ожидание нарушено)
        public string? FailureHint { get; set; }
    }

    // ── Результат одного дочернего процесса ──────────────────────────────────

    /// <summary>Код возврата, захваченный вывод и время дочернего процесса.</summary>
    private sealed record ChildResult(int Code, string StdOut, string StdErr, double Ms);

    // ------------- Ротация сида ПСЧ -------------

    /// <summary>
    /// Ротация сида ПСЧ стенда: сид меняется по плановому интервалу
    /// (по умолчанию — раз в 20 минут) либо после заданного числа итераций.
    /// Новый сид выводится из предыдущего и номера ротации (SHA-256): вся
    /// последовательность ротаций воспроизводима от начального сида.
    /// </summary>
    private sealed class SeedRotator
    {
        private readonly TimeSpan _interval;
        private readonly int _iterations;

        private DateTimeOffset _lastRotate = DateTimeOffset.UtcNow;
        private int _sinceRotate;

        /// <summary>Создать ротатор с начальным сидом.</summary>
        /// <param name="initialSeed">Начальный сид ПСЧ.</param>
        /// <param name="interval">Плановый интервал ротации.</param>
        /// <param name="iterations">Порог итераций ротации.</param>
        public SeedRotator(uint initialSeed, TimeSpan? interval = null, int iterations = 200)
        {
            Seed = initialSeed;
            _interval = interval ?? TimeSpan.FromMinutes(20);
            _iterations = iterations;
        }

        /// <summary>Текущий сид ПСЧ.</summary>
        public uint Seed { get; private set; }

        /// <summary>Число выполненных ротаций.</summary>
        public int Rotations { get; private set; }

        /// <summary>Причина последней ротации.</summary>
        public string LastReason { get; private set; } = "";

        /// <summary>
        /// Зафиксировать состоявшуюся итерацию; при исчерпании интервала либо
        /// порога итераций сменить сид. Возвращает true при ротации.
        /// </summary>
        public bool Note(out uint newSeed, out string reason)
        {
            newSeed = 0;

            reason = ++_sinceRotate >= _iterations ? "iterations threshold"
                : DateTimeOffset.UtcNow - _lastRotate >= _interval ? "planned interval"
                : "";

            if (reason.Length == 0)
                return false;

            newSeed = NextSeed();
            Seed = newSeed;
            LastReason = reason;
            Rotations++;
            _lastRotate = DateTimeOffset.UtcNow;
            _sinceRotate = 0;
            return true;
        }

        // Цепочка сидов: новый сид = Trunc32(SHA-256(предыдущий ‖ №ротации ‖
        // домен "DSCD")) — последовательность ротаций воспроизводима от
        // начального сида и не коррелирует с предыдущим сидом.
        private uint NextSeed()
        {
            var raw = new byte[12];
            BitConverter.GetBytes(Seed).CopyTo(raw, 0);
            BitConverter.GetBytes(Rotations + 1).CopyTo(raw, 4);
            BitConverter.GetBytes(0x4453_4344).CopyTo(raw, 8); // "DSCD"
            return BitConverter.ToUInt32(SHA256.HashData(raw), 0);
        }
    }

    // ------------- Журнал стенда -------------

    /// <summary>
    /// Журнал стенда: интегральный сброс статистики раз в 10 минут, события
    /// ротации сида и отказы. Запись защищена от любых сбоев (нет файла, нет
    /// диска, занято): не записали — и ладно, стенд продолжает работу.
    /// </summary>
    private static class StandLog
    {
        private static readonly string LogPath = Path.Combine(
            Path.GetTempPath(), "DataShield-logs", "DataShield.Console.Demo", "console-demo.log");

        private static readonly object Gate = new();

        /// <summary>Интервал интегрального сброса статистики в журнал.</summary>
        public static TimeSpan Interval { get; } = TimeSpan.FromMinutes(10);

        /// <summary>Дописать строки в журнал (сбои записи подавляются).</summary>
        public static void Write(IEnumerable<string> lines)
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                    File.AppendAllLines(LogPath, lines, Encoding.UTF8);
                }
            }
            catch
            {
                // Журнал не должен останавливать стенд
            }
        }
    }

    // ── Итог итерации плюс тайминги для ротации сида ─────────────────────────

    /// <summary>Результат итерации для основного цикла.</summary>
    private sealed record IterationOutcome(TestResult Result);

    /// <summary>Точка входа: проверка сборки консолей, затем основной цикл.</summary>
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        CleanupStaleWorkDirs();

        if (EnDll.Length == 0 || RuDll.Length == 0)
        {
            Console.Error.WriteLine(
                "Не найдены собранные DataShield.Console.EN / DataShield.Console.RU " +
                "(bin/Debug|bin/Release/net10.0). Соберите решение и перезапустите стенд.");
            return 3;
        }

        return RunStand(args);
    }

    /// <summary>
    /// Основной цикл: бесконечные итерации с перерисовкой таблицы; исключение
    /// итерации — строка FAIL без остановки, сломанное ожидание — остановка.
    /// Начальный сид задаётся аргументом командной строки (dotnet run -- seed).
    /// </summary>
    private static int RunStand(string[] args)
    {
        // +1: строки таблицы (TableWidth) должны быть короче окна, чтобы
        // хвостовая ячейка фона статуса была видна и перевод строки не
        // разворачивался в две строки экрана.
        _lastEnsuredWidth = TableWidth + 1;
        EnsureConsoleSize(_lastEnsuredWidth);
        HideCursor();
        ClearScreen();

        var results = new Queue<TestResult>(HistorySize);
        DrawCodecInfo();
        DrawHeader();
        int tableDataTop = CursorTopSafe;

        // Точная высота окна: шапка + таблица + статистика + строка сида.
        EnsureConsoleSize(_lastEnsuredWidth, tableDataTop + HistorySize + 5);

        // Сид: из командной строки (dotnet run -- <seed>) или случайный.
        uint initialSeed = args.Length > 0 && uint.TryParse(args[0], out var parsed)
            ? parsed
            : (uint)Environment.TickCount64;

        // Плановый интервал ротации (для ускоренной проверки; по умолчанию 20 минут).
        TimeSpan? interval = args.Length > 1 && int.TryParse(args[1], out var seconds)
            ? TimeSpan.FromSeconds(Math.Max(1, seconds))
            : null;
        var logInterval = args.Length > 2 && int.TryParse(args[2], out var logSeconds)
            ? TimeSpan.FromSeconds(Math.Max(1, logSeconds))
            : StandLog.Interval;

        var rotator = new SeedRotator(initialSeed, interval);
        var rng = new Random(unchecked((int)rotator.Seed));

        void DrawSeedLine()
        {
            MoveCursor(0, tableDataTop + HistorySize + 3);
            Console.ForegroundColor = ConsoleColor.DarkGray;
            var line = rotator.Rotations == 0
                ? $" random seed = {rotator.Seed}   consoles: EN + RU"
                : $" random seed = {rotator.Seed} (rotated x{rotator.Rotations}: {rotator.LastReason})   consoles: EN + RU";
            Console.Write(line.PadRight(WindowWidthSafe - 1) + "\n");
            Console.ResetColor();
        }

        DrawSeedLine();

        StandLog.Write(
        [
            $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} console stand started: initial seed = {initialSeed}, interval = {interval ?? TimeSpan.FromMinutes(20)}",
        ]);

        int n = 0;
        int totalSuccess = 0;
        int totalFail = 0;
        // Накопители средних времён дочерних процессов.
        double encMsSum = 0.0;
        double decMsSum = 0.0;
        int encRuns = 0;
        int decRuns = 0;
        var lastIntegralLog = DateTimeOffset.UtcNow;

        while (true)
        {
            n++;

            IterationOutcome outcome;
            try
            {
                outcome = RunIteration(n, rng);
            }
            catch (Exception ex)
            {
                // Исключение внутри итерации — баг стенда либо зависший процесс:
                // строка FAIL, стенд продолжает работу
                Console.Error.WriteLine($"[iter {n}] EXCEPTION: {ex}");
                outcome = new IterationOutcome(
                    new TestResult
                    {
                        N = n,
                        Scenario = "exc",
                        Passed = false,
                        Stress = false,
                        FailureHint = Truncate($"{ex.GetType().Name}: {ex.Message}", 200)
                    });
            }

            var result = outcome.Result;
            if (result.Passed) totalSuccess++;
            else totalFail++;

            result.TotalSuccess = totalSuccess;
            result.TotalFail = totalFail;

            if (result.EncMs > 0) { encMsSum += result.EncMs; encRuns++; }
            if (result.DecMs > 0) { decMsSum += result.DecMs; decRuns++; }

            EnqueueResult(results, result, HistorySize);

            // Перерисовываем таблицу и строку средних времён.
            DrawTable(results.ToList(), tableDataTop);
            DrawSpeedStats(encRuns > 0 ? encMsSum / encRuns : 0.0,
                decRuns > 0 ? decMsSum / decRuns : 0.0, encRuns, decRuns,
                tableDataTop + HistorySize);

            // Ротация сида: плановый интервал либо порог итераций.
            if (rotator.Note(out var newSeed, out var reason))
            {
                rng = new Random(unchecked((int)newSeed));
                DrawSeedLine();
                StandLog.Write(
                [
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} seed rotated: #{rotator.Rotations} -> {rotator.Seed} ({reason}; initial {initialSeed})",
                ]);
            }

            // Курсор — под таблицей, чтобы не мешать.
            MoveCursor(0, tableDataTop + HistorySize + 3);

            // Интегральный сброс статистики в журнал — по расписанию.
            if (DateTimeOffset.UtcNow - lastIntegralLog >= logInterval)
            {
                lastIntegralLog = DateTimeOffset.UtcNow;
                WriteIntegralLog(n, rotator, initialSeed, totalSuccess, totalFail,
                    encRuns > 0 ? encMsSum / encRuns : 0.0,
                    decRuns > 0 ? decMsSum / decRuns : 0.0, results);
            }

            // FAIL — остановка стенда; итог — в журнал.
            if (!result.Passed)
            {
                WriteIntegralLog(n, rotator, initialSeed, totalSuccess, totalFail,
                    encRuns > 0 ? encMsSum / encRuns : 0.0,
                    decRuns > 0 ? decMsSum / decRuns : 0.0, results,
                    footer: $"CONSOLE STAND STOPPED: iteration {n} broke the expectation. " +
                            $"Seed = {rotator.Seed} (initial {initialSeed}, rotations {rotator.Rotations}). " +
                            $"Hint: {result.FailureHint ?? "n/a"}.");

                Console.ForegroundColor = ConsoleColor.Black;
                Console.BackgroundColor = ConsoleColor.DarkRed;
                Console.WriteLine($" CONSOLE STAND STOPPED: iteration {n} broke the expectation. Seed = {rotator.Seed} (initial {initialSeed}, rotations {rotator.Rotations}). Hint: {result.FailureHint ?? "n/a"}. Press any key to exit... ");
                Console.ResetColor();
                ShowCursor();
                if (Console.IsInputRedirected)
                    Console.ReadLine();
                else
                    Console.ReadKey(intercept: true);
                return 1;
            }
        }
    }

    // ── Диспетчеризация сценария итерации ───────────────────────────────────

    /// <summary>
    /// Одна итерация: разыгрывание сценария (rt / mlt / dmg / hlp / err / nhc)
    /// и вызов соответствующего обработчика. Сценарии err / nhc — стрессовые:
    /// корректный исход — чистый отказ с ожидаемым кодом возврата и текстом.
    /// </summary>
    private static IterationOutcome RunIteration(int n, Random rng)
    {
        var roll = rng.Next(100);

        if (roll < HelpChancePercent)
            return ScenarioHelp(n, rng);

        if (roll < HelpChancePercent + MultiChancePercent)
            return ScenarioMultiFile(n, rng);

        if (roll < HelpChancePercent + MultiChancePercent + DamageChancePercent)
            return ScenarioDamaged(n, rng);

        if (roll < HelpChancePercent + MultiChancePercent + DamageChancePercent + ErrChancePercent)
            return ScenarioUsageError(n, rng);

        if (roll < HelpChancePercent + MultiChancePercent + DamageChancePercent + ErrChancePercent + NoHeadersChancePercent)
            return ScenarioNoHeaders(n, rng);

        return ScenarioRoundtrip(n, rng);
    }

    // ── Сценарий: справка (без аргументов и команда help) ────────────────────

    /// <summary>
    /// Справка: запуск без аргументов (ожидается ExitUsage=2, шапка с рамкой
    /// и текст справки) и команда help / --help (ожидается ExitOk=0, тот же
    /// вывод). Локаль консоли разыгрывается случайно.
    /// </summary>
    private static IterationOutcome ScenarioHelp(int n, Random rng)
    {
        var (dll, langTag, ru) = PickLanguage(rng);
        var usageHeader = ru ? "Использование:" : "Usage:";

        // Без аргументов: код 2, шапка + справка на stdout.
        var noArgs = RunConsole(dll, []);
        var okNoArgs = noArgs.Code == 2 &&
                       HasBanner(noArgs.StdOut) &&
                       noArgs.StdOut.Contains(usageHeader) &&
                       noArgs.StdErr.Length == 0;

        // Команда help (случайная форма): код 0, тот же вывод.
        var helpWord = rng.Next(3) switch { 0 => "help", 1 => "--help", _ => "/?" };
        var help = RunConsole(dll, [helpWord]);
        var okHelp = help.Code == 0 &&
                     HasBanner(help.StdOut) &&
                     help.StdOut.Contains(usageHeader);

        var passed = okNoArgs && okHelp;

        return new IterationOutcome(new TestResult
        {
            N = n,
            Lang = langTag,
            Scenario = "hlp",
            Format = "-",
            Via = $"noargs/{helpWord}",
            Passed = passed,
            FailureHint = passed ? null : Describe(
                ("no-args exit=2 + banner + usage", okNoArgs, noArgs),
                ($"help({helpWord}) exit=0 + banner + usage", okHelp, help))
        });
    }

    // ── Сценарий: одиночный roundtrip encode → decode ────────────────────────

    /// <summary>
    /// Одиночный roundtrip: случайный файл (иногда пустой), случайные опции
    /// (формат, ECC 0..1000 с уклоном в малые, заголовки 0..50, схема секторов,
    /// quiet, длинные/короткие формы опций, случайные локали encode/decode) →
    /// проверка кода возврата, шапки, SHA-256 в сводке и побитовой точности.
    /// </summary>
    private static IterationOutcome ScenarioRoundtrip(int n, Random rng)
    {
        var dir = PrepareWorkDir(n);

        var size = NextSize(rng);
        var content = new byte[size];
        rng.NextBytes(content);

        var eccPercent = RollEcc(rng, dataCount: Math.Max(1, (size + 63) / 64));
        var headerPercent = rng.Next(51);
        var format = rng.Next(2) == 0 ? "text" : "bin";
        var ext = format == "text" ? ".txt" : ".bin";
        var scheme = RollScheme(size, eccPercent, rng);
        var quiet = rng.Next(100) < QuietChancePercent;

        var inputPath = Path.Combine(dir, $"rt-{n:D6}.dat");
        File.WriteAllBytes(inputPath, content);

        var fecPath = Path.Combine(dir, $"rt-{n:D6}.DataShield{ext}");
        var encLang = PickLanguage(rng);

        // ── encode: код 0, шапка, SHA-256 входа в сводке, файл создан ──────
        var encArgs = BuildEncodeArgs(inputPath, fecPath, format, eccPercent,
            headerPercent, scheme, quiet, rng);
        var enc = RunConsole(encLang.Dll, encArgs);
        var shaHex = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

        var okEncode = enc.Code == 0 &&
                       HasBanner(enc.StdOut) &&
                       enc.StdOut.Contains(shaHex) &&
                       File.Exists(fecPath);

        // ── decode: случайные опции вывода, код 0, побитовое равенство ──────
        double decMs = 0.0;
        var okDecode = false;
        byte[]? restored = null;
        var decVia = "d";
        var decLangTag = "";

        if (okEncode)
        {
            var useAll = rng.Next(100) < 20;
            var useName = rng.Next(100) < 30;
            var decQuiet = rng.Next(100) < QuietChancePercent;
            var decLang = PickLanguage(rng);
            decLangTag = decLang.Tag;

            var outDir = Path.Combine(dir, "out");
            Directory.CreateDirectory(outDir);
            string expectedPath;

            if (useName)
            {
                // --use-name + существующий каталог: имя из заголовка
                decVia += "n";
                expectedPath = Path.Combine(outDir, $"rt-{n:D6}.dat");
            }
            else
            {
                expectedPath = Path.Combine(outDir, "restored.dat");
            }

            if (useAll) decVia += "a";
            if (decQuiet) decVia += "q";

            var decArgs = new List<string>
            {
                "decode", fecPath, "--output", expectedPath,
                // Схема приёмника соответствует схеме файла: для virtual
                // нужен гибридный приём, классические пакеты читаются тоже
                "--scheme", scheme
            };
            if (useAll) decArgs.Add(Option(rng, "--all", "-a"));
            if (useName) decArgs.Add(Option(rng, "--use-name", "-n"));
            if (decQuiet) decArgs.Add(Option(rng, "--quiet", "-q"));

            var dec = RunConsole(decLang.Dll, decArgs);
            decMs = dec.Ms;

            restored = File.Exists(expectedPath) ? File.ReadAllBytes(expectedPath) : null;
            var contentEqual = restored is not null &&
                               restored.AsSpan().SequenceEqual(content);

            okDecode = dec.Code == 0 &&
                       HasBanner(dec.StdOut) &&
                       contentEqual;
        }

        var passed = okEncode && okDecode;

        // Диагностика: какая именно проверка нарушена (код/шапка/вывод
        // дочернего процесса либо побитовое сравнение).
        string? hint = null;
        if (!passed)
        {
            hint = Describe(
                ("encode: exit=0, banner, SHA-256, output exists", okEncode, enc));

            if (hint is null)
            {
                hint = okDecode
                    ? null
                    : restored is null
                        ? "decode: exit=0/banner ok, restored file missing"
                        : restored.AsSpan().SequenceEqual(content)
                            ? null
                            : $"decode: exit=0/banner ok, content mismatch ({restored.Length} vs {size} bytes)";
            }
        }

        return new IterationOutcome(new TestResult
        {
            N = n,
            SizeBytes = size,
            Lang = encLang.Tag == decLangTag || decLangTag.Length == 0
                ? encLang.Tag : encLang.Tag + decLangTag,
            Scenario = "rt",
            Format = format == "text" ? "txt" : "bin",
            EccPercent = eccPercent,
            HeaderPercent = headerPercent,
            Via = $"e{(quiet ? "q" : "")}{(scheme == "virtual" ? "v" : "")}/{decVia}",
            EncMs = enc.Ms,
            DecMs = decMs,
            Passed = passed,
            FailureHint = hint
        });
    }

    // ── Сценарий: многофайловый поток ────────────────────────────────────────

    /// <summary>
    /// Многофайловый поток: 2–3 файла кодируются отдельными вызовами консоли
    /// (случайные локали/опции), FEC-потоки одного формата склеиваются в один
    /// файл и декодируются одним вызовом decode --all --use-name в каталог.
    /// Ожидание: код 0 и каждый файл побитово присутствует среди результатов.
    /// </summary>
    private static IterationOutcome ScenarioMultiFile(int n, Random rng)
    {
        var dir = PrepareWorkDir(n);

        var fileCount = rng.Next(2) == 0 ? 2 : 3;
        var format = rng.Next(2) == 0 ? "text" : "bin";
        var ext = format == "text" ? ".txt" : ".bin";

        var files = new List<(byte[] Content, string Name)>();
        var encMsSum = 0.0;
        var langTags = "";
        var eccSum = 0;
        var via = "";
        var anyVirtual = false;

        var combinedPath = Path.Combine(dir, $"m-{n:D6}.DataShield{ext}");

        using (var combined = new FileStream(combinedPath, FileMode.Create))
        {
            for (var f = 0; f < fileCount; f++)
            {
                var size = 1 + rng.Next(32 * 1024);
                var content = new byte[size];
                rng.NextBytes(content);

                var name = $"md-{n % 1000:D3}-{(char)('A' + f)}.dat";
                var eccPercent = RollEcc(
                    rng, dataCount: Math.Max(1, (size + 63) / 64));
                var headerPercent = rng.Next(51);
                var scheme = RollScheme(size, eccPercent, rng);
                anyVirtual |= scheme == "virtual";

                var inputPath = Path.Combine(dir, name);
                File.WriteAllBytes(inputPath, content);

                var fecPath = Path.Combine(dir, $"{name}.DataShield{ext}");
                var encLang = PickLanguage(rng);

                var enc = RunConsole(encLang.Dll, BuildEncodeArgs(
                    inputPath, fecPath, format, eccPercent, headerPercent,
                    scheme, quiet: false, rng));

                var ok = enc.Code == 0 && File.Exists(fecPath);
                if (!ok)
                    return new IterationOutcome(new TestResult
                    {
                        N = n,
                        SizeBytes = size,
                        Lang = encLang.Tag,
                        Scenario = "mlt",
                        Format = format == "text" ? "txt" : "bin",
                        Via = $"file[{f}] encode",
                        EncMs = enc.Ms,
                        Passed = false,
                        FailureHint = Describe(
                            ($"encode file[{f}]: exit=0, output exists", ok, enc))
                    });

                encMsSum += enc.Ms;
                if (!langTags.Contains(encLang.Tag)) langTags += encLang.Tag;
                eccSum += eccPercent;
                via += scheme == "virtual" ? "ev" : "e";
                files.Add((content, name));

                // Склейка потоков одного формата: пакеты одного файла за другим
                using var fec = File.OpenRead(fecPath);
                fec.CopyTo(combined);
            }
        }

        // Один декодер на весь перемешанный контент (порядок в файле —
        // последовательный, приём всё равно не зависит от порядка пакетов).
        // В потоке есть файлы схемы virtual — приёмник обязан быть гибридным
        // (-s virtual читает схемы A и B одновременно), иначе секторы схемы B
        // не распознаются классическим приёмником.
        var outDir = Path.Combine(dir, "out");
        Directory.CreateDirectory(outDir);

        var decLang = PickLanguage(rng);
        var decArgs = new List<string>
        {
            "decode", combinedPath, "--output", outDir, "--all", "--use-name"
        };
        if (anyVirtual)
            decArgs.AddRange(["--scheme", "virtual"]);
        var dec = RunConsole(decLang.Dll, decArgs);

        var okDecode = dec.Code == 0 && HasBanner(dec.StdOut);

        // Каждый ожидаемый файл должен найтись среди результатов побитово.
        List<byte[]?> produced = okDecode
            ? Directory.GetFiles(outDir).Select(f => (byte[]?)File.ReadAllBytes(f)).ToList()
            : [];
        var allPresent = okDecode;
        var missingIndex = -1;

        for (var f = 0; f < files.Count && allPresent; f++)
        {
            var found = false;
            for (var p = 0; p < produced.Count; p++)
            {
                if (produced[p] is { } candidate &&
                    candidate.AsSpan().SequenceEqual(files[f].Content))
                {
                    produced[p] = null; // каждый результат соответствует одному файлу
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                allPresent = false;
                missingIndex = f;
            }
        }

        var passed = okDecode && allPresent;

        return new IterationOutcome(new TestResult
        {
            N = n,
            SizeBytes = files.Sum(f => f.Content.Length),
            Lang = langTags.Contains(decLang.Tag) ? langTags : langTags + decLang.Tag,
            Scenario = "mlt",
            Format = format == "text" ? "txt" : "bin",
            EccPercent = eccSum / fileCount,
            HeaderPercent = 3,
            Via = $"{via}/dan{(anyVirtual ? "v" : "")}",
            EncMs = encMsSum,
            DecMs = dec.Ms,
            Passed = passed,
            FailureHint = passed ? null : Describe(
                ("decode --all --use-name: exit=0, banner", okDecode, dec))
                ?? $"file[{missingIndex}] not restored bit-perfect"
        });
    }

    // ── Сценарий: повреждённый поток через CLI ────────────────────────────────

    /// <summary>
    /// Повреждённый поток: файл кодируется in-process случайной реализацией
    /// и перегрузкой контракта, повреждается общим движком стендов (в бюджете
    /// либо сверх бюджета ECC, куски единого формата) и склеивается в один
    /// файл, который декодируется реальной консолью со случайными опциями.
    /// Ожидание: в бюджете — код 0 и побитовая точность; стресс — чистый
    /// отказ (код 1) либо точное восстановление.
    /// </summary>
    private static IterationOutcome ScenarioDamaged(int n, Random rng)
    {
        var dir = PrepareWorkDir(n);

        var plan = ScenarioEngine.RollPlan(n, rng, DamageOptions);
        var file = plan.Files[0];
        var content = file.Content;

        // Кодирование in-process: случайная реализация контракта и перегрузка
        var (encoder, _) = CodecVariants.CreateCodec(
            file.EccPercent, file.HeaderPercent, file.Scheme, file.SectorLimit, rng);
        List<byte[]> packets;
        EncodeStats stats;
        using (encoder)
            (packets, stats, _) = CodecVariants.EncodeVariants(
                encoder, content, file.Name, file.EccPercent, rng);

        // Повреждения: куски единого формата → один файл для CLI decode
        var damage = ScenarioEngine.ApplyDamageUniform(plan, [(packets, stats)], rng);
        var binary = damage.ChunkFormats[0] == OutputFormat.Binary;
        var fecPath = Path.Combine(dir,
            $"dmg-{n:D6}.DataShield{(binary ? ".bin" : ".txt")}");
        File.WriteAllBytes(fecPath, CodecVariants.ConcatChunks(damage));

        var stress = ScenarioEngine.IsStressOutcome(plan, damage);

        // decode: случайные опции вывода и локаль (как в rt)
        var decLang = PickLanguage(rng);
        var outDir = Path.Combine(dir, "out");
        Directory.CreateDirectory(outDir);

        var useAll = rng.Next(100) < 20;
        var useName = rng.Next(100) < 30;
        var quiet = rng.Next(100) < QuietChancePercent;

        var via = "d";
        if (useAll) via += "a";
        if (useName) via += "n";
        if (quiet) via += "q";

        // --use-name: имя берётся из заголовка — упакованное в 14 байт
        // (длинные исходные имена усекаются с маркером «~»)
        var expectedPath = useName
            ? Path.Combine(outDir, FileNameCodec.Pack(file.Name))
            : Path.Combine(outDir, "restored.dat");

        var decArgs = new List<string>
        {
            "decode", fecPath, "--output", expectedPath,
            // Схема приёмника соответствует схеме файла: для virtual
            // нужен гибридный приём, классические пакеты читаются тоже
            "--scheme", file.Scheme == SectorScheme.VirtualIndex ? "virtual" : "classic"
        };
        if (useAll) decArgs.Add(Option(rng, "--all", "-a"));
        if (useName) decArgs.Add(Option(rng, "--use-name", "-n"));
        if (quiet) decArgs.Add(Option(rng, "--quiet", "-q"));

        var dec = RunConsole(decLang.Dll, decArgs);
        var restored = File.Exists(expectedPath) ? File.ReadAllBytes(expectedPath) : null;
        var contentEqual = restored is not null &&
                           restored.AsSpan().SequenceEqual(content);

        var passed = stress
            ? dec.Code == 1 ||
              (dec.Code == 0 && HasBanner(dec.StdOut) && contentEqual)
            : dec.Code == 0 && HasBanner(dec.StdOut) && contentEqual;

        // Диагностика: какое ожидание нарушено (код/шапка дочернего
        // процесса либо побитовое сравнение).
        string? hint = null;
        if (!passed)
        {
            var expectation = stress
                ? "decode stress: exit=1 (clean refusal) or exit=0 + banner + bit-exact"
                : "decode: exit=0, banner, bit-exact content";
            hint = restored is null && dec.Code == 0
                ? Describe((expectation, false, dec)) + "; restored file missing"
                : Describe((expectation, false, dec));
        }

        return new IterationOutcome(new TestResult
        {
            N = n,
            SizeBytes = content.Length,
            Lang = decLang.Tag,
            Scenario = "dmg",
            Format = binary ? "bin" : "txt",
            EccPercent = file.EccPercent,
            HeaderPercent = file.HeaderPercent,
            Via = $"e{(file.Scheme == SectorScheme.VirtualIndex ? "v" : "")}/{via}",
            DecMs = dec.Ms,
            Passed = passed,
            Stress = stress,
            FailureHint = hint
        });
    }

    // ── Сценарий: ошибки использования (ожидаемый отказ) ─────────────────────

    /// <summary>
    /// Ошибки использования: неизвестная команда, значения опций вне диапазона
    /// или не-число, неизвестная опция, несуществующий вход. Ожидание: код 2,
    /// непустое stderr с локализованной подсказкой «--help» и без шапки.
    /// </summary>
    private static IterationOutcome ScenarioUsageError(int n, Random rng)
    {
        var dir = PrepareWorkDir(n);
        var (dll, langTag, ru) = PickLanguage(rng);
        var hint = ru ? "Вызовите с параметром --help" : "Run with --help";

        // Разыгрывается конкретная ошибка.
        var kind = rng.Next(7);
        List<string> args;
        var via = "";

        switch (kind)
        {
            case 0:
                args = ["frobnicate"];
                via = "unk-cmd";
                break;
            case 1:
                args = ["encode", Path.Combine(dir, "x.dat"), "--ecc", "1001"];
                via = "ecc>1000";
                break;
            case 2:
                args = ["encode", Path.Combine(dir, "x.dat"), "--ecc", "-5"];
                via = "ecc<0";
                break;
            case 3:
                args = ["encode", Path.Combine(dir, "x.dat"), "--ecc", "abc"];
                via = "ecc#";
                break;
            case 4:
                args = ["encode", Path.Combine(dir, "x.dat"), "--format", "zip"];
                via = "bad-fmt";
                break;
            case 5:
                args = ["decode", Path.Combine(dir, $"missing-{n}.DataShield.txt")];
                via = "no-file";
                break;
            default:
                args = ["encode", Path.Combine(dir, "x.dat"), "--wat"];
                via = "unk-opt";
                break;
        }

        var child = RunConsole(dll, args);

        var passed = child.Code == 2 &&
                     child.StdErr.Length > 0 &&
                     child.StdErr.Contains(hint) &&
                     !HasBanner(child.StdOut);

        return new IterationOutcome(new TestResult
        {
            N = n,
            Lang = langTag,
            Scenario = "err",
            Format = "-",
            Via = via,
            Passed = passed,
            Stress = true,
            FailureHint = passed ? null : Describe(
                ($"{via}: exit=2, stderr hint, no banner", passed, child))
        });
    }

    // ── Сценарий: поток без заголовков DataShield (ожидаемый отказ) ──────────

    /// <summary>
    /// Поток без заголовков: файл случайного мусора в формате txt либо bin.
    /// Ожидание: код 1, локализованное сообщение об отсутствии заголовков
    /// в stderr, корректный отказ без падения.
    /// </summary>
    private static IterationOutcome ScenarioNoHeaders(int n, Random rng)
    {
        var dir = PrepareWorkDir(n);
        var (dll, langTag, ru) = PickLanguage(rng);
        var noHeaders = ru ? "Заголовки DataShield" : "No DataShield headers";

        var binary = rng.Next(2) == 0;
        var garbagePath = Path.Combine(dir,
            binary ? $"garbage-{n}.DataShield.bin" : $"garbage-{n}.DataShield.txt");

        if (binary)
        {
            var bytes = new byte[1 + rng.Next(5 * 1024)];
            rng.NextBytes(bytes);
            File.WriteAllBytes(garbagePath, bytes);
        }
        else
        {
            // Печатный ASCII без валидных Base64-пакетов: строки «мусора»
            var sb = new StringBuilder();
            for (var line = 0; line < 20 + rng.Next(30); line++)
            {
                var len = 1 + rng.Next(120);
                for (var c = 0; c < len; c++)
                    sb.Append((char)('!' + rng.Next(94)));
                sb.Append('\n');
            }
            File.WriteAllText(garbagePath, sb.ToString());
        }

        var child = RunConsole(dll, ["decode", garbagePath]);

        var passed = child.Code == 1 &&
                     child.StdErr.Contains(noHeaders) &&
                     !HasBanner(child.StdOut);

        return new IterationOutcome(new TestResult
        {
            N = n,
            SizeBytes = (int)Math.Min(int.MaxValue,
                new FileInfo(garbagePath).Length),
            Lang = langTag,
            Scenario = "nhc",
            Format = binary ? "bin" : "txt",
            Via = binary ? "bin" : "txt",
            Passed = passed,
            Stress = true,
            FailureHint = passed ? null : Describe(
                ("decode garbage: exit=1, no-headers on stderr", passed, child))
        });
    }

    // ── Сборка аргументов encode ─────────────────────────────────────────────

    /// <summary>
    /// Аргументы encode со случайными длинными/короткими формами опций:
    /// покрывает -o/--output, -f/--format, -e/--ecc, -h/--header,
    /// -s/--scheme, -q/--quiet и форму --key=value.
    /// </summary>
    private static List<string> BuildEncodeArgs(
        string inputPath, string outputPath, string format, int eccPercent,
        int headerPercent, string scheme, bool quiet, Random rng)
    {
        var args = new List<string>
        {
            "encode",
            inputPath,
            Option(rng, "--output", "-o"),
            outputPath,
        };

        // Форма «--key=value» иногда вместо пары ключ-значение.
        if (rng.Next(4) == 0)
        {
            args.Add($"{Option(rng, "--format", "-f")}={format}");
        }
        else
        {
            args.Add(Option(rng, "--format", "-f"));
            args.Add(format);
        }

        args.Add(Option(rng, "--ecc", "-e"));
        args.Add(eccPercent.ToString());
        args.Add(Option(rng, "--header", "-h"));
        args.Add(headerPercent.ToString());
        args.Add($"{Option(rng, "--scheme", "-s")}");
        args.Add(scheme);

        if (quiet)
            args.Add(Option(rng, "--quiet", "-q"));

        return args;
    }

    /// <summary>Случайная форма опции: длинная либо короткая.</summary>
    private static string Option(Random rng, string Long, string Short) =>
        rng.Next(2) == 0 ? Long : Short;

    // ── Запуск дочернего процесса консоли ────────────────────────────────────

    /// <summary>
    /// Запустить консоль как дочерний процесс dotnet и дождаться завершения:
    /// код возврата, stdout/stderr (UTF-8) и время выполнения. Зависший
    /// процесс снимается по таймауту, исключение валируется наверх (FAIL).
    /// </summary>
    /// <summary>
    /// Определить хост dotnet для запуска дочерних консолей. Если текущий
    /// процесс исполняется самим dotnet — использовать его; иначе (apphost
    /// .exe) — искать dotnet в PATH и DOTNET_ROOT, чтобы не порождать копии
    /// самого себя вместо консолей EN/RU.
    /// </summary>
    private static string DotnetHostFileName =>
        OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";

    private static string DotnetHost()
    {
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(processPath))
        {
            var name = Path.GetFileNameWithoutExtension(processPath);
            if (string.Equals(name, "dotnet", StringComparison.OrdinalIgnoreCase))
                return processPath;
        }

        var root = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrEmpty(root))
        {
            var candidate = Path.Combine(root, DotnetHostFileName);
            if (File.Exists(candidate))
                return candidate;
        }

        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathVar.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir))
                continue;
            var candidate = Path.Combine(dir.Trim(), DotnetHostFileName);
            if (File.Exists(candidate))
                return candidate;
        }

        return "dotnet";
    }

    private static ChildResult RunConsole(string dll, IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = DotnetHost(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        psi.ArgumentList.Add(dll);
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        var sw = Stopwatch.StartNew();

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("не удалось запустить dotnet");

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(ChildTimeoutMs))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            throw new TimeoutException(
                $"дочерний процесс не завершился за {ChildTimeoutMs} мс");
        }

        var ms = sw.Elapsed.TotalMilliseconds;

        return new ChildResult(
            process.ExitCode,
            stdoutTask.Result,
            stderrTask.Result,
            ms);
    }

    // ── Проверки вывода ──────────────────────────────────────────────────────

    /// <summary>
    /// В выводе есть шапка с рамкой (стиль консольного RAR): рамка «╔═…»
    /// (до неё при не-quiet выводе идут перезаписываемые строки прогресса)
    /// и полная строка копирайта.
    /// </summary>
    private static bool HasBanner(string stdout) =>
        stdout.Contains("╔═") &&
        stdout.Contains("Artem Drobanov, Vladislav Utyumov");

    /// <summary>
    /// Описание первой нарушенной проверки для FailureHint: что ожидалось,
    /// фактический код возврата и фрагменты вывода дочернего процесса.
    /// </summary>
    private static string? Describe(
        params (string Expectation, bool Ok, ChildResult Child)[] checks)
    {
        foreach (var check in checks)
        {
            if (check.Ok)
                continue;

            var tail = new StringBuilder();
            tail.Append($"; child exit={check.Child.Code}, ")
                .Append($"stdout[{Truncate(check.Child.StdOut, 120)}], ")
                .Append($"stderr[{Truncate(check.Child.StdErr, 160)}]");
            return check.Expectation + tail.ToString();
        }

        return null;
    }

    /// <summary>Первые length символов строки в одну строку (для дампа).</summary>
    private static string Truncate(string value, int length)
    {
        var oneLine = value.Replace("\r", "").Replace('\n', '⏎');
        return oneLine.Length <= length ? oneLine : oneLine[..length] + "…";
    }

    // ── Случайные параметры ──────────────────────────────────────────────────

    /// <summary>
    /// Случайная локаль консоли: EN либо RU (DLL, метка колонки, признак
    /// русской локали для ожидаемых фрагментов вывода).
    /// </summary>
    private static (string Dll, string Tag, bool Ru) PickLanguage(Random rng) =>
        rng.Next(2) == 0
            ? (EnDll, "E", false)
            : (RuDll, "R", true);

    /// <summary>
    /// Случайный размер файла: лог-равномерный в 1..256 КБ,
    /// иногда (3%) — пустой файл.
    /// </summary>
    private static int NextSize(Random rng)
    {
        if (rng.Next(100) < EmptyChancePercent) return 0;

        var size = (int)Math.Round(Math.Pow(2.0, rng.NextDouble() * MaxSizeLog2));
        return Math.Clamp(size, MinFileSize, MaxFileSize);
    }

    /// <summary>
    /// Избыточность ECC через целевой бюджет томов: M разыгрывается в лёгком
    /// (0..32) либо расширенном (0..128) диапазоне, процент выводится из M и N
    /// и зажимается в допустимые 0..1000. Стоимость RS ~ O(E²·N), поэтому
    /// бюджет томов — не времени — ограничивает длительность кодирования;
    /// на малых файлах процент покрывает весь диапазон вплоть до 1000%.
    /// </summary>
    private static int RollEcc(Random rng, int dataCount)
    {
        var targetVolumes = rng.Next(100) < 70 ? rng.Next(33) : rng.Next(129);
        if (dataCount <= 0)
            return 0;

        var percent = (int)(((long)targetVolumes * 100 + dataCount / 2) / dataCount);
        return Math.Clamp(percent, 0, 1000);
    }

    /// <summary>
    /// Схема секторов: иногда virtual с откатом на classic. Схема B применима
    /// максимум к 512 томам N+M (кодер без даунгрейда отказывает), а её приём
    /// brute-force дорожает с объёмом — поэтому virtual разыгрывается только
    /// для компактных файлов, длинные кодируются classic.
    /// </summary>
    private static string RollScheme(int size, int eccPercent, Random rng)
    {
        if (rng.Next(100) >= VirtualIndexChancePercent || size > VirtualMaxSize)
            return "classic";

        var dataCount = Math.Max(1, (size + 63) / 64);
        var total = dataCount + ComputeEccCount(dataCount, eccPercent);
        return total <= 512 ? "virtual" : "classic";
    }

    /// <summary>Формула ECC-томов кодека: M = max(1, round(N·pct/100)).</summary>
    private static int ComputeEccCount(int dataCount, int eccPercent) =>
        eccPercent <= 0 || dataCount <= 0
            ? 0
            : Math.Max(1, (int)(((long)dataCount * eccPercent + 50) / 100));

    // ── Рабочий каталог ──────────────────────────────────────────────────────

    /// <summary>
    /// Подготовить чистый рабочий каталог итерации; после FAIL каталог
    /// сохраняется для разбора (артефакты итерации не удаляются).
    /// </summary>
    private static string PrepareWorkDir(int n)
    {
        var dir = Path.Combine(WorkDir, $"it-{n}");
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// Удалить чужие и устаревшие рабочие каталоги: подкаталоги console-demo,
    /// принадлежащие уже завершившимся процессам (p&lt;PID&gt;), и остатки
    /// прежней раскладки без PID (it-N и каталоги итераций в корне).
    /// Ошибки удаления (занятость, доступ) молча игнорируются.
    /// </summary>
    private static void CleanupStaleWorkDirs()
    {
        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(Directory.GetParent(WorkDir)!.FullName))
            {
                var name = Path.GetFileName(entry);
                if (name.StartsWith("p", StringComparison.Ordinal)
                    && int.TryParse(name.AsSpan(1), out var pid)
                    && !IsProcessAlive(pid))
                {
                    Directory.Delete(entry, recursive: true);
                }
                else if (name.StartsWith("it-", StringComparison.Ordinal))
                {
                    Directory.Delete(entry, recursive: true);
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Жив ли процесс с указанным идентификатором.</summary>
    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    // ── Поиск сборки решения и DLL консолей ──────────────────────────────────

    /// <summary>Корень решения: вверх от каталога сборки до DataShield.slnx.</summary>
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DataShield.slnx")))
            dir = dir.Parent;

        return dir?.FullName
            ?? throw new InvalidOperationException(
                "DataShield.slnx не найден вверх по дереву каталогов.");
    }

    /// <summary>
    /// DLL локализованной консоли: bin/Debug, затем bin/Release
    /// (Debug-сборка свежее в рабочем цикле; пустая строка — не найдена).
    /// </summary>
    private static string FindConsoleDll(string project)
    {
        foreach (var config in new[] { "Debug", "Release" })
        {
            var path = Path.Combine(
                RepoRoot, project, "bin", config, "net10.0", project + ".dll");
            if (File.Exists(path))
                return path;
        }

        return "";
    }

    // ------------- Обёртки над консольными манипуляциями -------------
    // CursorVisible/SetCursorPosition/WindowWidth бросают IOException при
    // перенаправленном выводе — обёртки делают операции необязательными:
    // в интерактивной консоли таблица рисуется на месте, при перенаправлении
    // строки просто печатаются потоком.

    /// <summary>Спрятать курсор (только в интерактивной консоли).</summary>
    private static void HideCursor()
    {
        if (!Console.IsOutputRedirected)
            Console.CursorVisible = false;
    }

    /// <summary>Вернуть курсор перед выходом.</summary>
    private static void ShowCursor()
    {
        if (!Console.IsOutputRedirected)
            Console.CursorVisible = true;
    }

    /// <summary>Переставить курсор (no-op при перенаправлении).</summary>
    private static void MoveCursor(int left, int top)
    {
        if (!Console.IsOutputRedirected)
            Console.SetCursorPosition(left, top);
    }

    /// <summary>Ширина окна (fallback 120 при перенаправлении).</summary>
    private static int WindowWidthSafe => Console.IsOutputRedirected ? 120 : Console.WindowWidth;

    /// <summary>Текущая строка курсора (0 при перенаправлении).</summary>
    private static int CursorTopSafe => Console.IsOutputRedirected ? 0 : Console.CursorTop;

    /// <summary>Очистить экран (no-op при перенаправлении).</summary>
    private static void ClearScreen()
    {
        if (!Console.IsOutputRedirected)
            Console.Clear();
    }

    /// <summary>
    /// Гарантировать, что окно и буфер консоли не уже требуемой ширины
    /// (только в интерактивной консоли; сбои подавляются).
    /// Окно только расширяется и не может быть шире LargestWindowWidth;
    /// буфер выравнивается точно на ширину окна (без скроллбара и переноса строк).
    /// </summary>
    private static void EnsureConsoleSize(int width, int? height = null)
    {
        if (Console.IsOutputRedirected)
            return;

        try
        {
            var targetHeight = Math.Max(height ?? _lastEnsuredHeight, 1);

            // Ширина окна подгоняется точно под запрошенную (в том числе
            // сужается до ширины таблицы), а не только растёт.
            var windowWidth = Math.Min(Math.Max(width, 20), Console.LargestWindowWidth);
            var windowHeight = Math.Min(Math.Max(targetHeight, Console.WindowHeight), Console.LargestWindowHeight);

            // Сначала расширить буфер под целевое окно: иначе SetWindowSize
            // тихо падает и курсор уходит за нижнюю строку (прокрутка экрана).
            var bufferWidth = Math.Max(Console.BufferWidth, windowWidth);
            var bufferHeight = Math.Max(Console.BufferHeight, windowHeight);
            if (Console.BufferWidth != bufferWidth || Console.BufferHeight != bufferHeight)
                Console.SetBufferSize(bufferWidth, bufferHeight);

            if (Console.WindowWidth != windowWidth || Console.WindowHeight != windowHeight)
                Console.SetWindowSize(windowWidth, windowHeight);

            // Выровнять ширину буфера точно на ширину окна (без скроллбара).
            if (Console.BufferWidth > windowWidth)
                Console.SetBufferSize(windowWidth, Math.Max(Console.BufferHeight, windowHeight));

            _lastEnsuredHeight = windowHeight;
        }
        catch
        {
            // Размер консоли некритичен для работы стенда
        }
    }

    // ------------- Информация и отрисовка -------------

    /// <summary>Строка «продукт — версия — копирайт» для шапки стенда.</summary>
    private static string VersionLine
    {
        get
        {
            var ver = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version;
            return $"DataShield v{ver?.Major ?? 1}.{ver?.Minor ?? 0} {BuildInfo.BuildLabel}   Copyright (c) 2026 Artem Drobanov, Vladislav Utyumov";
        }
    }

    /// <summary>Информационный блок о стенде и сценариях.</summary>
    private static void DrawCodecInfo()
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(" DATASHIELD CONSOLE CONTINUOUS USER-INTERACTION TEST");
        Console.ResetColor();

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine(" " + VersionLine);
        Console.ResetColor();
        Console.WriteLine();

        Console.ForegroundColor = ConsoleColor.Gray;
        Console.WriteLine(" Target:  child processes DataShield.Console.EN / DataShield.Console.RU");
        Console.WriteLine(" Scn:     rt = encode→decode roundtrip, mlt = multi-file stream,");
        Console.WriteLine("          dmg = damaged stream (in-process damage → CLI decode),");
        Console.WriteLine("          hlp = help/no-args, err = usage errors (expected), nhc = no headers");
        Console.WriteLine(" Lng:     E = EN console, R = RU console (random per invocation)");
        Console.WriteLine(" Via:     e/d + q(uiet) v(irtual) a(ll) n(ame); exit codes: 0 ok, 2 usage, 1 error");
        Console.ResetColor();
    }

    /// <summary>Легенда статусов и шапка таблицы результатов.</summary>
    private static void DrawHeader()
    {
        HideCursor();

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine();
        Console.WriteLine("  Legend: ");
        Console.Write("    ");
        Console.ForegroundColor = ConsoleColor.Black;
        Console.BackgroundColor = ConsoleColor.DarkGreen;
        Console.Write(" PASS ");
        Console.ResetColor();
        Console.Write(" - user contract verified (roundtrip / help output)\n");

        Console.Write("    ");
        Console.ForegroundColor = ConsoleColor.Black;
        Console.BackgroundColor = ConsoleColor.DarkYellow;
        Console.Write(" WARN ");
        Console.ResetColor();
        Console.Write(" - deliberate misuse: clean refusal with expected exit code\n");

        Console.Write("    ");
        Console.ForegroundColor = ConsoleColor.Black;
        Console.BackgroundColor = ConsoleColor.DarkRed;
        Console.Write(" FAIL ");
        Console.ResetColor();
        Console.Write(" - expectation broken: wrong exit code / output / data (bug)\n");
        Console.WriteLine();

        // Шапка таблицы.
        Console.ForegroundColor = ConsoleColor.White;
        Console.BackgroundColor = ConsoleColor.DarkBlue;
        WriteLineFit(HeaderLine);
        Console.ResetColor();
    }

    /// <summary>Динамически расширить консоль, если строка шире текущей подгонки.</summary>
    private static void EnsureRowFits(string line)
    {
        if (line == null || line.Length <= _lastEnsuredWidth)
            return;

        _lastEnsuredWidth = line.Length;
        EnsureConsoleSize(_lastEnsuredWidth);    }

    /// <summary>
    /// WriteLine без полноширинного переноса: строка ровно в ширину консоли
    /// плюс перевод строки занимает две строки (курсор переносится на новую
    /// строку, затем перевод строки добавляет ещё одну) — поэтому при
    /// интерактивном выводе ширина строки ограничивается шириной окна минус один.
    /// </summary>
    private static void WriteLineFit(string line)
    {
        if (Console.IsOutputRedirected || line.Length < WindowWidthSafe)
        {
            Console.WriteLine(line);
            return;
        }

        Console.WriteLine(line.Substring(0, Math.Max(WindowWidthSafe - 1, 1)));
    }

    /// <summary>Строка таблицы результатов (общая для отрисовки и журнала).</summary>
    private static string RowText(TestResult r) => string.Format(RowFormat,
        r.N,
        SizeText(r.SizeBytes),
        r.Lang,
        r.Scenario,
        r.Format,
        r.EccPercent,
        r.HeaderPercent,
        r.Via,
        r.EncMs,
        r.DecMs,
        r.Status);

    /// <summary>Перерисовать строки таблицы с позиции topRow (цвет статуса).</summary>
    private static void DrawTable(IReadOnlyList<TestResult> buffer, int topRow)
    {
        HideCursor();
        MoveCursor(0, topRow);

        foreach (var r in buffer)
        {
            Console.ForegroundColor = ConsoleColor.Black;
            Console.BackgroundColor = r.Status switch
            {
                "PASS" => ConsoleColor.DarkGreen,
                "WARN" => ConsoleColor.DarkYellow,
                _ => ConsoleColor.DarkRed
            };

            var row = RowText(r);
            EnsureRowFits(row);
            WriteLineFit(row);

            Console.ResetColor();
        }
    }

    /// <summary>Добавить результат в кольцевой буфер, вытесняя самый старый.</summary>
    private static void EnqueueResult(Queue<TestResult> q, TestResult r, int max)
    {
        if (q.Count == max)
            q.Dequeue();
        q.Enqueue(r);
    }

    /// <summary>Средние времена дочерних процессов под таблицей.</summary>
    private static void DrawSpeedStats(
        double avgEncMs, double avgDecMs, int encRuns, int decRuns, int topRow)
    {
        int winWidth = WindowWidthSafe;
        // Стираем старые строки статистики.
        MoveCursor(0, topRow);
        Console.ForegroundColor = ConsoleColor.White;
        Console.BackgroundColor = ConsoleColor.Black;
        Console.Write(new string(' ', winWidth));
        MoveCursor(0, topRow + 1);
        Console.Write(new string(' ', winWidth));

        // Рисуем заново, подгоняя под ширину таблицы.
        MoveCursor(0, topRow);
        Console.ForegroundColor = ConsoleColor.White;
        Console.BackgroundColor = ConsoleColor.DarkBlue;
        string encStr = $" Average encode process: {avgEncMs,5:F0} ms ({encRuns} runs) ";
        string decStr = $" Average decode process: {avgDecMs,5:F0} ms ({decRuns} runs) ";
        encStr = encStr.PadRight(TableWidth).Substring(0, TableWidth);
        decStr = decStr.PadRight(TableWidth).Substring(0, TableWidth);
        Console.Write(encStr);
        if (TableWidth < winWidth)
        {
            Console.BackgroundColor = ConsoleColor.Black;
            Console.Write(new string(' ', winWidth - TableWidth));
        }

        MoveCursor(0, topRow + 1);
        Console.BackgroundColor = ConsoleColor.DarkBlue;
        Console.Write(decStr);
        if (TableWidth < winWidth)
        {
            Console.BackgroundColor = ConsoleColor.Black;
            Console.Write(new string(' ', winWidth - TableWidth));
        }
        Console.ResetColor();
    }

    // ------------- Журналирование итогов -------------

    /// <summary>
    /// Интегральный сброс статистики в журнал: момент, сид, накопленные
    /// итоги, средние времена процессов и снимок последних итераций.
    /// </summary>
    private static void WriteIntegralLog(
        int n, SeedRotator rotator, uint initialSeed,
        int totalSuccess, int totalFail,
        double avgEncMs, double avgDecMs,
        Queue<TestResult> history,
        string? footer = null)
    {
        var lines = new List<string>
        {
            $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} integral stats: iteration {n}, " +
            $"pass {totalSuccess}, fail {totalFail}, " +
            $"avg encode {avgEncMs:F0} ms, avg decode {avgDecMs:F0} ms",
            $"  seed = {rotator.Seed} (initial {initialSeed}, rotations {rotator.Rotations})",
            "  recent iterations:",
        };
        lines.AddRange(history.Select(RowText));
        if (footer is not null)
            lines.Add("  " + footer);

        StandLog.Write(lines);
    }

    /// <summary>Человекочитаемый размер: B / KB / MB.</summary>
    private static string SizeText(int size) =>
        size >= 1 << 20 ? $"{size / (double)(1 << 20):F2}MB"
        : size >= 1024 ? $"{size / 1024.0:F1}KB"
        : $"{size}B";
}
