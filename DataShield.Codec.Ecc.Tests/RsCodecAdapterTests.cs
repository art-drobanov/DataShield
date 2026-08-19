using DataShield.Codec.Ecc;
using DataShield.Codec.Reporting;
using Xunit;

namespace DataShield.Codec.Ecc.Tests;

// ─────────────────────────────────────────────────────────────────────────────
//  RsCodecAdapter — кодирование и восстановление томов над GF(2¹⁶)
// ─────────────────────────────────────────────────────────────────────────────

public sealed class RsCodecAdapterTests
{
    private const int Payload = 64; // PacketFormat.PayloadSize
    private const int K = 4;
    private const int M = 2;

    private readonly RsCodecAdapter _rs = new();

    private static byte[] Volume(int seed)
    {
        var data = new byte[Payload];
        new Random(seed).NextBytes(data);
        return data;
    }

    private static byte[][] DataVolumes() =>
        Enumerable.Range(0, K).Select(Volume).ToArray();

    // ── Кодирование ───────────────────────────────────────────────────────

    [Fact]
    public void Encode_ProducesRequestedEccVolumes()
    {
        var data = DataVolumes();

        var ecc = _rs.Encode(data, M);

        Assert.Equal(M, ecc.Count);
        Assert.All(ecc, volume => Assert.Equal(Payload, volume.Length));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Encode_NonPositiveEccCount_ReturnsEmpty(int eccCount)
    {
        var data = DataVolumes();

        var ecc = _rs.Encode(data, eccCount);

        Assert.Empty(ecc);
    }

    [Fact]
    public void Encode_EmptyInput_ReturnsEmpty()
    {
        var ecc = _rs.Encode(Array.Empty<byte[]>(), M);

        Assert.Empty(ecc);
    }

    [Fact]
    public void Encode_CountsExceedingFieldSize_Throw()
    {
        var data = DataVolumes();

        Assert.Throws<InvalidOperationException>(
            () => _rs.Encode(data, eccCount: 65536 - data.Length));
    }

    [Fact]
    public void Encode_ReportsEccEncodingPhase()
    {
        var data = DataVolumes();
        var reported = new List<(int Percent, string Phase)>();
        var progress = new Collector(reported);

        _rs.Encode(data, M, progress, ct: default);

        Assert.Contains(reported, entry => entry.Phase == "ECC encoding");
        Assert.Equal(100, reported[^1].Percent);
    }

    // ── Восстановление ────────────────────────────────────────────────────

    [Fact]
    public void Decode_AllDataPresent_ReturnsOriginalVolumes()
    {
        var data = DataVolumes();
        var ecc = _rs.Encode(data, M);

        var sectors = data.Concat(ecc).Cast<byte[]?>().ToArray();
        var result = _rs.Decode(sectors, AllValid(K + M), K);

        Assert.NotNull(result);
        for (var i = 0; i < K; i++)
            Assert.Equal(data[i], result![i]);
    }

    [Theory]
    [InlineData(0, 3)]
    [InlineData(1, 3)]
    [InlineData(1, 2)]
    [InlineData(3, 0)]
    public void Decode_ErasedDataVolumes_AreRecovered(int firstErased, int secondErased)
    {
        var data = DataVolumes();
        var ecc = _rs.Encode(data, M);

        var sectors = data.Concat(ecc).Cast<byte[]?>().ToArray();
        var map = AllValid(K + M);
        map[firstErased] = false;
        map[secondErased] = false;
        sectors[firstErased] = null;
        sectors[secondErased] = null;

        var result = _rs.Decode(sectors, map, K);

        Assert.NotNull(result);
        for (var i = 0; i < K; i++)
            Assert.Equal(data[i], result![i]);
    }

    [Fact]
    public void Decode_ErasuresExceedingEcc_ReturnsNull()
    {
        var data = DataVolumes();
        var ecc = _rs.Encode(data, M);

        var sectors = data.Concat(ecc).Cast<byte[]?>().ToArray();
        var map = AllValid(K + M);
        for (var i = 0; i <= M; i++) // стёрто data больше, чем есть ECC
        {
            map[i] = false;
            sectors[i] = null;
        }

        Assert.Null(_rs.Decode(sectors, map, K));
    }

    [Fact]
    public void Decode_ErasedEccOnly_DataIsPassthrough()
    {
        var data = DataVolumes();
        var ecc = _rs.Encode(data, M);

        var sectors = data.Concat(ecc).Cast<byte[]?>().ToArray();
        var map = AllValid(K + M);
        for (var j = 0; j < M; j++)
        {
            map[K + j] = false;
            sectors[K + j] = null;
        }

        var result = _rs.Decode(sectors, map, K);

        Assert.NotNull(result);
        for (var i = 0; i < K; i++)
            Assert.Equal(data[i], result![i]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(6)] // >= total
    public void Decode_InvalidDataCount_ReturnsNull(int dataCount)
    {
        var data = DataVolumes();

        var result = _rs.Decode(data, AllValid(K), dataCount);

        Assert.Null(result);
    }

    [Fact]
    public void Decode_MapLengthMismatch_ReturnsNull()
    {
        var data = DataVolumes();

        Assert.Null(_rs.Decode(data, AllValid(K + 1), K));
    }

    [Fact]
    public void Decode_OddPayloadLength_ReturnsNull()
    {
        var odd = new byte[][] { new byte[63], new byte[63] };

        Assert.Null(_rs.Decode(odd, AllValid(2), dataCount: 1));
    }

    [Fact]
    public void Decode_ValidFlagWithoutBuffer_ReturnsNull()
    {
        var data = DataVolumes();
        var sectors = data.Cast<byte[]?>().ToArray();
        sectors[1] = null; // карта говорит «валиден», данных нет

        Assert.Null(_rs.Decode(sectors, AllValid(K), K));
    }

    [Fact]
    public void Decode_ReportsRsRecoveryPhase()
    {
        var data = DataVolumes();
        var ecc = _rs.Encode(data, M);
        var sectors = data.Concat(ecc).Cast<byte[]?>().ToArray();
        var map = AllValid(K + M);
        map[1] = false;
        sectors[1] = null;

        var reported = new List<(int Percent, string Phase)>();

        var result = _rs.Decode(sectors, map, K, new Collector(reported), ct: default);

        Assert.NotNull(result);
        Assert.Contains(reported, entry => entry.Phase == "RS recovery");
        Assert.Equal(100, reported[^1].Percent);
    }

    private static bool[] AllValid(int count)
    {
        var map = new bool[count];
        Array.Fill(map, true);
        return map;
    }

    private sealed class Collector : IProgress<CodecProgress>
    {
        private readonly List<(int Percent, string Phase)> _list;

        public Collector(List<(int Percent, string Phase)> list) => _list = list;

        public void Report(CodecProgress value) => _list.Add((value.Percent, value.Phase));
    }
}
