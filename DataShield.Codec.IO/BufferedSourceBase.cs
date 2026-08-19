using DataShield.Interfaces;

namespace DataShield.Codec.IO;

// в”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђ
//  Р‘Р°Р·РѕРІР°СЏ СЂРµР°Р»РёР·Р°С†РёСЏ Р±СѓС„РµСЂРёР·РѕРІР°РЅРЅРѕРіРѕ РёСЃС‚РѕС‡РЅРёРєР° РґР°РЅРЅС‹С…
// в”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђв”Ђ

/// <summary>
/// Р‘СѓС„РµСЂРёР·РѕРІР°РЅРЅС‹Р№ РёСЃС‚РѕС‡РЅРёРє РґР°РЅРЅС‹С… СЃ РїРѕР±Р°Р№С‚РѕРІС‹Рј С‡С‚РµРЅРёРµРј (РѕР±СЉС‘РјС‹ РѕР±СЂР°Р±Р°С‚С‹РІР°РµРјС‹С…
/// РґР°РЅРЅС‹С… РЅРµРІРµР»РёРєРё, РїРѕР±Р°Р№С‚РѕРІРѕРµ С‡С‚РµРЅРёРµ СѓРїСЂРѕС‰Р°РµС‚ Р»РѕРіРёРєСѓ). РќР°СЃРѕСЃ С‡С‚РµРЅРёСЏ СЂР°Р±РѕС‚Р°РµС‚
/// РІ С„РѕРЅРѕРІРѕРј Р·Р°РґР°РЅРёРё: С‡РёС‚Р°РµС‚ Р±Р°Р№С‚С‹ РІ Р±СѓС„РµСЂ, РїСЂРё Р·Р°РїРѕР»РЅРµРЅРёРё РїСЂРёРѕСЃС‚Р°РЅР°РІР»РёРІР°РµС‚СЃСЏ
/// Рё РІС‹Р±СЂР°СЃС‹РІР°РµС‚ DataReady, РїРѕСЃР»Рµ РІС‹С‡РёС‚РєРё РІРѕР·РѕР±РЅРѕРІР»СЏРµС‚СЃСЏ. EOF Рё РѕСЃС‚Р°РЅРѕРІРєР°
/// РѕС‚РґР°СЋС‚ РѕСЃС‚Р°С‚РѕРє Р±СѓС„РµСЂР° СЃРѕР±С‹С‚РёРµРј Рё Р·Р°РІРµСЂС€Р°СЋС‚ <see cref="IDataSource.Completion"/>.
/// </summary>
public abstract class BufferedSourceBase : IDataSource
{
    private readonly byte[] _buffer;
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource _completion = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly object _sync = new();
    private int _fill;
    private bool _running;
    private bool _taken;
    private Exception? _error;

    /// <summary>РЎРѕР·РґР°С‚СЊ РёСЃС‚РѕС‡РЅРёРє СЃ Р±СѓС„РµСЂРѕРј Р·Р°РґР°РЅРЅРѕРіРѕ СЂР°Р·РјРµСЂР°.</summary>
    /// <param name="bufferSize">Р Р°Р·РјРµСЂ Р±СѓС„РµСЂР°, Р±Р°Р№С‚ (в‰Ґ 1).</param>
    protected BufferedSourceBase(int bufferSize)
    {
        if (bufferSize < 1)
            throw new ArgumentOutOfRangeException(nameof(bufferSize));
        _buffer = new byte[bufferSize];
    }

    /// <inheritdoc cref="IDataSource.BufferSize"/>
    public int BufferSize => _buffer.Length;

    /// <inheritdoc cref="IDataSource.IsRunning"/>
    public bool IsRunning
    {
        get { lock (_sync) return _running; }
    }

    /// <inheritdoc cref="IDataSource.Completion"/>
    public Task Completion => _completion.Task;

    /// <inheritdoc cref="IDataSource.Error"/>
    public Exception? Error
    {
        get { lock (_sync) return _error; }
    }

    /// <inheritdoc cref="IDataSource.DataReady"/>
    public event DataReadyHandler? DataReady;

    /// <inheritdoc cref="IDataSource.Start"/>
    public void Start()
    {
        lock (_sync)
        {
            if (_running) return;
            _running = true;
        }

        Task.Run(PumpLoop);
    }

    /// <inheritdoc cref="IDataSource.Stop"/>
    public void Stop()
    {
        lock (_sync)
        {
            if (!_running) return;
            _running = false;
        }
        _cts.Cancel();
        // РћСЃС‚Р°С‚РѕРє Р±СѓС„РµСЂР° РѕС‚РґР°СЃС‚ РЅР°СЃРѕСЃ РїРµСЂРµРґ РІС‹С…РѕРґРѕРј (СЃРј. PumpLoop).
    }

    /// <summary>РџСЂРѕС‡РёС‚Р°С‚СЊ РѕС‡РµСЂРµРґРЅРѕР№ Р±Р°Р№С‚ РёСЃС‚РѕС‡РЅРёРєР°. -1 = EOF.</summary>
    protected abstract int ReadByteCore();

    private void PumpLoop()
    {
        try
        {
            while (true)
            {
                lock (_sync)
                {
                    if (!_running) break;
                }

                int b;
                try
                {
                    b = ReadByteCore();
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                lock (_sync)
                {
                    if (!_running) break;

                    if (b >= 0)
                    {
                        _buffer[_fill++] = (byte)b;
                        if (_fill < _buffer.Length) continue;
                    }
                    else
                    {
                        // EOF: РіРѕС‚РѕРІРЅРѕСЃС‚СЊ СЃ РѕСЃС‚Р°С‚РєРѕРј Рё РѕСЃС‚Р°РЅРѕРІРєР°
                        _running = false;
                    }
                }

                Deliver();
                if (b < 0) break;
            }
        }
        catch (Exception ex)
        {
            lock (_sync)
            {
                _error ??= ex;
                _running = false;
            }
        }
        finally
        {
            // РћСЃС‚Р°РЅРѕРІР»РµРЅРЅС‹Р№ РёСЃС‚РѕС‡РЅРёРє РѕС‚РґР°С‘С‚ РёРјРµСЋС‰РёР№СЃСЏ РІ Р±СѓС„РµСЂРµ РѕСЃС‚Р°С‚РѕРє
            Deliver();
            lock (_sync) _running = false;
            _cts.Dispose();

            Exception? error;
            lock (_sync) error = _error;
            if (error is null)
                _completion.TrySetResult();
            else
                _completion.TrySetException(error);
        }
    }

    private void Deliver()
    {
        lock (_sync)
        {
            if (_fill == 0) return;
            _taken = false;
        }

        // РЎРѕР±С‹С‚РёРµ РІРЅРµ Р±Р»РѕРєРёСЂРѕРІРєРё: РѕР±СЂР°Р±РѕС‚С‡РёРє РІС‹С‡РёС‚С‹РІР°РµС‚ Р±СѓС„РµСЂ РґРµР»РµРіР°С‚РѕРј,
        // С‡С‚РµРЅРёРµ РІРѕР·РѕР±РЅРѕРІРёС‚СЃСЏ РїРѕСЃР»Рµ РІРѕР·РІСЂР°С‚Р° РѕР±СЂР°Р±РѕС‚С‡РёРєР°.
        DataReady?.Invoke(Take);

        lock (_sync) _fill = 0;
    }

    private byte[] Take()
    {
        lock (_sync)
        {
            if (_taken) return Array.Empty<byte>();
            _taken = true;

            var result = new byte[_fill];
            Buffer.BlockCopy(_buffer, 0, result, 0, _fill);
            return result;
        }
    }
}
