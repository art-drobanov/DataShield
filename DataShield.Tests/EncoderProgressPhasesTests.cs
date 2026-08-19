using DataShield.Codec;
using DataShield.Codec.Reporting;
using Xunit;

namespace DataShield.Tests;

// ─────────────────────────────────────────────────────────────────────────────
//  Фазы прогресса кодера по умолчанию (английский)
// ─────────────────────────────────────────────────────────────────────────────

public sealed class EncoderProgressPhasesTests
{
    [Fact]
    public void ProgressPhases_AreEnglish_ByDefault()
    {
        var encoder = new FileEncoder(eccPercent: 10);
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

    private sealed class Collector : IProgress<CodecProgress>
    {
        private readonly List<string> _phases;

        public Collector(List<string> phases) => _phases = phases;

        public void Report(CodecProgress value) => _phases.Add(value.Phase);
    }
}
