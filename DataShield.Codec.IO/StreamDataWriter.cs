using DataShield.Interfaces;

namespace DataShield.Codec.IO;

// ─────────────────────────────────────────────────────────────────────────────
//  Приёмник в Stream
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Приёмник данных в <see cref="Stream"/>. Поток не закрывается приёмником.
/// </summary>
public sealed class StreamDataWriter : WriterBase
{
    private readonly Stream _stream;
    private readonly object _sync = new();

    /// <summary>Создать приёмник поверх потока.</summary>
    /// <param name="stream">Записываемый поток (не закрывается приёмником).</param>
    public StreamDataWriter(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanWrite)
            throw new ArgumentException("Поток не поддерживает запись.", nameof(stream));
        _stream = stream;
    }

    /// <inheritdoc/>
    public override void Write(ReadOnlySpan<byte> data)
    {
        lock (_sync)
        {
            _stream.Write(data);
        }
    }
}
