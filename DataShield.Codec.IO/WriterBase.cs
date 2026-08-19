using DataShield.Interfaces;

namespace DataShield.Codec.IO;

// в”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђ
//  Р‘Р°Р·РѕРІР°СЏ СЂРµР°Р»РёР·Р°С†РёСЏ РїСЂРёС‘РјРЅРёРєР° РґР°РЅРЅС‹С…
// в”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђ

/// <summary>
/// Р‘Р°Р·РѕРІР°СЏ СЂРµР°Р»РёР·Р°С†РёСЏ <see cref="IDataWriter"/>: РїРѕРґРєР»СЋС‡РµРЅРёРµ Рє РёСЃС‚РѕС‡РЅРёРєСѓ
/// РїРѕРґРїРёСЃРєРѕР№ РЅР° РµРіРѕ DataReady СЃ РІС‹С‡РёС‚РєРѕР№ Р±СѓС„РµСЂРѕРІ С‡РµСЂРµР·
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

    private void DetachLocked()
    {
        if (_source is null) return;
        _source.DataReady -= HandleDataReady;
        _source = null;
    }

    private void HandleDataReady(TakeBufferDelegate take) => Write(take());
}
