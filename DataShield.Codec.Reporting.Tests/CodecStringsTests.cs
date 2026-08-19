using DataShield.Codec.Reporting;
using Xunit;

namespace DataShield.Codec.Reporting.Tests;

// ─────────────────────────────────────────────────────────────────────────────
//  CodecStrings — язык фаз прогресса кодека
// ─────────────────────────────────────────────────────────────────────────────

public class CodecStringsTests
{
    [Fact]
    public void Default_Language_Is_English() =>
        Assert.Equal(CodecLanguage.English, CodecStrings.Language);

    [Fact]
    public void PhaseStrings_FollowSelectedLanguage()
    {
        var saved = CodecStrings.Language;

        try
        {
            CodecStrings.Language = CodecLanguage.English;

            Assert.Equal("Preparing data", CodecStrings.DataPreparation);
            Assert.Equal("ECC encoding", CodecStrings.EccEncoding);
            Assert.Equal("Done", CodecStrings.Done);
            Assert.Equal("Searching for headers", CodecStrings.HeaderSearch);
            Assert.Equal("Searching for sectors", CodecStrings.SectorSearch);
            Assert.Equal("RS recovery", CodecStrings.RsRecovery);
            Assert.Equal("Assembly finished", CodecStrings.AssemblyFinished);

            CodecStrings.Language = CodecLanguage.Russian;

            Assert.Equal("Подготовка данных", CodecStrings.DataPreparation);
            Assert.Equal("ECC-кодирование", CodecStrings.EccEncoding);
            Assert.Equal("Готово", CodecStrings.Done);
            Assert.Equal("Поиск заголовков", CodecStrings.HeaderSearch);
            Assert.Equal("Поиск секторов", CodecStrings.SectorSearch);
            Assert.Equal("RS-восстановление", CodecStrings.RsRecovery);
            Assert.Equal("Сборка завершена", CodecStrings.AssemblyFinished);
            Assert.Equal("больше 9.22e18", CodecStrings.MoreThanLongMax);
        }
        finally
        {
            CodecStrings.Language = saved;
        }
    }

    [Fact]
    public void ProgressPhases_AreEnglish_ByDefault()
    {
        // Фазы кодека по умолчанию — английские строки.
        Assert.Equal("Preparing data", CodecStrings.DataPreparation);
        Assert.Equal("ECC encoding", CodecStrings.EccEncoding);
        Assert.Equal("Building packets", CodecStrings.PacketBuilding);
        Assert.Equal("Done", CodecStrings.Done);
    }
}
