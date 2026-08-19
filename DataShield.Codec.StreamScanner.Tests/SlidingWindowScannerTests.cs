using DataShield.Codec.StreamScanner;
using DataShield.Interfaces;

namespace DataShield.Codec.StreamScanner.Tests;

// ─────────────────────────────────────────────────────────────────────────────
//  Побайтовый сканер со скользящим окном
// ─────────────────────────────────────────────────────────────────────────────

public sealed class SlidingWindowScannerTests
{
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

    /// <summary>
    /// Обработчик окна: распознаёт точное совпадение с образцом,
    /// при успехе выбрасывает окно и проматывает на его длину.
    /// </summary>
    private static int MatchExact(byte[] pattern, ReadOnlySpan<byte> window, out byte[]? emitted)
    {
        emitted = null;
        if (!window.SequenceEqual(pattern)) return 1;
        emitted = window.ToArray();
        return pattern.Length;
    }

    private static List<byte[]> Run(
        SlidingWindowScanner scanner, byte[] input, int chunkSize = 8)
    {
        var output = new List<byte[]>();
        scanner.DataReady += take => output.Add(take());

        var source = new ManualSource(input, chunkSize);
        scanner.Attach(source);
        source.Pump();
        scanner.Complete();
        scanner.Detach();

        return output;
    }

    private static byte[] Bytes(string s) => s.Select(c => (byte)c).ToArray();

    // ── Прямой проход ───────────────────────────────────────────────────────

    [Fact]
    public void Scan_RecognizedWindows_AdvanceByWindow()
    {
        var scanner = new SlidingWindowScanner(
            3, (w, out e) => MatchExact(Bytes("ABC"), w, out e));

        var output = Run(scanner, Bytes("ABCABCABC"));

        // Все три окна распознаны — выход содержит ровно их байты
        var flat = output.SelectMany(c => c).ToArray();
        Assert.Equal(Bytes("ABCABCABC"), flat);
    }

    [Fact]
    public void Scan_UnrecognizedWindow_ShiftsByOne()
    {
        // ABC (принимается) + xxABC: окно сдвигается по одному до совпадения
        var scanner = new SlidingWindowScanner(
            3, (w, out e) => MatchExact(Bytes("ABC"), w, out e));

        var output = Run(scanner, Bytes("AxxABCABC"));

        var packets = new List<byte[]>();
        foreach (var chunk in output)
            for (var i = 0; i < chunk.Length; i += 3)
                packets.Add(chunk.AsSpan(i, 3).ToArray());

        Assert.Equal(2, packets.Count);
        Assert.All(packets, p => Assert.Equal(Bytes("ABC"), p));
    }

    [Fact]
    public void Scan_WindowSpansChunkBoundary_IsFound()
    {
        var scanner = new SlidingWindowScanner(
            3, (w, out e) => MatchExact(Bytes("ABC"), w, out e));

        // Кускы по 2 байта: окно ABC всегда разрезано границей куска
        var output = Run(scanner, Bytes("xxABCxx"), chunkSize: 2);

        Assert.Single(output);
        Assert.Equal(Bytes("ABC"), output[0]);
    }

    [Fact]
    public void Scan_ShortInput_NoWindows()
    {
        var scanner = new SlidingWindowScanner(
            5, (w, out e) => MatchExact(Bytes("ABCDE"), w, out e));

        Assert.Empty(Run(scanner, Bytes("ABC")));
    }

    [Fact]
    public void Scan_TrailingPartialWindow_IsNotScanned()
    {
        var scanner = new SlidingWindowScanner(
            3, (w, out e) => MatchExact(Bytes("ABC"), w, out e));

        // Хвост "AB" короче окна и не сканируется
        var output = Run(scanner, Bytes("ABCAB"));

        Assert.Single(output);
    }

    [Fact]
    public void Constructor_InvalidArguments_Throw()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new SlidingWindowScanner(0, (w, out e) => { e = null; return 1; }));
        Assert.Throws<ArgumentNullException>(
            () => new SlidingWindowScanner(3, null!));
    }

    // ── Прогресс и удержание ────────────────────────────────────────────────

    [Fact]
    public void ConsumedAdvanced_ReportsProgress()
    {
        var scanner = new SlidingWindowScanner(
            3, (w, out e) => MatchExact(Bytes("ABC"), w, out e));
        var consumed = new List<long>();
        scanner.ConsumedAdvanced += consumed.Add;

        Run(scanner, Bytes("ABCABC"), chunkSize: 3);

        Assert.Equal(6, consumed[^1]);
        Assert.Equal(6, scanner.ConsumedBytes);
        Assert.Equal(6, scanner.RetainedBytes);
    }

    // ── Повторное сканирование ──────────────────────────────────────────────

    [Fact]
    public void RequestRescan_IdleScanner_CoversAllScannedData()
    {
        // Основной проход принимает только XYZ; ABC пропускается сдвигом
        var scanner = new SlidingWindowScanner(
            3, (w, out e) => MatchExact(Bytes("XYZ"), w, out e));

        var output = Run(scanner, Bytes("ABCXYZ"));
        Assert.Single(output); // только XYZ

        // Адресная перепривязка: ищем ABC по удержанным данным
        scanner.RequestRescan((w, out e) => MatchExact(Bytes("ABC"), w, out e));
        scanner.Complete();

        Assert.Equal(2, output.Count);
        Assert.Equal(Bytes("ABC"), output[1]);
    }

    [Fact]
    public void RequestRescan_DuringScan_IsDeferredAndBounded()
    {
        var windowSize = 4;
        var patternB = Bytes("BBBB");
        var patternA = Bytes("AAAA");
        var patternZ = Bytes("ZZZZ");

        // Данные: AAAA B*20 ZZZZ. Основной проход принимает только BBBB
        // (5 окон), выдача — 4-байтные пакеты, порог буфера 16 байт.
        var data = patternA.Concat(Enumerable.Repeat((byte)'B', 20))
            .Concat(patternZ).ToArray();

        var scanner = new SlidingWindowScanner(
            windowSize, (w, out e) => MatchExact(patternB, w, out e),
            bufferSize: 16);

        var output = new List<byte[]>();
        scanner.DataReady += take =>
        {
            output.Add(take());
            // По первой выдаче просим перепривязку: принять AAAA и ZZZZ.
            // Прямой проход активен — запрос откладывается и ограничивается
            // границей выдачи (AAAA внутри, ZZZZ после неё — вне границы).
            if (output.Count == 1)
                scanner.RequestRescan((w, out e) => Match(w, out e));
        };

        int Match(ReadOnlySpan<byte> w, out byte[]? e)
        {
            if (w.SequenceEqual(patternA)) { e = w.ToArray(); return 4; }
            if (w.SequenceEqual(patternZ)) { e = w.ToArray(); return 4; }
            e = null;
            return 1;
        }

        var source = new ManualSource(data, 4);
        scanner.Attach(source);
        source.Pump();
        scanner.Complete();

        var packets = new List<byte[]>();
        foreach (var chunk in output)
            for (var i = 0; i < chunk.Length; i += windowSize)
                packets.Add(chunk.AsSpan(i, windowSize).ToArray());

        // 5 окон BBBB прямым проходом + AAAA перепривязкой; ZZZZ — за границей
        Assert.Equal(6, packets.Count);
        Assert.Contains(packets, p => p.SequenceEqual(patternA));
        Assert.DoesNotContain(packets, p => p.SequenceEqual(patternZ));
        Assert.Equal(5, packets.Count(p => p.SequenceEqual(patternB)));
    }

    [Fact]
    public void RequestRescan_RecoversWindowSkippedByDirectPassJump()
    {
        // Прямой проход распознаёт ABC в позиции 0 и прыгает на 3 байта,
        // перепрыгивая начало окна BCD (позиция 1), перекрывающегося с ABC.
        // Исчерпывающий повторный проход проверяет каждую позицию и находит BCD.
        var scanner = new SlidingWindowScanner(
            3, (w, out e) => MatchExact(Bytes("ABC"), w, out e));

        var output = Run(scanner, Bytes("ABCD"));

        Assert.Single(output);                   // прямым проходом — только ABC
        Assert.Equal(3, scanner.ConsumedBytes);  // проход остановился на позиции 3
        Assert.Equal(4, scanner.RetainedBytes);  // но поток удержан целиком

        scanner.RequestRescan((w, out e) => MatchExact(Bytes("BCD"), w, out e));
        scanner.Complete();

        Assert.Equal(2, output.Count);
        Assert.Equal(Bytes("BCD"), output[1]);
    }

    [Fact]
    public void RetainedData_PersistsAcrossAttachedSources()
    {
        // Первый источник: ABC не распознан (обработчик ищет XYZ)
        var scanner = new SlidingWindowScanner(
            3, (w, out e) => MatchExact(Bytes("XYZ"), w, out e));

        var output = new List<byte[]>();
        scanner.DataReady += take => output.Add(take());

        var first = new ManualSource(Bytes("ABC"), 3);
        scanner.Attach(first);
        first.Pump();
        scanner.Complete();
        Assert.Empty(output); // ничего не распознано

        // Удержанные данные первого куска сохраняются: перепривязка находит ABC
        scanner.RequestRescan((w, out e) => MatchExact(Bytes("ABC"), w, out e));
        scanner.Complete();

        // Второй источник добавляет XYZ — распознаётся прямым проходом
        scanner.Detach();
        var second = new ManualSource(Bytes("XYZ"), 3);
        scanner.Attach(second);
        second.Pump();
        scanner.Complete();

        var flat = output.SelectMany(c => c).ToArray();
        Assert.Equal(Bytes("ABCXYZ"), flat);
    }
}
