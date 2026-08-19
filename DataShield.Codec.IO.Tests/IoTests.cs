using DataShield.Codec.IO;
using DataShield.Interfaces;

namespace DataShield.Codec.IO.Tests;

// ─────────────────────────────────────────────────────────────────────────────
//  Источники данных и приёмники
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Тесты реализаций IDataSource (ByteArraySource, StreamSource, FileSource)
/// и IDataWriter (PreallocatedBufferWriter, ByteListWriter, StreamDataWriter,
/// FileDataWriter).
///
/// Контракт источника: события DataReady с делегатом-«взятием» порции,
/// Completion-задача на завершение, свойство Error при сбое чтения,
/// Stop() останавливает подачу. Контракт приёмника: Attach/Detach к
/// источнику и Write(byte[]). В тестах источники гоняются в реальном
/// асинхронном режиме (без моков), сбои чтения моделируются потоками-стрелками.
/// </summary>
public sealed class IoTests
{
    /// <summary>Детерминированный псевдослучайный массив длины length.</summary>
    private static byte[] Data(int length, int seed)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    /// <summary>
    /// Прогнать источник до конца, собирая все порции через DataReady,
    /// и вернуть их списком.
    /// </summary>
    private static List<byte[]> RunSource(IDataSource source)
    {
        var chunks = new List<byte[]>();
        source.DataReady += take => chunks.Add(take());
        source.Start();
        source.Completion.Wait();
        return chunks;
    }

    /// <summary>Склеить порции обратно в один массив (в порядке доставки).</summary>
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

    // ── Источники ───────────────────────────────────────────────────────────

    /// <summary>
    /// Данные, кратные bufferSize: ровно 3 полных порции без хвоста,
    /// после завершения IsRunning = false.
    /// </summary>
    [Fact]
    public void ByteArraySource_ExactBufferMultiples_DeliveredWhole()
    {
        var data = Data(12, 1);
        var source = new ByteArraySource(data, bufferSize: 4);

        var chunks = RunSource(source);

        Assert.Equal(3, chunks.Count);
        Assert.All(chunks, c => Assert.Equal(4, c.Length));
        Assert.Equal(data, Flatten(chunks));
        Assert.False(source.IsRunning);
    }

    /// <summary>
    /// Некратный хвост: последняя порция короче буфера (2 байта при 10 = 4+4+2).
    /// </summary>
    [Fact]
    public void ByteArraySource_TrailingRemainder_DeliveredAtEof()
    {
        var data = Data(10, 2);
        var source = new ByteArraySource(data, bufferSize: 4);

        var chunks = RunSource(source);

        Assert.Equal(3, chunks.Count);
        Assert.Equal(2, chunks[^1].Length);
        Assert.Equal(data, Flatten(chunks));
    }

    /// <summary>Минимальный вход 1 байт: единственная порция доставляется целиком.</summary>
    [Fact]
    public void ByteArraySource_SingleByteRemainder_IsDelivered()
    {
        var source = new ByteArraySource(new byte[] { 42 }, bufferSize: 4);

        var chunks = RunSource(source);

        var chunk = Assert.Single(chunks);
        Assert.Equal(new byte[] { 42 }, chunk);
    }

    /// <summary>Пустой вход: ни одного события DataReady.</summary>
    [Fact]
    public void ByteArraySource_EmptyInput_NoEvents()
    {
        var source = new ByteArraySource(Array.Empty<byte>(), bufferSize: 4);

        Assert.Empty(RunSource(source));
    }

    /// <summary>
    /// Stop() изнутри обработчика: подача прекращается после текущей порции,
    /// источник корректно завершается.
    /// </summary>
    [Fact]
    public void ByteArraySource_Stop_MidStream_StopsAndServesRemainder()
    {
        var source = new ByteArraySource(Data(100, 3), bufferSize: 4);
        var chunks = new List<byte[]>();
        source.DataReady += take =>
        {
            chunks.Add(take());
            source.Stop();
        };

        source.Start();
        source.Completion.Wait();

        Assert.False(source.IsRunning);
        Assert.Single(chunks);
    }

    /// <summary>bufferSize ≤ 0 отклоняется конструктором.</summary>
    [Fact]
    public void ByteArraySource_InvalidBufferSize_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ByteArraySource(Array.Empty<byte>(), bufferSize: 0));
    }

    /// <summary>
    /// StreamSource читает поток порциями, хвост доставляется в конце;
    /// сам поток источником не закрывается (владение — у вызывающего).
    /// </summary>
    [Fact]
    public void StreamSource_ReadsStreamAndDeliversEofRemainder()
    {
        var data = Data(50, 4);
        var stream = new MemoryStream(data, writable: false);
        var source = new StreamSource(stream, bufferSize: 16);

        var chunks = RunSource(source);

        Assert.Equal(data, Flatten(chunks));
        // Поток не закрывается источником
        Assert.True(stream.CanRead);
        stream.Dispose();
    }

    /// <summary>Нечитаемый (закрытый) поток отклоняется конструктором.</summary>
    [Fact]
    public void StreamSource_UnreadableStream_Throws()
    {
        using var stream = new MemoryStream();
        stream.Dispose();
        Assert.Throws<ArgumentException>(() => new StreamSource(stream));
    }

    /// <summary>
    /// Сбой чтения: Completion завершается в состоянии faulted с тем же исключением,
    /// Error его раскрывает, IsRunning сбрасывается.
    /// </summary>
    [Fact]
    public async Task StreamSource_ReadFailure_SurfacesErrorAndFailsCompletion()
    {
        using var stream = new ThrowingStream();
        var source = new StreamSource(stream, bufferSize: 4);

        source.Start();

        await Assert.ThrowsAsync<IOException>(() => source.Completion);
        Assert.IsType<IOException>(source.Error);
        Assert.False(source.IsRunning);
    }

    /// <summary>
    /// Сбой после успешных порций: уже доставленные данные не теряются,
    /// ошибка всё равно прокидывается в Completion/Error.
    /// </summary>
    [Fact]
    public async Task StreamSource_ReadFailure_AfterGoodChunk_KeepsDeliveredData()
    {
        var prefix = Data(4, 9); // ровно один буфер выдачи
        using var stream = new PrefixThenThrowingStream(prefix);
        var source = new StreamSource(stream, bufferSize: 4);
        var chunks = new List<byte[]>();
        source.DataReady += take => chunks.Add(take());

        source.Start();

        await Assert.ThrowsAsync<IOException>(() => source.Completion);
        Assert.Equal(prefix, Flatten(chunks));
        Assert.IsType<IOException>(source.Error);
    }

    /// <summary>Поток, падающий при первом же чтении.</summary>
    private sealed class ThrowingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) =>
            throw new IOException("Simulated read failure");
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Поток: отдаёт префикс, затем падает.</summary>
    private sealed class PrefixThenThrowingStream : Stream
    {
        private readonly byte[] _prefix;
        private int _position;

        public PrefixThenThrowingStream(byte[] prefix) => _prefix = prefix;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _prefix.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override void Flush() { }

        // Пока префикс не исчерпан — отдаём его байты, после — IOException
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= _prefix.Length)
                throw new IOException("Simulated read failure");
            var n = Math.Min(count, _prefix.Length - _position);
            Buffer.BlockCopy(_prefix, _position, buffer, offset, n);
            _position += n;
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// FileSource читает временный файл в temp-каталоге и доставляет
    /// его содержимое без искажений.
    /// </summary>
    [Fact]
    public void FileSource_ReadsFileContent()
    {
        var data = Data(100, 5);
        var path = Path.Combine(Path.GetTempPath(), $"ds-io-test-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(path, data);

        using (var source = new FileSource(path, bufferSize: 16))
        {
            var chunks = RunSource(source);
            Assert.Equal(data, Flatten(chunks));
        }

        File.Delete(path);
    }

    // ── Приёмники ───────────────────────────────────────────────────────────

    /// <summary>
    /// PreallocatedBufferWriter, подключённый к источнику через Attach,
    /// собирает все порции в заранее выделенный буфер.
    /// </summary>
    [Fact]
    public void PreallocatedBufferWriter_CollectsAttachedSourceData()
    {
        var data = Data(20, 6);
        var buffer = new byte[data.Length];
        var writer = new PreallocatedBufferWriter(buffer);

        var source = new ByteArraySource(data, bufferSize: 6);
        writer.Attach(source);
        source.Start();
        source.Completion.Wait();
        writer.Detach();

        Assert.Equal(data.Length, writer.WrittenCount);
        Assert.Equal(data, writer.ToArray());
    }

    /// <summary>
    /// Переполнение предвыделенного буфера — InvalidOperationException;
    /// уже записанные до сбоя байты сохраняются.
    /// </summary>
    [Fact]
    public void PreallocatedBufferWriter_Overflow_Throws()
    {
        var writer = new PreallocatedBufferWriter(new byte[4]);

        writer.Write(new byte[3]);

        Assert.Throws<InvalidOperationException>(() => writer.Write(new byte[2]));
        Assert.Equal(3, writer.WrittenCount);
    }

    /// <summary>ByteListWriter дописывает порции во внешний List(byte).</summary>
    [Fact]
    public void ByteListWriter_AppendsToCollection()
    {
        var list = new List<byte>();
        var writer = new ByteListWriter(list);

        writer.Write(new byte[] { 1, 2, 3 });
        writer.Write(new byte[] { 4 });

        Assert.Equal(new byte[] { 1, 2, 3, 4 }, list);
        Assert.Equal(4, writer.WrittenCount);
    }

    /// <summary>StreamDataWriter пишет порции напрямую в поток.</summary>
    [Fact]
    public void StreamDataWriter_WritesIntoStream()
    {
        var stream = new MemoryStream();
        var writer = new StreamDataWriter(stream);

        writer.Write(new byte[] { 7, 8, 9 });

        Assert.Equal(new byte[] { 7, 8, 9 }, stream.ToArray());
        stream.Dispose();
    }

    /// <summary>FileDataWriter создаёт файл и записывает данные как есть.</summary>
    [Fact]
    public void FileDataWriter_WritesFile()
    {
        var data = Data(30, 7);
        var path = Path.Combine(Path.GetTempPath(), $"ds-io-test-{Guid.NewGuid():N}.bin");

        using (var writer = new FileDataWriter(path))
        {
            writer.Write(data);
        }

        Assert.Equal(data, File.ReadAllBytes(path));
        File.Delete(path);
    }

    /// <summary>
    /// Режим append: повторное открытие дописывает в конец,
    /// а не перетирает содержимое.
    /// </summary>
    [Fact]
    public void FileDataWriter_AppendMode_PreservesContent()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ds-io-test-{Guid.NewGuid():N}.bin");

        using (var writer = new FileDataWriter(path, append: false))
            writer.Write(new byte[] { 1 });
        using (var writer = new FileDataWriter(path, append: true))
            writer.Write(new byte[] { 2 });

        Assert.Equal(new byte[] { 1, 2 }, File.ReadAllBytes(path));
        File.Delete(path);
    }

    /// <summary>
    /// End-to-end: источник → приёмник через Attach переносит весь массив
    /// без потерь и переупорядочения.
    /// </summary>
    [Fact]
    public void Writer_EndToEndWithSource_TransfersWholeStream()
    {
        var data = Data(500, 8);
        var buffer = new byte[data.Length];
        var writer = new PreallocatedBufferWriter(buffer);

        var source = new ByteArraySource(data, bufferSize: 64);
        writer.Attach(source);
        source.Start();
        source.Completion.Wait();

        Assert.Equal(data, writer.ToArray());
    }
}
