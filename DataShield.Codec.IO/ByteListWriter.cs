using DataShield.Interfaces;

namespace DataShield.Codec.IO;

// ─────────────────────────────────────────────────────────────────────────────
//  Приёмник в коллекцию байт
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Приёмник данных в растущую коллекцию байт (например, <see cref="List{T}"/> байт).
/// </summary>
public sealed class ByteListWriter : WriterBase
{
    private readonly ICollection<byte> _collection;
    private readonly object _sync = new();

    /// <summary>Создать приёмник поверх коллекции байт.</summary>
    /// <param name="collection">Коллекция-назначение.</param>
    public ByteListWriter(ICollection<byte> collection)
    {
        ArgumentNullException.ThrowIfNull(collection);
        _collection = collection;
    }

    /// <summary>Сколько байт записано.</summary>
    public long WrittenCount
    {
        get { lock (_sync) return _collection.Count; }
    }

    /// <inheritdoc/>
    public override void Write(ReadOnlySpan<byte> data)
    {
        lock (_sync)
        {
            foreach (var b in data)
                _collection.Add(b);
        }
    }
}
