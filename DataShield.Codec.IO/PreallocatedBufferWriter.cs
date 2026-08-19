using DataShield.Interfaces;

namespace DataShield.Codec.IO;

// ─────────────────────────────────────────────────────────────────────────────
//  Приёмник в подготовленный массив байт
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Приёмник данных в подготовленный массив байт фиксированной ёмкости.
/// При переполнении выбрасывается исключение.
/// </summary>
public sealed class PreallocatedBufferWriter : WriterBase
{
    private readonly byte[] _buffer;
    private readonly object _sync = new();
    private int _position;

    /// <summary>Создать приёмник поверх подготовленного массива.</summary>
    /// <param name="buffer">Массив-назначение (заполняется с начала).</param>
    public PreallocatedBufferWriter(byte[] buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        _buffer = buffer;
    }

    /// <summary>Сколько байт записано.</summary>
    public int WrittenCount
    {
        get { lock (_sync) return _position; }
    }

    /// <summary>Записанные данные (копия).</summary>
    public byte[] ToArray()
    {
        lock (_sync)
        {
            var result = new byte[_position];
            Buffer.BlockCopy(_buffer, 0, result, 0, _position);
            return result;
        }
    }

    /// <inheritdoc/>
    public override void Write(ReadOnlySpan<byte> data)
    {
        lock (_sync)
        {
            if (_position + data.Length > _buffer.Length)
                throw new InvalidOperationException(
                    $"Буфер приёмника переполнен: {_position} + {data.Length} > {_buffer.Length}.");

            data.CopyTo(_buffer.AsSpan(_position));
            _position += data.Length;
        }
    }
}
