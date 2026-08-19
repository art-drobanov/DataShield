using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace DataShield.Gui;

/// <summary>
/// Точка входа Avalonia-приложения DataShield. Создаёт единую ViewModel
/// (переживает смену языка) и главное окно; при смене языка интерфейса
/// окно пересоздаётся, чтобы разметка заново выбрала локализованные строки.
/// </summary>
public partial class App : Application
{
    /// <summary>Единая ViewModel приложения; сохраняется при смене языка.</summary>
    public MainViewModel ViewModel { get; private set; } = null!;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Язык из настроек пользователя (по умолчанию EN) — до создания
        // ViewModel и окна, чтобы они сразу увидели правильные строки.
        LanguageManager.LoadPersisted();

        ViewModel = new MainViewModel();
        LanguageManager.Applied += RecreateMainWindow;

        var window = new MainWindow(ViewModel);
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = window;

        base.OnFrameworkInitializationCompleted();
        window.Show();
    }

    /// <summary>
    /// Пересоздать главное окно после смены языка: статические строки
    /// XAML ({x:Static}) вычитываются при разборе разметки. ViewModel
    /// и её состояние (пути, режим) переносятся в новое окно.
    /// </summary>
    private void RecreateMainWindow()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return;

        var old = desktop.MainWindow;

        var fresh = new MainWindow(ViewModel);
        desktop.MainWindow = fresh;
        fresh.Show();

        old?.Close();
    }
}
