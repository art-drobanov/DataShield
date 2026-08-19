namespace DataShield.Codec.StreamFilter;

// ─────────────────────────────────────────────────────────────────────────────
//  Диапазон байт
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Диапазон байт [<see cref="From"/>..<see cref="To"/>] включительно.
/// </summary>
/// <param name="From">Начало диапазона.</param>
/// <param name="To">Конец диапазона (включительно).</param>
public readonly record struct ByteRange(byte From, byte To)
{
    /// <summary>
    /// Диапазоны алфавита Base64: A-Z, a-z, 0-9, «+», «/».
    /// Дефолтная защита от мусора txt-входа.
    /// </summary>
    public static IEnumerable<ByteRange> Base64Ranges
    {
        get
        {
            yield return new ByteRange((byte)'A', (byte)'Z');
            yield return new ByteRange((byte)'a', (byte)'z');
            yield return new ByteRange((byte)'0', (byte)'9');
            yield return new ByteRange((byte)'+', (byte)'+');
            yield return new ByteRange((byte)'/', (byte)'/');
        }
    }
}
