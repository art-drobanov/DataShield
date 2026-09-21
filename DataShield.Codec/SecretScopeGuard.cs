namespace DataShield.Codec;

// ─────────────────────────────────────────────────────────────────────────────
//  Контекстный скоуп секрета хеширования (см. Sha256Secure/Sha512Secure)
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Устанавливает секрет хеширования на время операции кодека и восстанавливает
/// прежнее значение при освобождении (в том числе при исключении). Секрет
/// контекстен (<see cref="System.Threading.AsyncLocal{T}"/>): параллельные
/// задачи в рамках операции наследуют его, прочие сессии не затрагиваются.
/// Пустой секрет операции не перекрывает секрет, установленный вызывающим
/// кодом напрямую через <c>Sha256Secure.HashSecret</c>.
/// </summary>
internal readonly struct SecretScopeGuard : IDisposable
{
    private readonly string? _prior256;
    private readonly string? _prior512;
    private readonly bool _applied;

    public SecretScopeGuard(string? secret)
    {
        _prior256 = Sha256Secure.HashSecret;
        _prior512 = Sha512Secure.HashSecret;
        if (string.IsNullOrEmpty(secret))
        {
            _applied = false;
            return;
        }
        Sha256Secure.HashSecret = secret;
        Sha512Secure.HashSecret = secret;
        _applied = true;
    }

    public void Dispose()
    {
        if (!_applied) return;
        Sha256Secure.HashSecret = _prior256;
        Sha512Secure.HashSecret = _prior512;
    }
}
