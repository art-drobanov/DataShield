using DataShield.Codec.IO;
using DataShield.Interfaces;

namespace DataShield.Codec.IO.Tests;

// ─────────────────────────────────────────────────────────────────────────────
//  Источники данных и приёмники
// ─────────────────────────────────────────────────────────────────────────────

public sealed class IoTests
{
    private static byte[] Data(int length, int seed)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    private static List<byte[]> RunSource(IDataSource source)
    {
        var chunks = new List<byte[]>();
        source.DataReady += take => chunks.Add(take());
        source.Start();
        source.Completion.Wait();
        return chunks;
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

    // ── Источники ───────────────────────────────────────────────────────────

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

    [Fact]
    public void ByteArraySource_SingleByteRemainder_IsDelivered()
    {
        var source = new ByteArraySource(new byte[] { 42 }, bufferSize: 4);

        var chunks = RunSource(source);

        var chunk = Assert.Single(chunks);
        Assert.Equal(new byte[] { 42 }, chunk);
    }

    [Fact]
    public void ByteArraySource_EmptyInput_NoEvents()
    {
        var source = new ByteArraySource(Array.Empty<byte>(), bufferSize: 4);

        Assert.Empty(RunSource(source));
    }

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

    [Fact]
    public void ByteArraySource_InvalidBufferSize_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ByteArraySource(Array.Empty<byte>(), bufferSize: 0));
    }

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

    [Fact]
    public void StreamSource_UnreadableStream_Throws()
    {
        using var stream = new MemoryStream();
        stream.Dispose();
        Assert.Throws<ArgumentException>(() => new StreamSource(stream));
    }

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

    [Fact]
    public void PreallocatedBufferWriter_Overflow_Throws()
    {
        var writer = new PreallocatedBufferWriter(new byte[4]);

        writer.Write(new byte[3]);

        Assert.Throws<InvalidOperationException>(() => writer.Write(new byte[2]));
        Assert.Equal(3, writer.WrittenCount);
    }

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

    [Fact]
    public void StreamDataWriter_WritesIntoStream()
    {
        var stream = new MemoryStream();
        var writer = new StreamDataWriter(stream);

        writer.Write(new byte[] { 7, 8, 9 });

        Assert.Equal(new byte[] { 7, 8, 9 }, stream.ToArray());
        stream.Dispose();
    }

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
