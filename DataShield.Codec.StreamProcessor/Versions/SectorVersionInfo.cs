namespace DataShield.Codec.StreamProcessor.Versions;

// ─────────────────────────────────────────────────────────────────────────────
//  Снимок принятой версии сектора (публичное API ReceptionSlot)
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Снимок одной принятой версии сектора.
/// Payload является копией внутренних данных.
/// </summary>
/// <param name="Payload">64-байтный payload версии.</param>
/// <param name="ConfirmationCount">Сколько копий этой версии принято из потока.</param>
public sealed record SectorVersionInfo(
    ReadOnlyMemory<byte> Payload,
    int ConfirmationCount);
