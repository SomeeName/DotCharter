using DotCharter.Services.Editing.Selection;

namespace DotCharter.Tests.Services.Selection;

/// <summary>
///     Select-like for FRET-HAND POSITIONS. The whole point of these tests is
///     the negative one: matching on the fret alone would be a chart-wide
///     hazard, because there are many different reasons to park the hand on a
///     given fret and re-fretting all of them because one was wrong corrupts
///     passages the user never looked at. An anchor is only interchangeable
///     with another when the hand is doing the same JOB — same fret, same
///     width, same run of sounds underneath.
/// </summary>
public sealed class SelectLikeAnchorTests
{
    private static ISelectLikeService NewService() => new SelectLikeService(new FakeAppLogger());

    private static FractionalPosition At(int beat) => new(beat);

    private static GuitarNote Note(int beat, int stringIndex, int fret) => new()
    {
        Position = At(beat), StringIndex = stringIndex, Fret = fret
    };

    /// <summary>Two runs that are IDENTICAL under their anchors, four bars apart.</summary>
    private static ChartLevel TwoIdenticalRuns()
    {
        var level = new ChartLevel();
        level.Anchors.Add(new FretAnchor(At(0), 7, 4));
        level.Notes.Add(Note(0, 0, 7));
        level.Notes.Add(Note(1, 1, 9));

        level.Anchors.Add(new FretAnchor(At(8), 7, 4));
        level.Notes.Add(Note(8, 0, 7));
        level.Notes.Add(Note(9, 1, 9));
        return level;
    }

    [Fact]
    public void SameFretSameWidthSameRun_Matches()
    {
        var level = TwoIdenticalRuns();

        var matches = NewService().FindLikeAnchors(level, [level.Anchors[0]]);

        Assert.Same(level.Anchors[1], Assert.Single(matches));
    }

    /// <summary>The headline guard: the owner's explicit requirement.</summary>
    [Fact]
    public void SameFretButADifferentRunUnderneath_DoesNotMatch()
    {
        var level = new ChartLevel();
        level.Anchors.Add(new FretAnchor(At(0), 7, 4));
        level.Notes.Add(Note(0, 0, 7));
        level.Notes.Add(Note(1, 1, 9));

        // Same fret 7, same width — but a completely different passage under it.
        level.Anchors.Add(new FretAnchor(At(8), 7, 4));
        level.Notes.Add(Note(8, 3, 10));
        level.Notes.Add(Note(9, 4, 8));
        level.Notes.Add(Note(10, 5, 7));

        var matches = NewService().FindLikeAnchors(level, [level.Anchors[0]]);

        Assert.Empty(matches);
    }

    [Fact]
    public void SameFretAndRunButADifferentWidth_DoesNotMatch()
    {
        var level = TwoIdenticalRuns();
        level.Anchors[1].Width = 5;

        Assert.Empty(NewService().FindLikeAnchors(level, [level.Anchors[0]]));
    }

    [Fact]
    public void ADifferentFret_DoesNotMatch()
    {
        var level = TwoIdenticalRuns();
        level.Anchors[1].Fret = 9;

        Assert.Empty(NewService().FindLikeAnchors(level, [level.Anchors[0]]));
    }

    [Fact]
    public void AnEmptyAnchor_MatchesOnlyOtherEmptyAnchorsOfThatShape()
    {
        var level = new ChartLevel();
        level.Anchors.Add(new FretAnchor(At(0), 5, 4)); // empty
        level.Anchors.Add(new FretAnchor(At(4), 5, 4)); // empty too
        level.Anchors.Add(new FretAnchor(At(8), 5, 4)); // has a note under it
        level.Notes.Add(Note(8, 0, 5));

        var matches = NewService().FindLikeAnchors(level, [level.Anchors[0]]);

        Assert.Same(level.Anchors[1], Assert.Single(matches));
    }

    /// <summary>A chord and the same pitches as loose notes are the same hand
    /// shape, so they must compare equal — the hand does not care how the sound
    /// is spelled.</summary>
    [Fact]
    public void AChordAndTheSamePitchesAsLooseNotes_AreTheSameHandJob()
    {
        var level = new ChartLevel();
        level.Anchors.Add(new FretAnchor(At(0), 7, 4));
        var chord = new Chord { Position = At(0) };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 7 });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 9 });
        level.Chords.Add(chord);

        level.Anchors.Add(new FretAnchor(At(8), 7, 4));
        level.Notes.Add(Note(8, 0, 7));
        level.Notes.Add(Note(8, 1, 9));

        Assert.Same(level.Anchors[1], Assert.Single(NewService().FindLikeAnchors(level, [level.Anchors[0]])));
    }

    [Fact]
    public void TheSeedItselfIsNeverReturned()
    {
        var level = TwoIdenticalRuns();

        var matches = NewService().FindLikeAnchors(level, [level.Anchors[0]]);

        Assert.DoesNotContain(level.Anchors[0], matches);
    }

    [Fact]
    public void NoSeedOrNoLevel_ReturnsEmpty()
    {
        var level = TwoIdenticalRuns();

        Assert.Empty(NewService().FindLikeAnchors(level, []));
        Assert.Empty(NewService().FindLikeAnchors(null, [level.Anchors[0]]));
    }

    /// <summary>An anchor's span ends at the NEXT anchor: a note after that
    /// belongs to the next hand position, not this one.</summary>
    [Fact]
    public void TheSpanStopsAtTheNextAnchor()
    {
        var level = new ChartLevel();
        level.Anchors.Add(new FretAnchor(At(0), 7, 4));
        level.Notes.Add(Note(0, 0, 7));
        level.Anchors.Add(new FretAnchor(At(2), 12, 4)); // ends the first span
        level.Notes.Add(Note(2, 5, 12));

        level.Anchors.Add(new FretAnchor(At(8), 7, 4));
        level.Notes.Add(Note(8, 0, 7));
        level.Anchors.Add(new FretAnchor(At(10), 12, 4));
        level.Notes.Add(Note(10, 5, 12));

        // The fret-7 anchors match each other and NOT the fret-12 ones.
        var matches = NewService().FindLikeAnchors(level, [level.Anchors[0]]);

        Assert.Same(level.Anchors[2], Assert.Single(matches));
    }
}
