using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace DataShieldTests;

// ─────────────────────────────────────────────────────────────────────────────
//  Sha256Compact — эталон для будущего порта на C++
// ─────────────────────────────────────────────────────────────────────────────

public class Sha256CompactTests
{
    // Известный вектор FIPS 180-4: SHA-256("abc").
    private const string AbcVector =
        "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";

    // Известный вектор: SHA-256("").
    private const string EmptyVector =
        "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    private static string Hex(byte[] hash) =>
        Convert.ToHexString(hash).ToLowerInvariant();

    // ────────────────────────────────────────────────────────────────────────
    //  Известные векторы
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Empty_Input_MatchesKnownVector() =>
        Assert.Equal(EmptyVector, Hex(Sha256Compact.HashData(Array.Empty<byte>())));

    [Fact]
    public void Abc_MatchesKnownVector() =>
        Assert.Equal(AbcVector, Hex(Sha256Compact.HashData(
            Encoding.ASCII.GetBytes("abc"))));

    // ────────────────────────────────────────────────────────────────────────
    //  Границы блоков (512-битный блок = 64 байта)
    // ────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(31)]
    [InlineData(32)]
    [InlineData(54)]
    [InlineData(55)]
    [InlineData(56)]
    [InlineData(63)]
    [InlineData(64)]
    [InlineData(65)]
    [InlineData(119)]
    [InlineData(120)]
    [InlineData(128)]
    public void Boundary_Lengths_MatchBclSha256(int length)
    {
        var data = new byte[length];
        new Random(1000 + length).NextBytes(data);

        Assert.Equal(
            SHA256.HashData(data),
            Sha256Compact.HashData(data));
    }

    [Fact]
    public void Large_Input_MatchesBclSha256()
    {
        var data = new byte[1_000_003]; // некратный размер: хвост + паддинг
        new Random(42).NextBytes(data);

        Assert.Equal(
            SHA256.HashData(data),
            Sha256Compact.HashData(data));
    }

    [Fact]
    public void AllZero_And_AllFF_MatchBcl()
    {
        var zeros = new byte[200];
        var ff = Enumerable.Repeat((byte)0xFF, 200).ToArray();

        Assert.Equal(SHA256.HashData(zeros), Sha256Compact.HashData(zeros));
        Assert.Equal(SHA256.HashData(ff), Sha256Compact.HashData(ff));
    }

    // ────────────────────────────────────────────────────────────────────────
    //  Свойства
    // ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Deterministic_ForSameInput()
    {
        var data = Encoding.ASCII.GetBytes("determinism");

        Assert.Equal(
            Sha256Compact.HashData(data),
            Sha256Compact.HashData(data));
    }

    [Fact]
    public void Avalanche_SingleBitFlip_ChangesHash()
    {
        var a = new byte[64];
        new Random(7).NextBytes(a);
        var b = (byte[])a.Clone();
        b[10] ^= 0x01;

        Assert.NotEqual(Sha256Compact.HashData(a), Sha256Compact.HashData(b));
    }

    [Fact]
    public void Output_Length_Is_32()
    {
        Assert.Equal(32, Sha256Compact.HashData(new byte[777]).Length);
    }
}
