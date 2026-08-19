using DataShield.Codec.Ecc;
using DataShield.Codec.Reporting;
using Xunit;

namespace DataShield.Codec.Ecc.Tests;

// ─────────────────────────────────────────────────────────────────────────────
//  RsCodecAdapter — кодирование и восстановление томов над GF(2¹⁶)
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Тесты адаптера Рида–Соломона: расчёт ECC-томов по data-томам,
/// восстановление стёртых data-томов и валидация некорректных входов.
///
/// Типовой сценарий: K = 4 data-тома по 64 байта (размер payload-пакета),
/// M = 2 ECC-тома; стирания моделируются обнулением сектора и
/// сбросом флага в карте валидности.
/// </summary>
public sealed class RsCodecAdapterTests
{
    // Длина тома в байтах — совпадает с PacketFormat.PayloadSize:
    // RS работает над 16-битными символами, поэтому длина обязана быть чётной
    private const int Payload = 64; // PacketFormat.PayloadSize

    // Базовая конфигурация: 4 data-тома + 2 ECC-тома
    private const int K = 4;
    private const int M = 2;

    private readonly RsCodecAdapter _rs = new();

    /// <summary>
    /// Детерминированный «случайный» том: содержимое зависит только от seed,
    /// что делает тесты воспроизводимыми.
    /// </summary>
    private static byte[] Volume(int seed)
    {
        var data = new byte[Payload];
        new Random(seed).NextBytes(data);
        return data;
    }

    /// <summary>Набор из K различных data-томов (seed = индекс тома).</summary>
    private static byte[][] DataVolumes() =>
        Enumerable.Range(0, K).Select(Volume).ToArray();

    // ── Кодирование ───────────────────────────────────────────────────────

    /// <summary>
    /// Encode(data, M) возвращает ровно M ECC-томов, каждый длиной
    /// с data-томом (64 байта).
    /// </summary>
    [Fact]
    public void Encode_ProducesRequestedEccVolumes()
    {
        var data = DataVolumes();

        var ecc = _rs.Encode(data, M);

        Assert.Equal(M, ecc.Count);
        Assert.All(ecc, volume => Assert.Equal(Payload, volume.Length));
    }

    /// <summary>
    /// Нулевое или отрицательное число ECC-томов не считается ошибкой —
    /// кодирование просто не требуется, результат пуст.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Encode_NonPositiveEccCount_ReturnsEmpty(int eccCount)
    {
        var data = DataVolumes();

        var ecc = _rs.Encode(data, eccCount);

        Assert.Empty(ecc);
    }

    /// <summary>Пустой набор data-томов — нет данных, нет и ECC.</summary>
    [Fact]
    public void Encode_EmptyInput_ReturnsEmpty()
    {
        var ecc = _rs.Encode(Array.Empty<byte[]>(), M);

        Assert.Empty(ecc);
    }

    /// <summary>
    /// N + M не может превышать размер поля GF(2¹⁶) = 65536 элементов:
    /// запрос ECC сверх лимита завершается InvalidOperationException.
    /// </summary>
    [Fact]
    public void Encode_CountsExceedingFieldSize_Throw()
    {
        var data = DataVolumes();

        Assert.Throws<InvalidOperationException>(
            () => _rs.Encode(data, eccCount: 65536 - data.Length));
    }

    /// <summary>
    /// Во время кодирования сообщается фаза «ECC encoding»,
    /// и прогресс завершается на 100%.
    /// </summary>
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

    /// <summary>
    /// Baseline: при полном наборе секторов Decode возвращает исходные
    /// data-тома без изменений (ECC не нужен).
    /// </summary>
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

    /// <summary>
    /// Основной сценарий: любые два стёртых тома (data или ECC)
    /// восстанавливаются — M = 2 ECC-тома компенсируют 2 стирания.
    /// </summary>
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

    /// <summary>
    /// Стираний больше, чем ECC-томов (M + 1 при M = 2) — восстановление
    /// невозможно, Decode возвращает null вместо исключения.
    /// </summary>
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

    /// <summary>
    /// Стёрты только ECC-тома: data целы, декодер пропускает их
    /// насквозь без обращения к RS-алгебре.
    /// </summary>
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

    /// <summary>
    /// Некорректное число data-томов: 0, отрицательное или равное общему
    /// числу секторов (ECC не остаётся) — Decode возвращает null.
    /// </summary>
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

    /// <summary>Карта валидности длиннее набора секторов — вход некорректен.</summary>
    [Fact]
    public void Decode_MapLengthMismatch_ReturnsNull()
    {
        var data = DataVolumes();

        Assert.Null(_rs.Decode(data, AllValid(K + 1), K));
    }

    /// <summary>
    /// RS работает над 16-битными символами: нечётная длина тома (63 байта)
    /// не раскладывается на символы, Decode возвращает null.
    /// </summary>
    [Fact]
    public void Decode_OddPayloadLength_ReturnsNull()
    {
        var odd = new byte[][] { new byte[63], new byte[63] };

        Assert.Null(_rs.Decode(odd, AllValid(2), dataCount: 1));
    }

    /// <summary>
    /// Рассинхронизация карты и данных: флаг «валиден», но сам буфер
    /// отсутствует (null) — Decode возвращает null.
    /// </summary>
    [Fact]
    public void Decode_ValidFlagWithoutBuffer_ReturnsNull()
    {
        var data = DataVolumes();
        var sectors = data.Cast<byte[]?>().ToArray();
        sectors[1] = null; // карта говорит «валиден», данных нет

        Assert.Null(_rs.Decode(sectors, AllValid(K), K));
    }

    /// <summary>
    /// Во время восстановления сообщается фаза «RS recovery»,
    /// и прогресс завершается на 100%.
    /// </summary>
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

    /// <summary>Карта валидности из count флагов «все секторы на месте».</summary>
    private static bool[] AllValid(int count)
    {
        var map = new bool[count];
        Array.Fill(map, true);
        return map;
    }

    /// <summary>
    /// Сборщик отчётов прогресса: запоминает каждую пару
    /// (процент, фаза) для последующих проверок в тестах.
    /// </summary>
    private sealed class Collector : IProgress<CodecProgress>
    {
        private readonly List<(int Percent, string Phase)> _list;

        public Collector(List<(int Percent, string Phase)> list) => _list = list;

        public void Report(CodecProgress value) => _list.Add((value.Percent, value.Phase));
    }
}
