using System;
using System.Diagnostics;
using System.Security.Cryptography;
using DataShield.Interfaces;

namespace ShaCompactDemo
{
    // ─────────────────────────────────────────────────────────────────────────────
    //  Демо: бесконечный стресс-тест Sha256Compact/Sha512Compact против .NET
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Программа в бесконечном цикле случайно выбирает алгоритм (SHA-256 или
    /// SHA-512), генерирует случайный буфер произвольного размера (1 байт..1 МБ),
    /// считает его хеш собственной компактной реализацией <c>Sha256Compact</c> или
    /// <c>Sha512Compact</c> и встроенной реализацией из .NET, после чего сверяет
    /// результаты. Первое же несовпадение выводит оба хеша и завершает работу;
    /// иначе раз в 100 итераций обновляется строка прогресса.
    /// </summary>
    class Program
    {
        /// <summary>Строка «продукт — версия — копирайт» для шапки демо.</summary>
        private static string VersionLine
        {
            get
            {
                var ver = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version;
                return $"DataShield v{ver?.Major ?? 1}.{ver?.Minor ?? 0} {BuildInfo.BuildLabel}   Copyright (c) 2026 Artem Drobanov, Vladislav Utyumov";
            }
        }

        /// <summary>
        /// Точка входа: цикл сравнения хешей и отображение статистики
        /// (число итераций, объём обработанных данных, текущая и средняя
        /// скорость в МБ/с).
        /// </summary>
        static void Main()
        {
            // Ctrl+C: вернуть курсор и завершиться штатно (без стек-дампа)
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                if (OperatingSystem.IsWindows() && !Console.IsOutputRedirected)
                    Console.CursorVisible = true;
                Environment.Exit(0);
            };

            if (OperatingSystem.IsWindows() && !Console.IsOutputRedirected)
                Console.CursorVisible = false;
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.ResetColor();

            // Самопроверка по опубликованным векторам FIPS 180-4 ("abc")
            if (!Sha256Compact.Test() || !Sha512Compact.Test())
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("   ShaCompact self-test failed");
                Environment.ExitCode = 1;
                return;
            }

            // Шапка демо-программы
            PrintLine(" ╔═════════════════╗", ConsoleColor.DarkCyan);
            PrintLine(" ║ ShaCompact Demo ║", ConsoleColor.DarkCyan);
            PrintLine(" ╚═════════════════╝", ConsoleColor.DarkCyan);
            PrintLine(" " + VersionLine, ConsoleColor.DarkGray);
            Console.WriteLine();

            // Накопленная статистика: число тестов, суммарный объём данных,
            // скорости и отметка времени последнего обновления прогресса
            long tests = 0;
            ulong totalBytes = 0;
            var sw = Stopwatch.StartNew();
            double lastSpeed = 0, avgSpeed = 0;
            ulong lastBytes = 0;
            var lastUpdate = sw.Elapsed;

            // Переиспользуемый буфер максимального размера (без обнуления —
            // содержимое всё равно перезаписывается случайными данными)
            const int MaxBufferSize = 1024 * 1024;
            byte[] buffer = GC.AllocateUninitializedArray<byte>(MaxBufferSize);

            while (true)
            {
                // Случайный алгоритм, случайная длина 1..MaxBufferSize и содержимое
                bool sha512 = RandomNumberGenerator.GetInt32(2) == 1;
                int len = RandomNumberGenerator.GetInt32(1, MaxBufferSize + 1);
                var data = buffer.AsSpan(0, len);
                RandomNumberGenerator.Fill(data);

                // Хеш испытуемой реализацией и эталонной реализацией .NET
                byte[] ownHash = sha512 ? Sha512Compact.HashData(data) : Sha256Compact.HashData(data);
                byte[] netHash = sha512 ? SHA512.HashData(data) : SHA256.HashData(data);

                tests++;
                totalBytes += (uint)len;

                // Побайтовая сверка; сравнение за константное время исключает
                // утечку информации о расхождении через тайминги
                if (!CryptographicOperations.FixedTimeEquals(ownHash, netHash))
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"   Error in test #{tests}, data size: {len:N0}, algorithm: SHA-{(sha512 ? 512 : 256)}");
                    Console.WriteLine($"   Sha{(sha512 ? "512" : "256")}Compact: {Convert.ToHexString(ownHash)}");
                    Console.WriteLine($"              .NET: {Convert.ToHexString(netHash)}");
                    Environment.ExitCode = 1;
                    return;
                }

                // Каждые 100 итераций обновляем однострочный прогресс
                if (tests % 100 == 0)
                {
                    var now = sw.Elapsed;
                    var dt = (now - lastUpdate).TotalSeconds;

                    if (dt > 0)
                    {
                        // Текущая скорость — с момента прошлого обновления,
                        // средняя — от самого запуска программы
                        lastSpeed = ((totalBytes - lastBytes) / 1048576.0) / dt;
                        avgSpeed = (totalBytes / 1048576.0) / now.TotalSeconds;
                        lastBytes = totalBytes;
                        lastUpdate = now;
                    }

                    Console.Write("\r ");
                    PrintColored("OK", ConsoleColor.Green);
                    PrintColored($" {tests:N0}", ConsoleColor.Yellow);
                    Console.Write(" iter. | ");
                    PrintColored($"{totalBytes / 1048576.0:F1}", ConsoleColor.Yellow);
                    Console.Write(" MB | ");
                    PrintColored($"{lastSpeed:F1}", ConsoleColor.Yellow);
                    Console.Write(" MB/s (cur), ");
                    PrintColored($"{avgSpeed:F1}", ConsoleColor.Yellow);
                    Console.Write(" MB/s (avg)");
                }
            }

            // Вывести текст заданным цветом и вернуть стандартный серый цвет
            static void PrintColored(string text, ConsoleColor color)
            {
                Console.ForegroundColor = color;
                Console.Write(text);
                Console.ForegroundColor = ConsoleColor.Gray;
            }

            // Вывести цветную строку с переводом строки
            static void PrintLine(string text, ConsoleColor color)
            {
                PrintColored(text, color);
                Console.WriteLine();
            }
        }
    }
}