using System.Globalization;
using DataShield.Codec;
using DataShield.Codec.Reporting;

namespace DataShield.Gui;

/// <summary>
/// Управление языком интерфейса: применяет выбранной язык к ресурсам
/// UI (<see cref="UiStrings"/>) и сообщениям кодека (<see cref="CodecStrings"/>),
/// сохраняет выбор в настройках пользователя. По умолчанию — английский.
/// </summary>
public static class LanguageManager
{
    /// <summary>Язык применён (в том числе при смене во время работы).</summary>
    public static event Action? Applied;

    /// <summary>Текущий язык интерфейса. До первого <see cref="Apply"/> — English.</summary>
    public static UiLanguage Current { get; private set; } = UiLanguage.English;

    /// <summary>Код культуры для языка ("en" / "ru").</summary>
    public static string GetCultureName(UiLanguage language) => language switch
    {
        UiLanguage.English => "en",
        UiLanguage.Russian => "ru",
        _ => throw new ArgumentOutOfRangeException(nameof(language)),
    };

    /// <summary>Язык по коду культуры; неизвестные коды дают English.</summary>
    public static UiLanguage FromCultureName(string cultureName) => cultureName switch
    {
        "ru" => UiLanguage.Russian,
        _ => UiLanguage.English,
    };

    /// <summary>
    /// Применить язык: переключить ресурсы UI и кодека, при
    /// <paramref name="persist"/> = true — сохранить в настройках пользователя.
    /// </summary>
    public static void Apply(UiLanguage language, bool persist = true)
    {
        Current = language;

        UiStrings.Culture =
            CultureInfo.GetCultureInfo(GetCultureName(language));

        CodecStrings.Language = language == UiLanguage.Russian
            ? CodecLanguage.Russian
            : CodecLanguage.English;

        if (persist)
        {
            AppSettings.Language = GetCultureName(language);
            AppSettings.Save();
        }

        Applied?.Invoke();
    }

    /// <summary>
    /// Загрузить язык из настроек пользователя (по умолчанию English)
    /// и применить его без записи обратно в настройки.
    /// </summary>
    /// <returns>Применённый язык.</returns>
    public static UiLanguage LoadPersisted()
    {
        AppSettings.Load();
        var language = FromCultureName(AppSettings.Language);
        Apply(language, persist: false);
        return language;
    }
}
