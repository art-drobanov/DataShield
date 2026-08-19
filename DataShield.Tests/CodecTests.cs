using System.Buffers.Binary;
using DataShield.Codec;
using DataShield.Codec.Ecc;
using DataShield.Codec.Packets;
using Xunit;

namespace DataShield.Tests;

public class CodecTests
{
    private const int Payload = PacketFormat.PayloadSize; // 64

    private static byte[] RandomBytes(int len, int seed)
    {
        var r = new Random(seed);
        var b = new byte[len];
        r.NextBytes(b);
        return b;
    }

    private static string[] SplitLines(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries);

    // ────────────────────────────────────────────────────────────────────────
    //  Константы формата
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Packet_Size_Is_75() => Assert.Equal(75, PacketFormat.PacketSize);

    [Fact]
    public void Base64_Size_Is_100() => Assert.Equal(100, PacketFormat.Base64Size);

    [Fact]
    public void Content_Sizes_Are_51_And_66()
    {
        Assert.Equal(51, PacketFormat.HeaderContentSize);
        Assert.Equal(66, PacketFormat.SectorContentSize);
        Assert.Equal(24, PacketFormat.HeaderHashSize);
        Assert.Equal(9, PacketFormat.SectorHashSize);
        Assert.Equal(2, PacketFormat.SectorNumberSize);
    }

    [Fact]
    public void Payload_Is_64_And_Even()
    {
        Assert.Equal(64, PacketFormat.PayloadSize);
        Assert.Equal(0, PacketFormat.PayloadSize % 2);
    }

    [Fact]
    public void Header_Field_Offsets_Correct()
    {
        Assert.Equal(0, PacketFormat.FileNameOffset);
        Assert.Equal(14, PacketFormat.FileSizeOffset);
        Assert.Equal(17, PacketFormat.Sha256Offset);
        Assert.Equal(49, PacketFormat.EccCountOffset);
        Assert.Equal(51, PacketFormat.HeaderHashOffset);
    }

    // ────────────────────────────────────────────────────────────────────────
    //  HeaderContent сериализация
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void HeaderContent_Roundtrip_Preserves_All_Fields()
    {
        var sha = RandomBytes(32, 99);
        var original = new HeaderContent
        {
            FileName = "test.dat",
            FileSize = 12345,
            Sha256 = sha,
            EccCount = 7,
        };

        var bytes = original.ToBytes();
        Assert.Equal(PacketFormat.HeaderContentSize, bytes.Length);

        var restored = HeaderContent.ReadFrom(bytes);
        Assert.Equal("test.dat", restored.FileName);
        Assert.Equal((uint)12345, restored.FileSize);
        Assert.Equal(sha, restored.Sha256);
        Assert.Equal((ushort)7, restored.EccCount);
    }

    [Fact]
    public void HeaderContent_FileName_Space_Padded()
    {
        var header = new HeaderContent
        {
            FileName = "ab",
            FileSize = 0,
            Sha256 = new byte[32],
            EccCount = 0,
        };
        var bytes = header.ToBytes();
        // Байты после "ab" должны быть пробелами
        Assert.Equal((byte)'a', bytes[0]);
        Assert.Equal((byte)'b', bytes[1]);
        for (var i = 2; i < PacketFormat.FileNameSize; i++)
            Assert.Equal((byte)' ', bytes[i]);
    }

    [Fact]
    public void HeaderContent_LongName_Throws()
    {
        var longName = new string('x', PacketFormat.FileNameSize + 1);
        var header = new HeaderContent
        {
            FileName = longName,
            FileSize = 0,
            Sha256 = new byte[32],
            EccCount = 0,
        };
        Assert.Throws<InvalidOperationException>(() => header.ToBytes());
    }

    [Fact]
    public void HeaderContent_MaxName_OK()
    {
        var name14 = new string('y', PacketFormat.FileNameSize);
        var header = new HeaderContent
        {
            FileName = name14,
            FileSize = 0,
            Sha256 = new byte[32],
            EccCount = 0,
        };
        var bytes = header.ToBytes();
        var restored = HeaderContent.ReadFrom(bytes);
        Assert.Equal(name14, restored.FileName);
    }

    // ────────────────────────────────────────────────────────────────────────
    //  Базовый roundtrip encode → decode
    // ────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1, 1)]
    [InlineData(100, 2)]
    [InlineData(1024, 3)]
    [InlineData(5000, 4)]
    [InlineData(Payload, 5)]            // ровно один сектор
    [InlineData(Payload * 3, 6)]        // ровно три сектора
    [InlineData(Payload * 3 + 17, 7)]   // три сектора + хвост
    public void Encode_Decode_Roundtrip(int size, int seed)
    {
        var content = RandomBytes(size, seed);
        var encoder = new FileEncoder(eccPercent: 0);
        var text = encoder.EncodeToText(content, "test.bin");

        var decoder = new FileDecoder();
        decoder.Scan(SplitLines(text));

        Assert.Single(decoder.Slots);
        var slot = decoder.Slots[0];
        var restored = decoder.TryAssemble(slot.Header);

        Assert.NotNull(restored);
        Assert.Equal(content, restored);
    }

    [Fact]
    public void Roundtrip_With_Ecc()
    {
        var content = RandomBytes(5000, 42);
        var encoder = new FileEncoder(eccPercent: 20);
        var text = encoder.EncodeToText(content, "data.bin");

        var decoder = new FileDecoder();
        decoder.Scan(SplitLines(text));

        Assert.Single(decoder.Slots);
        var slot = decoder.Slots[0];
        Assert.True(slot.EccCount > 0);
        var restored = decoder.TryAssemble(slot.Header);
        Assert.Equal(content, restored);
    }

    [Fact]
    public void Empty_File_Roundtrip()
    {
        var content = Array.Empty<byte>();
        var encoder = new FileEncoder(eccPercent: 0);
        var text = encoder.EncodeToText(content, "empty.bin");

        var decoder = new FileDecoder();
        decoder.Scan(SplitLines(text));

        Assert.Single(decoder.Slots);
        var restored = decoder.TryAssemble(decoder.Slots[0].Header);
        Assert.NotNull(restored);
        Assert.Empty(restored);
    }

    // ────────────────────────────────────────────────────────────────────────
    //  SHA-256 и заголовок
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Header_Contains_Correct_SHA256()
    {
        var content = RandomBytes(2000, 5);
        var encoder = new FileEncoder(eccPercent: 10);
        var text = encoder.EncodeToText(content, "hash.bin");

        var decoder = new FileDecoder();
        decoder.Scan(SplitLines(text));

        var slot = decoder.Slots[0];
        var expectedSha = Sha256Compact.HashData(content);
        Assert.Equal(expectedSha, slot.Header.Sha256);
    }

    [Fact]
    public void Header_Fields_Preserved()
    {
        var content = RandomBytes(1000, 5);
        var encoder = new FileEncoder(eccPercent: 15);
        var text = encoder.EncodeToText(content, "my-file.dat");

        var decoder = new FileDecoder();
        decoder.Scan(SplitLines(text));

        var slot = decoder.Slots[0];
        Assert.Equal("my-file.dat", slot.Header.FileName);
        Assert.Equal((uint)1000, slot.Header.FileSize);
        Assert.True(slot.Header.EccCount >= 1);
    }

    [Fact]
    public void File_Long_Name_Is_Packed_Into_Field()
    {
        var longName = new string('x', 50) + ".bin";
        var content = RandomBytes(100, 9);

        var encoder = new FileEncoder(eccPercent: 0);
        var text = encoder.EncodeToText(content, longName);

        var decoder = new FileDecoder();
        decoder.Scan(SplitLines(text));

        Assert.Single(decoder.Slots);
        // База усечена до 9 символов + маркер «~», расширение сохранено
        Assert.Equal(
            new string('x', 9) + "~.bin",
            decoder.Slots[0].Header.FileName);
    }

    // ────────────────────────────────────────────────────────────────────────
    //  Накопление заголовков
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Header_Reception_Count_Is_Tracked()
    {
        var content = RandomBytes(500, 1);
        var encoder = new FileEncoder(eccPercent: 10);
        var packets = encoder.Encode(content, "count.bin");

        var decoder = new FileDecoder();
        var lines = packets.Select(p => Convert.ToBase64String(p)).ToArray();
        decoder.Scan(lines);

        Assert.Single(decoder.Slots);
        var slot = decoder.Slots[0];
        // Минимум 3 заголовка по стратегии
        Assert.True(slot.HeaderReceptionCount >= 3,
            $"Ожидалось >= 3 приёмов заголовка, получено {slot.HeaderReceptionCount}");
    }

    [Fact]
    public void Header_Strategy_At_Least_3_Copies()
    {
        var content = RandomBytes(100, 7); // 2 data-тома
        var encoder = new FileEncoder(eccPercent: 0);
        var packets = encoder.Encode(content, "small.bin");

        var headerCount = packets.Count(IsHeaderPacket);
        Assert.True(headerCount >= 3, $"Должно быть >= 3 заголовков, получено {headerCount}");
    }

    [Fact]
    public void Header_Strategy_At_Most_5_Percent()
    {
        var content = RandomBytes(64000, 11); // 1000 data-томов
        var encoder = new FileEncoder(eccPercent: 10);
        var packets = encoder.Encode(content, "big.bin");

        var dataCount = (64000 + Payload - 1) / Payload; // 1000
        var eccCount = FileEncoder.ComputeEccCount(dataCount, 10);
        var total = dataCount + eccCount;
        var headerCount = packets.Count(IsHeaderPacket);

        var fivePct = Math.Ceiling(total * 0.05);
        Assert.True(headerCount <= fivePct + 2,
            $"Заголовков {headerCount}, ожидаемое максимум ~{fivePct}");
    }

    // ────────────────────────────────────────────────────────────────────────
    //  Хеш-связывание сектора с заголовком через сид = H5
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Data_Sector_Hash_Uses_HeaderHash_As_Seed()
    {
        var content = RandomBytes(300, 11);
        var encoder = new FileEncoder(eccPercent: 0);
        var packets = encoder.Encode(content, "test.bin");

        // Находим первый data-сектор (не заголовок)
        var dataSector = packets.First(p => !IsHeaderPacket(p));

        // Вычисляем H5 из заголовка
        var headerPacket = packets.First(IsHeaderPacket);
        var h5 = PacketHasher.ComputeHeaderHash(
            headerPacket.AsSpan(0, PacketFormat.HeaderContentSize));

        // Хеш сектора должен сходиться при сиде = H5
        var hash = PacketHasher.ComputeSectorHash(
            dataSector.AsSpan(0, PacketFormat.SectorContentSize), h5);
        var stored = dataSector.AsSpan(
            PacketFormat.SectorHashOffset,
            PacketFormat.SectorHashSize);
        Assert.True(stored.SequenceEqual(hash));
    }

    [Fact]
    public void Data_Sector_Wrong_HeaderHash_Mismatch()
    {
        var content = RandomBytes(300, 12);
        var encoder = new FileEncoder(eccPercent: 0);
        var packets = encoder.Encode(content, "test.bin");
        var dataSector = packets.First(p => !IsHeaderPacket(p));

        // Чужой H5
        var wrongH5 = PacketHasher.ComputeHeaderHash(new byte[PacketFormat.HeaderContentSize]);
        Assert.False(PacketHasher.VerifySectorPacket(dataSector, wrongH5));
    }

    [Fact]
    public void Header_Hash_Is_Autonomous()
    {
        var content = RandomBytes(300, 13);
        var encoder = new FileEncoder(eccPercent: 0);
        var packets = encoder.Encode(content, "test.bin");

        // Хеш заголовка проверяется автономно
        var headerPacket = packets.First(IsHeaderPacket);
        Assert.True(PacketHasher.VerifyHeaderPacket(headerPacket));
    }

    // ────────────────────────────────────────────────────────────────────────
    //  ECC восстановление
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Missing_Sectors_Recovered_Via_Ecc()
    {
        var content = RandomBytes(5000, 77);
        var encoder = new FileEncoder(eccPercent: 30);
        var text = encoder.EncodeToText(content, "ecc.bin");
        var lines = SplitLines(text).ToList();

        // Удалим 2 data-сектора (не заголовки)
        var removed = 0;
        for (var i = lines.Count - 1; i >= 0 && removed < 2; i--)
        {
            if (!IsHeaderLine(lines[i]))
            {
                lines.RemoveAt(i);
                removed++;
            }
        }

        var decoder = new FileDecoder();
        decoder.Scan(lines);

        Assert.Single(decoder.Slots);
        var restored = decoder.TryAssemble(decoder.Slots[0].Header);
        Assert.NotNull(restored);
        Assert.Equal(content, restored);
    }

    [Fact]
    public void Too_Many_Missing_No_Recovery()
    {
        var content = RandomBytes(5000, 21);
        var encoder = new FileEncoder(eccPercent: 5);
        var text = encoder.EncodeToText(content, "fail.bin");
        var lines = SplitLines(text).ToList();

        // Удалим все data-сектора
        for (var i = lines.Count - 1; i >= 0; i--)
        {
            if (!IsHeaderLine(lines[i]))
                lines.RemoveAt(i);
        }

        var decoder = new FileDecoder();
        decoder.Scan(lines);

        Assert.Single(decoder.Slots);
        var restored = decoder.TryAssemble(decoder.Slots[0].Header);
        Assert.Null(restored);
    }

    // ────────────────────────────────────────────────────────────────────────
    //  RS codec — прямые тесты
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Rs_Encode_Decode_Recover_One_Erasure()
    {
        const int K = 10, M = 3;
        var rnd = new Random(42);
        var data = new byte[K][];
        for (var i = 0; i < K; i++) { data[i] = new byte[Payload]; rnd.NextBytes(data[i]); }

        var rs = new RsCodecAdapter();
        var ecc = rs.Encode(data, M);
        Assert.Equal(M, ecc.Count);

        var total = K + M;
        var received = new byte[total][];
        var valid = new bool[total];
        for (var i = 0; i < K; i++) { received[i] = data[i]; valid[i] = true; }
        for (var i = 0; i < M; i++) { received[K + i] = ecc[i]; valid[K + i] = true; }

        valid[3] = false; received[3] = null!;

        var recovered = rs.Decode(received, valid, K);
        Assert.NotNull(recovered);
        Assert.Equal(K, recovered!.Count);
        Assert.Equal(data[3], recovered[3]);
    }

    [Fact]
    public void Rs_Encode_Decode_Recover_Multiple_Erasures()
    {
        const int K = 20, M = 5;
        var rnd = new Random(7);
        var data = new byte[K][];
        for (var i = 0; i < K; i++) { data[i] = new byte[Payload]; rnd.NextBytes(data[i]); }

        var rs = new RsCodecAdapter();
        var ecc = rs.Encode(data, M);

        var total = K + M;
        var received = new byte[total][];
        var valid = new bool[total];
        for (var i = 0; i < K; i++) { received[i] = data[i]; valid[i] = true; }
        for (var i = 0; i < M; i++) { received[K + i] = ecc[i]; valid[K + i] = true; }

        valid[5] = false; received[5] = null!;
        valid[10] = false; received[10] = null!;
        valid[15] = false; received[15] = null!;
        valid[K + 1] = false; received[K + 1] = null!;
        valid[K + 3] = false; received[K + 3] = null!;

        var recovered = rs.Decode(received, valid, K);
        Assert.NotNull(recovered);
        Assert.Equal(data[5], recovered![5]);
        Assert.Equal(data[10], recovered[10]);
        Assert.Equal(data[15], recovered[15]);
    }

    [Fact]
    public void Rs_Too_Many_Erasures_Returns_Null()
    {
        const int K = 10, M = 2;
        var rnd = new Random(1);
        var data = new byte[K][];
        for (var i = 0; i < K; i++) { data[i] = new byte[Payload]; rnd.NextBytes(data[i]); }

        var rs = new RsCodecAdapter();
        var ecc = rs.Encode(data, M);

        var total = K + M;
        var received = new byte[total][];
        var valid = new bool[total];
        for (var i = 0; i < K; i++) { received[i] = data[i]; valid[i] = true; }
        for (var i = 0; i < M; i++) { received[K + i] = ecc[i]; valid[K + i] = true; }

        valid[1] = false; received[1] = null!;
        valid[2] = false; received[2] = null!;
        valid[3] = false; received[3] = null!;

        Assert.Null(rs.Decode(received, valid, K));
    }

    [Fact]
    public void Rs_Throws_When_K_Plus_M_Exceeds_GF16()
    {
        var rs = new RsCodecAdapter();
        var k = GF16.GFSizeConst;
        var data = new byte[k][];
        for (var i = 0; i < k; i++) data[i] = new byte[2];
        Assert.Throws<InvalidOperationException>(() => rs.Encode(data, 1));
    }

    // ────────────────────────────────────────────────────────────────────────
    //  Мультифайловый поток
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Two_Files_In_One_Stream_Separated()
    {
        var a = RandomBytes(1500, 1);
        var b = RandomBytes(2500, 2);

        var encoder = new FileEncoder(eccPercent: 10);
        var textA = encoder.EncodeToText(a, "file_a.bin");
        var textB = encoder.EncodeToText(b, "file_b.bin");

        var lines = SplitLines(textA + textB).ToList();
        var rnd = new Random(123);
        for (var i = lines.Count - 1; i > 0; i--)
        {
            var j = rnd.Next(i + 1);
            (lines[i], lines[j]) = (lines[j], lines[i]);
        }

        var decoder = new FileDecoder();
        decoder.Scan(lines);

        Assert.Equal(2, decoder.FileCount);

        foreach (var slot in decoder.Slots)
        {
            var restored = decoder.TryAssemble(slot.Header);
            Assert.NotNull(restored);
            if (slot.Header.FileName == "file_a.bin")
                Assert.Equal(a, restored);
            else
                Assert.Equal(b, restored);
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    //  Помехоустойчивость сканера
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Random_Order_Still_Assembled()
    {
        var content = RandomBytes(3500, 42);
        var encoder = new FileEncoder(eccPercent: 0);
        var lines = SplitLines(encoder.EncodeToText(content, "rnd.bin")).ToList();

        var rnd = new Random(7);
        for (var i = lines.Count - 1; i > 0; i--)
        {
            var j = rnd.Next(i + 1);
            (lines[i], lines[j]) = (lines[j], lines[i]);
        }

        var decoder = new FileDecoder();
        decoder.Scan(lines);

        var restored = decoder.TryAssemble(decoder.Slots[0].Header);
        Assert.Equal(content, restored);
    }

    [Fact]
    public void Duplicate_Lines_Ignored()
    {
        var content = RandomBytes(2000, 55);
        var encoder = new FileEncoder(eccPercent: 0);
        var lines = SplitLines(encoder.EncodeToText(content, "dup.bin")).ToList();

        lines.Add(lines[0]);
        lines.Add(lines[3]);

        var decoder = new FileDecoder();
        decoder.Scan(lines);

        var restored = decoder.TryAssemble(decoder.Slots[0].Header);
        Assert.Equal(content, restored);
    }

    [Fact]
    public void Junk_Lines_Tolerated()
    {
        var content = RandomBytes(1000, 13);
        var encoder = new FileEncoder(eccPercent: 0);
        var lines = SplitLines(encoder.EncodeToText(content, "junk.bin")).ToList();

        var junk = Convert.ToBase64String(RandomBytes(PacketFormat.PacketSize, 99));
        lines.Insert(lines.Count / 2, junk);
        lines.Add(junk);

        var decoder = new FileDecoder();
        decoder.Scan(lines);

        var restored = decoder.TryAssemble(decoder.Slots[0].Header);
        Assert.Equal(content, restored);
    }

    // ────────────────────────────────────────────────────────────────────────
    //  RS Codec — Границы восстановления
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Rs_Recovery_At_Exact_Boundary_Erase_Equal_M()
    {
        const int K = 10, M = 3;
        var rnd = new Random(42);
        var data = MakeData(K, Payload, rnd);

        var rs = new RsCodecAdapter();
        var ecc = rs.Encode(data, M);

        var (received, valid) = BuildReceived(data, ecc, K, M);

        for (var i = 0; i < M; i++) { valid[i] = false; received[i] = null!; }

        var recovered = rs.Decode(received, valid, K);
        Assert.NotNull(recovered);
        for (var i = 0; i < M; i++)
            Assert.Equal(data[i], recovered![i]);
    }

    [Fact]
    public void Rs_Recovery_One_Over_Boundary_Fails()
    {
        const int K = 10, M = 3;
        var rnd = new Random(42);
        var data = MakeData(K, Payload, rnd);

        var rs = new RsCodecAdapter();
        var ecc = rs.Encode(data, M);

        var (received, valid) = BuildReceived(data, ecc, K, M);

        for (var i = 0; i <= M; i++) { valid[i] = false; received[i] = null!; }

        Assert.Null(rs.Decode(received, valid, K));
    }

    [Fact]
    public void Rs_Passthrough_No_Data_Erased()
    {
        const int K = 10, M = 3;
        var rnd = new Random(99);
        var data = MakeData(K, Payload, rnd);

        var rs = new RsCodecAdapter();
        var ecc = rs.Encode(data, M);

        var (received, valid) = BuildReceived(data, ecc, K, M);

        valid[K] = false; received[K] = null!;
        valid[K + 1] = false; received[K + 1] = null!;

        var recovered = rs.Decode(received, valid, K);
        Assert.NotNull(recovered);
        for (var i = 0; i < K; i++)
            Assert.Equal(data[i], recovered![i]);
    }

    [Fact]
    public void Rs_All_Ecc_Erased_Data_Intact_Recovers()
    {
        const int K = 8, M = 4;
        var rnd = new Random(7);
        var data = MakeData(K, Payload, rnd);

        var rs = new RsCodecAdapter();
        var ecc = rs.Encode(data, M);

        var (received, valid) = BuildReceived(data, ecc, K, M);

        for (var i = 0; i < M; i++) { valid[K + i] = false; received[K + i] = null!; }

        var recovered = rs.Decode(received, valid, K);
        Assert.NotNull(recovered);
        for (var i = 0; i < K; i++)
            Assert.Equal(data[i], recovered![i]);
    }

    // ── RS Codec — Позиционное покрытие ───────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void Rs_Erase_Each_Single_Data_Position(int eraseIdx)
    {
        const int K = 5, M = 2;
        var rnd = new Random(31);
        var data = MakeData(K, Payload, rnd);

        var rs = new RsCodecAdapter();
        var ecc = rs.Encode(data, M);

        var (received, valid) = BuildReceived(data, ecc, K, M);
        valid[eraseIdx] = false; received[eraseIdx] = null!;

        var recovered = rs.Decode(received, valid, K);
        Assert.NotNull(recovered);
        for (var i = 0; i < K; i++)
            Assert.Equal(data[i], recovered![i]);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(6)]
    public void Rs_Erase_Each_Single_Ecc_Position(int eraseIdx)
    {
        const int K = 5, M = 2;
        var rnd = new Random(31);
        var data = MakeData(K, Payload, rnd);

        var rs = new RsCodecAdapter();
        var ecc = rs.Encode(data, M);

        var (received, valid) = BuildReceived(data, ecc, K, M);
        valid[eraseIdx] = false; received[eraseIdx] = null!;

        var recovered = rs.Decode(received, valid, K);
        Assert.NotNull(recovered);
        for (var i = 0; i < K; i++)
            Assert.Equal(data[i], recovered![i]);
    }

    // ── RS Codec — Минимальные и крупные конфигурации ─────────────────────────

    [Fact]
    public void Rs_Minimal_K1_M1()
    {
        const int K = 1, M = 1;
        var rnd = new Random(1);
        var data = MakeData(K, Payload, rnd);

        var rs = new RsCodecAdapter();
        var ecc = rs.Encode(data, M);
        Assert.Single(ecc);

        var (received, valid) = BuildReceived(data, ecc, K, M);
        valid[0] = false; received[0] = null!;

        var recovered = rs.Decode(received, valid, K);
        Assert.NotNull(recovered);
        Assert.Equal(data[0], recovered![0]);
    }

    [Fact]
    public void Rs_Large_K100_M10()
    {
        const int K = 100, M = 10;
        var rnd = new Random(2024);
        var data = MakeData(K, Payload, rnd);

        var rs = new RsCodecAdapter();
        var ecc = rs.Encode(data, M);

        var (received, valid) = BuildReceived(data, ecc, K, M);

        var eraseAt = new[] { 0, 25, 50, 75, 99 };
        foreach (var idx in eraseAt) { valid[idx] = false; received[idx] = null!; }

        var recovered = rs.Decode(received, valid, K);
        Assert.NotNull(recovered);
        foreach (var idx in eraseAt)
            Assert.Equal(data[idx], recovered![idx]);
    }

    [Fact]
    public void Rs_NonStandard_Payload_8Bytes()
    {
        const int K = 6, M = 2;
        const int SmallPayload = 8;
        var rnd = new Random(55);
        var data = MakeData(K, SmallPayload, rnd);

        var rs = new RsCodecAdapter();
        var ecc = rs.Encode(data, M);

        var (received, valid) = BuildReceived(data, ecc, K, M);
        valid[2] = false; received[2] = null!;
        valid[4] = false; received[4] = null!;

        var recovered = rs.Decode(received, valid, K);
        Assert.NotNull(recovered);
        Assert.Equal(data[2], recovered![2]);
        Assert.Equal(data[4], recovered[4]);
    }

    // ── RS Codec — Валидация аргументов ───────────────────────────────────────

    [Fact]
    public void Rs_Encode_Zero_Ecc_Returns_Empty()
    {
        var rs = new RsCodecAdapter();
        var data = MakeData(5, Payload, new Random(0));
        Assert.Empty(rs.Encode(data, 0));
    }

    [Fact]
    public void Rs_Encode_Zero_Data_Returns_Empty()
    {
        var rs = new RsCodecAdapter();
        Assert.Empty(rs.Encode(Array.Empty<byte[]>(), 3));
    }

    [Fact]
    public void Rs_Decode_ValidityMap_Length_Mismatch_Returns_Null()
    {
        var rs = new RsCodecAdapter();
        var sectors = new byte[][] { new byte[64], new byte[64] };
        var wrongMap = new bool[] { true };
        Assert.Null(rs.Decode(sectors, wrongMap, 1));
    }

    [Fact]
    public void Rs_Decode_Empty_Sectors_Returns_Null()
    {
        var rs = new RsCodecAdapter();
        Assert.Null(rs.Decode(Array.Empty<byte[]>(), Array.Empty<bool>(), 0));
    }

    // ────────────────────────────────────────────────────────────────────────
    //  ComputeEccCount / ComputeHeaderCount — формулы
    // ────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(100, 10, 10)]
    [InlineData(100, 0, 0)]
    [InlineData(1, 50, 1)]
    [InlineData(3, 33, 1)]
    [InlineData(1000, 50, 500)]
    [InlineData(7, 15, 2)]
    [InlineData(0, 10, 0)]
    public void ComputeEccCount_Formula(int dataCount, int eccPercent, int expected)
        => Assert.Equal(expected, FileEncoder.ComputeEccCount(dataCount, eccPercent));

    [Theory]
    [InlineData(100, 3, 3)]
    [InlineData(0, 3, 3)]
    [InlineData(1000, 3, 30)]
    [InlineData(100, 0, 3)]
    [InlineData(10, 50, 5)]
    [InlineData(1, 3, 3)]
    [InlineData(33, 10, 4)]
    public void ComputeHeaderCount_Formula(int total, int headerPercent, int expected)
        => Assert.Equal(expected, FileEncoder.ComputeHeaderCount(total, headerPercent));

    // ────────────────────────────────────────────────────────────────────────
    //  Интеграция: ECC на границе и граничные параметры
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Ecc_FileLevel_Recovery_At_Boundary()
    {
        var content = RandomBytes(5000, 77);
        var encoder = new FileEncoder(eccPercent: 30);
        var packets = encoder.Encode(content, "boundary.bin");

        var dataCount = (5000 + Payload - 1) / Payload;
        var eccCount = FileEncoder.ComputeEccCount(dataCount, 30);

        var lines = packets.Select(p => Convert.ToBase64String(p)).ToList();
        var removed = 0;
        for (var i = lines.Count - 1; i >= 0 && removed < eccCount; i--)
        {
            if (!IsHeaderLine(lines[i]))
            {
                lines.RemoveAt(i);
                removed++;
            }
        }
        Assert.Equal(eccCount, removed);

        var decoder = new FileDecoder();
        decoder.Scan(lines);

        Assert.Single(decoder.Slots);
        var restored = decoder.TryAssemble(decoder.Slots[0].Header);
        Assert.NotNull(restored);
        Assert.Equal(content, restored);
    }

    [Fact]
    public void Ecc_FileLevel_One_Over_Boundary_Fails()
    {
        var content = RandomBytes(5000, 21);
        var encoder = new FileEncoder(eccPercent: 10);
        var packets = encoder.Encode(content, "over.bin");

        var dataCount = (5000 + Payload - 1) / Payload;
        var eccCount = FileEncoder.ComputeEccCount(dataCount, 10);

        var lines = packets.Select(p => Convert.ToBase64String(p)).ToList();
        var removed = 0;
        var target = eccCount + 1;
        for (var i = lines.Count - 1; i >= 0 && removed < target; i--)
        {
            if (!IsHeaderLine(lines[i]))
            {
                lines.RemoveAt(i);
                removed++;
            }
        }

        var decoder = new FileDecoder();
        decoder.Scan(lines);

        Assert.Single(decoder.Slots);
        var restored = decoder.TryAssemble(decoder.Slots[0].Header);
        Assert.Null(restored);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(10)]
    [InlineData(25)]
    [InlineData(50)]
    public void Various_Ecc_Percentages_Roundtrip(int eccPercent)
    {
        var content = RandomBytes(3000, eccPercent);
        var encoder = new FileEncoder(eccPercent: eccPercent);
        var text = encoder.EncodeToText(content, "ecc.bin");

        var decoder = new FileDecoder();
        decoder.Scan(SplitLines(text));

        Assert.Single(decoder.Slots);
        var restored = decoder.TryAssemble(decoder.Slots[0].Header);
        Assert.NotNull(restored);
        Assert.Equal(content, restored);
    }

    [Fact]
    public void HeaderPercent_Zero_Still_Minimum_Three()
    {
        var content = RandomBytes(500, 3);
        var encoder = new FileEncoder(eccPercent: 10, headerPercent: 0);
        var packets = encoder.Encode(content, "hdr.bin");

        var headerCount = packets.Count(IsHeaderPacket);
        Assert.True(headerCount >= 3,
            $"Ожидалось >= 3 заголовков при headerPercent=0, получено {headerCount}");
    }

    // ────────────────────────────────────────────────────────────────────────
    //  Утилиты
    // ────────────────────────────────────────────────────────────────────────

    private static byte[][] MakeData(int k, int payloadSize, Random rnd)
    {
        var data = new byte[k][];
        for (var i = 0; i < k; i++) { data[i] = new byte[payloadSize]; rnd.NextBytes(data[i]); }
        return data;
    }

    private static (byte[][] received, bool[] valid) BuildReceived(
        byte[][] data, IReadOnlyList<byte[]> ecc, int k, int m)
    {
        var total = k + m;
        var received = new byte[total][];
        var valid = new bool[total];
        for (var i = 0; i < k; i++) { received[i] = data[i]; valid[i] = true; }
        for (var i = 0; i < m; i++) { received[k + i] = ecc[i]; valid[k + i] = true; }
        return (received, valid);
    }

    private static bool IsHeaderPacket(byte[] packet) =>
        PacketHasher.VerifyHeaderPacket(packet);

    private static bool IsHeaderLine(string base64)
    {
        try
        {
            var bytes = Convert.FromBase64String(base64);
            return bytes.Length == PacketFormat.PacketSize && IsHeaderPacket(bytes);
        }
        catch { return false; }
    }
}