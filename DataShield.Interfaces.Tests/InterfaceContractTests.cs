using DataShield.Interfaces;

namespace DataShield.Interfaces.Tests;

// ─────────────────────────────────────────────────────────────────────────────
//  Контрактные тесты интерфейсов потоковой модели
// ─────────────────────────────────────────────────────────────────────────────

public sealed class InterfaceContractTests
{
    // ── Тестовые двойники ───────────────────────────────────────────────────

    /// <summary>Простой синхронный источник фиксированных данных.</summary>
    private sealed class ArraySource : IDataSource
    {
        private readonly byte[] _data;

        public ArraySource(byte[] data) => _data = data;

        public int BufferSize => 4;

        public bool IsRunning { get; private set; }

        public Task Completion => Task.CompletedTask;

        public Exception? Error => null;

        public event DataReadyHandler? DataReady;

        public void Start()
        {
            IsRunning = true;
            for (var offset = 0; offset < _data.Length; offset += BufferSize)
            {
                var slice = _data.AsSpan(offset, Math.Min(BufferSize, _data.Length - offset)).ToArray();
                var taken = false;
                DataReady?.Invoke(() =>
                {
                    if (taken) return Array.Empty<byte>();
                    taken = true;
                    return slice;
                });
            }
            IsRunning = false;
        }

        public void Stop() => IsRunning = false;
    }

    /// <summary>Простой обработчик: добавляет маркер к каждому входному байту.</summary>
    private sealed class MarkerProcessor : DataProcessorBase
    {
        public const byte Marker = 0xAA;

        public MarkerProcessor() : base(bufferSize: 3)
        {
        }

        protected override void ProcessChunk(byte[] chunk)
        {
            foreach (var b in chunk)
            {
                Emit(Marker);
                Emit(b);
            }
        }
    }

    /// <summary>Простой приёмник в список.</summary>
    private sealed class ListWriter : IDataWriter
    {
        public List<byte> Received { get; } = new();

        public void Write(ReadOnlySpan<byte> data) => Received.AddRange(data);

        public void Attach(IDataSource source) =>
            source.DataReady += take => Write(take());

        public void Detach()
        {
        }
    }

    // ── Контракты ───────────────────────────────────────────────────────────

    [Fact]
    public void IDataProcessor_IsAnIDataSource()
    {
        Assert.True(typeof(IDataSource).IsAssignableFrom(typeof(IDataProcessor)));
    }

    [Fact]
    public void DataProcessorBase_ImplementsIDataProcessor()
    {
        Assert.True(typeof(IDataProcessor).IsAssignableFrom(typeof(DataProcessorBase)));
    }

    [Fact]
    public void Chain_SourceToProcessorToWriter_TransformsData()
    {
        var source = new ArraySource(new byte[] { 1, 2, 3, 4, 5 });
        var processor = new MarkerProcessor();
        var writer = new ListWriter();

        processor.Attach(source);
        writer.Attach(processor);
        processor.Start();
        processor.Complete();

        Assert.Equal(
            new byte[] { MarkerProcessor.Marker, 1, MarkerProcessor.Marker, 2, MarkerProcessor.Marker, 3, MarkerProcessor.Marker, 4, MarkerProcessor.Marker, 5 },
            writer.Received);
    }

    [Fact]
    public void TakeDelegate_SecondCall_ReturnsEmptyBuffer()
    {
        var source = new ArraySource(new byte[] { 9, 8, 7 });
        var first = Array.Empty<byte>();
        var second = Array.Empty<byte>();

        source.DataReady += take =>
        {
            first = take();
            second = take();
        };
        source.Start();

        Assert.Equal(new byte[] { 9, 8, 7 }, first);
        Assert.Empty(second);
    }

    [Fact]
    public void Detach_StopsDataFlowToProcessor()
    {
        var source = new ArraySource(new byte[] { 1, 2 });
        var processor = new MarkerProcessor();
        var writer = new ListWriter();

        processor.Attach(source);
        writer.Attach(processor);
        processor.Detach();
        source.Start();

        Assert.Empty(writer.Received);
    }

    [Fact]
    public void Writer_CanBeAttachedDirectlyToSource()
    {
        var source = new ArraySource(new byte[] { 4, 5, 6 });
        var writer = new ListWriter();

        writer.Attach(source);
        source.Start();

        Assert.Equal(new byte[] { 4, 5, 6 }, writer.Received);
    }
}
