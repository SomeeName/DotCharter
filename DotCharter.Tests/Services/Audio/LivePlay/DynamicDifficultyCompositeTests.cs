using DotCharter.Services.Audio.LivePlay;

namespace DotCharter.Tests.Services.Audio.LivePlay;

/// <summary>
///     #382: during a dynamic-difficulty run the 3D highway draws, phrase by phrase, the level being
///     judged. The composite and the matcher read one selection rule over the same spans, so what
///     is drawn is exactly what is judged.
/// </summary>
public sealed class DynamicDifficultyCompositeTests
{
    private static List<BeatMarker> Beats() =>
    [
        .. Enumerable.Range(0, 64).Select(i => new BeatMarker
        {
            Time = i * 500, Bpm = 120, BeatsInMeasure = 4, NoteDenominator = 4, IsMeasure = i % 4 == 0
        })
    ];

    /// <summary>A 48-beat one-level chart in three phrases, laddered to 8 levels.</summary>
    private static Arrangement Ladder(List<BeatMarker> beats)
    {
        var level = new ChartLevel();
        for (var beat = 0; beat < 48; beat++)
            level.Notes.Add(new GuitarNote { Position = new FractionalPosition(beat), StringIndex = beat % 6, Fret = 3 + beat % 5 });
        var arrangement = new Arrangement { Name = "Lead" };
        arrangement.Levels.Add(level);
        arrangement.Phrases.AddRange(["intro", "verse", "chorus"]);
        for (var p = 0; p < 3; p++)
            arrangement.PhraseIterations.Add(new PhraseIteration { Position = new FractionalPosition(p * 16), PhraseId = p });
        return DynamicDifficultyLadder.For(arrangement, beats);
    }

    private static int PickByPhrase(LivePhraseSpan span) => span.PhraseName switch
    {
        "intro" => 0,
        "verse" => 3,
        _ => 7
    };

    [Fact]
    public void TheCompositeDrawsExactlyTheOnsetsTheMatcherJudges()
    {
        var beats = Beats();
        var ladder = Ladder(beats);
        var matcher = new LiveNoteMatcher();
        matcher.Load(ladder, beats, PickByPhrase);

        var composite = DynamicDifficultyComposite.Build(ladder, beats, PickByPhrase)!;

        var drawn = composite.Notes.Select(n => Math.Round(n.Position.GetPositionMs(beats))).Order().ToList();
        var judged = matcher.Expected.Select(e => Math.Round(e.TimeMs)).Order().ToList();
        Assert.Equal(judged, drawn);
    }

    [Fact]
    public void EachPhraseCarriesItsOwnLevelsSounds()
    {
        var beats = Beats();
        var ladder = Ladder(beats);
        var levels = ladder.Levels;

        var composite = DynamicDifficultyComposite.Build(ladder, beats, PickByPhrase)!;

        int CountIn(ChartLevel level, int fromBeat, int toBeat) =>
            level.Notes.Count(n => n.Position.BeatId >= fromBeat && n.Position.BeatId < toBeat);
        Assert.Equal(CountIn(levels[0], 0, 16), CountIn(composite, 0, 16));
        Assert.Equal(CountIn(levels[3], 16, 32), CountIn(composite, 16, 32));
        Assert.Equal(CountIn(levels[7], 32, 48), CountIn(composite, 32, 48));
        Assert.True(CountIn(composite, 0, 16) < CountIn(composite, 32, 48), "the easy phrase is not thinner than the hard one");
    }

    [Fact]
    public void NothingToCompose_OnOneLevelOrWithoutPhrases()
    {
        var beats = Beats();
        var oneLevel = new Arrangement { Name = "Lead" };
        oneLevel.Levels.Add(new ChartLevel());
        var noPhrases = Ladder(beats);
        noPhrases.PhraseIterations.Clear();

        Assert.Null(DynamicDifficultyComposite.Build(oneLevel, beats, _ => 0));
        Assert.Null(DynamicDifficultyComposite.Build(noPhrases, beats, _ => 0));
    }

    /// <summary>The 3D driver reads the editor; the run publishes on the project state.</summary>
    [Fact]
    public void TheEditorHandsTheRenderPathWhatTheRunPublishes()
    {
        var h = new DotCharter.Tests.Presentation.Dialogs.ChartEditHarness();
        var level = new ChartLevel();

        h.State.GameplayLevel = level;
        Assert.Same(level, h.Editor.GameplayLevel);
        h.State.GameplayLevel = null;
        Assert.Null(h.Editor.GameplayLevel);
    }
}
