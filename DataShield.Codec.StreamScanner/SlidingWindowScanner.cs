using DataShield.Interfaces;

namespace DataShield.Codec.StreamScanner;

// ─────────────────────────────────────────────────────────────────────────────
//  Побайтовый сканер потока со скользящим окном
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Побайтовый сканер потока со скользящим окном. Цепляется к выходу
/// источника/фильтра, содержит делегат обработки окна: при успехе проматывает
/// поток на указанное смещение, при неуспехе декода сдвигает окно на 1 байт.
/// Распознанные пакеты выдаются событием DataReady неделимыми порциями.
///
/// Сканер удерживает весь пропущенный через себя поток: это позволяет
/// перепривязывать данные, не распознанные при прямом проходе (например,
/// секторы, пришедшие раньше своего заголовка), адресным повторным
/// сканированием через <see cref="RequestRescan"/>.
///
/// Повторное сканирование ограничено позицией, соответствующей последней
/// выданной прямой проходкой партии данных: дальше прямой проход уже
/// работал со знанием, накопленным к моменту выдачи, и повторная выдача
/// привела бы к задвоению подтверждений.
///
/// Прямой проход проматывает поток на продвижение, возвращённое
/// обработчиком (обычно размер окна), и на повреждённом потоке этот
/// прыжок может перепрыгнуть начало пакета, перекрывающегося с
/// распознанным. Закрытие таких пропусков — обязанность повторного
/// прохода, поэтому он исчерпывающий: окно проверяется на каждой
/// позиции региона. Потребитель, полагающийся на полноту приёма,
/// обязан завершать конвейер вызовом <see cref="IDataProcessor.Complete"/>:
/// финальная выдача накопленных пакетов запускает перепривязку,
/// покрывающую пропуски прямого прохода.
/// </summary>
public sealed class SlidingWindowScanner : DataProcessorBase
{
    private readonly int _windowSize;
    private readonly WindowHandler _handler;

    // Удержанный поток (все пропущенные байты) и позиция прямого прохода.
    private readonly List<byte> _retained = new();
    private int _scanPos;

    // Позиция, до которой данные уже выданы потребителю прямым проходом.
    private int _flushedPos;

    // Очередь отложенных повторных сканирований (запрошены во время прохода).
    private readonly Queue<(WindowHandler Handler, int Bound)> _rescans = new();

    private bool _scanning;
    private bool _inRescan;

    /// <summary>Создать сканер с заданным окном и обработчиком.</summary>
    /// <param name="windowSize">Размер окна, байт (≥ 1).</param>
    /// <param name="handler">Делегат обработки окна.</param>
    /// <param name="bufferSize">Порог выдачи выходных пакетов, байт.</param>
    public SlidingWindowScanner(
        int windowSize, WindowHandler handler, int bufferSize = 1200)
        : base(bufferSize)
    {
        if (windowSize < 1)
            throw new ArgumentOutOfRangeException(nameof(windowSize));
        ArgumentNullException.ThrowIfNull(handler);

        _windowSize = windowSize;
        _handler = handler;
    }

    /// <summary>Размер скользящего окна, байт.</summary>
    public int WindowSize => _windowSize;

    /// <summary>Сколько байт удержанного потока обработано прямым проходом.</summary>
    public long ConsumedBytes
    {
        get { lock (SyncRoot) return _scanPos; }
    }

    /// <summary>Объём удержанных данных (весь пропущенный поток).</summary>
    public long RetainedBytes
    {
        get { lock (SyncRoot) return _retained.Count; }
    }

    /// <summary>
    /// Уведомление о продвижении прямого прохода (обработанная позиция
    /// удержанного потока). Удобно для отчётов прогресса.
    /// </summary>
    public event Action<long>? ConsumedAdvanced;

    /// <summary>
    /// Запросить адресное повторное сканирование удержанных данных другим
    /// обработчиком (например, проверка секторов по опоздавшему заголовку).
    ///
    /// Запрос во время прямого прохода откладывается до его завершения и
    /// охватывает данные, уже выданные прямым проходом к моменту запроса
    /// (окна дальше проход обрабатывает уже с новым знанием — повторная
    /// выдача привела бы к задвоению). Запрос к простаивающему сканеру
    /// выполняется немедленно и охватывает весь пройденный объём: весь он
    /// предшествует запросу.
    /// </summary>
    /// <param name="handler">Обработчик окна повторного прохода.</param>
    public void RequestRescan(WindowHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        lock (SyncRoot)
        {
            if (_scanning)
            {
                _rescans.Enqueue((handler, _flushedPos));
                return;
            }
            Rescan(handler, ScannedExtent());
        }
    }

    /// <inheritdoc/>
    protected override void ProcessChunk(byte[] chunk)
    {
        _retained.AddRange(chunk);
        ScanForward();
        DrainRescans();
        ConsumedAdvanced?.Invoke(_scanPos);
    }

    private void ScanForward()
    {
        _scanning = true;
        try
        {
            var window = new byte[_windowSize];

            while (_scanPos + _windowSize <= _retained.Count)
            {
                _retained.CopyTo(_scanPos, window, 0, _windowSize);

                var advance = Math.Max(1, _handler(window, out var emitted));
                if (emitted is not null)
                    EmitPacket(emitted);

                _scanPos += advance;
            }
        }
        finally
        {
            _scanning = false;
        }
    }

    private void DrainRescans()
    {
        while (_rescans.Count > 0)
        {
            var (handler, bound) = _rescans.Dequeue();
            Rescan(handler, bound);
        }
    }

    private void Rescan(WindowHandler handler, int bound)
    {
        _scanning = true;
        _inRescan = true;
        try
        {
            var window = new byte[_windowSize];

            /*
             * Проход исчерпывающий: окно проверяется на каждой позиции
             * региона. Прыжок на размер окна после успеха (как в прямом
             * проходе) здесь недопустим: при повреждениях распознанное
             * окно может заканчиваться внутри чужого пакета — например,
             * фрагмент-префикс пакета, дополненный первым символом
             * следующей строки, — и прыжок перепрыгнет начало этого
             * пакета, безвозвратно потеряв его.
             */
            for (var pos = 0; pos + _windowSize <= bound; pos++)
            {
                _retained.CopyTo(pos, window, 0, _windowSize);

                handler(window, out var emitted);
                if (emitted is not null)
                    EmitPacket(emitted);
            }
        }
        finally
        {
            _inRescan = false;
            _scanning = false;
        }
    }

    /// <summary>
    /// Пройденный прямым проходом объём в байтах: все посещённые окна,
    /// включая последнее (его конец может выходить за пределы позиции прохода).
    /// </summary>
    private int ScannedExtent() =>
        _scanPos == 0
            ? 0
            : Math.Min(_retained.Count, _scanPos + _windowSize);

    /// <inheritdoc/>
    protected override void OnDelivering()
    {
        // Граница перепривязки растёт только выдачами прямого прохода:
        // повторные проходы не расширяют её. Граница охватывает все посещённые
        // окна, включая окно на текущей позиции (его конец может выходить
        // за пределы обработанной позиции).
        if (!_inRescan)
            _flushedPos = ScannedExtent();
    }
}
