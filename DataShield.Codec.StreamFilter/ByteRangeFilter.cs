using DataShield.Interfaces;

namespace DataShield.Codec.StreamFilter;

// ─────────────────────────────────────────────────────────────────────────────
//  Фильтр байтового потока по диапазонам
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Фильтр байтового потока: перечисление диапазонов байт конвертируется
/// внутри в булевскую карту допустимости с носителем признака типа byte
/// (bool[256]). Байты вне допустимых диапазонов отбрасываются.
///
/// Дефолтная конфигурация <see cref="CreateBase64"/> защищает от мусора вне
/// диапазона Base64 при работе с txt-входом. В режиме двоичного потока фильтр
/// исключается из цепочки обработки.
/// </summary>
public sealed class ByteRangeFilter : DataProcessorBase
{
    private readonly bool[] _map = new bool[256];

    /// <summary>Создать фильтр по перечислению диапазонов байт.</summary>
    /// <param name="ranges">Допустимые диапазоны байт.</param>
    /// <param name="bufferSize">Размер выходного буфера, байт.</param>
    public ByteRangeFilter(IEnumerable<ByteRange> ranges, int bufferSize = 4096)
        : base(bufferSize)
    {
        ArgumentNullException.ThrowIfNull(ranges);

        foreach (var range in ranges)
        {
            if (range.From > range.To)
                throw new ArgumentException(
                    $"Некорректный диапазон: {range.From} > {range.To}.", nameof(ranges));

            for (var b = range.From; b <= range.To; b++)
                _map[b] = true;
        }
    }

    /// <summary>Создать фильтр алфавита Base64 (защита txt-входа от мусора).</summary>
    /// <param name="bufferSize">Размер выходного буфера, байт.</param>
    public static ByteRangeFilter CreateBase64(int bufferSize = 4096) =>
        new(ByteRange.Base64Ranges, bufferSize);

    /// <summary>Пропускает ли фильтр байт с заданным значением.</summary>
    public bool Accepts(byte value) => _map[value];

    /// <inheritdoc/>
    protected override void ProcessChunk(byte[] chunk)
    {
        foreach (var b in chunk)
            if (_map[b])
                Emit(b);
    }
}
