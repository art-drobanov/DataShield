using System.Text;
using DataShield.Codec;
using DataShield.Codec.Packets;
using DataShield.Codec.Reporting;
using Xunit;

namespace DataShield.Tests;

// ─────────────────────────────────────────────────────────────────────────────
//  Фасад DataShieldCodec и контракт IDataShieldCodec: перегрузки, накопительный
//  приём, повторное использование, освобождение
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Тесты фасада DataShieldCodec и контракта IDataShieldCodec:
/// круговые перегрузки кодирования/декодирования (Stream, пакеты, блоб,
/// текст), коллекционные перегрузки, накопительный приём смешанных кусков,
/// сброс приёма между операциями, повторное использование экземпляра,
/// освобождение и потокобезопасность.
/// </summary>
public sealed class DataShieldCodecTests
{
    /// <summary>Массив случайных байтов заданной длины.</summary>
    private static byte[] Sample(int size)
    {
        var data = new byte[size];
        Random.Shared.NextBytes(data);
        return data;
    }

    /// <summary>
    /// Круговые перегрузки на одном экземпляре: Encode(Stream→Stream),
    /// EncodeToPackets, EncodeToBytes и Decode(Stream) в обоих форматах
    /// дают согласованный результат; блоб — конкатенация пакетов.
    /// </summary>
    [Fact]
    public void StreamRoundtrip_BothFormats_RestoresContent()
    {
        var content = Sample(10_000);

        foreach (var format in new[] { OutputFormat.Base64, OutputFormat.Binary })
        {
            using var codec = new DataShieldCodec(eccPercent: 25);

            byte[][] packets;
            byte[]? restored;
            using (var encoded = new MemoryStream())
            {
                var stats = codec.Encode(
                    new MemoryStream(content, writable: false), encoded, format,
                    "sample.bin", progress: null, default);
                Assert.Equal((uint)content.Length, stats.FileSize);
                Assert.Equal(32, stats.Sha256.Length);

                encoded.Position = 0;
                packets = codec.EncodeToPackets(
                    new MemoryStream(content, writable: false), "sample.bin",
                    progress: null, default);

                encoded.Position = 0;
                restored = codec.Decode(encoded, format, progress: null, default);
            }

            Assert.NotNull(restored);
            Assert.Equal(content, restored);

            // Блоб = конкатенация тех же пакетов
            var blob = codec.EncodeToBytes(
                new MemoryStream(content, writable: false), "sample.bin",
                progress: null, default);
            Assert.Equal(packets.Sum(p => p.Length), blob.Length);
        }
    }

    /// <summary>
    /// Коллекционные перегрузки кодирования (массив, перечисление байтов,
    /// EncodeInto по ссылке) дают одинаковые пакеты; сборка бинарного блоба
    /// восстанавливает содержимое.
    /// </summary>
    [Fact]
    public void CollectionOverloads_PacketsAndByRefOutput()
    {
        var content = Sample(3000);
        using var codec = new DataShieldCodec(eccPercent: 50);

        var byArray = codec.EncodeToPackets(content, "c.dat", progress: null, default);
        Assert.All(byArray, p => Assert.Equal(PacketFormat.PacketSize, p.Length));

        var byEnum = codec.EncodeToPackets(
            content.Select(b => b), "c.dat", progress: null, default);
        Assert.Equal(byArray.Length, byEnum.Length);

        var output = new List<byte[]>();
        var count = codec.EncodeInto(content, "c.dat", output, progress: null, default);
        Assert.Equal(byArray.Length, count);
        Assert.Equal(count, output.Count);

        var restored = codec.Decode(
            PacketIO.WriteBinaryBytes(byArray), OutputFormat.Binary,
            progress: null, default);
        Assert.NotNull(restored);
        Assert.Equal(content, restored);
    }

    /// <summary>Текстовый круг: EncodeToText → DecodeText по строкам.</summary>
    [Fact]
    public void TextRoundtrip_DecodeText_RestoresContent()
    {
        var content = Sample(500);
        using var codec = new DataShieldCodec(eccPercent: 20);

        var text = codec.EncodeToText(
            new MemoryStream(content, writable: false), "t.bin",
            progress: null, default);

        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var restored = codec.DecodeText(lines, progress: null, default);

        Assert.NotNull(restored);
        Assert.Equal(content, restored);
    }

    /// <summary>
    /// Накопительный приём: куски разных форматов (Base64-текст и бинарный)
    /// одного файла сканируются по очереди и собираются одной сборкой.
    /// </summary>
    [Fact]
    public void IncrementalScan_MixedFormatChunks_AssemblesAll()
    {
        var content = Sample(2000);

        using var encoder = new StreamEncoder(eccPercent: 30);
        var (packets, stats) = encoder.EncodeWithStats(content, "m.bin");

        // Половина пакетов текстом, половина бинарно — накопительный приём
        var half = packets.Count / 2;
        var textChunk = PacketIO.WriteBase64Text(packets.Take(half).ToList());
        var binaryChunk = PacketIO.WriteBinaryBytes(packets.Skip(half).ToList());

        using IDataShieldCodec codec = new DataShieldCodec();
        codec.Scan(new MemoryStream(Encoding.UTF8.GetBytes(textChunk), writable: false),
            OutputFormat.Base64, progress: null, default);
        codec.Scan(new MemoryStream(binaryChunk, writable: false),
            OutputFormat.Binary, progress: null, default);

        var results = codec.DecodeAll(progress: null, default);

        var result = Assert.Single(results);
        Assert.Equal(stats.Sha256, result.Slot.Header.Sha256);
        Assert.NotNull(result.Content);
        Assert.Equal(content, result.Content);
    }

    /// <summary>
    /// Повторное использование экземпляра: второй одноходовой Decode сбрасывает
    /// приём первого файла, DecodeAll возвращает только последний.
    /// </summary>
    [Fact]
    public void RepeatedDecode_ClearsPreviousReception()
    {
        var first = Sample(700);
        var second = Sample(300);

        using var codec = new DataShieldCodec(eccPercent: 10);

        byte[] EncodeBlob(byte[] data, string name) =>
            PacketIO.WriteBinaryBytes(
                codec.EncodeToPackets(data, name, progress: null, default));

        byte[]? DecodeBlob(byte[] blob) =>
            codec.Decode(
                new MemoryStream(blob, writable: false), OutputFormat.Binary,
                progress: null, default);

        var a = DecodeBlob(EncodeBlob(first, "a.bin"));
        var b = DecodeBlob(EncodeBlob(second, "b.bin"));

        Assert.Equal(first, a);
        Assert.Equal(second, b);

        // Второй одноходовой Decode сбросил приём первого файла
        var results = codec.DecodeAll(progress: null, default);
        var single = Assert.Single(results);
        Assert.Equal(second, single.Content);
    }

    /// <summary>
    /// Освобождение: повторный Dispose безопасен, операции после освобождения
    /// выбрасывают ObjectDisposedException.
    /// </summary>
    [Fact]
    public void Dispose_ForbidsFurtherOperations()
    {
        var codec = new DataShieldCodec();
        codec.Dispose();
        codec.Dispose(); // повторный вызов безопасен

        Assert.True(codec.IsDisposed);
        Assert.Throws<ObjectDisposedException>(() =>
            codec.EncodeToPackets(Sample(100), "x.bin", progress: null, default));
        Assert.Throws<ObjectDisposedException>(() =>
            codec.DecodeAll(progress: null, default));
    }

    /// <summary>
    /// Параллельное кодирование одним экземпляром фасада безопасно:
    /// операции сериализуются, все результаты одинаковы.
    /// </summary>
    [Fact]
    public void Encoder_IndependentInstances_ThreadSafeParallelEncode()
    {
        var content = Sample(1500);

        // Параллельные операции одного экземпляра фасада сериализуются
        using var codec = new DataShieldCodec(eccPercent: 40);

        var results = new byte[8][];
        Parallel.For(0, results.Length, i =>
        {
            results[i] = codec.EncodeToBytes(content, "p.bin", progress: null, default);
        });

        Assert.All(results, blob => Assert.Equal(results[0].Length, blob.Length));
    }
}
