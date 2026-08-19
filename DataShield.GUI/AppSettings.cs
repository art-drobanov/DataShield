using System.IO;
using System.Text.Json;

namespace DataShield.Gui;

/// <summary>
/// Персистентность настроек приложения: JSON-файл в пользовательском
/// каталоге (%APPDATA% на Windows, ~/.config на Linux). Кроссплатформенная
/// замена WPF Settings.settings.
/// </summary>
internal static class AppSettings
{
    private const string FileName = "settings.json";

    /// <summary>Код языка интерфейса ("en" / "ru"); по умолчанию английский.</summary>
    public static string Language { get; set; } = "en";

    private static string DirectoryPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DataShield");

    private static string FilePath => Path.Combine(DirectoryPath, FileName);

    /// <summary>Загрузить настройки с диска; ошибки молча игнорируются.</summary>
    public static void Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            var dto = JsonSerializer.Deserialize<Dto>(File.ReadAllText(FilePath));
            if (dto?.Language is not null)
                Language = dto.Language;
        }
        catch
        {
            // Повреждённый файл настроек не должен ломать запуск приложения.
        }
    }

    /// <summary>Сохранить настройки на диск; ошибки молча игнорируются.</summary>
    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(
                new Dto { Language = Language },
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Недоступный каталог настроек не должен ломать работу приложения.
        }
    }

    private sealed class Dto
    {
        public string? Language { get; set; }
    }
}
