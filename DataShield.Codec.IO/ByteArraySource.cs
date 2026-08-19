using DataShield.Interfaces;

namespace DataShield.Codec.IO;

// ─────────────────────────────────────────────────────────────────────────────
//  Источник данных на основе коллекции байт
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Источник данных на основе коллекции байт: массива, списка или иной
/// доступной только для чтения коллекции.
/// </summary>
public sealed class ByteArraySource : BufferedSourceBase
{
    private readonly IReadOnlyList<byte> _data;
    private int _position;

    /// <summary>Создать источник поверх коллекции байт.</summary>
    /// <param name="data">Коллекция байт (не копируется).</param>
    /// <param name="bufferSize">Размер буфера выдачи, байт.</param>
    public ByteArraySource(IReadOnlyList<byte> data, int bufferSize = 4096)
        : base(bufferSize)
    {
        ArgumentNullException.ThrowIfNull(data);
        _data = data;
    }

    /// <inheritdoc/>
    protected override int ReadByteCore() =>
        _position < _data.Count ? _data[_position++] : -1;
}
