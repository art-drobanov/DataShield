using DataShield.Console;

namespace DataShield.Console.En;

/// <summary>
/// Entry point of the English console: <see cref="ConsoleApp"/> wired to
/// the EnStrings localization set. The exit code is passed through as is.
/// </summary>
internal static class Program
{
    private static int Main(string[] args) =>
        new ConsoleApp(EnStrings.Instance).Run(args);
}
