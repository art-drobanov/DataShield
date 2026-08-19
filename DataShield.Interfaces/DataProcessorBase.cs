namespace DataShield.Interfaces;

// ─────────────────────────────────────────────────────────────────────────────
//  Базовая реализация обработчика данных
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Базовая реализация <see cref="IDataProcessor"/>: подключение к источнику,
/// каскадные запуск/остановка, выходной буфер с выдачей по
/// <see cref="IDataSource.DataReady"/>.
///
/// Наследники обрабатывают входные куски в <see cref="ProcessChunk"/> и
/// публикуют результат через <see cref="Emit"/> (байтовый поток) или
/// <see cref="EmitPacket"/> (поток неделимых пакетов). Потокобезопасность:
/// все переходы состояния и доставка выхода выполняются под
/// <see cref="SyncRoot"/>.
/// </summary>
public abstract class DataProcessorBase : IDataProcessor
{
    private readonly byte[] _outputBuffer;

    // Пакетный режим вывода: неделимые единицы, выдача при накоплении BufferSize байт.
    private readonly List<byte[]> _outputPackets = new();
    private int _outputPacketBytes;

    // Байтовый режим вывода: выдача при точном заполнении BufferSize.
    private int _outputFill;

    private IDataSource? _upstream;

    /// <summary>Создать обработчик с выходным буфером заданного размера.</summary>
    /// <param name="bufferSize">Размер выходного буфера, байт (≥ 1).</param>
    protected DataProcessorBase(int bufferSize)
    {
        if (bufferSize < 1)
            throw new ArgumentOutOfRangeException(nameof(bufferSize));
        _outputBuffer = new byte[bufferSize];
    }

    /// <summary>Объект синхронизации состояния обработчика (переиспользуется наследниками).</summary>
    protected object SyncRoot { get; } = new();

    /// <inheritdoc cref="IDataSource.BufferSize"/>
    public int BufferSize => _outputBuffer.Length;

    /// <inheritdoc cref="IDataSource.IsRunning"/>
    public bool IsRunning
    {
        get { lock (SyncRoot) return _upstream?.IsRunning ?? false; }
    }

    /// <inheritdoc cref="IDataSource.Completion"/>
    public Task Completion
    {
        get { lock (SyncRoot) return _upstream?.Completion ?? Task.CompletedTask; }
    }

    /// <inheritdoc cref="IDataSource.Error"/>
    public Exception? Error
    {
        get { lock (SyncRoot) return _upstream?.Error; }
    }

    /// <inheritdoc cref="IDataSource.DataReady"/>
    public event DataReadyHandler? DataReady;

    /// <inheritdoc cref="IDataProcessor.Attach"/>
    public void Attach(IDataSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        lock (SyncRoot)
        {
            if (ReferenceEquals(_upstream, source)) return;
            DetachLocked();
            _upstream = source;
            source.DataReady += HandleUpstreamData;
        }
    }

    /// <inheritdoc cref="IDataProcessor.Detach"/>
    public void Detach()
    {
        lock (SyncRoot) DetachLocked();
    }

    private void DetachLocked()
    {
        if (_upstream is null) return;
        _upstream.DataReady -= HandleUpstreamData;
        _upstream = null;
    }

    /// <inheritdoc cref="IDataSource.Start"/>
    public void Start()
    {
        IDataSource? upstream;
        lock (SyncRoot) upstream = _upstream;
        upstream?.Start();
    }

    /// <inheritdoc cref="IDataSource.Stop"/>
    public void Stop()
    {
        IDataSource? upstream;
        lock (SyncRoot) upstream = _upstream;
        upstream?.Stop();
    }

    /// <inheritdoc cref="IDataProcessor.Complete"/>
    public void Complete()
    {
        lock (SyncRoot) DeliverLocked();
    }

    private void HandleUpstreamData(TakeBufferDelegate take)
    {
        var chunk = take();
        lock (SyncRoot) ProcessChunk(chunk);
    }

    /// <summary>
    /// Обработать очередной входной кусок. Вызывается на потоке источника
    /// под <see cref="SyncRoot"/>.
    /// </summary>
    /// <param name="chunk">Вычитанный буфер вышестоящего источника.</param>
    protected abstract void ProcessChunk(byte[] chunk);

    /// <summary>
    /// Добавить байт в выходной буфер (байтовый режим). При заполнении буфера
    /// выбрасывается DataReady. Вызывать под <see cref="SyncRoot"/>.
    /// </summary>
    protected void Emit(byte value)
    {
        _outputBuffer[_outputFill++] = value;
        if (_outputFill == _outputBuffer.Length)
            DeliverLocked();
    }

    /// <summary>
    /// Добавить массив байт в выходной буфер побайтово (байтовый режим).
    /// Вызывать под <see cref="SyncRoot"/>.
    /// </summary>
    protected void EmitBytes(ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
            Emit(b);
    }

    /// <summary>
    /// Добавить неделимый пакет в выходной буфер (пакетный режим). Пакет не
    /// разрезается границами выдачи: DataReady выносит целые пакеты, когда их
    /// суммарный объём достигает BufferSize. Вызывать под <see cref="SyncRoot"/>.
    /// </summary>
    protected void EmitPacket(byte[] packet)
    {
        ArgumentNullException.ThrowIfNull(packet);

        _outputPackets.Add(packet);
        _outputPacketBytes += packet.Length;

        if (_outputPacketBytes >= _outputBuffer.Length)
            DeliverLocked();
    }

    /// <summary>Выдать накопленный выход через DataReady. Вызывать под SyncRoot.</summary>
    private void DeliverLocked()
    {
        // Обработчики могут добавить новый выход во время выдачи (например,
        // повторное сканирование) — выдаём до исчерпания.
        while (_outputPackets.Count > 0 || _outputFill > 0)
        {
            // Граница выдачи фиксируется до вызова обработчиков: обработчик
            // нижестоящего модуля может запросить адресную перепривязку данных,
            // и граница должна соответствовать именно этой партии.
            OnDelivering();

            if (_outputPackets.Count > 0)
            {
                var buffer = new byte[_outputPacketBytes];
                var offset = 0;
                foreach (var p in _outputPackets)
                {
                    p.CopyTo(buffer, offset);
                    offset += p.Length;
                }
                _outputPackets.Clear();
                _outputPacketBytes = 0;
                Fire(buffer);
            }

            if (_outputFill > 0)
            {
                var buffer = new byte[_outputFill];
                Buffer.BlockCopy(_outputBuffer, 0, buffer, 0, _outputFill);
                _outputFill = 0;
                Fire(buffer);
            }
        }
    }

    private void Fire(byte[] payload)
    {
        if (DataReady is null) return;

        var taken = false;
        DataReady.Invoke(() =>
        {
            if (taken) return Array.Empty<byte>();
            taken = true;
            return payload;
        });
    }

    /// <summary>
    /// Уведомление наследника о начинающейся выдаче партии выходных данных
    /// (до вызова обработчиков DataReady).
    /// </summary>
    protected virtual void OnDelivering()
    {
    }
}
