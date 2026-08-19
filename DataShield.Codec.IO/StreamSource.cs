using DataShield.Interfaces;

namespace DataShield.Codec.IO;

// ─────────────────────────────────────────────────────────────────────────────
//  Источник данных на основе Stream
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Источник данных на основе <see cref="Stream"/> (в том числе
/// <see cref="MemoryStream"/>). Поток не закрывается источником.
/// </summary>
public sealed class StreamSource : BufferedSourceBase
{
    private readonly Stream _stream;

    /// <summary>Создать источник поверх потока.</summary>
    /// <param name="stream">Читаемый поток (не закрывается источником).</param>
    /// <param name="bufferSize">Размер буфера выдачи, байт.</param>
    public StreamSource(Stream stream, int bufferSize = 4096)
        : base(bufferSize)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead)
            throw new ArgumentException("Поток не поддерживает чтение.", nameof(stream));
        _stream = stream;
    }

    /// <inheritdoc/>
    protected override int ReadByteCore() => _stream.ReadByte();
}
