namespace DataShield.Codec.Packets;

// ─────────────────────────────────────────────────────────────────────────────
//  Формат вывода FEC-потока
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Формат вывода FEC-потока пакетов.
/// </summary>
public enum OutputFormat
{
    /// <summary>Base64 текст, по строке на пакет (.DataShield.txt).</summary>
    Base64,

    /// <summary>Двоичные пакеты подряд без разделителей (.DataShield.bin).</summary>
    Binary
}
