using System.Text;
using DataShield.Codec;
using DataShield.Codec.Packets;
using Xunit;

namespace DataShield.Tests;

/// <summary>
/// Тесты сигнатурных пакетов SHA-512: примитивы пакета (S1 ‖ S2),
/// режим «секрета» хешей, раскладка потока кодером и режимы приёма
/// декодера (Base/Auto/Force), включая roundtrip с секретом.
/// </summary>
public sealed class SignatureTests
{
    /// <summary>Детерминированный псевдослучайный массив.</summary>
    private static byte[] RandomBytes(int len, int seed)
    {
        var r = new Random(seed);
        var b = new byte[len];
        r.NextBytes(b);
        return b;
    }

    /// <summary>
    /// Сборка и автономная проверка S2; порча S2, чужой S1 или неверная
    /// длина ломают пакет; BuildFromSignature воспроизводит пакет.
    /// </summary>
    [Fact]
    public void Build_Verify_Roundtrip()
    {
        var content = RandomBytes(2000, 1);
        var packet = SignaturePacket.Build(content);

        Assert.Equal(PacketFormat.PacketSize, packet.Length);
        Assert.True(SignaturePacket.Verify(packet));
        Assert.Equal(Sha512Compact.HashData(content), SignaturePacket.ReadSignature(packet));
        Assert.Equal(packet, SignaturePacket.BuildFromSignature(SignaturePacket.ReadSignature(packet)));

        var corruptedTag = (byte[])packet.Clone();
        corruptedTag[^1] ^= 0xFF;
        Assert.False(SignaturePacket.Verify(corruptedTag));

        var strangerS1 = (byte[])packet.Clone();
        strangerS1[0] ^= 0xFF;
        Assert.False(SignaturePacket.Verify(strangerS1));

        Assert.False(SignaturePacket.Verify(packet[..70]));
    }

    /// <summary>
    /// Режим «секрет»: без секрета идентичен обычному хешу, с секретом —
    /// хеш конкатенации (секрет ‖ данные); Test() проходит в обоих режимах.
    /// Секрет — контекстное состояние, снимается в finally.
    /// </summary>
    [Fact]
    public void SecureHashes_Secret_Prefix_Scheme()
    {
        var data = RandomBytes(300, 2);
        var secret = Encoding.UTF8.GetBytes("top-secret-ключ");

        try
        {
            Sha256Secure.HashSecret = null;
            Sha512Secure.HashSecret = null;

            Assert.Equal(Sha256Compact.HashData(data), Sha256Secure.HashData(data));
            Assert.Equal(Sha512Compact.HashData(data), Sha512Secure.HashData(data));
            Assert.True(Sha256Secure.Test());
            Assert.True(Sha512Secure.Test());

            Sha256Secure.HashSecret = "top-secret-ключ";
            Sha512Secure.HashSecret = "top-secret-ключ";

            Assert.Equal(Sha256Compact.HashData(secret.Concat(data).ToArray()),
                Sha256Secure.HashData(data));
            Assert.NotEqual(Sha256Compact.HashData(data), Sha256Secure.HashData(data));

            Assert.Equal(Sha512Compact.HashData(secret.Concat(data).ToArray()),
                Sha512Secure.HashData(data));
            Assert.NotEqual(Sha512Compact.HashData(data), Sha512Secure.HashData(data));

            Assert.True(Sha256Secure.Test());
            Assert.True(Sha512Secure.Test());
        }
        finally
        {
            Sha256Secure.HashSecret = null;
            Sha512Secure.HashSecret = null;
        }
    }

    /// <summary>
    /// Раскладка: сигнатурных пакетов ровно по числу копий заголовка, каждый
    /// проходит автономную проверку; статистика несёт S1 и реальные хеши.
    /// </summary>
    [Fact]
    public void Encoder_Places_Signature_Packet_After_Each_Header_Copy()
    {
        var content = RandomBytes(8000, 3);
        var encoder = new StreamEncoder(signaturePackets512: true);
        var (packets, stats) = encoder.EncodeWithStats(content, "sig.bin");

        Assert.True(stats.SignaturePackets512);
        Assert.True(stats.HeaderCopies >= 3);
        Assert.Equal(stats.HeaderCopies, stats.SignatureCopies);
        Assert.Equal(
            stats.DataCount + stats.EccCount + stats.HeaderCopies + stats.SignatureCopies,
            stats.TotalPackets);
        Assert.Equal(packets.Count, stats.TotalPackets);

        Assert.Equal(stats.SignatureCopies, packets.Count(p => SignaturePacket.Verify(p)));

        Assert.Equal(Sha512Compact.HashData(content), stats.Sha512);
        Assert.Equal(Sha512Compact.HashData(content), stats.RealSha512);
        Assert.Equal(Sha256Compact.HashData(content), stats.RealSha256);
    }

    /// <summary>
    /// Auto: при наличии сигнатурных пакетов расширенная проверка активна,
    /// принята ровно одна уникальная сигнатура — SHA-512 содержимого.
    /// </summary>
    [Fact]
    public void Decoder_Auto_Activates_Check_With_Signatures()
    {
        var content = RandomBytes(6000, 4);
        var text = new StreamEncoder(signaturePackets512: true)
            .EncodeToText(content, "sig.bin");

        var decoder = new StreamDecoder();
        decoder.Scan(text.Split('\n', StringSplitOptions.RemoveEmptyEntries));

        var slot = decoder.Slots.Single();
        Assert.True(decoder.SignaturePacketCount > 0);
        Assert.True(decoder.SignatureCheckActive);
        Assert.Equal(Sha512Compact.HashData(content), Assert.Single(decoder.AcceptedSignatures));
        Assert.Equal(content, decoder.TryAssemble(slot.Header));
    }

    /// <summary>Base: сигнатуры считаются, но проверка не влияет на сборку.</summary>
    [Fact]
    public void Decoder_Base_Counts_Packets_Without_Check()
    {
        var content = RandomBytes(6000, 5);
        var text = new StreamEncoder(signaturePackets512: true)
            .EncodeToText(content, "sig.bin");

        var decoder = new StreamDecoder(signatureMode: SignatureMode.Base);
        decoder.Scan(text.Split('\n', StringSplitOptions.RemoveEmptyEntries));

        var slot = decoder.Slots.Single();
        Assert.True(decoder.SignaturePacketCount > 0);
        Assert.False(decoder.SignatureCheckActive);
        Assert.Equal(content, decoder.TryAssemble(slot.Header));
    }

    /// <summary>
    /// Force без сигнатурных пакетов в потоке: проверка активна, отказ
    /// отличим — <see cref="SignatureVerificationException"/> (SHA-256
    /// пройден, Any(SHA-512) нечем подтвердить).
    /// </summary>
    [Fact]
    public void Decoder_Force_Without_Signatures_Fails_Assembly()
    {
        var content = RandomBytes(6000, 6);
        var text = new StreamEncoder().EncodeToText(content, "plain.bin");

        var decoder = new StreamDecoder(signatureMode: SignatureMode.Force);
        decoder.Scan(text.Split('\n', StringSplitOptions.RemoveEmptyEntries));

        var slot = decoder.Slots.Single();
        Assert.Equal(0, decoder.SignaturePacketCount);
        Assert.True(decoder.SignatureCheckActive);
        Assert.Throws<SignatureVerificationException>(() => decoder.TryAssemble(slot.Header));
    }

    /// <summary>
    /// Чужая сигнатура (SHA-512 другого содержимого) при Force: файл
    /// собирается и проходит SHA-256, но Any(SHA-512) не подтверждается —
    /// отличимый отказ <see cref="SignatureVerificationException"/>,
    /// а не тихий null.
    /// </summary>
    [Fact]
    public void Decoder_Force_With_Foreign_Signature_Throws()
    {
        var content = RandomBytes(6000, 11);
        var other = RandomBytes(4000, 12);
        var packets = new StreamEncoder().Encode(content, "plain.bin");

        var stream = PacketIO.WriteBinaryBytes(packets)
            .Concat(SignaturePacket.Build(other))
            .ToArray();

        var decoder = new StreamDecoder(signatureMode: SignatureMode.Force);
        decoder.Scan(stream);

        var slot = decoder.Slots.Single();
        Assert.Equal(1, decoder.SignaturePacketCount);
        Assert.True(decoder.SignatureCheckActive);
        Assert.Equal(Sha512Compact.HashData(other),
            Assert.Single(decoder.AcceptedSignatures));
        Assert.Throws<SignatureVerificationException>(() => decoder.TryAssemble(slot.Header));
    }

    /// <summary>Force с сигнатурными пакетами: файл собирается и проходит проверку.</summary>
    [Fact]
    public void Decoder_Force_With_Signatures_Assembles()
    {
        var content = RandomBytes(6000, 7);
        var text = new StreamEncoder(signaturePackets512: true)
            .EncodeToText(content, "sig.bin");

        var decoder = new StreamDecoder(signatureMode: SignatureMode.Force);
        decoder.Scan(text.Split('\n', StringSplitOptions.RemoveEmptyEntries));

        var slot = decoder.Slots.Single();
        Assert.True(decoder.SignatureCheckActive);
        Assert.Equal(content, decoder.TryAssemble(slot.Header));
    }

    /// <summary>
    /// Случайная перестановка пакетов потока (Fisher–Yates), включая
    /// сигнатурные: приём позиционо-независим, сборка и расширенная
    /// проверка Any(SHA-512) проходят в режиме Force.
    /// </summary>
    [Fact]
    public void Random_Order_With_Signatures_Still_Assembled()
    {
        var content = RandomBytes(3500, 9);
        var encoder = new StreamEncoder(eccPercent: 0, signaturePackets512: true);
        var lines = encoder
            .EncodeToText(content, "rnd-sig.bin")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .ToList();

        var rnd = new Random(7);
        for (var i = lines.Count - 1; i > 0; i--)
        {
            var j = rnd.Next(i + 1);
            (lines[i], lines[j]) = (lines[j], lines[i]);
        }

        var decoder = new StreamDecoder(signatureMode: SignatureMode.Force);
        decoder.Scan(lines);

        var slot = decoder.Slots.Single();
        Assert.True(decoder.SignaturePacketCount > 0);
        Assert.True(decoder.SignatureCheckActive);
        Assert.Equal(Sha512Compact.HashData(content),
            Assert.Single(decoder.AcceptedSignatures));
        Assert.Equal(content, decoder.TryAssemble(slot.Header));
    }

    /// <summary>
    /// Roundtrip с секретом через ctor: хеши «на проводе» защищены
    /// (отличаются от реальных), декодер с тем же секретом собирает файл
    /// и принимает защищённую сигнатуру, декодер с чужим секретом не
    /// опознаёт заголовки. Секрет контекстен — глобальные
    /// Sha*Secure.HashSecret не затрагиваются.
    /// </summary>
    [Fact]
    public void Secret_Roundtrip_Protects_OnWire_Hashes()
    {
        var content = RandomBytes(6000, 8);

        var encoder = new StreamEncoder(signaturePackets512: true, hashSecret: "один-секрет");
        var (packets, stats) = encoder.EncodeWithStats(content, "sec.bin");

        Assert.NotEqual(Sha256Compact.HashData(content), stats.Sha256);
        Assert.Equal(Sha256Compact.HashData(content), stats.RealSha256);
        Assert.NotEqual(Sha512Compact.HashData(content), stats.Sha512);
        Assert.Equal(Sha512Compact.HashData(content), stats.RealSha512);

        // Операции с ctor-секретом не затрагивают глобальный скоуп
        Assert.Null(Sha256Secure.HashSecret);
        Assert.Null(Sha512Secure.HashSecret);

        var decoder = new StreamDecoder(hashSecret: "один-секрет");
        decoder.Scan(PacketIO.WriteBinaryBytes(packets));

        var slot = decoder.Slots.Single();
        Assert.True(decoder.SignatureCheckActive);
        Assert.Equal(stats.Sha512, Assert.Single(decoder.AcceptedSignatures));
        Assert.Equal(content, decoder.TryAssemble(slot.Header));

        Assert.Null(Sha256Secure.HashSecret);
        Assert.Null(Sha512Secure.HashSecret);

        var stranger = new StreamDecoder(hashSecret: "чужой-секрет");
        stranger.Scan(PacketIO.WriteBinaryBytes(packets));
        Assert.Equal(0, stranger.FileCount);
    }

    /// <summary>
    /// Параллельные операции с разными секретами изолированы: каждая
    /// защищает собственные хеши «на проводе» (все различны), реальные
    /// хеши совпадают, глобальный скоуп остаётся пустым.
    /// </summary>
    [Fact]
    public async Task Parallel_Operations_With_Different_Secrets_Are_Isolated()
    {
        var content = RandomBytes(6000, 10);
        var secrets = new string?[] { "alpha-секрет", "beta-секрет", null };

        var results = await Task.WhenAll(secrets.Select(secret => Task.Run(() =>
        {
            var encoder = new StreamEncoder(signaturePackets512: true, hashSecret: secret);
            return encoder.EncodeWithStats(content, "par.bin").stats;
        })));

        var plain256 = Sha256Compact.HashData(content);
        var plain512 = Sha512Compact.HashData(content);

        foreach (var stats in results)
        {
            Assert.Equal(plain256, stats.RealSha256);
            Assert.Equal(plain512, stats.RealSha512);
        }

        Assert.Equal(3, results.Select(s => Convert.ToHexString(s.Sha256)).Distinct().Count());
        Assert.Equal(3, results.Select(s => Convert.ToHexString(s.Sha512!)).Distinct().Count());
        Assert.Null(Sha256Secure.HashSecret);
        Assert.Null(Sha512Secure.HashSecret);
    }
}
