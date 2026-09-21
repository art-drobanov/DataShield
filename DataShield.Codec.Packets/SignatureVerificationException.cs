namespace DataShield.Codec.Packets;

// ─────────────────────────────────────────────────────────────────────────────
//  Отказ расширенной проверки SHA-512 при сборке
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Сборка файла прошла базовую проверку SHA-256 (Exact), но не подтверждена
/// ни одной принятой сигнатурой SHA-512 (Any). Типовые причины: режим
/// <see cref="SignatureMode.Force"/> при отсутствии сигнатурных пакетов в
/// потоке либо сигнатуры иного содержимого (в том числе иного секрета
/// хеширования).
///
/// Отличает отказ расширенной проверки от повреждения потока: повреждение
/// (провал SHA-256) выражается возвратом null без исключения.
/// </summary>
public sealed class SignatureVerificationException : Exception
{
    /// <summary>Создать исключение с типовым сообщением.</summary>
    public SignatureVerificationException()
        : base("Файл собран и подтверждён SHA-256, но не подтверждён ни одной принятой сигнатурой SHA-512.")
    {
    }

    /// <summary>Создать исключение с заданным сообщением.</summary>
    public SignatureVerificationException(string message)
        : base(message)
    {
    }

    /// <summary>Создать исключение с заданным сообщением и причиной.</summary>
    public SignatureVerificationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
