namespace DataShield.Codec.Reporting;

// ─────────────────────────────────────────────────────────────────────────────
//  Масштабирование локального прогресса подфазы в глобальную шкалу
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Адаптер, масштабирующий локальный прогресс подфазы (0..100) в диапазон
/// [<paramref name="start"/>..<paramref name="end"/>] родительской шкалы.
/// Позволяет нижележащему слою (например, RS-ECC) сообщать свой локальный
/// прогресс, а вышележащему — видеть глобальный процент и единое имя фазы.
/// </summary>
public sealed class ScaledProgress : IProgress<CodecProgress>
{
    private readonly IProgress<CodecProgress>? _target;
    private readonly string _phase;
    private readonly int _start;
    private readonly int _end;

    /// <summary>
    /// Создать адаптер, отображающий локальную шкалу подфазы (0..100)
    /// в заданный диапазон родительской шкалы.
    /// </summary>
    /// <param name="target">Родительский приёмник (может быть null).</param>
    /// <param name="phase">Имя фазы в родительской шкале.</param>
    /// <param name="start">Начало диапазона родительской шкалы.</param>
    /// <param name="end">Конец диапазона родительской шкалы.</param>
    public ScaledProgress(IProgress<CodecProgress>? target, string phase, int start, int end)
    {
        _target = target;
        _phase = phase;
        _start = start;
        _end = end;
    }

    /// <summary>
    /// Масштабировать отчёт подфазы в родительскую шкалу
    /// и переслать родительскому приёмнику (если задан).
    /// </summary>
    public void Report(CodecProgress value)
    {
        if (_target is null) return;
        var pct = _start + value.Percent * (_end - _start) / 100;
        _target.Report(CodecProgress.Create(pct, _phase));
    }
}
