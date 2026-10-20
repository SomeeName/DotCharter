using DotCharter.Tone.Audio.Tuner;
using DotCharter.Tone.Audio.Tuning;

namespace DotCharter.Tests.Services.Audio.Tuning;

/// <summary>
///     A chart referenced off A440 has to be TUNED off A440, or live judging
///     disagrees with the player's tuner.
///     <para>
///         <c>TuningCheckTargets</c> has always folded <c>Arrangement.CentOffset</c>
///         into every target frequency and says in its own doc why.
///         <c>ArrangementTunerLogic</c> — the engine behind Tools ▸ Tuner — never
///         read the field at all: it computed the deviation from an integer MIDI
///         number. So the tuner dialog told the player to tune to A440 while the
///         Get Ready guided check, over the SAME arrangement, told them to tune
///         50 ¢ flat.
///     </para>
/// </summary>
public sealed class TunerCentOffsetTests
{
    private static Arrangement Chart(double centOffset) => new()
    {
        ArrangementType = ArrangementType.Lead,
        StringCount = 6,
        CentOffset = centOffset
    };

    private static ArrangementTunerLogic Loaded(Arrangement arr)
    {
        var logic = new ArrangementTunerLogic();
        logic.Load(arr);
        return logic;
    }

    /// <summary>50 ¢ flat of the high E (MIDI 64) IS this chart's high E.</summary>
    [Fact]
    public void AStringAtTheChartsOwnReference_ReadsInTune()
    {
        var logic = Loaded(Chart(-50));

        var frame = logic.Process(ArrangementTunerLogic.FrequencyOf(64 - 0.5), deltaMs: 600);

        Assert.Equal(5, frame.NearestString);
        Assert.Equal(0.0, frame.CentsOff, 6);
        Assert.True(frame.InTune);
        Assert.True(frame.StringJustConfirmed);
    }

    /// <summary>…and a string at concert pitch is 50 ¢ SHARP of it.</summary>
    [Fact]
    public void AStringAtA440_ReadsSharpOfAFlatReferencedChart()
    {
        var logic = Loaded(Chart(-50));

        var frame = logic.Process(ArrangementTunerLogic.FrequencyOf(64), deltaMs: 600);

        Assert.Equal(5, frame.NearestString);
        Assert.Equal(50.0, frame.CentsOff, 6);
        Assert.False(frame.InTune);
        Assert.False(logic.IsStringTuned(5));
    }

    /// <summary>
    ///     The two screens over one arrangement. This is the assertion that would
    ///     have caught it: the guided check's target table and the tuner dialog's
    ///     must be the same frequencies.
    /// </summary>
    [Theory]
    [InlineData(-50.0)]
    [InlineData(0.0)]
    [InlineData(31.7)]
    public void TheTunerDialogAndTheGuidedCheck_AimAtTheSameFrequencies(double centOffset)
    {
        var arr = Chart(centOffset);
        var logic = Loaded(arr);
        var guided = TuningCheckTargets.Build(arr);

        Assert.Equal(guided.Length, logic.Targets.Count);
        for (var i = 0; i < guided.Length; i++)
        {
            Assert.Equal(guided[i].Midi, logic.Targets[i].Midi);
            Assert.Equal(guided[i].NoteName, logic.Targets[i].NoteName);
            Assert.Equal(guided[i].FrequencyHz, logic.Targets[i].FrequencyHz, 9);
        }
    }

    /// <summary>A chart with no offset is untouched — the overwhelming case.</summary>
    [Fact]
    public void NoOffset_IsAStrictNoOp()
    {
        var logic = Loaded(Chart(0));

        Assert.Equal(ArrangementTunerLogic.FrequencyOf(59), logic.Targets[4].FrequencyHz, 9);
        var frame = logic.Process(ArrangementTunerLogic.FrequencyOf(59), deltaMs: 600);
        Assert.Equal(4, frame.NearestString);
        Assert.Equal(0.0, frame.CentsOff, 6);
        Assert.True(frame.InTune);
    }
}
