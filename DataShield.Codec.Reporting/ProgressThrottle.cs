namespace DataShield.Codec.Reporting;

// ─────────────────────────────────────────────────────────────────────────────
//  Троттлинг отчётов прогресса из горячих циклов кодека
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Помощник троттлинга: проверяет отмену и сообщает прогресс только когда
/// целочисленный процент меняется. Вызывается из горячих циклов кодека.
/// </summary>
public static class ProgressThrottle
{
    /// <summary>
    /// Проверить отмену и, при изменении процента, сообщить прогресс.
    /// </summary>
    /// <param name="progress">Приёмник прогресса (может быть null).</param>
    /// <param name="lastPercent">Кэш последнего сообщённого процента (ref).</param>
    /// <param name="current">Текущая позиция.</param>
    /// <param name="total">Общий объём работы фазы.</param>
    /// <param name="phase">Имя фазы.</param>
    /// <param name="ct">Токен отмены.</param>
    public static void Tick(
        IProgress<CodecProgress>? progress,
        ref int lastPercent,
        long current, long total,
        string phase,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (progress is null) return;
        var pct = total <= 0 ? 0 : (int)(current * 100 / total);
        if (pct == lastPercent) return;
        lastPercent = pct;
        progress.Report(CodecProgress.Create(pct, phase));
    }
}
