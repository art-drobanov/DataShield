namespace DataShield.Codec.Reporting;

// ─────────────────────────────────────────────────────────────────────────────
//  Отчёт о прогрессе кодирования/декодирования
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Отчёт о прогрессе долгой операции кодека.
/// </summary>
/// <param name="Percent">Глобальный процент выполнения, 0..100.</param>
/// <param name="Phase">Человекочитаемое название текущей фазы.</param>
public readonly record struct CodecProgress(int Percent, string Phase = "")
{
    /// <summary>Создать отчёт с ограничением процента в диапазоне 0..100.</summary>
    public static CodecProgress Create(int percent, string phase)
    {
        if (percent < 0) percent = 0;
        else if (percent > 100) percent = 100;
        return new CodecProgress(percent, phase ?? "");
    }
}
