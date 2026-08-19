namespace DataShield.Interfaces;

// ─────────────────────────────────────────────────────────────────────────────
//  Приёмник данных
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Приёмник данных: похож на <see cref="IDataSource"/>, но ничего не выдаёт
/// на выход — только принимает.
/// </summary>
public interface IDataWriter
{
    /// <summary>Принять порцию данных на запись.</summary>
    void Write(ReadOnlySpan<byte> data);

    /// <summary>Подключить источник: приёмник вычитывает его буферы через Write.</summary>
    void Attach(IDataSource source);

    /// <summary>Отключиться от источника.</summary>
    void Detach();
}
