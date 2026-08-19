using DataShield.Interfaces;

namespace DataShield.Codec.IO;

// ─────────────────────────────────────────────────────────────────────────────
//  Базовая реализация приёмника данных
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Базовая реализация <see cref="IDataWriter"/>: подключение к источнику
/// подпиской на его DataReady с вычиткой буферов через
/// <see cref="IDataWriter.Write"/>.
/// </summary>
public abstract class WriterBase : IDataWriter
{
    private readonly object _sync = new();
    private IDataSource? _source;

    /// <inheritdoc cref="IDataWriter.Write"/>
    public abstract void Write(ReadOnlySpan<byte> data);

    /// <inheritdoc cref="IDataWriter.Attach"/>
    public void Attach(IDataSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        lock (_sync)
        {
            if (ReferenceEquals(_source, source)) return;
            DetachLocked();
            _source = source;
            source.DataReady += HandleDataReady;
        }
    }

    /// <inheritdoc cref="IDataWriter.Detach"/>
    public void Detach()
    {
        lock (_sync) DetachLocked();
    }

    // Отписка от текущего источника (вызывать под _sync)
    private void DetachLocked()
    {
        if (_source is null) return;
        _source.DataReady -= HandleDataReady;
        _source = null;
    }

    // Вычитка буфера источника и запись в приёмник
    private void HandleDataReady(TakeBufferDelegate take) => Write(take());
}
