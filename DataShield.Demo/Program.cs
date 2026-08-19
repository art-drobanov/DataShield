using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using DataShield.Codec;
using DataShield.Codec.Packets;
using DataShield.Interfaces;
using DataShield.TestsHarness;

/// <summary>
/// Консольный стенд непрерывной стабильности: в бесконечном цикле
/// кодирует случайные файлы (одиночные и мультифайловые потоки, обе схемы
/// секторов, случайные проценты ECC), вносит повреждения в бюджете и сверх
/// бюджета ECC и проверяет декодирование. PASS — точное восстановление,
/// WARN — корректный отказ при сознательном сверхповреждении, FAIL — сломанное
/// ожидание (останавливает стенд). Таблица последних итераций и средние
/// скорости рисуются на месте (при перенаправленном выводе — потоком).
///
/// Стенд работает только через единый контракт <see cref="IDataShieldCodec"/>:
/// каждая операция выполняется случайно созданным экземпляром библиотечного
    /// типа DataShieldCodec (колонка Api = A) либо консольного DataShieldConsole
/// (Api = C); экземпляры освобождаются через IDisposable после операции.
/// Сид ПСЧ ротируется при исчерпании разнообразия классов сценариев либо
/// по плановому интервалу (по умолчанию — раз в час).
/// </summary>
internal static class Program
{
    // Строк таблицы в кольцевом буфере.
    private const int HistorySize = 20;

    // Ширина таблицы результатов; вычисляется в DrawHeader.
    private static int _tableWidth;

    /// <summary>Шапка таблицы результатов (общая для отрисовки и вычисления ширины).</summary>
    private static readonly string HeaderLine = string.Format(
        " {0,8} | {1,8} | {2,3} | {3,3} | {4,3} | {5,5} | {6,3} | {7,3} | {8,5}/{9,-5} | {10,5} | {11,6} | {12,6} | {13,6} | {14,7} | {15,7} | {16,4} ",
        "N", "Size", "Fmt", "Sch", "Api", "Via", "Ecc", "Hdr", "Data", "Ecc", "Lost", "Dmg",
        "T.Pass", "T.Fail", "EncMBps", "DecMBps", "Stat");

    // Формат строки таблицы (общий для отрисовки и журнала).
    private const string RowFormat =
        " {0,8} | {1,8} | {2,3} | {3,3} | {4,3} | {5,5} | {6,3} | {7,3} | {8,5}/{9,-5} | {10,5} | {11,6:X6} | {12,6} | {13,6} | {14,7:F2} | {15,7:F2} | {16,4} ";

    // Реальная ширина таблицы: длина отформатированной строки-образца
    // (все поля фиксированной ширины).
    private static readonly int TableWidth = string.Format(
        RowFormat, 0, 0, "", "", "", "", 0, 0, 0, 0, 0, 0u, 0, 0, 0.0, 0.0, "").Length;

    // Последняя применённая ширина консоли (кэш, чтобы не дёргать
    // resize на каждом кадре; растёт по фактической длине строк).
    private static int _lastEnsuredWidth;

    // Последняя применённая высота окна консоли (кэш; уточняется после
    // отрисовки шапки — по фактической высоте раскладки экрана).
    private static int _lastEnsuredHeight = 42;

    /// <summary>Итог одной итерации стенда.</summary>
    private sealed class TestResult
    {
        public int N { get; set; }               // номер итерации
        public int SizeBytes { get; set; }       // размер входа (сумма для multi)
        public string Format { get; set; } = ""; // txt / bin / mlt
        public string Scheme { get; set; } = ""; // схема секторов: A / B / AB (смесь)
        public string Impl { get; set; } = "";   // реализации контракта: A / C / AC
        public string Via { get; set; } = "";    // перегрузки: кодирование/декодирование (S/P/B/W/I/T × D/T/N/A/M)
        public int EccPercent { get; set; }      // избыточность, % (средняя для multi)
        public int HeaderPercent { get; set; }   // избыточность заголовков, %
        public int DataCount { get; set; }       // data-тома
        public int EccCount { get; set; }        // ECC-тома
        public int LostSectors { get; set; }     // потеряно секторов (> Ecc при WARN)
        public uint DamageMask { get; set; }     // маска повреждений (hex)
        public bool Passed { get; set; }         // ожидание итерации выполнено
        public bool Stress { get; set; }         // режим ожидаемого отказа
        public int TotalSuccess { get; set; }    // накопленный успех (PASS+WARN)
        public int TotalFail { get; set; }       // накопленные неудачи
        public double EncMBps { get; set; }      // скорость кодирования
        public double DecMBps { get; set; }      // скорость декодирования

        /// <summary>Строка статуса: FAIL / WARN (ожидаемый отказ) / PASS.</summary>
        public string Status => !Passed ? "FAIL" : Stress ? "WARN" : "PASS";

        // Краткая причина неудачи (позиция расхождения, длина, null и т.п.)
        public string? FailureHint { get; set; }
    }

    // ------------- Ротация сида ПСЧ -------------

    /// <summary>
    /// Ротация сида ПСЧ стенда: сид меняется при исчерпании разнообразия
    /// (каждый класс сценариев И каждая перегрузка контракта покрыты заданное
    /// число раз), при застое разнообразия (долго нет новых классов/перегрузок)
    /// либо по плановому интервалу (по умолчанию — раз в час). Новый сид
    /// выводится из предыдущего и номера ротации (SHA-256): вся
    /// последовательность ротаций воспроизводима от начального сида.
    /// </summary>
    private sealed class SeedRotator
    {
        public const int EccBuckets = 8;   // корзины ECC: шаг 25%, 0..7
        public const int HeaderBuckets = 4; // корзины заголовков: шаг 3%, 0..3 (1..10%)

        private const int SingleClasses = 2 * 2 * 2 * EccBuckets * HeaderBuckets; // стресс × формат × схема × ECC × заголовки
        private const int MultiSchemeCombos = 3; // A / B / AB
        private const int MultiClasses = MultiSchemeCombos * EccBuckets * HeaderBuckets;
        private const int TotalClasses = SingleClasses + MultiClasses;

        public const int EncodeOverloads = 6; // S / P / B / W / I / T
        public const int DecodeOverloads = 5; // D / T / N / A / M

        // Ротация при застое: столько итераций подряд без новых классов/перегрузок
        private const int StallIterations = 150;

        private readonly TimeSpan _interval;
        private readonly int _coverageTarget;
        private readonly int[] _coverage = new int[TotalClasses];
        private readonly int[] _encCoverage = new int[EncodeOverloads];
        private readonly int[] _decCoverage = new int[DecodeOverloads];
        private int _covered;
        private int _overloadsCovered;
        private int _sinceProgress;
        private DateTimeOffset _lastRotate = DateTimeOffset.UtcNow;

        /// <summary>Создать ротатор с начальным сидом.</summary>
        /// <param name="initialSeed">Начальный сид ПСЧ.</param>
        /// <param name="interval">Плановый интервал ротации (по умолчанию 1 час).</param>
        /// <param name="coverageTarget">Порог покрытия каждого класса и перегрузки.</param>
        public SeedRotator(uint initialSeed, TimeSpan? interval = null, int coverageTarget = 3)
        {
            Seed = initialSeed;
            _interval = interval ?? TimeSpan.FromHours(1);
            _coverageTarget = coverageTarget;
        }

        /// <summary>Текущий сид ПСЧ.</summary>
        public uint Seed { get; private set; }

        /// <summary>Число выполненных ротаций.</summary>
        public int Rotations { get; private set; }

        /// <summary>Причина последней ротации.</summary>
        public string LastReason { get; private set; } = "";

        /// <summary>Доля покрытия пространства разнообразия (классы + перегрузки), 0..1.</summary>
        public double CoverageFraction =>
            ((double)_covered + _overloadsCovered) /
            (TotalClasses + EncodeOverloads + DecodeOverloads);

        /// <summary>Класс сценария одиночной итерации: стресс × формат × схема × корзины ECC и заголовков.</summary>
        public static int SingleClass(
            bool stress, bool binary, bool virtualIndex, int eccPercent, int headerPercent) =>
            (stress ? 1 : 0) |
            ((binary ? 1 : 0) << 1) |
            ((virtualIndex ? 1 : 0) << 2) |
            (EccBucket(eccPercent) << 3) |
            (HeaderBucket(headerPercent) << 6);

        /// <summary>Класс многофайловой итерации: combo схем × корзины ECC и заголовков.</summary>
        public static int MultiClass(int schemeCombo, int eccPercent, int headerPercent) =>
            SingleClasses +
            Math.Clamp(schemeCombo, 0, MultiSchemeCombos - 1) * EccBuckets * HeaderBuckets +
            EccBucket(eccPercent) * HeaderBuckets +
            HeaderBucket(headerPercent);

        /// <summary>Корзина избыточности ECC: шаг 25%.</summary>
        private static int EccBucket(int eccPercent) =>
            Math.Clamp(eccPercent / 25, 0, EccBuckets - 1);

        /// <summary>Корзина избыточности заголовков: шаг 3% в диапазоне 1..10%.</summary>
        private static int HeaderBucket(int headerPercent) =>
            Math.Clamp((headerPercent - 1) / 3, 0, HeaderBuckets - 1);

        /// <summary>Индекс перегрузки кодирования (S/P/B/W/I/T) или -1.</summary>
        public static int EncodeViaIndex(char via) => via switch
        {
            'S' => 0, 'P' => 1, 'B' => 2, 'W' => 3, 'I' => 4, 'T' => 5, _ => -1
        };

        /// <summary>Индекс перегрузки декодирования (D/T/N/A/M) или -1.</summary>
        public static int DecodeViaIndex(char via) => via switch
        {
            'D' => 0, 'T' => 1, 'N' => 2, 'A' => 3, 'M' => 4, _ => -1
        };

        /// <summary>
        /// Зафиксировать состоявшуюся итерацию (класс сценария и использованные
        /// перегрузки); при исчерпании/застое разнообразия либо по плановому
        /// интервалу сменить сид. Возвращает true при ротации (новый сид — в
        /// <paramref name="newSeed"/>).
        /// </summary>
        public bool Note(
            int scenarioClass, string encVias, char decVia,
            out uint newSeed, out string reason)
        {
            newSeed = 0;
            var progress = false;

            if (scenarioClass >= 0 && scenarioClass < TotalClasses &&
                _coverage[scenarioClass] < _coverageTarget)
            {
                if (++_coverage[scenarioClass] == _coverageTarget)
                {
                    _covered++;
                    progress = true;
                }
            }

            foreach (var via in encVias)
            {
                var e = EncodeViaIndex(via);
                if (e >= 0 && _encCoverage[e] < _coverageTarget &&
                    ++_encCoverage[e] == _coverageTarget)
                {
                    _overloadsCovered++;
                    progress = true;
                }
            }

            var d = DecodeViaIndex(decVia);
            if (d >= 0 && _decCoverage[d] < _coverageTarget &&
                ++_decCoverage[d] == _coverageTarget)
            {
                _overloadsCovered++;
                progress = true;
            }

            _sinceProgress = progress ? 0 : _sinceProgress + 1;

            reason =
                _covered >= TotalClasses &&
                _overloadsCovered >= EncodeOverloads + DecodeOverloads
                    ? "diversity exhausted"
                    : _sinceProgress >= StallIterations
                        ? "diversity stalled"
                        : DateTimeOffset.UtcNow - _lastRotate >= _interval
                            ? "planned interval"
                            : "";

            if (reason.Length == 0)
                return false;

            newSeed = NextSeed();
            Seed = newSeed;
            LastReason = reason;
            Rotations++;
            _lastRotate = DateTimeOffset.UtcNow;
            Array.Clear(_coverage, 0, _coverage.Length);
            Array.Clear(_encCoverage, 0, _encCoverage.Length);
            Array.Clear(_decCoverage, 0, _decCoverage.Length);
            _covered = 0;
            _overloadsCovered = 0;
            _sinceProgress = 0;
            return true;
        }

        /// <summary>
        /// Цепочка сидов: новый сид = Trunc32(SHA-256(предыдущий ‖ №ротации ‖
        /// домен "DSLD")) — последовательность ротаций воспроизводима от
        /// начального сида и при этом не коррелирует с предыдущим сидом.
        /// </summary>
        private uint NextSeed()
        {
            var raw = new byte[12];
            BitConverter.GetBytes(Seed).CopyTo(raw, 0);
            BitConverter.GetBytes(Rotations + 1).CopyTo(raw, 4);
            BitConverter.GetBytes(0x4453_4C44).CopyTo(raw, 8); // "DSLD"
            return BitConverter.ToUInt32(SHA256.HashData(raw), 0);
        }
    }

    // ------------- Журнал стенда -------------

    /// <summary>
    /// Журнал стенда: интегральный сброс статистики раз в 15 минут, события
    /// ротации сида и отказы. Запись защищена от любых сбоев (нет файла, нет
    /// диска, занято): не записали — и ладно, стенд продолжает работу.
    /// </summary>
    private static class StandLog
    {
        private static readonly string LogPath = Path.Combine(
            Path.GetTempPath(), "DataShield-logs", "DataShield.Demo", "demo-stand.log");

        private static readonly object Gate = new();

        /// <summary>Интервал интегрального сброса статистики в журнал.</summary>
        public static TimeSpan Interval { get; } = TimeSpan.FromMinutes(15);

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

    // ------------- Обёртки над консольными манипуляциями -------------
    // CursorVisible/SetCursorPosition/WindowWidth бросают IOException при
    // перенаправленном выводе (нет консольного дескриптора) — например, при
    // запуске в пайпе или CI. Обёртки делают эти операции необязательными:
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

            var windowWidth = Math.Min(Math.Max(width, Console.WindowWidth), Console.LargestWindowWidth);
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

    /// <summary>Динамически расширить консоль, если строка шире текущей подгонки.</summary>
    private static void EnsureRowFits(string line)
    {
        if (line == null || line.Length <= _lastEnsuredWidth)
            return;

        _lastEnsuredWidth = line.Length;
        EnsureConsoleSize(_lastEnsuredWidth);
    }

    // ------------- Параметры кодека и легенда -------------

    /// <summary>Строка «продукт — версия — копирайт» для шапки стенда.</summary>
    private static string VersionLine
    {
        get
        {
            var ver = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version;
            return $"DataShield v{ver?.Major ?? 1}.{ver?.Minor ?? 0} {BuildInfo.BuildLabel}   Copyright (c) 2026 Artem Drobanov, Vladislav Utyumov";
        }
    }

    /// <summary>Информационный блок о параметрах кодека и легенде маски повреждений.</summary>
    private static void DrawCodecInfo()
    {
        const int LabelWidth = 10;

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(" DATASHIELD CODEC CONTINUOUS STABILITY AND PERFORMANCE TEST");
        Console.ResetColor();

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine(" " + VersionLine);
        Console.ResetColor();
        Console.WriteLine();

        Console.ForegroundColor = ConsoleColor.Gray;

        void Info(string label, string text) =>
            Console.WriteLine(" " + label.PadRight(LabelWidth) + text);

        Info("Codec:", "75B packets / 100 Base64 chars, 64B payload, RS GF(2^16)");
        Info("Sectors:", "A = classic (D1+payload+D3), B = virtual-index (payload+Trunc11, K<=512)");
        Info("API:", "IDataShieldCodec, random instance per operation:");
        Info("", "DataShieldCodec [A] / DataShieldConsole [C], IDisposable");
        Info("Via:", "encode S/P/B/W/I/T × decode D/T/N/A/M — random overload per operation");
        Console.ForegroundColor = ConsoleColor.DarkGray;

        // Легенда маски повреждений: перенос по словам с выравниванием
        // под колонку значений
        var indent = new string(' ', LabelWidth + 1);
        var col = indent.Length;
        Console.Write(" " + "Damage:".PadRight(LabelWidth));
        foreach (var token in DamageBits.Legend.Split(' '))
        {
            if (col > indent.Length)
            {
                Console.Write(' ');
                col++;
            }

            if (col + token.Length > TableWidth)
            {
                Console.WriteLine();
                Console.Write(indent);
                col = indent.Length;
            }

            Console.Write(token);
            col += token.Length;
        }

        Console.WriteLine();
        Console.ResetColor();
    }

    // ------------- Отрисовка: шапка и таблица -------------

    /// <summary>Легенда статусов и шапка таблицы результатов; фиксирует ширину таблицы.</summary>
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
        Console.Write(" - in-budget damage, restored bit-perfect\n");

        Console.Write("    ");
        Console.ForegroundColor = ConsoleColor.Black;
        Console.BackgroundColor = ConsoleColor.DarkYellow;
        Console.Write(" WARN ");
        Console.ResetColor();
        Console.Write(" - deliberate over-damage: codec reports failure correctly\n");

        Console.Write("    ");
        Console.ForegroundColor = ConsoleColor.Black;
        Console.BackgroundColor = ConsoleColor.DarkRed;
        Console.Write(" FAIL ");
        Console.ResetColor();
        Console.Write(" - expectation broken: wrong data / crash (codec bug)\n");
        Console.WriteLine();

        // Шапка таблицы.
        int headerTop = CursorTopSafe;
        Console.ForegroundColor = ConsoleColor.White;
        Console.BackgroundColor = ConsoleColor.DarkBlue;
        if (Console.IsOutputRedirected)
        {
            Console.WriteLine(HeaderLine);
        }
        else
        {
            // Строка ровно в ширину окна плюс перевод строки занимает две
            // строки экрана; выводим по явной позиции без перевода строки,
            // сохраняя хвостовую ячейку фона шапки.
            Console.Write(HeaderLine.Length > WindowWidthSafe
                ? HeaderLine.Substring(0, Math.Max(WindowWidthSafe, 1))
                : HeaderLine);
            MoveCursor(0, headerTop + 1);
        }

        // Запоминаем ширину таблицы для выравнивания строк статистики.
        _tableWidth = TableWidth;
        Console.ResetColor();
    }

    /// <summary>Строка таблицы результатов (общая для отрисовки и журнала).</summary>
    private static string RowText(TestResult r) => string.Format(RowFormat,
        r.N,
        SizeText(r.SizeBytes),
        r.Format,
        r.Scheme,
        r.Impl,
        r.Via,
        r.EccPercent,
        r.HeaderPercent,
        r.DataCount,
        r.EccCount,
        r.LostSectors,
        r.DamageMask,
        r.TotalSuccess,
        r.TotalFail,
        r.EncMBps,
        r.DecMBps,
        r.Status);

    /// <summary>Перерисовать строки таблицы с позиции topRow (цвет статуса).</summary>
    private static void DrawTable(IReadOnlyList<TestResult> buffer, int topRow)
    {
        HideCursor();
        MoveCursor(0, topRow);

        var redirected = Console.IsOutputRedirected;
        for (int i = 0; i < buffer.Count; i++)
        {
            var r = buffer[i];
            Console.ForegroundColor = ConsoleColor.Black;
            Console.BackgroundColor = r.Status switch
            {
                "PASS" => ConsoleColor.DarkGreen,
                "WARN" => ConsoleColor.DarkYellow,
                _ => ConsoleColor.DarkRed
            };

            var row = RowText(r);
            EnsureRowFits(row);

            if (redirected)
            {
                Console.WriteLine(row);
            }
            else
            {
                // Строка ровно в ширину окна плюс перевод строки занимает две
                // строки экрана; выводим по явной позиции без перевода строки,
                // сохраняя хвостовую ячейку фона статуса.
                MoveCursor(0, topRow + i);
                Console.Write(row.Length > WindowWidthSafe
                    ? row.Substring(0, Math.Max(WindowWidthSafe, 1))
                    : row);
            }

            Console.ResetColor();
        }
    }

    // Кольцевой буфер последних серий для отрисовки.
    /// <summary>Добавить результат в кольцевой буфер, вытесняя самый старый.</summary>
    private static void EnqueueResult(Queue<TestResult> q, TestResult r, int max)
    {
        if (q.Count == max)
            q.Dequeue();
        q.Enqueue(r);
    }

    // Строка средних скоростей под таблицей; перерисовывается каждый раз.
    /// <summary>Средние скорости кодирования/декодирования под таблицей.</summary>
    private static void DrawSpeedStats(double avgEnc, double avgDec, int topRow)
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
        string encStr = $" Average encoding speed: {avgEnc,6:F2} MB/s ";
        string decStr = $" Average decoding speed: {avgDec,6:F2} MB/s ";
        encStr = encStr.PadRight(_tableWidth).Substring(0, _tableWidth);
        decStr = decStr.PadRight(_tableWidth).Substring(0, _tableWidth);
        Console.Write(encStr);
        if (_tableWidth < winWidth)
        {
            Console.BackgroundColor = ConsoleColor.Black;
            Console.Write(new string(' ', winWidth - _tableWidth));
        }

        MoveCursor(0, topRow + 1);
        Console.BackgroundColor = ConsoleColor.DarkBlue;
        Console.Write(decStr);
        if (_tableWidth < winWidth)
        {
            Console.BackgroundColor = ConsoleColor.Black;
            Console.Write(new string(' ', winWidth - _tableWidth));
        }
        Console.ResetColor();
    }

    // ------------- Диагностический дамп неудачной итерации -------------

    /// <summary>
    /// Диагностический дамп неудачной одиночной итерации в
    /// %TEMP%\DataShield-logs\DataShield.Demo:
    /// статистика слотов, карты валидности и все повреждённые куски потока.
    /// </summary>
    private static void DumpFailure(
        int n, List<DecodeResult> results, DamageResult damage, EncodeStats stats,
        byte[]? restored, byte[] content)
    {
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "DataShield-logs", "DataShield.Demo");
            Directory.CreateDirectory(dir);

            var sb = new StringBuilder();
            sb.AppendLine($"iteration={n} mask=0x{damage.Mask:X6} lost={damage.LostSectors}");
            sb.AppendLine($"stats: data={stats.DataCount} ecc={stats.EccCount}");
            sb.AppendLine($"contentLen={content.Length} restored={(restored is null ? "null" : restored.Length.ToString())}");
            sb.AppendLine($"slots={results.Count}");

            for (var s = 0; s < results.Count; s++)
            {
                var slot = results[s].Slot;
                var map = slot.BuildValidityMap();
                var erasedData = 0;
                for (var i = 0; i < slot.DataVolumeCount; i++)
                    if (!map[i]) erasedData++;
                var eccAvail = 0;
                for (var i = slot.DataVolumeCount; i < map.Length; i++)
                    if (map[i]) eccAvail++;

                sb.AppendLine($"slot[{s}]: hdrRx={slot.HeaderReceptionCount} N={slot.DataVolumeCount} M={slot.EccCount} " +
                              $"rx={slot.ReceivedSectorCount} copies={slot.ReceivedSectorCopyCount} coll={slot.CollisionSectorCount} " +
                              $"cov={slot.Coverage:F1}% erasedData={erasedData} eccAvail={eccAvail}");
                sb.AppendLine($"map={slot.FormatValidityMap()}");
            }

            for (var i = 0; i < damage.Chunks.Count; i++)
            {
                var path = Path.Combine(dir, $"failchunk-{n}-{i}.bin");
                File.WriteAllBytes(path, damage.Chunks[i]);
                sb.AppendLine($"chunk[{i}]: format={damage.ChunkFormats[i]} len={damage.Chunks[i].Length} -> {path}");
            }

            File.WriteAllText(Path.Combine(dir, $"faildump-{n}.txt"), sb.ToString());
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"dump failed: {ex.Message}");
        }
    }

    // ------------- Диагностический дамп неудачной многофайловой итерации -------

    /// <summary>
    /// Диагностический дамп неудачной многофайловой итерации: сопоставление
    /// слотов ожидаемым файлам по содержимому, версии коллизионных секторов
    /// и куски потока.
    /// </summary>
    private static void DumpMultiFailure(
        int n, List<DecodeResult> results, DamageResult damage,
        IReadOnlyList<ScenarioFile> files)
    {
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "DataShield-logs", "DataShield.Demo");
            Directory.CreateDirectory(dir);

            var sb = new StringBuilder();
            sb.AppendLine($"iteration={n} mask=0x{damage.Mask:X6} lost={damage.LostSectors}");
            sb.AppendLine($"expectedFiles={files.Count} actualSlots={results.Count}");

            foreach (var f in files)
                sb.AppendLine($"expected[{f.Name}] len={f.Content.Length}");

            for (var s = 0; s < results.Count; s++)
            {
                var slot = results[s].Slot;
                var restored = results[s].Content;
                var map = slot.BuildValidityMap();
                var erasedData = 0;
                for (var i = 0; i < slot.DataVolumeCount; i++)
                    if (!map[i]) erasedData++;
                var eccAvail = 0;
                for (var i = slot.DataVolumeCount; i < map.Length; i++)
                    if (map[i]) eccAvail++;

                sb.AppendLine($"slot[{s}]: name={slot.Header.FileName} hdrRx={slot.HeaderReceptionCount} " +
                              $"N={slot.DataVolumeCount} M={slot.EccCount} rx={slot.ReceivedSectorCount} " +
                              $"copies={slot.ReceivedSectorCopyCount} coll={slot.CollisionSectorCount} " +
                              $"cov={slot.Coverage:F1}% erasedData={erasedData} eccAvail={eccAvail}");
                sb.AppendLine($"map={slot.FormatValidityMap()}");

                if (slot.CollisionSectorCount > 0)
                    for (var sec = 0; sec < slot.TotalVolumeCount; sec++)
                    {
                        var versions = slot.GetSectorVersions(sec);
                        if (versions.Count == 0) continue;
                        sb.AppendLine($"  sector[{sec}] versions: " +
                            string.Join(", ", versions.Select(v => v.ConfirmationCount)));
                    }

                if (restored is null)
                {
                    sb.AppendLine("  restored=null");
                    continue;
                }

                var file = files.FirstOrDefault(x =>
                    FileNameCodec.Pack(x.Name) == slot.Header.FileName &&
                    x.Content.AsSpan().SequenceEqual(restored));
                if (file is not null)
                {
                    sb.AppendLine($"  content OK ({file.Name})");
                    continue;
                }

                var byLength = files.FirstOrDefault(x =>
                    FileNameCodec.Pack(x.Name) == slot.Header.FileName &&
                    x.Content.Length == restored.Length);
                if (byLength is not null)
                    sb.AppendLine($"  content mismatch vs {byLength.Name}");
                else
                    sb.AppendLine($"  no matching expected file (len={restored.Length})");
            }

            for (var i = 0; i < damage.Chunks.Count; i++)
            {
                var path = Path.Combine(dir, $"mfailchunk-{n}-{i}.txt");
                File.WriteAllBytes(path, damage.Chunks[i]);
                sb.AppendLine($"chunk[{i}]: format={damage.ChunkFormats[i]} len={damage.Chunks[i].Length} -> {path}");
            }

            File.WriteAllText(Path.Combine(dir, $"mfaildump-{n}.txt"), sb.ToString());
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"dump failed: {ex.Message}");
        }
    }

    // ------------- Вспомогательные -------------

    /// <summary>Человекочитаемый размер: B / KB / MB.</summary>
    private static string SizeText(int size) =>
        size >= 1 << 20 ? $"{size / (double)(1 << 20):F2}MB"
        : size >= 1024 ? $"{size / 1024.0:F1}KB"
        : $"{size}B";

    /// <summary>
    /// Добавить тег использованной реализации контракта в строку колонки Api
    /// (порядок сохраняется, дубликаты сжимаются: A, C, AC).
    /// </summary>
    private static string AppendTag(string tags, char tag) =>
        tags.Contains(tag) ? tags : tags + tag;

    // ------------- Одна итерация: кодирование → повреждения → декодирование -------------

    /// <summary>Результат итерации плюс тайминги, объём и метки для ротации сида.</summary>
    private sealed record IterationOutcome(
        TestResult Result, double EncSeconds, double DecSeconds, int ContentBytes,
        int ScenarioClass, string EncVias, char DecVia);

    /// <summary>
    /// Одна итерация: план сценария (см. <see cref="ScenarioEngine.RollPlan"/>)
    /// → кодирование → повреждения (бюджет/сверх бюджета) → декодирование →
    /// проверка ожидания. Все операции — через случайно созданные экземпляры
    /// контракта.
    /// </summary>
    private static IterationOutcome RunIteration(int n, Random rng)
    {
        var plan = ScenarioEngine.RollPlan(n, rng);
        return plan.IsMulti
            ? RunMultiFile(n, rng, plan)
            : RunSingleFile(n, rng, plan);
    }

    /// <summary>
    /// Одиночная итерация: кодирование случайной перегрузкой, повреждения
    /// в бюджете или сверх бюджета, сканирование кусков и сборка; проверка
    /// ожидания (точное восстановление или чистый отказ в стрессе).
    /// </summary>
    private static IterationOutcome RunSingleFile(int n, Random rng, ScenarioPlan plan)
    {
        var file = plan.Files[0];
        var content = file.Content;
        var impl = "";
        char encVia;

        // Кодирование (замеряемое): случайная реализация контракта и перегрузка
        var (encoder, encTag) = CodecVariants.CreateCodec(
            file.EccPercent, file.HeaderPercent, file.Scheme, file.SectorLimit, rng);
        impl = AppendTag(impl, encTag);

        List<byte[]> packets;
        EncodeStats stats;
        double encSeconds;

        using (encoder)
        {
            var sw = Stopwatch.StartNew();
            (packets, stats, encVia) = CodecVariants.EncodeVariants(
                encoder, content, file.Name, file.EccPercent, rng);
            sw.Stop();
            encSeconds = sw.Elapsed.TotalSeconds;
        }

        // Повреждения: комбинированные в бюджете или сверх бюджета (план);
        // коллизии версий — адверсариальный случай, учитываемый проверкой
        // итогового стресса
        var damage = ScenarioEngine.ApplyDamage(plan, [(packets, stats)], rng);
        var stress = ScenarioEngine.IsStressOutcome(plan, damage);

        // Декодирование: случайная реализация контракта; схема приёмника
        // соответствует схеме файла: для B — гибридный приём (флаг
        // VirtualIndex, классический приём проверяется первым), для A —
        // обычный классический приём.
        var (decoder, decTag) = CodecVariants.CreateCodec(
            file.EccPercent, file.HeaderPercent, file.Scheme, file.SectorLimit, rng);
        impl = AppendTag(impl, decTag);

        byte[]? restored;
        char decVia;
        double decSeconds;

        using (decoder)
        {
            var sw = Stopwatch.StartNew();
            (restored, decVia) = CodecVariants.DecodeVariants(decoder, damage, rng);
            sw.Stop();
            decSeconds = sw.Elapsed.TotalSeconds;
        }

        // Ожидание: обычный режим — точное восстановление;
        // стресс — чистый отказ (null) или точное восстановление
        var passed = ScenarioEngine.MeetsExpectation(plan, damage, restored, content);

        string? hint = null;
        if (!passed && !stress)
        {
            // Диагностический проход: слоты приёма для дампа (основная
            // перегрузка могла не возвращать список результатов)
            using var diagCodec = new DataShieldCodec(
                file.EccPercent, file.HeaderPercent, file.Scheme, file.SectorLimit);
            DumpFailure(n, CodecVariants.DecodeChunks(diagCodec, damage), damage, stats, restored, content);
            if (restored is null)
                hint = "restored null";
            else if (restored.Length != content.Length)
                hint = $"length mismatch {restored.Length} vs {content.Length}";
            else
            {
                var idx = restored.AsSpan().IndexOfAnyExcept(content.AsSpan());
                hint = $"mismatch at byte {idx}";
            }
        }

        return new IterationOutcome(
            new TestResult
            {
                N = n,
                SizeBytes = content.Length,
                Format = plan.Format == OutputFormat.Base64 ? "txt" : "bin",
                Scheme = file.Scheme == SectorScheme.VirtualIndex ? "B" : "A",
                Impl = impl,
                Via = $"{encVia}/{decVia}",
                EccPercent = file.EccPercent,
                HeaderPercent = file.HeaderPercent,
                DataCount = stats.DataCount,
                EccCount = stats.EccCount,
                LostSectors = damage.LostSectors,
                DamageMask = damage.Mask,
                Passed = passed,
                Stress = stress,
                FailureHint = hint
            },
            encSeconds, decSeconds, content.Length,
            SeedRotator.SingleClass(
                stress, plan.Format == OutputFormat.Binary,
                file.Scheme == SectorScheme.VirtualIndex, file.EccPercent, file.HeaderPercent),
            encVia.ToString(), decVia);
    }

    /// <summary>
    /// Многофайловая итерация: 2–3 файла обеих схем в общем потоке —
    /// перемешанном повреждённом либо чистой последовательной склейке
    /// FEC-потоков (план сценария); один декодер; проверка — каждый
    /// различимый файл собран точно (неразличимые заголовки сливаются в
    /// один слот — это ожидаемо). Все операции — через случайно созданные
    /// экземпляры контракта.
    /// </summary>
    private static IterationOutcome RunMultiFile(int n, Random rng, ScenarioPlan plan)
    {
        var files = plan.Files;
        var fileCount = files.Count;
        var encoded = new List<(IReadOnlyList<byte[]> Packets, EncodeStats Stats)>(fileCount);
        var impl = "";

        var sw = Stopwatch.StartNew();
        var eccSum = 0;
        var headerSum = 0;
        var via = "";
        for (var f = 0; f < fileCount; f++)
        {
            var file = files[f];
            var (encoder, encTag) = CodecVariants.CreateCodec(
                file.EccPercent, file.HeaderPercent, file.Scheme, file.SectorLimit, rng);
            impl = AppendTag(impl, encTag);

            using (encoder)
            {
                var (packets, stats, viaFile) = CodecVariants.EncodeVariants(
                    encoder, file.Content, file.Name, file.EccPercent, rng);
                via = AppendTag(via, viaFile);
                encoded.Add((packets, stats));
                eccSum += (int)Math.Round(
                    stats.EccCount * 100.0 / Math.Max(1, stats.DataCount));
                headerSum += file.HeaderPercent;
            }
        }
        sw.Stop();
        double encSeconds = sw.Elapsed.TotalSeconds;

        // Поток по плану сценария: перемешанный повреждённый либо (как
        // консольный mlt-сценарий) чистая последовательная склейка
        // FEC-потоков — приём одним декодером, основной сценарий
        // сосуществования схем. Если в потоке
        // есть файл B, декодер гибридный (флаг VirtualIndex читает схемы
        // A и B одновременно); поток из одних A принимает классический
        // приёмник.
        var damage = ScenarioEngine.ApplyDamage(plan, encoded, rng);

        var anyVirtual = files.Any(f => f.Scheme == SectorScheme.VirtualIndex);
        var (decoder, decTag) = CodecVariants.CreateCodec(
            1, 1,
            anyVirtual ? SectorScheme.VirtualIndex : SectorScheme.Classic,
            VirtualIndexHasher.DefaultSectorLimit,
            rng);
        impl = AppendTag(impl, decTag);

        List<DecodeResult> results;
        sw.Restart();
        using (decoder)
        {
            results = CodecVariants.DecodeChunks(decoder, damage);
        }
        sw.Stop();
        double decSeconds = sw.Elapsed.TotalSeconds;

        // Каждый файл должен собраться точно; порядок слотов произволен, а
        // упакованные в 14 байт имена у всех файлов итерации совпадают, так
        // что слоты сопоставляются ожидаемым файлам по содержимому. Файлы
        // с неразличимыми заголовками (равные упакованное имя, размер,
        // SHA-256 и число ECC-томов — например, два пустых файла с равным
        // ECC) в потоке неразделимы и сливаются в один слот: такой класс
        // эквивалентности ожидается ровно одним слотом.
        // Исключение — коллизии версий (подделка с корректным хешем может
        // обгонять верную версию по подтверждениям после дублирования
        // строк): для такого слота допустим чистый отказ сборки, как и для
        // одиночного файла с Collision-повреждением.
        bool stress = ScenarioEngine.IsStressOutcome(plan, damage);

        var pending = new List<byte[]>();
        var seenHeaders = new HashSet<string>();
        for (var f = 0; f < files.Count; f++)
        {
            var headerKey = string.Join('|',
                FileNameCodec.Pack(files[f].Name),
                files[f].Content.Length,
                Convert.ToHexString(SHA256.HashData(files[f].Content)),
                encoded[f].Stats.EccCount);
            if (seenHeaders.Add(headerKey))
                pending.Add(files[f].Content);
        }

        bool passed = results.Count == pending.Count;
        if (passed)
        {
            var unverifiable = 0;
            foreach (var result in results)
            {
                var restored = result.Content;
                if (restored is null)
                {
                    if (result.Slot.CollisionSectorCount == 0)
                    {
                        passed = false;
                        break;
                    }

                    unverifiable++;
                    continue;
                }

                var match = pending.FindIndex(c => c.AsSpan().SequenceEqual(restored));
                if (match < 0)
                {
                    passed = false;
                    break;
                }
                pending.RemoveAt(match);
            }

            if (passed && pending.Count != unverifiable)
                passed = false;
        }

        if (!passed)
            DumpMultiFailure(n, results, damage, files);

        int totalSize = files.Sum(f => f.Content.Length);
        int dataCount = encoded.Sum(e => e.Stats.DataCount);
        int eccCount = encoded.Sum(e => e.Stats.EccCount);
        var avgEcc = eccSum / fileCount;
        var avgHeader = headerSum / fileCount;

        // Схема строки: A, B или AB/BA — какие схемы попали в смешанный поток.
        var schemeText = string.Concat(files
            .Select(f => f.Scheme == SectorScheme.VirtualIndex ? "B" : "A")
            .Distinct());

        // Combo схем для класса сценария: 0 = только A, 1 = только B, 2 = смесь.
        var schemeCombo = schemeText.Length == 2 ? 2 : schemeText == "B" ? 1 : 0;

        return new IterationOutcome(
            new TestResult
            {
                N = n,
                SizeBytes = totalSize,
                Format = "mlt",
                Scheme = schemeText,
                Impl = impl,
                Via = $"{via}/A",
                EccPercent = avgEcc,
                HeaderPercent = avgHeader,
                DataCount = dataCount,
                EccCount = eccCount,
                LostSectors = damage.LostSectors,
                DamageMask = damage.Mask,
                Passed = passed,
                Stress = stress
            },
            encSeconds, decSeconds, totalSize,
            SeedRotator.MultiClass(schemeCombo, avgEcc, avgHeader),
            via, 'A');
    }

    // ------------- Основной цикл стенда -------------

    /// <summary>
    /// Основной цикл: бесконечные итерации с перерисовкой таблицы;
    /// исключение итерации — строка FAIL без остановки, сломанное
    /// ожидание — остановка (рестарт нецелесообразен: после разбора ошибки
    /// код меняется). Начальный сид задаётся аргументом командной строки
    /// (dotnet run -- seed) для воспроизведения; далее сид ротируется при
    /// исчерпании/застое разнообразия либо раз в час (см.
    /// <see cref="SeedRotator"/>). Интегральная статистика сбрасывается в
    /// журнал раз в 15 минут; события ротации и отказы журналируются;
    /// сбои записи журнала подавляются (см. <see cref="StandLog"/>).
    /// </summary>
    private static void Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        EnsureConsoleSize(TableWidth);
        _lastEnsuredWidth = TableWidth;
        HideCursor();
        ClearScreen();

        var results = new Queue<TestResult>(HistorySize);
        DrawCodecInfo();
        DrawHeader();
        int tableDataTop = CursorTopSafe; // вершина таблицы данных

        // Точная высота окна: шапка + таблица + статистика + строка сида.
        EnsureConsoleSize(_lastEnsuredWidth, tableDataTop + HistorySize + 5);

        // Сид: из командной строки (dotnet run -- <seed>) или случайный.
        uint initialSeed = args.Length > 0 && uint.TryParse(args[0], out var parsed)
            ? parsed
            : (uint)Environment.TickCount64;

        // Плановый интервал ротации и порог покрытия (для ускоренной проверки
        // ротации стенда; по умолчанию — 1 час и 3 покрытия каждого класса).
        TimeSpan? interval = args.Length > 1 && int.TryParse(args[1], out var seconds)
            ? TimeSpan.FromSeconds(Math.Max(1, seconds))
            : null;
        int coverageTarget = args.Length > 2 && int.TryParse(args[2], out var target)
            ? Math.Max(1, target)
            : 3;
        var logInterval = args.Length > 3 && int.TryParse(args[3], out var logSeconds)
            ? TimeSpan.FromSeconds(Math.Max(1, logSeconds))
            : StandLog.Interval;

        var rotator = new SeedRotator(initialSeed, interval, coverageTarget);
        var rng = new Random(unchecked((int)rotator.Seed));

        // Строка сида под таблицей: текущий сид и причина последней ротации.
        void DrawSeedLine()
        {
            MoveCursor(0, tableDataTop + HistorySize + 3);
            Console.ForegroundColor = ConsoleColor.DarkGray;
            var line = rotator.Rotations == 0
                ? $" random seed = {rotator.Seed}"
                : $" random seed = {rotator.Seed} (rotated x{rotator.Rotations}: {rotator.LastReason})";
            Console.Write(line.PadRight(WindowWidthSafe - 1) + "\n");
            Console.ResetColor();
        }

        DrawSeedLine();

        StandLog.Write(
        [
            $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} stand started: initial seed = {initialSeed}, interval = {interval ?? TimeSpan.FromHours(1)}, coverage target = {coverageTarget}",
        ]);

        int n = 0;
        int totalSuccess = 0;
        int totalFail = 0;
        // Накопители средних скоростей.
        double totalEncSpeed = 0.0;
        double totalDecSpeed = 0.0;
        int iterationCount = 0;
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
                // Исключение внутри итерации — баг кодека: строка FAIL,
                // стенд продолжает работу
                Console.Error.WriteLine($"[iter {n}] EXCEPTION: {ex}");
                outcome = new IterationOutcome(
                    new TestResult
                    {
                        N = n,
                        SizeBytes = 0,
                        Format = "err",
                        EccPercent = 0,
                        Passed = false,
                        Stress = false
                    },
                    0, 0, 0, 0, "", ' ');
            }

            var result = outcome.Result;
            if (result.Passed) totalSuccess++;
            else totalFail++;

            double sizeMB = outcome.ContentBytes / (1024.0 * 1024.0);
            result.TotalSuccess = totalSuccess;
            result.TotalFail = totalFail;
            result.EncMBps = outcome.EncSeconds > 0 ? sizeMB / outcome.EncSeconds : 0.0;
            result.DecMBps = outcome.DecSeconds > 0 ? sizeMB / outcome.DecSeconds : 0.0;

            totalEncSpeed += result.EncMBps;
            totalDecSpeed += result.DecMBps;
            iterationCount++;

            EnqueueResult(results, result, HistorySize);

            // Перерисовываем таблицу и строку средних скоростей.
            DrawTable(results.ToList(), tableDataTop);
            DrawSpeedStats(totalEncSpeed / iterationCount, totalDecSpeed / iterationCount,
                tableDataTop + HistorySize);

            // Ротация сида: исчерпание/застой разнообразия либо плановый
            // интервал (по умолчанию — раз в час).
            if (rotator.Note(
                    outcome.ScenarioClass, outcome.EncVias, outcome.DecVia,
                    out var newSeed, out var reason))
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

            // Интегральный сброс статистики в журнал — по расписанию
            // (по умолчанию раз в 15 минут).
            if (DateTimeOffset.UtcNow - lastIntegralLog >= logInterval)
            {
                lastIntegralLog = DateTimeOffset.UtcNow;
                WriteIntegralLog(n, rotator, initialSeed,
                    totalSuccess, totalFail,
                    totalEncSpeed / iterationCount, totalDecSpeed / iterationCount,
                    results);
            }

            // FAIL — остановка стенда (рестарт нецелесообразен); итог — в журнал.
            if (!result.Passed)
            {
                WriteIntegralLog(n, rotator, initialSeed,
                    totalSuccess, totalFail,
                    totalEncSpeed / iterationCount, totalDecSpeed / iterationCount,
                    results,
                    footer: $"TEST STOPPED: iteration {n} broke the expectation. " +
                            $"Seed = {rotator.Seed} (initial {initialSeed}, rotations {rotator.Rotations}). " +
                            $"Hint: {result.FailureHint ?? "n/a"}.");

                Console.ForegroundColor = ConsoleColor.Black;
                Console.BackgroundColor = ConsoleColor.DarkRed;
                Console.WriteLine($" TEST STOPPED: iteration {n} broke the expectation. Seed = {rotator.Seed} (initial {initialSeed}, rotations {rotator.Rotations}). Hint: {result.FailureHint ?? "n/a"}. Press any key to exit... ");
                Console.ResetColor();
                ShowCursor();
                if (Console.IsInputRedirected)
                    Console.ReadLine();
                else
                    Console.ReadKey(intercept: true);
                return;
            }
        }
    }

    /// <summary>
    /// Интегральный сброс статистики в журнал: момент, сид и покрытие,
    /// накопленные итоги, средние скорости и снимок последних итераций.
    /// </summary>
    private static void WriteIntegralLog(
        int n, SeedRotator rotator, uint initialSeed,
        int totalSuccess, int totalFail,
        double avgEnc, double avgDec,
        Queue<TestResult> history,
        string? footer = null)
    {
        var lines = new List<string>
        {
            $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} integral stats: iteration {n}, " +
            $"pass {totalSuccess}, fail {totalFail}, " +
            $"avg enc {avgEnc:F2} MB/s, avg dec {avgDec:F2} MB/s",
            $"  seed = {rotator.Seed} (initial {initialSeed}, rotations {rotator.Rotations}, " +
            $"coverage {rotator.CoverageFraction:P0})",
            "  recent iterations:",
        };
        lines.AddRange(history.Select(RowText));
        if (footer is not null)
            lines.Add("  " + footer);

        StandLog.Write(lines);
    }
}
