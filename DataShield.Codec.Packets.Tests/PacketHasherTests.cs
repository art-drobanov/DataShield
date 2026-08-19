using DataShield.Codec.Packets;
using Xunit;

namespace DataShield.Codec.Packets.Tests;

// ─────────────────────────────────────────────────────────────────────────────
//  Усечённые хеши SHA-256 для пакетов
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Тесты PacketHasher: хеши заголовочных и классических секторных пакетов.
///
/// Заголовок: Trunc24(SHA-256(content)) — усечение до HeaderHashSize байт.
/// Сектор: Trunc9(SHA-256(H5 ‖ content)) — хеш «привязан» к хешу заголовка,
/// поэтому сектор чужого файла не проходит проверку.
/// Проверяются: соответствие полному SHA-256, детерминизм, лавинный эффект
/// на одиночные битовые инверсии, валидация длин и reject повреждённых пакетов.
/// </summary>
public class PacketHasherTests
{
    /// <summary>Детерминированный псевдослучайный контент заголовка (seed).</summary>
    private static byte[] MakeHeaderContent(int seed = 1)
    {
        var content = new byte[PacketFormat.HeaderContentSize];
        new Random(seed).NextBytes(content);
        return content;
    }

    /// <summary>Детерминированный псевдослучайный контент сектора (seed).</summary>
    private static byte[] MakeSectorContent(int seed = 2)
    {
        var content = new byte[PacketFormat.SectorContentSize];
        new Random(seed).NextBytes(content);
        return content;
    }

    /// <summary>Собрать валидный заголовочный пакет: контент + хеш заголовка.</summary>
    private static byte[] MakeHeaderPacket(byte[] content)
    {
        var packet = new byte[PacketFormat.PacketSize];
        content.CopyTo(packet, 0);
        PacketHasher.ComputeHeaderHash(content)
            .CopyTo(packet, PacketFormat.HeaderHashOffset);
        return packet;
    }

    /// <summary>Собрать валидный секторный пакет: контент + хеш сектора.</summary>
    private static byte[] MakeSectorPacket(byte[] content, byte[] headerHash)
    {
        var packet = new byte[PacketFormat.PacketSize];
        content.CopyTo(packet, 0);
        PacketHasher.ComputeSectorHash(content, headerHash)
            .CopyTo(packet, PacketFormat.SectorHashOffset);
        return packet;
    }

    // ── Заголовок: Trunc24(SHA-256) ─────────────────────────────────────────

    /// <summary>
    /// Хеш заголовка — это в точности первые HeaderHashSize байт полного
    /// SHA-256 от контента (без каких-либо салт/модификаций).
    /// </summary>
    [Fact]
    public void HeaderHash_Is_TruncatedSha256()
    {
        var content = MakeHeaderContent();

        var hash = PacketHasher.ComputeHeaderHash(content);

        Assert.Equal(PacketFormat.HeaderHashSize, hash.Length);
        Assert.Equal(
            Sha256Compact.HashData(content)[..PacketFormat.HeaderHashSize],
            hash);
    }

    /// <summary>Один и тот же контент всегда даёт один и тот же хеш.</summary>
    [Fact]
    public void HeaderHash_IsDeterministic()
    {
        var content = MakeHeaderContent();
        Assert.Equal(
            PacketHasher.ComputeHeaderHash(content),
            PacketHasher.ComputeHeaderHash(content));
    }

    /// <summary>
    /// Лавинный эффект: инверсия любого бита контента меняет хеш.
    /// После каждой проверки бит возвращается, поэтому обходятся все биты
    /// исходного содержимого.
    /// </summary>
    [Fact]
    public void HeaderHash_SingleBitFlip_ChangesHash()
    {
        var content = MakeHeaderContent();
        var original = PacketHasher.ComputeHeaderHash(content);

        for (var i = 0; i < content.Length; i++)
        {
            for (var bit = 0; bit < 8; bit++)
            {
                content[i] ^= (byte)(1 << bit);
                Assert.NotEqual(original, PacketHasher.ComputeHeaderHash(content));
                content[i] ^= (byte)(1 << bit);
            }
        }
    }

    /// <summary>Контент нестандартной длины отклоняется ArgumentException.</summary>
    [Fact]
    public void HeaderHash_WrongLength_Throws()
    {
        Assert.Throws<ArgumentException>(
            () => PacketHasher.ComputeHeaderHash(new byte[PacketFormat.HeaderContentSize - 1]));
        Assert.Throws<ArgumentException>(
            () => PacketHasher.ComputeHeaderHash(new byte[PacketFormat.HeaderContentSize + 1]));
    }

    /// <summary>
    /// Проверка пакета: собственный пакет проходит; порча контента
    /// или самого поля хеша — отвергается.
    /// </summary>
    [Fact]
    public void VerifyHeaderPacket_MatchesOwn_AndRejectsDamaged()
    {
        var content = MakeHeaderContent();
        var packet = MakeHeaderPacket(content);

        Assert.True(PacketHasher.VerifyHeaderPacket(packet));

        // Инверсия бита в контенте — хеш больше не сходится
        packet[0] ^= 0x01;
        Assert.False(PacketHasher.VerifyHeaderPacket(packet));

        // Возврат контента и порча поля хеша — тоже не проходит
        packet[0] ^= 0x01;
        packet[PacketFormat.HeaderHashOffset] ^= 0x01;
        Assert.False(PacketHasher.VerifyHeaderPacket(packet));
    }

    /// <summary>Пакет нестандартной длины не проходит проверку (без исключения).</summary>
    [Fact]
    public void VerifyHeaderPacket_WrongLength_ReturnsFalse()
    {
        Assert.False(PacketHasher.VerifyHeaderPacket(
            new byte[PacketFormat.PacketSize - 1]));
    }

    // ── Сектор: Trunc9(SHA-256(H5 ‖ D1 ‖ D2)) ───────────────────────────────

    /// <summary>
    /// Хеш сектора — первые SectorHashSize байт SHA-256 от конкатенации
    /// хеша заголовка и контента сектора.
    /// </summary>
    [Fact]
    public void SectorHash_Is_TruncatedSha256OfHeaderHashAndContent()
    {
        var content = MakeSectorContent();
        var headerHash = PacketHasher.ComputeHeaderHash(MakeHeaderContent());

        var hash = PacketHasher.ComputeSectorHash(content, headerHash);

        Assert.Equal(PacketFormat.SectorHashSize, hash.Length);

        // Эталонный вход: H5 ‖ content
        var input = headerHash.Concat(content).ToArray();
        Assert.Equal(
            Sha256Compact.HashData(input)[..PacketFormat.SectorHashSize],
            hash);
    }

    /// <summary>
    /// Привязка к заголовку: один и тот же контент с разными хешами
    /// заголовков даёт разные хеши секторов.
    /// </summary>
    [Fact]
    public void SectorHash_DifferentHeaderHash_ProducesDifferentHash()
    {
        var content = MakeSectorContent();
        var hashA = PacketHasher.ComputeHeaderHash(MakeHeaderContent(seed: 10));
        var hashB = PacketHasher.ComputeHeaderHash(MakeHeaderContent(seed: 11));

        Assert.NotEqual(
            PacketHasher.ComputeSectorHash(content, hashA),
            PacketHasher.ComputeSectorHash(content, hashB));
    }

    /// <summary>Лавинный эффект по контенту сектора (по биту на каждый байт).</summary>
    [Fact]
    public void SectorHash_SingleBitFlip_ChangesHash()
    {
        var content = MakeSectorContent();
        var headerHash = PacketHasher.ComputeHeaderHash(MakeHeaderContent());
        var original = PacketHasher.ComputeSectorHash(content, headerHash);

        for (var i = 0; i < content.Length; i++)
        {
            content[i] ^= 0x01;
            Assert.NotEqual(original, PacketHasher.ComputeSectorHash(content, headerHash));
            content[i] ^= 0x01;
        }
    }

    /// <summary>Нестандартная длина контента сектора или хеша заголовка — исключение.</summary>
    [Fact]
    public void SectorHash_WrongLength_Throws()
    {
        var headerHash = new byte[PacketFormat.HeaderHashSize];

        Assert.Throws<ArgumentException>(
            () => PacketHasher.ComputeSectorHash(
                new byte[PacketFormat.SectorContentSize - 1], headerHash));

        Assert.Throws<ArgumentException>(
            () => PacketHasher.ComputeSectorHash(
                new byte[PacketFormat.SectorContentSize],
                new byte[PacketFormat.HeaderHashSize - 1]));
    }

    /// <summary>
    /// Проверка секторного пакета: проходит со своим заголовком,
    /// отвергается с чужим заголовком и при порче контента.
    /// </summary>
    [Fact]
    public void VerifySectorPacket_MatchesOwn_AndRejectsForeignHeader()
    {
        var content = MakeSectorContent();
        var headerHash = PacketHasher.ComputeHeaderHash(MakeHeaderContent(seed: 20));
        var foreign = PacketHasher.ComputeHeaderHash(MakeHeaderContent(seed: 21));
        var packet = MakeSectorPacket(content, headerHash);

        Assert.True(PacketHasher.VerifySectorPacket(packet, headerHash));
        Assert.False(PacketHasher.VerifySectorPacket(packet, foreign));

        packet[3] ^= 0x01;
        Assert.False(PacketHasher.VerifySectorPacket(packet, headerHash));
    }

    /// <summary>Нестандартная длина пакета или хеша заголовка — false без исключения.</summary>
    [Fact]
    public void VerifySectorPacket_WrongLengths_ReturnFalse()
    {
        var headerHash = new byte[PacketFormat.HeaderHashSize];

        Assert.False(PacketHasher.VerifySectorPacket(
            new byte[PacketFormat.PacketSize - 1], headerHash));
        Assert.False(PacketHasher.VerifySectorPacket(
            new byte[PacketFormat.PacketSize], new byte[PacketFormat.HeaderHashSize - 1]));
    }

    // ── Статистические свойства ─────────────────────────────────────────────

    /// <summary>
    /// Грубая проверка на коллизии: 500 разных контентов заголовка
    /// дают 500 попарно различных хешей.
    /// </summary>
    [Fact]
    public void HeaderHash_DistinctContents_ProduceDistinctHashes()
    {
        var hashes = new HashSet<string>();

        for (var seed = 0; seed < 500; seed++)
        {
            var hash = PacketHasher.ComputeHeaderHash(MakeHeaderContent(seed));
            hashes.Add(Convert.ToHexString(hash));
        }

        Assert.Equal(500, hashes.Count);
    }
}
