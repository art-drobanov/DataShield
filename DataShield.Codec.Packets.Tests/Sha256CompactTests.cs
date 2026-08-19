using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace DataShieldTests;

// ─────────────────────────────────────────────────────────────────────────────
//  Sha256Compact — эталон для будущего порта на C++
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Тесты собственной реализации SHA-256. Цель — гарантировать побитовое
/// совпадение с BCL (System.Security.Cryptography.SHA256), поскольку
/// Sha256Compact служит эталоном для будущего порта на C++.
///
/// Проверяются известные векторы FIPS 180-4, граничные длины блоков
/// (512-битный блок = 64 байта, паддинг при хвосте ≥56 байт), большие
/// входы и общие криптографические свойства (детерминизм, лавинный эффект).
/// </summary>
public class Sha256CompactTests
{
    // Известный вектор FIPS 180-4: SHA-256("abc").
    private const string AbcVector =
        "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";

    // Известный вектор: SHA-256("").
    private const string EmptyVector =
        "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    /// <summary>Хеш в виде hex-строки нижнего регистра.</summary>
    private static string Hex(byte[] hash) =>
        Convert.ToHexString(hash).ToLowerInvariant();

    // ────────────────────────────────────────────────────────────────────────
    //  Известные векторы
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>Пустой вход — вектор SHA-256("") из FIPS 180-4.</summary>
    [Fact]
    public void Empty_Input_MatchesKnownVector() =>
        Assert.Equal(EmptyVector, Hex(Sha256Compact.HashData(Array.Empty<byte>())));

    /// <summary>Строка «abc» — классический вектор из FIPS 180-4.</summary>
    [Fact]
    public void Abc_MatchesKnownVector() =>
        Assert.Equal(AbcVector, Hex(Sha256Compact.HashData(
            Encoding.ASCII.GetBytes("abc"))));

    /// <summary>Самотест инструмента (вектор «abc») возвращает true.</summary>
    [Fact]
    public void SelfTest_ReturnsTrue() =>
        Assert.True(Sha256Compact.Test());

    // ────────────────────────────────────────────────────────────────────────
    //  Границы блоков (512-битный блок = 64 байта)
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Длины вокруг границ блоков и паддинга: 55/56 — порог, при котором
    /// добавляется второй блок под padding; 63/64/65 — конец/начало блока;
    /// 119/120/128 — то же для второго блока. Хеш совпадает с BCL.
    /// </summary>
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

    /// <summary>
    /// Большой вход ~1 МБ с некратным 64 размером: полный проход по блокам
    /// плюс хвост с паддингом.
    /// </summary>
    [Fact]
    public void Large_Input_MatchesBclSha256()
    {
        var data = new byte[1_000_003]; // некратный размер: хвост + паддинг
        new Random(42).NextBytes(data);

        Assert.Equal(
            SHA256.HashData(data),
            Sha256Compact.HashData(data));
    }

    /// <summary>
    /// Вырожденные паттерны (все 0x00 и все 0xFF) — совпадение с BCL.
    /// </summary>
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

    /// <summary>Хеширование детерминировано: один вход — один выход.</summary>
    [Fact]
    public void Deterministic_ForSameInput()
    {
        var data = Encoding.ASCII.GetBytes("determinism");

        Assert.Equal(
            Sha256Compact.HashData(data),
            Sha256Compact.HashData(data));
    }

    /// <summary>Лавинный эффект: инверсия одного бита меняет весь хеш.</summary>
    [Fact]
    public void Avalanche_SingleBitFlip_ChangesHash()
    {
        var a = new byte[64];
        new Random(7).NextBytes(a);
        var b = (byte[])a.Clone();
        b[10] ^= 0x01;

        Assert.NotEqual(Sha256Compact.HashData(a), Sha256Compact.HashData(b));
    }

    /// <summary>Длина выхода всегда 32 байта (256 бит).</summary>
    [Fact]
    public void Output_Length_Is_32()
    {
        Assert.Equal(32, Sha256Compact.HashData(new byte[777]).Length);
    }
}
