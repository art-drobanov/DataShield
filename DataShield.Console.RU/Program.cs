using DataShield.Console;

namespace DataShield.Console.Ru;

/// <summary>
/// Точка входа русскоязычной консоли: <see cref="ConsoleApp"/> с набором
/// строк RuStrings. Код возврата передаётся вызывающей стороне без изменений.
/// </summary>
internal static class Program
{
    private static int Main(string[] args) =>
        new ConsoleApp(RuStrings.Instance).Run(args);
}
