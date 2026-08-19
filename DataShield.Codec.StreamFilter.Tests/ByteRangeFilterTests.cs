using DataShield.Codec.StreamFilter;
using DataShield.Interfaces;

namespace DataShield.Codec.StreamFilter.Tests;

// ─────────────────────────────────────────────────────────────────────────────
//  Фильтр байтового потока по диапазонам
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Тесты ByteRangeFilter: пропускной фильтрации потока по диапазонам байтов.
///
/// Пресет Base64 оставляет A–Z, a–z, 0–9, +, /; пробелы, переводы строк
/// и прочий мусор отбрасываются. Проверяются: карта приёмки, сборка
/// по кастомным диапазонам, валидация конструктора, выдача порциями
/// фиксированного размера с флашем хвоста при Complete и непрерывность
/// фильтрации при произвольной нарезке входа кусками.
/// </summary>
public sealed class ByteRangeFilterTests
{
    /// <summary>Вход с мусором: Base64-символы вперемешку с «=», \n, пробелом, \t.</summary>
    private static readonly byte[] Base64Sample =
        "ABCDabcd0129+/==\nXYZ \tqQ9".Select(c => (byte)c).ToArray();

    /// <summary>Ожидаемый выход: только символы алфавита Base64.</summary>
    private static readonly byte[] Base64Expected =
        "ABCDabcd0129+/XYZqQ9".Select(c => (byte)c).ToArray();

    /// <summary>
    /// Прогнать вход через фильтр (источник режет его кусками по sourceChunk)
    /// и собрать все выдачи фильтра списком.
    /// </summary>
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

    /// <summary>Склеить выдачи в один массив (в порядке доставки).</summary>
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

    /// <summary>
    /// Пресет Base64: буквы, цифры, «+» и «/» проходят; «=», пробельные
    /// и любые байты ≥0x80 — нет.
    /// </summary>
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

    /// <summary>Кастомные диапазоны: границы включаются, вне — отбрасывается.</summary>
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

    /// <summary>Инвертированный диапазон (from > to) — ArgumentException.</summary>
    [Fact]
    public void Constructor_InvertedRange_Throws()
    {
        Assert.Throws<ArgumentException>(
            () => new ByteRangeFilter(new[] { new ByteRange(20, 10) }));
    }

    // ── Фильтрация потока ───────────────────────────────────────────────────

    /// <summary>Мусор удаляется, порядок годных байтов сохраняется.</summary>
    [Fact]
    public void Filter_DropsGarbage_KeepsOrder()
    {
        var filter = ByteRangeFilter.CreateBase64(bufferSize: 4);

        var output = Run(filter, Base64Sample);

        Assert.Equal(Base64Expected, Flatten(output));
    }

    /// <summary>
    /// Буферизация: все выдачи, кроме последней, имеют точный размер
    /// bufferSize — сканер-потребитель получает плотные порции.
    /// </summary>
    [Fact]
    public void Filter_DeliversExactBufferSizeChunks()
    {
        var filter = ByteRangeFilter.CreateBase64(bufferSize: 4);

        var output = Run(filter, Base64Sample);

        // Все выдачи, кроме последней, имеют точный размер буфера
        for (var i = 0; i < output.Count - 1; i++)
            Assert.Equal(4, output[i].Length);
    }

    /// <summary>
    /// Complete флашит недонабранный буфер: при большом bufferSize весь
    /// выход приходит одной выдачей.
    /// </summary>
    [Fact]
    public void Complete_FlushesPartialRemainder()
    {
        var filter = ByteRangeFilter.CreateBase64(bufferSize: 100);

        var output = Run(filter, Base64Sample);

        var chunk = Assert.Single(output);
        Assert.Equal(Base64Expected, chunk);
    }

    /// <summary>Вход из одного мусора — ни одной выдачи.</summary>
    [Fact]
    public void Filter_AllGarbage_NoOutput()
    {
        var filter = ByteRangeFilter.CreateBase64();

        Assert.Empty(Run(filter, new byte[] { 0, 1, 2, (byte)'=', (byte)'\n', 0xFF }));
    }

    /// <summary>
    /// Непрерывность: вход, порезанный по 1 байту, фильтруется как единое
    /// целое — ничего не теряется и не дублируется.
    /// </summary>
    [Fact]
    public void Filter_InputSplitAcrossChunks_IsContinuous()
    {
        var filter = ByteRangeFilter.CreateBase64(bufferSize: 3);
        var input = "aGb0cZ+/".Select(c => (byte)c).ToArray();

        var output = Run(filter, input, sourceChunk: 1);

        Assert.Equal(input, Flatten(output));
    }
}
