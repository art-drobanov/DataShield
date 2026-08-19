using DataShield.Codec.StreamFilter;
using DataShield.Interfaces;

namespace DataShield.Codec.StreamFilter.Tests;

// ─────────────────────────────────────────────────────────────────────────────
//  Фильтр байтового потока по диапазонам
// ─────────────────────────────────────────────────────────────────────────────

public sealed class ByteRangeFilterTests
{
    private static readonly byte[] Base64Sample =
        "ABCDabcd0129+/==\nXYZ \tqQ9".Select(c => (byte)c).ToArray();

    private static readonly byte[] Base64Expected =
        "ABCDabcd0129+/XYZqQ9".Select(c => (byte)c).ToArray();

    private static List<byte[]> Run(IDataProcessor filter, byte[] input, int sourceChunk = 5)
    {
        var output = new List<byte[]>();
        filter.DataReady += take => output.Add(take());

        var source = new ManualSource(input, sourceChunk);
        filter.Attach(source);
        source.Pump();
        filter.Complete();
        filter.Detach();

        return output;
    }

    private static byte[] Flatten(List<byte[]> chunks)
    {
        var result = new byte[chunks.Sum(c => c.Length)];
        var offset = 0;
        foreach (var chunk in chunks)
        {
            chunk.CopyTo(result, offset);
            offset += chunk.Length;
        }
        return result;
    }

    /// <summary>Синхронный источник с ручной прокачкой кусками.</summary>
    private sealed class ManualSource : IDataSource
    {
        private readonly byte[] _data;
        private readonly int _chunkSize;

        public ManualSource(byte[] data, int chunkSize)
        {
            _data = data;
            _chunkSize = chunkSize;
        }

        public int BufferSize => _chunkSize;
        public bool IsRunning { get; private set; }
        public Task Completion => Task.CompletedTask;
        public Exception? Error => null;
        public event DataReadyHandler? DataReady;

        public void Pump()
        {
            IsRunning = true;
            for (var offset = 0; offset < _data.Length; offset += _chunkSize)
            {
                var slice = _data.AsSpan(offset, Math.Min(_chunkSize, _data.Length - offset)).ToArray();
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

        public void Start() => Pump();
        public void Stop() => IsRunning = false;
    }

    // ── Диапазоны ───────────────────────────────────────────────────────────

    [Fact]
    public void Base64Preset_PassesOnlyBase64Bytes()
    {
        var filter = ByteRangeFilter.CreateBase64();

        foreach (var b in "AZaz09+/".Select(c => (byte)c))
            Assert.True(filter.Accepts(b));

        Assert.False(filter.Accepts((byte)'='));
        Assert.False(filter.Accepts((byte)' '));
        Assert.False(filter.Accepts(0x80));
        Assert.False(filter.Accepts((byte)'\n'));
    }

    [Fact]
    public void CustomRanges_BuildAcceptanceMap()
    {
        var filter = new ByteRangeFilter(new[] { new ByteRange(10, 12) });

        Assert.False(filter.Accepts(9));
        Assert.True(filter.Accepts(10));
        Assert.True(filter.Accepts(11));
        Assert.True(filter.Accepts(12));
        Assert.False(filter.Accepts(13));
    }

    [Fact]
    public void Constructor_InvertedRange_Throws()
    {
        Assert.Throws<ArgumentException>(
            () => new ByteRangeFilter(new[] { new ByteRange(20, 10) }));
    }

    // ── Фильтрация потока ───────────────────────────────────────────────────

    [Fact]
    public void Filter_DropsGarbage_KeepsOrder()
    {
        var filter = ByteRangeFilter.CreateBase64(bufferSize: 4);

        var output = Run(filter, Base64Sample);

        Assert.Equal(Base64Expected, Flatten(output));
    }

    [Fact]
    public void Filter_DeliversExactBufferSizeChunks()
    {
        var filter = ByteRangeFilter.CreateBase64(bufferSize: 4);

        var output = Run(filter, Base64Sample);

        // Все выдачи, кроме последней, имеют точный размер буфера
        for (var i = 0; i < output.Count - 1; i++)
            Assert.Equal(4, output[i].Length);
    }

    [Fact]
    public void Complete_FlushesPartialRemainder()
    {
        var filter = ByteRangeFilter.CreateBase64(bufferSize: 100);

        var output = Run(filter, Base64Sample);

        var chunk = Assert.Single(output);
        Assert.Equal(Base64Expected, chunk);
    }

    [Fact]
    public void Filter_AllGarbage_NoOutput()
    {
        var filter = ByteRangeFilter.CreateBase64();

        Assert.Empty(Run(filter, new byte[] { 0, 1, 2, (byte)'=', (byte)'\n', 0xFF }));
    }

    [Fact]
    public void Filter_InputSplitAcrossChunks_IsContinuous()
    {
        var filter = ByteRangeFilter.CreateBase64(bufferSize: 3);
        var input = "aGb0cZ+/".Select(c => (byte)c).ToArray();

        var output = Run(filter, input, sourceChunk: 1);

        Assert.Equal(input, Flatten(output));
    }
}
