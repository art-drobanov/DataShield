namespace DataShield.Codec.StreamProcessor.Versions;

// ─────────────────────────────────────────────────────────────────────────────
//  Внутренний накопитель одной версии payload сектора
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Одна версия payload сектора со счётчиком подтверждений.
/// Хранится в списках <see cref="ReceptionSlot"/>, где порядок
/// определяется убыванием <see cref="ConfirmationCount"/>.
/// </summary>
internal sealed class SectorVariant
{
    internal SectorVariant(byte[] payload)
    {
        Payload = payload;
        ConfirmationCount = 1;
    }

    /// <summary>Payload версии (64 байта).</summary>
    internal byte[] Payload { get; }

    /// <summary>Сколько копий этой версии принято из потока.</summary>
    internal int ConfirmationCount { get; set; }
}
