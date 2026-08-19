using DataShield.Interfaces;

namespace DataShield.Codec.IO;

// ─────────────────────────────────────────────────────────────────────────────
//  Базовая реализация буферизованного источника данных
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Буферизованный источник данных с побайтовым чтением (объёмы обрабатываемых
/// данных невелики, побайтовое чтение упрощает логику). Насос чтения работает
/// в фоновой задаче: читает байты в буфер, при заполнении приостанавливается
/// и выбрасывает DataReady, после вычитки возобновляется. EOF и остановка
/// отдают остаток буфера событием и завершают <see cref="IDataSource.Completion"/>.
/// </summary>
public abstract class BufferedSourceBase : IDataSource
{
    // Кольцевой (сбрасываемый) буфер выдачи и примитивы завершения/отмены
    private readonly byte[] _buffer;
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource _completion = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    // Состояние под замком: флаг работы, заполненность буфера,
    // признак «порция выдана» и отложенная ошибка чтения
    private readonly object _sync = new();
    private int _fill;
    private bool _running;
    private bool _taken;
    private Exception? _error;

    /// <summary>Создать источник с буфером заданного размера.</summary>
    /// <param name="bufferSize">Размер буфера, байт (≥ 1).</param>
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
        // Остаток буфера отдаст насос перед выходом (см. PumpLoop).
    }

    /// <summary>Прочитать очередной байт источника. -1 = EOF.</summary>
    protected abstract int ReadByteCore();

    // Насос чтения: заполняет буфер, по заполнению/EOF выдаёт порцию;
    // любая ошибка сохраняется и проваливается в Completion
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
                        // EOF: готовность с остатком и остановка
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
            // Остановленный источник отдаёт имеющийся в буфере остаток
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

    // Выдать накопленную порцию подписчикам; после вычитки буфер сбрасывается
    private void Deliver()
    {
        lock (_sync)
        {
            if (_fill == 0) return;
            _taken = false;
        }

        // Событие вне блокировки: обработчик вычитывает буфер делегатом,
        // чтение возобновится после возврата обработчика.
        DataReady?.Invoke(Take);

        lock (_sync) _fill = 0;
    }

    // Делегат «взять порцию»: копия накопленного, повторный вызов — пусто
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
