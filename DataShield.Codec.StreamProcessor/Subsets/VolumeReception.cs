namespace DataShield.Codec.StreamProcessor.Subsets;

// ─────────────────────────────────────────────────────────────────────────────
//  Снимок состояния приёма одного тома для планировщика подмножеств
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Снимок приёма одного тома (сектора): достаточно сведений, чтобы оценить
/// подозрительность тома без доступа к живому слоту. Отсутствующий том
/// описывается значениями по умолчанию (<see cref="Present"/> = false).
/// </summary>
/// <param name="Present">Принята ли хотя бы одна версия тома.</param>
/// <param name="VariantCount">Число различных версий payload (больше 1 — коллизия).</param>
/// <param name="HeadConfirmationCount">Подтверждения наиболее подтверждённой версии.</param>
public readonly record struct VolumeReception(
    bool Present,
    int VariantCount,
    int HeadConfirmationCount)
{
    /// <summary>Том принят и имеет несколько конкурирующих версий.</summary>
    public bool HasCollision => Present && VariantCount > 1;
}
