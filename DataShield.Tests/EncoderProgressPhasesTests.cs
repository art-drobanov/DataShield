using DataShield.Codec;
using DataShield.Codec.Reporting;
using Xunit;

namespace DataShield.Tests;

// ─────────────────────────────────────────────────────────────────────────────
//  Фазы прогресса кодера по умолчанию (английский)
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Тест фаз прогресса кодера: без переключения языка глобальный вывод —
/// английские строки фаз (Preparing data → ECC encoding → Building packets
/// → Done) в правильном порядке.
/// </summary>
public sealed class EncoderProgressPhasesTests
{
    /// <summary>
    /// Кодирование файла с подпиской на прогресс: в отчётах присутствуют
    /// все фазы, последняя — «Done».
    /// </summary>
    [Fact]
    public void ProgressPhases_AreEnglish_ByDefault()
    {
        var encoder = new StreamEncoder(eccPercent: 10);
        var phases = new List<string>();

        encoder.Encode(
            new byte[5000],
            "lang.bin",
            new Collector(phases),
            default);

        Assert.Contains("Preparing data", phases);
        Assert.Contains("ECC encoding", phases);
        Assert.Contains("Building packets", phases);
        Assert.Equal("Done", phases[^1]);
    }

    /// <summary>Синхронный сборщик названий фаз прогресса.</summary>
    private sealed class Collector : IProgress<CodecProgress>
    {
        private readonly List<string> _phases;

        public Collector(List<string> phases) => _phases = phases;

        public void Report(CodecProgress value) => _phases.Add(value.Phase);
    }
}
