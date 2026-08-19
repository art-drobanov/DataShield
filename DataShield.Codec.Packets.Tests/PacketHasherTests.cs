using DataShield.Codec.Packets;
using Xunit;

namespace DataShield.Codec.Packets.Tests;

// ─────────────────────────────────────────────────────────────────────────────
//  Усечённые хеши SHA-256 для пакетов
// ─────────────────────────────────────────────────────────────────────────────

public class PacketHasherTests
{
    private static byte[] MakeHeaderContent(int seed = 1)
    {
        var content = new byte[PacketFormat.HeaderContentSize];
        new Random(seed).NextBytes(content);
        return content;
    }

    private static byte[] MakeSectorContent(int seed = 2)
    {
        var content = new byte[PacketFormat.SectorContentSize];
        new Random(seed).NextBytes(content);
        return content;
    }

    private static byte[] MakeHeaderPacket(byte[] content)
    {
        var packet = new byte[PacketFormat.PacketSize];
        content.CopyTo(packet, 0);
        PacketHasher.ComputeHeaderHash(content)
            .CopyTo(packet, PacketFormat.HeaderHashOffset);
        return packet;
    }

    private static byte[] MakeSectorPacket(byte[] content, byte[] headerHash)
    {
        var packet = new byte[PacketFormat.PacketSize];
        content.CopyTo(packet, 0);
        PacketHasher.ComputeSectorHash(content, headerHash)
            .CopyTo(packet, PacketFormat.SectorHashOffset);
        return packet;
    }

    // ── Заголовок: Trunc24(SHA-256) ─────────────────────────────────────────

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

    [Fact]
    public void HeaderHash_IsDeterministic()
    {
        var content = MakeHeaderContent();
        Assert.Equal(
            PacketHasher.ComputeHeaderHash(content),
            PacketHasher.ComputeHeaderHash(content));
    }

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

    [Fact]
    public void HeaderHash_WrongLength_Throws()
    {
        Assert.Throws<ArgumentException>(
            () => PacketHasher.ComputeHeaderHash(new byte[PacketFormat.HeaderContentSize - 1]));
        Assert.Throws<ArgumentException>(
            () => PacketHasher.ComputeHeaderHash(new byte[PacketFormat.HeaderContentSize + 1]));
    }

    [Fact]
    public void VerifyHeaderPacket_MatchesOwn_AndRejectsDamaged()
    {
        var content = MakeHeaderContent();
        var packet = MakeHeaderPacket(content);

        Assert.True(PacketHasher.VerifyHeaderPacket(packet));

        packet[0] ^= 0x01;
        Assert.False(PacketHasher.VerifyHeaderPacket(packet));

        packet[0] ^= 0x01;
        packet[PacketFormat.HeaderHashOffset] ^= 0x01;
        Assert.False(PacketHasher.VerifyHeaderPacket(packet));
    }

    [Fact]
    public void VerifyHeaderPacket_WrongLength_ReturnsFalse()
    {
        Assert.False(PacketHasher.VerifyHeaderPacket(
            new byte[PacketFormat.PacketSize - 1]));
    }

    // ── Сектор: Trunc9(SHA-256(H5 ‖ D1 ‖ D2)) ───────────────────────────────

    [Fact]
    public void SectorHash_Is_TruncatedSha256OfHeaderHashAndContent()
    {
        var content = MakeSectorContent();
        var headerHash = PacketHasher.ComputeHeaderHash(MakeHeaderContent());

        var hash = PacketHasher.ComputeSectorHash(content, headerHash);

        Assert.Equal(PacketFormat.SectorHashSize, hash.Length);

        var input = headerHash.Concat(content).ToArray();
        Assert.Equal(
            Sha256Compact.HashData(input)[..PacketFormat.SectorHashSize],
            hash);
    }

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
