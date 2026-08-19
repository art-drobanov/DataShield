using System;
using Avalonia;

namespace DataShield.Gui;

/// <summary>
/// Точка входа кроссплатформенного (Avalonia) приложения DataShield.
/// </summary>
internal static class Program
{
    [STAThread]
    public static void Main(string[] args) => BuildAvaloniaApp()
        .StartWithClassicDesktopLifetime(args);

    /// <summary>Конфигурация AppBuilder (используется и в дизайне, и при запуске).</summary>
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
