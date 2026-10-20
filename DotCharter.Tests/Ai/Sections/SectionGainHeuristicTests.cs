using DotCharter.Services.Ai.Match;
using DotCharter.Services.Ai.Match.Sections;

namespace DotCharter.Tests.Ai.Sections;

/// <summary>
///     The one guess in this lane, pinned so that it stays a guess about GAIN and does
///     not quietly become a guess about level, length or brightness.
/// </summary>
public sealed class SectionGainHeuristicTests
{
    [Fact]
    public void CleanPlucks_ReadAsClean()
    {
        var reading = SectionGainHeuristic.Measure(new MonoAudio(SectionFakes.Plucks(6.0), SectionFakes.Rate));

        Assert.Equal(SectionGainHeuristic.CleanSuffix, reading.Suffix);
        Assert.True(reading.CrestDb >= SectionGainHeuristic.CleanCrestDb ||
                    reading.HighRatio <= SectionGainHeuristic.CleanHighRatio,
            $"crest {reading.CrestDb:0.0} dB, high band {reading.HighRatio:0.000}");
    }

    [Fact]
    public void HardClippedPlucks_ReadAsDriven()
    {
        var reading = SectionGainHeuristic.Measure(new MonoAudio(SectionFakes.Clipped(6.0), SectionFakes.Rate));

        Assert.Equal(SectionGainHeuristic.DrivenSuffix, reading.Suffix);
        Assert.True(reading.CrestDb <= SectionGainHeuristic.DrivenCrestDb, $"crest {reading.CrestDb:0.0} dB");
        Assert.True(reading.HighRatio >= SectionGainHeuristic.DrivenHighRatio, $"high band {reading.HighRatio:0.000}");
    }

    /// <summary>
    ///     A HALVED clean part is the same tone at a lower level. Anything that reads
    ///     level rather than gain flips here, and that is the mistake this heuristic is
    ///     most likely to drift into: both of its measurements are ratios on purpose.
    /// </summary>
    [Fact]
    public void LevelDoesNotChangeTheAnswer()
    {
        var samples = SectionFakes.Plucks(6.0);
        var quiet = Array.ConvertAll(samples, static s => s * 0.05f);

        Assert.Equal(
            SectionGainHeuristic.Measure(new MonoAudio(samples, SectionFakes.Rate)).Suffix,
            SectionGainHeuristic.Measure(new MonoAudio(quiet, SectionFakes.Rate)).Suffix);
    }

    /// <summary>Silence has no timbre, and a section the proposer could not fill must
    /// not come back labelled.</summary>
    [Fact]
    public void SilenceSaysNothing()
    {
        Assert.Null(SectionGainHeuristic.Suffix(new MonoAudio(new float[SectionFakes.Rate], SectionFakes.Rate)));
        Assert.Null(SectionGainHeuristic.Suffix(new MonoAudio([], SectionFakes.Rate)));
    }

    /// <summary>
    ///     The two reference readings the thresholds were set from, pinned as NUMBERS
    ///     rather than as a verdict. Measured 2026-09-12 in this worktree; if a future
    ///     change moves them, this says by how much and in which direction, which is
    ///     the thing a bare "(clean)"/"(driven)" assertion can never tell anyone.
    /// </summary>
    [Fact]
    public void TheReferenceReadingsAreWhatTheThresholdsWereSetFrom()
    {
        var clean = SectionGainHeuristic.Measure(new MonoAudio(SectionFakes.Plucks(6.0), SectionFakes.Rate));
        var driven = SectionGainHeuristic.Measure(new MonoAudio(SectionFakes.Clipped(6.0), SectionFakes.Rate));

        Assert.Equal(14.602, clean.CrestDb, 2);
        Assert.Equal(0.00236, clean.HighRatio, 5);
        Assert.Equal(0.528, driven.CrestDb, 2);
        Assert.Equal(0.00943, driven.HighRatio, 5);
    }

    [Fact]
    public void ItIsDeterministic()
    {
        var audio = new MonoAudio(SectionFakes.Clipped(4.0), SectionFakes.Rate);
        Assert.Equal(SectionGainHeuristic.Measure(audio), SectionGainHeuristic.Measure(audio));
    }
}
