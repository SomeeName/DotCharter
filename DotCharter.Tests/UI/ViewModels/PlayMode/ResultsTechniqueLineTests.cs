using DotCharter.Services.Audio.LivePlay;
using DotCharter.UI.ViewModels.PlayMode;

namespace DotCharter.Tests.UI.ViewModels.PlayMode;

/// <summary>
///     The results screen's technique line (#645): the techniques a run hit WITHOUT their
///     technique, played-without over judged, in a fixed order — and nothing at all for a clean
///     run, so the line hides.
/// </summary>
public sealed class ResultsTechniqueLineTests
{
    private static readonly FakeLocalization English = FakeLocalization.English();

    [Fact]
    public void Lists_every_downgraded_technique_with_its_count_in_a_fixed_order()
    {
        TechniqueResult[] run =
        [
            new(PlayedTechnique.PalmMute, 59, 5, 0),
            new(PlayedTechnique.Bend, 8, 3, 1),
            new(PlayedTechnique.Vibrato, 7, 2, 0),
            new(PlayedTechnique.Slide, 3, 1, 0)
        ];

        Assert.Equal("Played without the technique: bends 3/12 · slides 1/4 · vibrato 2/9 · palm mutes 5/64",
            ResultsTechniqueLine.For(run, English));
    }

    [Fact]
    public void Leaves_out_techniques_played_cleanly_and_ones_nothing_can_downgrade()
    {
        TechniqueResult[] run =
        [
            new(PlayedTechnique.Bend, 12, 0, 2),
            new(PlayedTechnique.Chord, 40, 0, 3),
            new(PlayedTechnique.Vibrato, 4, 1, 0)
        ];

        Assert.Equal("Played without the technique: vibrato 1/5", ResultsTechniqueLine.For(run, English));
    }

    [Fact]
    public void A_clean_run_or_no_tallies_is_empty_so_the_line_hides()
    {
        Assert.Equal("", ResultsTechniqueLine.For(null, English));
        Assert.Equal("", ResultsTechniqueLine.For([], English));
        Assert.Equal("", ResultsTechniqueLine.For([new TechniqueResult(PlayedTechnique.Bend, 10, 0, 0)], English));
    }

    [Fact]
    public void Every_listed_key_is_an_English_resource()
    {
        var keys = ResultsTechniqueLine.Listed.Select(l => l.Key).Append("PlayMode_Results_TechniqueLine");
        foreach (var key in keys)
        {
            Assert.NotEqual(key, English[key]);
        }
    }
}
