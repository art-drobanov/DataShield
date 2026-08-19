namespace DataShield.Codec.StreamProcessor.Subsets;

// ─────────────────────────────────────────────────────────────────────────────
//  Настройки подбора подмножества томов (стадия 3 сборки)
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Настройки подбора подмножества томов при сборке файла.
///
/// Стадия включается, когда прямая сборка, RS-восстановление по полной карте
/// и перебор версий коллизионных слотов не дали результата: часть принятых
/// томов сознательно помечается стёртой, и RS восстанавливает их по остальным.
/// Арбитр прежний — финальный SHA-256.
///
/// Дефолты откалиброваны бенчмарком RsRaid16Demo (режим «bench»): одна попытка
/// стоит Init (~O(E²·N)) + 32×Process (~O(E·N)) + SHA-256(N·64); при E ≤ 32
/// даже максимальный файл поля (N+M = 65535) укладывается в ≥40 попыток
/// за 30 секунд.
/// </summary>
public sealed class VolumeSubsetSearchOptions
{
    /// <summary>
    /// Потолок общего числа попыток стадии. В сочетании с
    /// <see cref="TimeBudget"/> срабатывает раньше тот, что наступит первым.
    /// </summary>
    public long MaxAttempts { get; init; } = 100_000;

    /// <summary>Мягкий бюджет стадии; проверяется между попытками.</summary>
    public TimeSpan TimeBudget { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Кап на E — суммарное число стёртых data-томов (пропущенные +
    /// исключённые коллизионные + дополнительные исключения). E — главный
    /// драйвер стоимости гауссовой инверсии (~E²·N), поэтому ограничивается
    /// отдельно от числа попыток.
    /// </summary>
    public int MaxErasedDataVolumes { get; init; } = 32;

    /// <summary>
    /// Размер шорт-листа подозрительных томов для уровней исключений
    /// t ≥ 2 (пары, тройки): C(S, t) комбинаций на уровень.
    /// </summary>
    public int ShortlistSize { get; init; } = 64;

    /// <summary>
    /// Максимальный размер дополнительных исключений t. Уровень 1 — одиночные
    /// исключения по всем кандидатам; уровни 2..N — сочетания из шорт-листа.
    /// </summary>
    public int MaxExtraExclusionLevel { get; init; } = 3;

    /// <summary>Настройки по умолчанию (клибровка по бенчмарку RS-декодера).</summary>
    public static VolumeSubsetSearchOptions Default { get; } = new();

    /// <summary>Проверить допустимость значений; выбрасывает исключение при ошибке.</summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Любое значение вне допустимого диапазона.
    /// </exception>
    public void Validate()
    {
        if (MaxAttempts < 1)
            throw new ArgumentOutOfRangeException(nameof(MaxAttempts));

        if (TimeBudget <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(TimeBudget));

        if (MaxErasedDataVolumes < 1)
            throw new ArgumentOutOfRangeException(nameof(MaxErasedDataVolumes));

        if (ShortlistSize < 2)
            throw new ArgumentOutOfRangeException(nameof(ShortlistSize));

        if (MaxExtraExclusionLevel < 1)
            throw new ArgumentOutOfRangeException(nameof(MaxExtraExclusionLevel));
    }
}
