using DotCharter.Tone.Audio;
using DotCharter.Tone.Audio.Equalizer;
using JuceSharp;

namespace DotCharter.Tests.Services.Audio;

/// <summary>
///     The EQ band push must COMPLETE, band by band, whatever one band does.
///     <para>
///         <c>JuceAudioEngine.SetEqBand</c> THROWS on a bad index since the
///         wave-4 wrapper audit — it used to log a warning and return — and the
///         loop that calls it is <c>PushEqTo</c>, which is not only the live
///         apply but the engine-REBUILD replay: a device change, the NAM 48 kHz
///         request, a watchdog recovery. A throw on band <c>i</c> abandoned bands
///         <c>i+1…n</c> AND the solo / route / trim / power calls that follow the
///         loop, which is exactly the half-written coefficient set the ordering in
///         that method was written to prevent — and left the section OFF on the
///         rebuilt engine while the UI kept drawing the user's curve. That is the
///         replay-on-rebuild bug this codebase has already shipped twice (co-op
///         input routing, and the EQ itself).
///     </para>
///     <para>
///         Asserted through <c>WriteEqBands</c>'s delegate rather than a real
///         engine: reaching the throw at runtime needs an audio device AND a
///         device change, whereas the invariant is entirely about the loop.
///     </para>
/// </summary>
public sealed class EqBandWriteReplayTests
{
    private static EqSection SectionWithEveryBandOn()
    {
        var section = new EqSection();
        for (var i = 0; i < section.Bands.Count; i++)
            section.SetBandEnabled(i, true);
        return section;
    }

    /// <summary>
    ///     A band the engine rejects costs one log line. Every OTHER band is
    ///     still written — including the ones AFTER it, which is the half that
    ///     used to be lost.
    /// </summary>
    [Fact]
    public void AThrowingBand_DoesNotStopTheRemainingBands()
    {
        var section = SectionWithEveryBandOn();
        var logger = new FakeAppLogger();
        var seen = new List<int>();

        var written = JuceAudioPlaybackService.WriteEqBands(section,
            (i, _, _, _, _, _) =>
            {
                if (i == 2) throw new ArgumentOutOfRangeException(nameof(i), "band index rejected");
                seen.Add(i);
            },
            logger);

        Assert.Equal(section.Bands.Count - 1, written);
        Assert.Equal(Enumerable.Range(0, section.Bands.Count).Where(i => i != 2), seen);
        // The rejection is REPORTED. A silent continue is the other way to fail
        // this feature: the replay completes and nobody ever learns a band is gone.
        Assert.Contains(logger.Entries,
            e => e.Message.Contains("EQ band 2 was rejected", StringComparison.Ordinal));
    }

    /// <summary>
    ///     Anti-vacuity: with no band throwing, every band lands and nothing is
    ///     logged. Without this, a <c>WriteEqBands</c> that wrote NOTHING would
    ///     pass the test above.
    /// </summary>
    [Fact]
    public void WithNoRejection_EveryBandIsWrittenInOrder_AndNothingIsLogged()
    {
        var section = SectionWithEveryBandOn();
        var logger = new FakeAppLogger();
        var seen = new List<int>();

        var written = JuceAudioPlaybackService.WriteEqBands(section, (i, _, _, _, _, _) => seen.Add(i), logger);

        Assert.Equal(section.Bands.Count, written);
        Assert.Equal(Enumerable.Range(0, section.Bands.Count), seen);
        Assert.DoesNotContain(logger.Entries, e => e.Level is "ERROR" or "WARN");
    }

    /// <summary>
    ///     The section's band count and the engine's are the same number today,
    ///     and this is what keeps them honest: a section that grew a seventh band
    ///     must not reach <c>SetEqBand(6, …)</c>, which the ABI refuses by
    ///     construction (<c>DCA_EQ_BAND_COUNT</c>).
    /// </summary>
    [Fact]
    public void NoBandIndexIsEverOfferedPastTheEnginesFixedCount()
    {
        var section = SectionWithEveryBandOn();
        var highest = -1;

        JuceAudioPlaybackService.WriteEqBands(section, (i, _, _, _, _, _) => highest = Math.Max(highest, i), null);

        Assert.True(highest < JuceAudioEngine.EqBandCount,
            $"Band index {highest} was offered to an engine whose EqBandCount is {JuceAudioEngine.EqBandCount}.");
    }
}
