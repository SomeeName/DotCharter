using DotCharter.Tone.Audio.Tuner;
using DotCharter.Tone.Audio.Tuning;

namespace DotCharter.Tests.Services.Audio.Tuning;

/// <summary>
///     Every tuner screen resolves a chart's open strings through ONE resolver,
///     <see cref="ArrangementTunerLogic.TryResolveTuningSteps" />.
///     <para>
///         <c>ArrangementTunerLogic.Load(Arrangement)</c> — the engine behind Tools ▸
///         Tuner — used to read <c>TuningPitches</c> raw, so a chart that carried only
///         a <c>TuningType</c> name read as E standard there, while the Get Ready check
///         (<see cref="TuningCheckTargets" />) decoded the name: one chart, two answers.
///     </para>
/// </summary>
public sealed class TunerTargetResolutionTests
{
    private static Arrangement NamedOnly(string tuningType, ArrangementType type = ArrangementType.Lead,
        int strings = 6, double centOffset = 0)
    {
        return new Arrangement
        {
            ArrangementType = type,
            StringCount = strings,
            TuningType = tuningType,
            CentOffset = centOffset
        };
    }

    private static ArrangementTunerLogic Loaded(Arrangement arrangement)
    {
        var logic = new ArrangementTunerLogic();
        logic.Load(arrangement);
        return logic;
    }

    [Fact]
    public void A_named_only_drop_D_chart_reads_as_drop_D_in_the_tuner_dialog()
    {
        var logic = Loaded(NamedOnly("E_DROP_D"));

        Assert.Equal(new[] { 38, 45, 50, 55, 59, 64 }, logic.Targets.Select(t => t.Midi));
        Assert.Equal("D2", logic.Targets[0].NoteName);
    }

    /// <summary>The two screens over one named-only arrangement aim at the same
    /// notes AND the same frequencies (a cent offset included).</summary>
    [Theory]
    [InlineData("E_DROP_D", ArrangementType.Lead, 6, 0.0)]
    [InlineData("D_STANDARD", ArrangementType.Rhythm, 6, -31.0)]
    [InlineData("E_DROP_D", ArrangementType.Bass, 4, 0.0)]
    [InlineData("D_STANDARD", ArrangementType.Bass, 4, 12.5)]
    public void The_tuner_dialog_and_the_guided_check_agree_on_a_named_only_chart(
        string tuningType, ArrangementType type, int strings, double centOffset)
    {
        var arrangement = NamedOnly(tuningType, type, strings, centOffset);
        var logic = Loaded(arrangement);

        Assert.True(TuningCheckTargets.TryBuild(arrangement, TuningCheckCapoMode.TuneOpenThenPlaceCapo,
            out var guided));
        Assert.Equal(guided.Length, logic.Targets.Count);
        for (var i = 0; i < guided.Length; i++)
        {
            Assert.Equal(guided[i].Midi, logic.Targets[i].Midi);
            Assert.Equal(guided[i].NoteName, logic.Targets[i].NoteName);
            Assert.Equal(guided[i].FrequencyHz, logic.Targets[i].FrequencyHz, 6);
        }

        // Not E standard: the name was decoded on both screens.
        Assert.NotEqual(type == ArrangementType.Bass ? 28 : 40, logic.Targets[0].Midi);
    }

    /// <summary>The Get Ready screen builds its check through the same table.</summary>
    [Fact]
    public void The_guided_check_aims_a_named_only_chart_at_the_named_tuning()
    {
        var check = new GuidedTuningCheck(NamedOnly("E_DROP_D"));

        Assert.Equal(38, check.Targets[0].Midi);
        Assert.Equal(Loaded(NamedOnly("E_DROP_D")).Targets.Select(t => t.Midi), check.Targets.Select(t => t.Midi));
    }

    [Fact]
    public void Per_string_pitches_win_over_the_name()
    {
        var arrangement = NamedOnly("E_DROP_D");
        foreach (var step in new[] { -1, -1, -1, -1, -1, -1 }) arrangement.TuningPitches.Add(new TuningOffset(step));

        Assert.True(ArrangementTunerLogic.TryResolveTuningSteps(arrangement, out var steps));
        Assert.Equal(new[] { -1, -1, -1, -1, -1, -1 }, steps);
        Assert.Equal(39, Loaded(arrangement).Targets[0].Midi);
    }

    /// <summary>A name nothing can decode: the resolver says so, and both the
    /// tuner dialog and the guided check fall back to standard the same way.</summary>
    [Fact]
    public void An_unknown_name_is_unresolved_and_both_screens_fall_back_to_standard()
    {
        var arrangement = NamedOnly("NOT_A_TUNING");

        Assert.False(ArrangementTunerLogic.TryResolveTuningSteps(arrangement, out _));
        Assert.False(TuningCheckTargets.TryBuild(arrangement, TuningCheckCapoMode.TuneOpenThenPlaceCapo, out _));
        Assert.Equal(new[] { 40, 45, 50, 55, 59, 64 }, Loaded(arrangement).Targets.Select(t => t.Midi));
        Assert.Equal(new[] { 40, 45, 50, 55, 59, 64 }, TuningCheckTargets.Build(arrangement).Select(t => t.Midi));
    }

    /// <summary>The capo never moves the tuner dialog's targets: the player tunes
    /// the open strings, as in the guided check's default flow.</summary>
    [Fact]
    public void A_capo_leaves_the_tuner_dialog_on_the_open_strings_like_the_default_guided_flow()
    {
        var arrangement = NamedOnly("E_DROP_D");
        arrangement.Capo = 2;

        Assert.Equal(TuningCheckTargets.Build(arrangement).Select(t => t.Midi),
            Loaded(arrangement).Targets.Select(t => t.Midi));
    }
}
