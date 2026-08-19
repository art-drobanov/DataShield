using DataShield.Codec.Packets;
using Xunit;

namespace DataShield.Codec.Packets.Tests;

// ─────────────────────────────────────────────────────────────────────────────
//  Упаковка имени файла в поле H1 (14 байт, вариант Б: первая точка)
// ─────────────────────────────────────────────────────────────────────────────

public class FileNameCodecTests
{
    [Theory]
    [InlineData("a.txt", "a.txt")]
    [InlineData("archive.zip", "archive.zip")]                       // 11 ≤ 14
    [InlineData("backup.tar.gz", "backup.tar.gz")]                   // 13 ≤ 14, расширение целиком
    [InlineData("documents.tar.gz", "docume~.tar.gz")]               // база 9, бюджет 7 → 6 + ~
    [InlineData("verylongfilename.zip", "verylongf~.zip")]           // база 16, бюджет 10 → 9 + ~
    [InlineData("123456789.zip", "123456789.zip")]                   // база 9, бюджет 10 — влезает
    [InlineData("README", "README")]                                 // без точки
    [InlineData(".gitignore", ".gitignore")]                         // точка первая → вся строка база
    [InlineData("abcdefghijklmnop", "abcdefghijklm~")]               // база 16 без точки → 13 + ~
    [InlineData("", "")]
    public void Pack_BasicCases(string input, string expected)
        => Assert.Equal(expected, FileNameCodec.Pack(input));

    [Fact]
    public void Pack_TruncationMarker_OnlyWhenActuallyTruncated()
    {
        // 14 символов ровно — влезает, тильды нет
        Assert.Equal("abcdefghij.zip", FileNameCodec.Pack("abcdefghij.zip"));

        // база 11 при бюджете 10 — усечение с тильдой
        Assert.Equal("abcdefghi~.zip", FileNameCodec.Pack("abcdefghijk.zip"));
    }

    [Theory]
    [InlineData("123456789012345678.tar.gz")]  // ext 7 → бюджет 7, база 18 → 6 + ~
    [InlineData("a..tar.gz")]                  // ext «..tar.gz» (8) → бюджет 6, база 1 — влезает
    public void Pack_LongExtension_FitsInField(string input)
    {
        var packed = FileNameCodec.Pack(input);
        Assert.True(packed.Length <= PacketFormat.FileNameSize);
    }

    [Fact]
    public void Pack_ExtensionTooLong_Throws()
    {
        // ext = ".tar.gz.bak2x" (13) → budget 1 < 2, base "documents" не влезает
        Assert.Throws<InvalidOperationException>(
            () => FileNameCodec.Pack("documents.tar.gz.bak2x"));
    }

    [Fact]
    public void Pack_SplitIsOnFirstDot()
    {
        // Первая точка: ext = ".name.txt" (9), budget = 5, base "my" влезает
        Assert.Equal("my.name.txt", FileNameCodec.Pack("my.name.txt"));

        // base "mylo" (4) влезает в budget 5
        Assert.Equal("mylo.name.txt", FileNameCodec.Pack("mylo.name.txt"));

        // base "mylong" (6) > 5 → усечение до 4 + ~
        Assert.Equal("mylo~.name.txt", FileNameCodec.Pack("mylong.name.txt"));
    }

    [Fact]
    public void Pack_MinimalBase_WithTilde()
    {
        // ext = ".tar.gz.bak" (11) → budget 3: база "abc" влезает без усечения
        Assert.Equal("abc.tar.gz.bak", FileNameCodec.Pack("abc.tar.gz.bak"));

        // база "abcd" > 3 → усечение до 2 + ~
        Assert.Equal("ab~.tar.gz.bak", FileNameCodec.Pack("abcd.tar.gz.bak"));
    }

    [Fact]
    public void Pack_MinimalBudget_SingleCharPlusTilde()
    {
        // ext 12 → budget 2: база "xyz" (3) > 2 → 1 символ + ~
        Assert.Equal("x~.tar.gz.bak2", FileNameCodec.Pack("xyz.tar.gz.bak2"));
    }

    [Fact]
    public void Pack_ResultNeverLongerThanField()
    {
        var samples = new[]
        {
            "a", "ab", "a.b", "file.tar.gz", "superlongname.tar.gz",
            "x.123456789012", "no-extension-here-at-all"
        };

        foreach (var name in samples)
            Assert.True(FileNameCodec.Pack(name).Length <= PacketFormat.FileNameSize);
    }

    [Fact]
    public void Pack_Null_ReturnsEmpty()
        => Assert.Equal("", FileNameCodec.Pack(null!));
}
