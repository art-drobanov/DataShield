namespace DataShield.Gui;

/// <summary>Режим работы приложения.</summary>
public enum WorkMode
{
    /// <summary>Кодирование: файл → FEC-поток пакетов.</summary>
    Encode,

    /// <summary>Декодирование: FEC-поток → восстановленный файл.</summary>
    Decode,
}
