using DotCharter.Services.Editing.Tones;

namespace DotCharter.Tests.Services.Editing.Tones;

/// <summary>
///     #759: the I30 fix moves a tone change on a note ONE 32nd note earlier (it was a 16th until the
///     charting conventions asked for about a 32nd), measured on the beat grid (an eighth of a 4/4 beat, a
///     quarter of a 6/8 beat, the previous beat's own meter once it reaches back
///     over a beat line), or not at all when a guard stops it. One undo step; the warning is gone after it.
/// </summary>
public sealed class ToneChangeNudgeTests
{
    private static List<BeatMarker> Beats(double beatMs, int denominator, int count = 16, double firstMs = 0) =>
        [.. Enumerable.Range(0, count).Select(i => new BeatMarker
        {
            Time = firstMs + i * beatMs, Bpm = 60000.0 / beatMs * 4.0 / denominator, NoteDenominator = denominator
        })];

    private static Arrangement WithChangeOnNote(FractionalPosition at)
    {
        var arr = new Arrangement { ToneBase = "clean", ToneA = "lead" };
        arr.Levels.Add(new ChartLevel());
        arr.ToneChanges.Add(new ToneChange { Position = at, ToneName = "lead" });
        arr.Levels[0].Notes.Add(new GuitarNote { Position = at, Fret = 5 });
        return arr;
    }

    private static int I30Count(Arrangement arr, IList<BeatMarker> beats) =>
        new ArrangementValidationService(new FakeAppLogger()).Validate(arr, beats)
            .Count(i => i.Type == ArrangementIssueType.ToneChangeOnNote);

    [Fact]
    public void In44At120Bpm_TheChangeMovesAnEighthBeat_TheWarningGoes_AndUndoBringsItBack()
    {
        var beats = Beats(500, 4);
        var arr = WithChangeOnNote(new FractionalPosition(2));
        Assert.Equal(1, I30Count(arr, beats));

        var nudge = ToneChangeNudge.For(arr, beats, 1000)!;
        Assert.True(nudge.CanApply);
        Assert.Equal(new FractionalPosition(1, new Fraction(7, 8)), nudge.To);

        var history = new HistoryService(new FakeAppLogger());
        history.Do(nudge.Action()!);
        Assert.Equal(937.5, arr.ToneChanges[0].Position.GetPositionMs(beats), 3);
        Assert.Equal(0, I30Count(arr, beats));

        history.Undo();
        Assert.Equal(new FractionalPosition(2), arr.ToneChanges[0].Position);
        Assert.Equal(1, I30Count(arr, beats));
    }

    [Fact]
    public void In68_TheChangeMovesAQuarterOfAnEighthNoteBeat()
    {
        var beats = Beats(400, 8);
        var arr = WithChangeOnNote(new FractionalPosition(3));

        var nudge = ToneChangeNudge.For(arr, beats, 1200)!;

        Assert.Equal(new FractionalPosition(2, new Fraction(3, 4)), nudge.To);
        Assert.Equal(1100.0, nudge.To.GetPositionMs(beats), 3);
        nudge.Action()!.Execute();
        Assert.Equal(0, I30Count(arr, beats));
    }

    [Fact]
    public void ReachingBackOverABeatLine_MeasuresTheRestOnThePreviousBeatsMeter()
    {
        var beats = Beats(500, 4);
        beats[1].NoteDenominator = 8;

        Assert.Equal(new FractionalPosition(1, new Fraction(3, 4)),
            ToneChangeNudge.ThirtySecondEarlier(new FractionalPosition(2), beats));
        Assert.Equal(new FractionalPosition(2, new Fraction(1, 4)),
            ToneChangeNudge.ThirtySecondEarlier(new FractionalPosition(2, new Fraction(3, 8)), beats));
        // A 64th on beat 2 (4/4), the other 64th on beat 1 (6/8): an eighth of that beat.
        Assert.Equal(new FractionalPosition(1, new Fraction(7, 8)),
            ToneChangeNudge.ThirtySecondEarlier(new FractionalPosition(2, new Fraction(1, 16)), beats));
    }

    [Fact]
    public void AChangeLessThanA32ndAfterTheFirstBeat_IsBlocked()
    {
        var beats = Beats(500, 4);
        var arr = WithChangeOnNote(new FractionalPosition(0, new Fraction(1, 16)));

        var nudge = ToneChangeNudge.For(arr, beats, 31.25)!;

        Assert.Equal(ToneChangeNudgeBlock.ChartStart, nudge.Block);
        Assert.Null(nudge.Action());
    }

    [Fact]
    public void APreviousToneChangeInsideThe32nd_OrExactlyThere_IsNeverStacked()
    {
        var beats = Beats(500, 4);
        foreach (var previous in new[] { new FractionalPosition(1, new Fraction(7, 8)), new FractionalPosition(1, new Fraction(15, 16)) })
        {
            var arr = WithChangeOnNote(new FractionalPosition(2));
            arr.ToneChanges.Insert(0, new ToneChange { Position = previous, ToneName = "clean" });

            var nudge = ToneChangeNudge.For(arr, beats, 1000)!;

            Assert.Same(arr.ToneChanges[1], nudge.Change);
            Assert.Equal(ToneChangeNudgeBlock.ToneChange, nudge.Block);
        }
    }

    [Fact]
    public void AnEarlierPhraseStartInsideThe32nd_Blocks_ButAChangeOnAPhraseStartMayMoveBeforeIt()
    {
        var beats = Beats(500, 4);
        var crossing = WithChangeOnNote(new FractionalPosition(2));
        crossing.EventPoints.Add(new EventPoint { Position = new FractionalPosition(1, new Fraction(15, 16)), Phrase = "verse" });
        Assert.Equal(ToneChangeNudgeBlock.PhraseStart, ToneChangeNudge.For(crossing, beats, 1000)!.Block);

        var onStart = WithChangeOnNote(new FractionalPosition(2));
        onStart.EventPoints.Add(new EventPoint { Position = new FractionalPosition(2), Phrase = "chorus", Section = "chorus" });
        Assert.True(ToneChangeNudge.For(onStart, beats, 1000)!.CanApply);
    }

    [Fact]
    public void ANoteA32ndEarlier_Blocks_BecauseTheWarningWouldMoveToIt()
    {
        var beats = Beats(500, 4);
        var arr = WithChangeOnNote(new FractionalPosition(2));
        arr.Levels[0].Notes.Insert(0, new GuitarNote { Position = new FractionalPosition(1, new Fraction(7, 8)), Fret = 3 });

        Assert.Equal(ToneChangeNudgeBlock.Note, ToneChangeNudge.For(arr, beats, 1000)!.Block);
    }

    [Fact]
    public void NoToneChangeAtTheFindingsTime_OffersNothing()
    {
        var beats = Beats(500, 4);
        Assert.Null(ToneChangeNudge.For(WithChangeOnNote(new FractionalPosition(2)), beats, 1500));
    }

    [Fact]
    public void AChangeTheWarningMatchesWithinARoundedMillisecond_IsOffered()
    {
        // I30 rounds both times to whole ms: a change at 1001.25 ms on a note at 1000 ms is flagged.
        var beats = Beats(500, 4);
        var arr = WithChangeOnNote(new FractionalPosition(2));
        arr.ToneChanges[0].Position = new FractionalPosition(2, new Fraction(1, 400));
        Assert.Equal(1, I30Count(arr, beats));

        var nudge = ToneChangeNudge.For(arr, beats, 1000);

        Assert.NotNull(nudge);
        Assert.Same(arr.ToneChanges[0], nudge.Change);
    }

    [Fact]
    public void ANoteTheWarningWouldMatchAtTheLandingSpot_Blocks()
    {
        // The move lands on 937.5 ms; a note at 938.75 ms rounds within I30's slop, so it would be flagged.
        var beats = Beats(500, 4);
        var arr = WithChangeOnNote(new FractionalPosition(2));
        arr.Levels[0].Notes.Insert(0, new GuitarNote { Position = new FractionalPosition(1, new Fraction(351, 400)), Fret = 3 });

        Assert.Equal(ToneChangeNudgeBlock.Note, ToneChangeNudge.For(arr, beats, 1000)!.Block);
    }

    [Fact]
    public void TwoToneChangesOnTheFlaggedOnset_AreNotMoved()
    {
        var beats = Beats(500, 4);
        var arr = WithChangeOnNote(new FractionalPosition(2));
        arr.ToneChanges.Add(new ToneChange { Position = new FractionalPosition(2), ToneName = "clean" });

        var nudge = ToneChangeNudge.For(arr, beats, 1000)!;

        Assert.Equal(ToneChangeNudgeBlock.SeveralChanges, nudge.Block);
        Assert.Null(nudge.Action());
    }

    [Fact]
    public void BeforeTheFirstBeat_TheLeadInGridIsUsed_AndOnlyTimeZeroBlocks()
    {
        // First beat at 1000 ms: a change a sixteenth of a beat after it moves back over the first beat
        // onto the lead-in extrapolated from the first interval.
        var beats = Beats(500, 4, firstMs: 1000);
        var arr = WithChangeOnNote(new FractionalPosition(0, new Fraction(1, 16)));

        var nudge = ToneChangeNudge.For(arr, beats, 1031.25)!;

        Assert.True(nudge.CanApply);
        Assert.Equal(968.75, nudge.To.GetPositionMs(beats), 3);
        nudge.Action()!.Execute();
        Assert.Equal(0, I30Count(arr, beats));

        var early = Beats(500, 4, firstMs: 50);
        var onFirst = WithChangeOnNote(new FractionalPosition(0));
        Assert.Equal(ToneChangeNudgeBlock.ChartStart, ToneChangeNudge.For(onFirst, early, 50)!.Block);
    }
}
