using System.Diagnostics;

internal static class Program
{
    // Геометрия массива: общее число томов (Data + ECC).
    private const int MinVolumes = 2048;
    private const int MaxVolumes = 4096;

    // Ширина таблицы результатов; вычисляется в DrawHeader.
    private static int _tableWidth;

    /// <summary>Итоги одной серии (фиксированные DataCount/EccCount, много итераций).</summary>
    private sealed class TestResult
    {
        public int N { get; set; }             // номер серии
        public int BitMask { get; set; }       // битовая маска символа (0xFFFF)
        public int DataCount { get; set; }     // число информационных блоков
        public int EccCount { get; set; }      // число проверочных блоков
        public int DamageCount { get; set; }   // число повреждённых блоков в серии
        public int Iterations { get; set; }    // итераций в серии
        public int Success { get; set; }       // успешных декодирований
        public int Fail { get; set; }          // неудач
        public int TotalSuccess { get; set; }  // накопленный Success
        public int TotalFail { get; set; }     // накопленный Fail
        public double EncMBps { get; set; }    // скорость кодирования
        public double DecMBps { get; set; }    // скорость декодирования
        public int MaxErrDataBlocks { get; set; }   // макс. повреждённых data-блоков за серию
        public bool TooManyErrorsFlag { get; set; } // фатальная ошибка "слишком много стираний"

        // Серия пройдена: нет неудач и фатальных ошибок.
        public bool Passed => Fail == 0 && !TooManyErrorsFlag;

        // Режим в пределах корректирующей способности: фаталов нет и
        // хоть что-то успешно декодировано.
        public bool WithinCapability => !TooManyErrorsFlag && Success > 0;

        public string Status =>
            Passed && WithinCapability ? "PASS" :
            !Passed && !WithinCapability ? "WARN" : "FAIL";
    }

    // ------------- Обёртки над консольными манипуляциями -------------
    // CursorVisible/SetCursorPosition/WindowWidth бросают IOException при
    // перенаправленном выводе (нет консольного дескриптора) — например, при
    // запуске в пайпе или CI. Обёртки делают эти операции необязательными:
    // в интерактивной консоли таблица рисуется на месте, при перенаправлении
    // строки просто печатаются потоком.

    private static void HideCursor()
    {
        if (!Console.IsOutputRedirected)
            Console.CursorVisible = false;
    }

    private static void MoveCursor(int left, int top)
    {
        if (!Console.IsOutputRedirected)
            Console.SetCursorPosition(left, top);
    }

    private static int WindowWidthSafe => Console.IsOutputRedirected ? 120 : Console.WindowWidth;

    private static int CursorTopSafe => Console.IsOutputRedirected ? 0 : Console.CursorTop;

    private static void ClearScreen()
    {
        if (!Console.IsOutputRedirected)
            Console.Clear();
    }

    // ------------- Параметры кодека по количеству томов -------------

    // Минимум и максимум томов данных и ECC, выводимые из констант:
    // ecc >= 1 => data <= total - 1; ecc <= data => data >= total / 2.
    private static void DrawCodecInfo()
    {
        int dataMin = MinVolumes;
        int dataMax = MaxVolumes;
        int eccMin = 1;
        int eccMax = MaxVolumes;

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(" RS-RAID STABILITY AND PERFORMANCE TEST (RsRaid16, GF(2^16))");
        Console.ResetColor();

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine();
        Console.Write(" Codec: GF(2^16), 16-bit symbols, Cauchy matrix, O(n*m) | ");
        Console.ForegroundColor = ConsoleColor.Gray;
        Console.WriteLine($"Data: {dataMin}..{dataMax} | Ecc: {eccMin}..{eccMax} | Total: {MinVolumes}..{MaxVolumes}");
        Console.ResetColor();
    }

    // ------------- Отрисовка: шапка и таблица -------------

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
        Console.Write(" - no fails, no fatal errors\n");

        Console.Write("    ");
        Console.ForegroundColor = ConsoleColor.Black;
        Console.BackgroundColor = ConsoleColor.DarkYellow;
        Console.Write(" WARN ");
        Console.ResetColor();
        Console.Write(" - some fails / regime stress (but not fatal)\n");

        Console.Write("    ");
        Console.ForegroundColor = ConsoleColor.Black;
        Console.BackgroundColor = ConsoleColor.DarkRed;
        Console.Write(" FAIL ");
        Console.ResetColor();
        Console.Write(" - fatal condition (too many errors etc.)\n");
        Console.WriteLine();

        // Шапка таблицы.
        Console.ForegroundColor = ConsoleColor.White;
        Console.BackgroundColor = ConsoleColor.DarkBlue;
        string headerLine = string.Format(
            " {0,8} | {1,6} | {2,9} | {3,5} | {4,5} | {5,5} | {6,5} | {7,10} | {8,10} | {9,8} | {10,8} | {11,5} ",
            "N", "Mask", "Data/Ecc", "Damg", "Iter", "Succ", "Fail",
            "Total.Succ", "Total.Fail", "EncMBps", "DecMBps", "Stat");
        Console.WriteLine(headerLine);

        // Запоминаем ширину таблицы для выравнивания строк статистики.
        _tableWidth = headerLine.Length;
        Console.ResetColor();
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.ResetColor();
    }

    private static void DrawTable(IReadOnlyList<TestResult> buffer, int topRow)
    {
        HideCursor();
        MoveCursor(0, topRow);

        foreach (var r in buffer)
        {
            ConsoleColor fg = ConsoleColor.Black;
            ConsoleColor bg = r.Passed && r.WithinCapability ? ConsoleColor.DarkGreen
                             : !r.Passed && !r.WithinCapability ? ConsoleColor.DarkYellow
                             : ConsoleColor.DarkRed;

            Console.ForegroundColor = fg;
            Console.BackgroundColor = bg;

            Console.WriteLine(
                " {0,8} | {1,6} | {2,4}/{3,-4} | {4,5} | {5,5} | {6,5} | {7,5} | {8,10} | {9,10} | {10,8:F2} | {11,8:F2} | {12,5} ",
                r.N,
                $"0x{r.BitMask:X2}",
                r.DataCount,
                r.EccCount,
                r.DamageCount,
                r.Iterations,
                r.Success,
                r.Fail,
                r.TotalSuccess,
                r.TotalFail,
                r.EncMBps,
                r.DecMBps,
                r.Status);

            Console.ResetColor();
        }
    }

    // Кольцевой буфер последних max серий для отрисовки.
    private static void EnqueueResult(Queue<TestResult> q, TestResult r, int max)
    {
        if (q.Count == max)
            q.Dequeue();
        q.Enqueue(r);
    }

    // Строка средних скоростей под таблицей; перерисовывается каждый раз.
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

    // ------------- Основной цикл стенда -------------

    private static void Main(string[] args)
    {
        // Режим «bench [--csv]»: детерминированная сетка калибровки декодера
        // вместо интерактивного стенда.
        if (args.Length > 0 &&
            args[0].Equals("bench", StringComparison.OrdinalIgnoreCase))
        {
            Bench.Run(csv: args.Skip(1).Contains("--csv", StringComparer.OrdinalIgnoreCase));
            return;
        }

        const int bitMask = 0xFFFF;  // 16-битные символы данных (GF(2^16)
        const int iterations = 1;    // итераций в каждой серии

        Console.OutputEncoding = System.Text.Encoding.UTF8;
        HideCursor();
        ClearScreen();

        const int historySize = 15;
        var results = new Queue<TestResult>(historySize);
        DrawCodecInfo();
        DrawHeader();
        int tableDataTop = CursorTopSafe; // вершина таблицы данных

        int n = 0;
        int totalSuccess = 0;
        int totalFail = 0;
        // Накопители средних скоростей.
        double totalEncSpeed = 0.0;
        double totalDecSpeed = 0.0;
        int seriesCount = 0;

        // Сид печатается — прогон можно воспроизвести.
        uint seed = (uint)Environment.TickCount64;
        MoveCursor(0, tableDataTop + historySize + 3);
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($" random seed = {seed}");
        Console.ResetColor();

        var rng = new Mt19937(seed);

        while (true)
        {
            // Случайные параметры серии: общее число томов из [Min, Max],
            // затем делёж на данные/ECC с соблюдением ecc <= data*factor.
            int totalCount = MinVolumes + (int)(rng.Genrand() % (uint)(MaxVolumes - MinVolumes + 1));
            int dataMin = (totalCount + 1) / 2;          // ecc <= data => data >= total/2
            int dataMax = totalCount - 1;                // ecc >= 1
            int dataCount = dataMin + (int)(rng.Genrand() % (uint)(dataMax - dataMin + 1));
            int eccCount = totalCount - dataCount;

            // Число повреждённых блоков: строго меньше числа проверочных,
            // поэтому восстановление заведомо возможно.
            int damageCount = (int)(rng.Genrand() % (uint)eccCount);
            if (damageCount > eccCount)
                throw new Exception("DamageCount > EccCount");

            // Кодек — на серию
            var rs = new RsRaid16();
            if (!rs.Init(dataCount, eccCount, null))
                throw new Exception("RsRaid Init (encode) failed");

            var data = new int[totalCount];
            var savedData = new int[dataCount];
            var blockFound = new bool[totalCount];

            // Счётчики серии.
            int success = 0;
            int fail = 0;
            int maxErrDataBlocks = 0;
            bool tooManyErrorsFlag = false;

            var sw = new Stopwatch();
            double encodingTime = 0.0;
            double decodingTime = 0.0;
            long bytesProcessedByEncoder = 0;
            long bytesProcessedByDecoder = 0;

            // Итерации серии.
            for (int k = 0; k < iterations; k++)
            {
                int errCount = 0;

                // Новые данные итерации; сохраняем копию для сверки.
                for (int i = 0; i < dataCount; i++)
                    savedData[i] = data[i] = (int)(rng.Genrand() & bitMask);

                // Кодирование (замеряемое).
                sw.Restart();
                rs.Process(data);
                sw.Stop();
                encodingTime += sw.Elapsed.TotalSeconds;
                bytesProcessedByEncoder += dataCount * 2L;

                // Изначально все блоки живы.
                for (int i = 0; i < totalCount; i++)
                    blockFound[i] = true;

                // Повреждаем damageCount случайных блоков (стирания).
                for (int i = 0; i < damageCount; i++)
                {
                    int pos = (int)(rng.Genrand() % (uint)totalCount);
                    if (!blockFound[pos])
                    {
                        i--;
                        continue;
                    }

                    blockFound[pos] = false;
                    if (pos < dataCount)
                        errCount++;
                }

                if (errCount > maxErrDataBlocks)
                    maxErrDataBlocks = errCount;

                // Порча содержимого потерянных блоков (как в оригинале):
                // декодер эти блоки игнорирует, порча имитирует мусор на диске.
                for (int i = 0; i < totalCount; i++)
                    if (!blockFound[i])
                        data[i] ^= 0xFFFF;

                // Подготовка декодера (в тайминг не входит — как rs.Init
                // в оригинале): карта валидности + построение/инверсия матрицы
                var rsDec = new RsRaid16();
                bool ok = rsDec.Init(dataCount, eccCount, blockFound);
                if (!ok)
                {
                    // Стираний больше корректирующей способности — фатально.
                    // При damageCount < eccCount недостижимо, оставлено как защита.
                    tooManyErrorsFlag = true;
                    fail++;
                    break;
                }

                // Декодирование (замеряемое).
                sw.Restart();
                rsDec.Process(data);
                sw.Stop();
                decodingTime += sw.Elapsed.TotalSeconds;
                bytesProcessedByDecoder += dataCount * 2L;

                // Сверка результата: восстановлены должны быть все data-блоки.
                int errFound = 0;
                for (int i = 0; i < dataCount; i++)
                    if (data[i] != savedData[i])
                        errFound++;

                if (errFound != 0)
                    fail++;
                else
                    success++;
            }

            // Обновляем накопители по сериям.
            totalSuccess += success;
            totalFail += fail;

            double encMB = bytesProcessedByEncoder / (1024.0 * 1024.0);
            double decMB = bytesProcessedByDecoder / (1024.0 * 1024.0);
            double encSpeed = encodingTime > 0 ? encMB / encodingTime : 0.0;
            double decSpeed = decodingTime > 0 ? decMB / decodingTime : 0.0;

            var result = new TestResult
            {
                N = ++n,
                BitMask = bitMask,
                DataCount = dataCount,
                EccCount = eccCount,
                DamageCount = damageCount,
                Iterations = iterations,
                Success = success,
                Fail = fail,
                TotalSuccess = totalSuccess,
                TotalFail = totalFail,
                EncMBps = encSpeed,
                DecMBps = decSpeed,
                MaxErrDataBlocks = maxErrDataBlocks,
                TooManyErrorsFlag = tooManyErrorsFlag
            };

            totalEncSpeed += encSpeed;
            totalDecSpeed += decSpeed;
            seriesCount++;

            EnqueueResult(results, result, historySize);

            // Перерисовываем таблицу и строку средних скоростей.
            DrawTable(results.ToList(), tableDataTop);
            DrawSpeedStats(totalEncSpeed / seriesCount, totalDecSpeed / seriesCount,
                tableDataTop + historySize);

            // Курсор — под таблицей, чтобы не мешать.
            MoveCursor(0, tableDataTop + historySize + 3);
        }
    }
}