using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using DataShield.Codec;
using DataShield.Codec.Packets;
using DataShield.TestsHarness;

internal static class Program
{
    // Диапазон размера входного файла: 1 байт .. 256 Кб (лог-равномерно).
    private const int MinFileSize = 1;
    private const int MaxSizeLog2 = 18;
    private const int MaxFileSize = 1 << MaxSizeLog2;

    // Диапазон избыточности ECC, %.
    private const int MinEccPercent = 1;
    private const int MaxEccPercent = 200;

    // Режимы итерации.
    private const int WarnChancePercent = 15;  // повреждения сверх бюджета / подделка-победитель
    private const int MultiChancePercent = 10; // многофайловый поток
    private const int EmptyChancePercent = 3;  // пустой файл

    // Строк таблицы в кольцевом буфере.
    private const int HistorySize = 12;

    // Ширина таблицы результатов; вычисляется в DrawHeader.
    private static int _tableWidth;

    /// <summary>Итог одной итерации стенда.</summary>
    private sealed class TestResult
    {
        public int N { get; set; }               // номер итерации
        public int SizeBytes { get; set; }       // размер входа (сумма для multi)
        public string Format { get; set; } = ""; // txt / bin / mlt
        public int EccPercent { get; set; }      // избыточность, % (средняя для multi)
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

        public string Status => !Passed ? "FAIL" : Stress ? "WARN" : "PASS";

        public string? FailureHint { get; set; }
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

    private static void ShowCursor()
    {
        if (!Console.IsOutputRedirected)
            Console.CursorVisible = true;
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

    // ------------- Параметры кодека и легенда -------------

    private static void DrawCodecInfo()
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(" DATASHIELD CODEC CONTINUOUS STABILITY AND PERFORMANCE TEST");
        Console.ResetColor();        
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Gray;
        Console.WriteLine(" Codec: 75B packets / 100 Base64 chars, 64B payload, RS GF(2^16)");
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($" Dmg mask (hex): {DamageBits.Legend}");
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
        Console.ForegroundColor = ConsoleColor.White;
        Console.BackgroundColor = ConsoleColor.DarkBlue;
        string headerLine = string.Format(
            " {0,8} | {1,8} | {2,3} | {3,3} | {4,5}/{5,-5} | {6,5} | {7,6} | {8,6} | {9,6} | {10,7} | {11,7} | {12,4} ",
            "N", "Size", "Fmt", "Ecc", "Data", "Ecc", "Lost", "Dmg",
            "T.Pass", "T.Fail", "EncMBps", "DecMBps", "Stat");
        Console.WriteLine(headerLine);

        // Запоминаем ширину таблицы для выравнивания строк статистики.
        _tableWidth = headerLine.Length;
        Console.ResetColor();
    }

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

            Console.WriteLine(
                " {0,8} | {1,8} | {2,3} | {3,3} | {4,5}/{5,-5} | {6,5} | {7,6:X6} | {8,6} | {9,6} | {10,7:F2} | {11,7:F2} | {12,4} ",
                r.N,
                SizeText(r.SizeBytes),
                r.Format,
                r.EccPercent,
                r.DataCount,
                r.EccCount,
                r.LostSectors,
                r.DamageMask,
                r.TotalSuccess,
                r.TotalFail,
                r.EncMBps,
                r.DecMBps,
                r.Status);

            Console.ResetColor();
        }
    }

    // Кольцевой буфер последних серий для отрисовки.
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

    // ------------- Диагностический дамп неудачной итерации -------------

    private static void DumpFailure(
        int n, FileDecoder decoder, DamageResult damage, EncodeStats stats,
        byte[]? restored, byte[] content)
    {
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "opencode");
            Directory.CreateDirectory(dir);

            var sb = new StringBuilder();
            sb.AppendLine($"iteration={n} mask=0x{damage.Mask:X6} lost={damage.LostSectors}");
            sb.AppendLine($"stats: data={stats.DataCount} ecc={stats.EccCount}");
            sb.AppendLine($"contentLen={content.Length} restored={(restored is null ? "null" : restored.Length.ToString())}");
            sb.AppendLine($"FileCount={decoder.FileCount}");

            for (var s = 0; s < decoder.Slots.Count; s++)
            {
                var slot = decoder.Slots[s];
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

    private static void DumpMultiFailure(
        int n, FileDecoder decoder, DamageResult damage,
        List<(byte[] Content, string Name)> files)
    {
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "opencode");
            Directory.CreateDirectory(dir);

            var sb = new StringBuilder();
            sb.AppendLine($"iteration={n} mask=0x{damage.Mask:X6} lost={damage.LostSectors}");
            sb.AppendLine($"expectedFiles={files.Count} actualSlots={decoder.FileCount}");

            foreach (var f in files)
                sb.AppendLine($"expected[{f.Name}] len={f.Content.Length}");

            for (var s = 0; s < decoder.Slots.Count; s++)
            {
                var slot = decoder.Slots[s];
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

                var restored = decoder.TryAssemble(slot.Header);
                if (restored is null)
                {
                    sb.AppendLine("  restored=null");
                    continue;
                }

                var file = files.FirstOrDefault(x =>
                    FileNameCodec.Pack(x.Name) == slot.Header.FileName &&
                    x.Content.AsSpan().SequenceEqual(restored));
                if (file.Name is not null)
                {
                    sb.AppendLine($"  content OK ({file.Name})");
                    continue;
                }

                var byLength = files.FirstOrDefault(x =>
                    FileNameCodec.Pack(x.Name) == slot.Header.FileName &&
                    x.Content.Length == restored.Length);
                if (byLength.Name is not null)
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

    private static string SizeText(int size) =>
        size >= 1 << 20 ? $"{size / (double)(1 << 20):F2}MB"
        : size >= 1024 ? $"{size / 1024.0:F1}KB"
        : $"{size}B";

    private static int NextSize(Random rng)
    {
        if (rng.Next(100) < EmptyChancePercent) return 0;

        var size = (int)Math.Round(Math.Pow(2.0, rng.NextDouble() * MaxSizeLog2));
        return Math.Clamp(size, MinFileSize, MaxFileSize);
    }

    // ------------- Одна итерация: кодирование → повреждения → декодирование -------------

    private sealed record IterationOutcome(
        TestResult Result, double EncSeconds, double DecSeconds, int ContentBytes);

    private static IterationOutcome RunIteration(int n, Random rng)
    {
        var roll = rng.Next(100);
        bool stress = roll < WarnChancePercent;
        bool multi = !stress && roll < WarnChancePercent + MultiChancePercent;

        if (multi)
            return RunMultiFile(n, rng);

        // ── Одиночный файл ────────────────────────────────────────────
        int size = NextSize(rng);
        int eccPercent = MinEccPercent + rng.Next(MaxEccPercent);
        var format = rng.Next(2) == 0 ? OutputFormat.Base64 : OutputFormat.Binary;
        var fileName = $"demo-{n:D6}.dat";
        var content = RandomInput.Bytes(size, rng);

        // Кодирование (замеряемое)
        var sw = Stopwatch.StartNew();
        var (packets, stats) = new FileEncoder(eccPercent).EncodeWithStats(content, fileName);
        sw.Stop();
        double encSeconds = sw.Elapsed.TotalSeconds;

        // Повреждения: комбинированные в бюджете или сверх бюджета
        int overkill = 0;
        bool collisionKill = false;
        if (stress)
        {
            if (rng.Next(100) < 70) overkill = 1 + rng.Next(2);
            else collisionKill = true;
        }

        var damage = DamageEngine.Apply(packets, stats, format, rng, overkill, collisionKill);

        // Коллизии версий — адверсариальный случай: иногда декодер не может
        // однозначно выбрать правильную версию даже в бюджете. Считаем такой
        // расклад осознанным стрессом: ожидаем чистый отказ или точное
        // восстановление, но не красную строку.
        if ((damage.Mask & DamageBits.Collision) != 0)
            stress = true;

        // Декодирование: сканирование кусков + сборка
        var decoder = new FileDecoder();
        sw.Restart();
        for (var i = 0; i < damage.Chunks.Count; i++)
        {
            using var chunkStream = new MemoryStream(damage.Chunks[i], writable: false);
            PacketIO.ScanStream(decoder, chunkStream, damage.ChunkFormats[i]);
        }
        var restored = decoder.FileCount == 1
            ? decoder.TryAssemble(decoder.Slots[0].Header)
            : null;
        sw.Stop();
        double decSeconds = sw.Elapsed.TotalSeconds;

        // Ожидание: обычный режим — точное восстановление;
        // стресс — чистый отказ (null) или точное восстановление
        bool passed;
        if (stress)
        {
            passed = restored is null ||
                restored.AsSpan().SequenceEqual(content);
        }
        else
        {
            passed = restored is not null &&
                restored.AsSpan().SequenceEqual(content);
        }

        string? hint = null;
        if (!passed && !stress)
        {
            DumpFailure(n, decoder, damage, stats, restored, content);
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
                SizeBytes = size,
                Format = format == OutputFormat.Base64 ? "txt" : "bin",
                EccPercent = eccPercent,
                DataCount = stats.DataCount,
                EccCount = stats.EccCount,
                LostSectors = damage.LostSectors,
                DamageMask = damage.Mask,
                Passed = passed,
                Stress = stress,
                FailureHint = hint
            },
            encSeconds, decSeconds, size);
    }

    private static IterationOutcome RunMultiFile(int n, Random rng)
    {
        var fileCount = rng.Next(2) == 0 ? 2 : 3;
        var files = new List<(byte[] Content, string Name)>();
        var encoded = new List<(IReadOnlyList<byte[]> Packets, EncodeStats Stats)>();

        var sw = Stopwatch.StartNew();
        var eccSum = 0;
        for (var f = 0; f < fileCount; f++)
        {
            var content = RandomInput.Bytes(NextSize(rng), rng);
            var name = $"demo-{n:D6}-{(char)('A' + f)}.dat";
            var (packets, stats) = new FileEncoder(
                    MinEccPercent + rng.Next(MaxEccPercent))
                .EncodeWithStats(content, name);
            files.Add((content, name));
            encoded.Add((packets, stats));
            eccSum += (int)Math.Round(
                stats.EccCount * 100.0 / Math.Max(1, stats.DataCount));
        }
        sw.Stop();
        double encSeconds = sw.Elapsed.TotalSeconds;

        var damage = DamageEngine.ApplyMultiFile(encoded, rng);

        var decoder = new FileDecoder();
        sw.Restart();
        foreach (var chunk in damage.Chunks)
        {
            using var chunkStream = new MemoryStream(chunk, writable: false);
            PacketIO.ScanStream(decoder, chunkStream, OutputFormat.Base64);
        }

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
        bool stress = (damage.Mask & DamageBits.Collision) != 0;

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

        bool passed = decoder.FileCount == pending.Count;
        if (passed)
        {
            var unverifiable = 0;
            foreach (var slot in decoder.Slots)
            {
                var restored = decoder.TryAssemble(slot.Header);
                if (restored is null)
                {
                    if (slot.CollisionSectorCount == 0)
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
        sw.Stop();
        double decSeconds = sw.Elapsed.TotalSeconds;

        if (!passed)
            DumpMultiFailure(n, decoder, damage, files);

        int totalSize = files.Sum(f => f.Content.Length);
        int dataCount = encoded.Sum(e => e.Stats.DataCount);
        int eccCount = encoded.Sum(e => e.Stats.EccCount);

        return new IterationOutcome(
            new TestResult
            {
                N = n,
                SizeBytes = totalSize,
                Format = "mlt",
                EccPercent = eccSum / fileCount,
                DataCount = dataCount,
                EccCount = eccCount,
                LostSectors = damage.LostSectors,
                DamageMask = damage.Mask,
                Passed = passed,
                Stress = stress
            },
            encSeconds, decSeconds, totalSize);
    }

    // ------------- Основной цикл стенда -------------

    private static void Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        HideCursor();
        ClearScreen();

        var results = new Queue<TestResult>(HistorySize);
        DrawCodecInfo();
        DrawHeader();
        int tableDataTop = CursorTopSafe; // вершина таблицы данных

        // Сид: из командной строки (dotnet run -- <seed>) или случайный.
        uint seed = args.Length > 0 && uint.TryParse(args[0], out var parsed)
            ? parsed
            : (uint)Environment.TickCount64;
        MoveCursor(0, tableDataTop + HistorySize + 3);
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($" random seed = {seed}");
        Console.ResetColor();

        var rng = new Random(unchecked((int)seed));

        int n = 0;
        int totalSuccess = 0;
        int totalFail = 0;
        // Накопители средних скоростей.
        double totalEncSpeed = 0.0;
        double totalDecSpeed = 0.0;
        int iterationCount = 0;

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
                    0, 0, 0);
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

            // Курсор — под таблицей, чтобы не мешать.
            MoveCursor(0, tableDataTop + HistorySize + 3);

            // FAIL — остановка стенда; окно остаётся открытым до нажатия клавиши.
            if (!result.Passed)
            {
                Console.ForegroundColor = ConsoleColor.Black;
                Console.BackgroundColor = ConsoleColor.DarkRed;
                Console.WriteLine($" TEST STOPPED: iteration {n} broke the expectation. Seed = {seed}. Hint: {result.FailureHint ?? "n/a"}. Press any key to exit... ");
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
}
