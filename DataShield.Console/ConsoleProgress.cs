using DataShield.Codec.Reporting;

namespace DataShield.Console;

// ─────────────────────────────────────────────────────────────────────────────
//  Прогресс в консоли: перезаписываемая строка «фаза + проценты»
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Приёмник прогресса для консоли: перезаписываемая строка
/// «фаза + проценты» (печатается в текущую строку, затирается
/// <see cref="Done"/>). Потокобезопасен: повторные проценты и
/// параллельные вызовы подавляются.
/// </summary>
public sealed class ConsoleProgress : IProgress<CodecProgress>
{
    private readonly object _gate = new();
    private readonly bool _enabled;
    private int _lastPercent = -1;

    /// <summary>Создать приёмник; quiet отключает вывод.</summary>
    public ConsoleProgress(bool quiet) => _enabled = !quiet;

    /// <inheritdoc/>
    public void Report(CodecProgress value)
    {
        if (!_enabled)
            return;

        lock (_gate)
        {
            if (value.Percent == _lastPercent)
                return;

            _lastPercent = value.Percent;

            var phase = value.Phase;
            if (phase.Length > 30)
                phase = phase[..30];

            System.Console.Write($"\r    {phase,-30} {value.Percent,3}%".PadRight(45));
        }
    }

    /// <summary>Затереть строку прогресса и сбросить подавление процентов.</summary>
    public void Done()
    {
        if (!_enabled)
            return;

        lock (_gate)
        {
            System.Console.Write("\r" + new string(' ', 45) + "\r");
            _lastPercent = -1;
        }
    }
}

/// <summary>
/// Композиция приёмников прогресса: значение направляется каждому
/// подписчику по порядку. Используется консольной реализацией кодека
/// для одновременного отчёта в консоль и внешнему наблюдателю.
/// </summary>
internal sealed class CompositeProgress(
    IProgress<CodecProgress> first,
    IProgress<CodecProgress> second) : IProgress<CodecProgress>
{
    /// <summary>Продублировать отчёт обоим подписчикам по порядку.</summary>
    public void Report(CodecProgress value)
    {
        first.Report(value);
        second.Report(value);
    }
}
