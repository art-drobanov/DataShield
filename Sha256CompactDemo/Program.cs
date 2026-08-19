using System;
using System.Diagnostics;
using System.Security.Cryptography;

namespace Sha256CompactDemo
{
    class Program
    {
        static void Main()
        {
            Console.CursorVisible = false;
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.ResetColor();

            PrintLine(" ╔════════════════════╗", ConsoleColor.DarkCyan);
            PrintLine(" ║ Sha256Compact Demo ║", ConsoleColor.DarkCyan);
            PrintLine(" ╚════════════════════╝", ConsoleColor.DarkCyan);
            Console.WriteLine();

            long tests = 0;
            ulong totalBytes = 0;
            var sw = Stopwatch.StartNew();
            double lastSpeed = 0, avgSpeed = 0;
            ulong lastBytes = 0;
            var lastUpdate = sw.Elapsed;

            const int MaxBufferSize = 1024 * 1024;
            byte[] buffer = GC.AllocateUninitializedArray<byte>(MaxBufferSize);

            while (true)
            {
                int len = RandomNumberGenerator.GetInt32(1, MaxBufferSize + 1);
                var data = buffer.AsSpan(0, len);
                RandomNumberGenerator.Fill(data);

                var ownHash = Sha256Compact.HashData(data);
                var netHash = SHA256.HashData(data);

                tests++;
                totalBytes += (uint)len;

                if (!CryptographicOperations.FixedTimeEquals(ownHash, netHash))
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"   Error in test #{tests}, data size: {len:N0}");
                    Console.WriteLine($"   Sha256Compact: {Convert.ToHexString(ownHash)}");
                    Console.WriteLine($"            .NET: {Convert.ToHexString(netHash)}");
                    return;
                }

                if (tests % 100 == 0)
                {
                    var now = sw.Elapsed;
                    var dt = (now - lastUpdate).TotalSeconds;

                    if (dt > 0)
                    {
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

            static void PrintColored(string text, ConsoleColor color)
            {
                Console.ForegroundColor = color;
                Console.Write(text);
                Console.ForegroundColor = ConsoleColor.Gray;
            }

            static void PrintLine(string text, ConsoleColor color)
            {
                PrintColored(text, color);
                Console.WriteLine();
            }
        }
    }
}