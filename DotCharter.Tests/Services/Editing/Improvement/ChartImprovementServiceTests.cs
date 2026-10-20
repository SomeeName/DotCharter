namespace DotCharter.Tests.Services.Editing.Improvement;

public sealed class ChartImprovementServiceTests
{
    private static List<BeatMarker> Beats(int count = 12, double bpm = 120)
    {
        var ms = 60000.0 / bpm;
        return [.. Enumerable.Range(0, count).Select(i => new BeatMarker { Time = i * ms, Bpm = bpm, IsMeasure = i % 4 == 0 })];
    }

    private static AppConfig Config() => new()
    {
        MinNoteDistanceMode = MinNoteDistanceMode.BeatFraction,
        MinNoteDistanceDivisor = 64
    };

    [Fact]
    public void ImproveProject_AddsEndPhrase_WhenArrangementIsMissingOne()
    {
        var project = new ChartProject();
        foreach (var beat in Beats()) project.Beats.Add(beat);

        var arrangement = new Arrangement { Name = "Lead" };
        arrangement.Levels.Add(new ChartLevel());
        arrangement.EventPoints.Add(new EventPoint { Position = new FractionalPosition(0), Phrase = "intro" });
        arrangement.EventPoints.Add(new EventPoint { Position = new FractionalPosition(4), Phrase = "verse" });
        project.Arrangements.Add(arrangement);

        var result = new ChartImprovementService(new FakeAppLogger()).ImproveProject(project, Config(), 0);

        Assert.Contains(arrangement.EventPoints, ep => string.Equals(ep.Phrase, "END", StringComparison.OrdinalIgnoreCase));
        Assert.True(result.TotalChanges > 0);
    }

    [Fact]
    public void ImproveProject_RemovesInvalidLinkNextFlags()
    {
        var project = new ChartProject();
        foreach (var beat in Beats()) project.Beats.Add(beat);

        var arrangement = new Arrangement { Name = "Lead" };
        var level = new ChartLevel();

        var noteLinkedToChord = new GuitarNote
        {
            Position = new FractionalPosition(1),
            Sustain = new FractionalPosition(1),
            Fret = 5,
            StringIndex = 0,
            LinkNext = true
        };
        level.Notes.Add(noteLinkedToChord);

        var unpitchedSlide = new GuitarNote
        {
            Position = new FractionalPosition(3),
            Sustain = new FractionalPosition(1),
            Fret = 7,
            StringIndex = 1,
            LinkNext = true,
            UnpitchedSlide = true,
            SlideTo = 9
        };
        level.Notes.Add(unpitchedSlide);
        level.Notes.Add(new GuitarNote
        {
            Position = new FractionalPosition(4),
            Fret = 9,
            StringIndex = 1
        });

        var chord = new Chord { Position = new FractionalPosition(2) };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 5 });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 2, Fret = 5, LinkNext = true });
        level.Chords.Add(chord);
        level.Notes.Add(new GuitarNote
        {
            Position = new FractionalPosition(3),
            Fret = 5,
            StringIndex = 2
        });

        arrangement.Levels.Add(level);
        project.Arrangements.Add(arrangement);

        var result = new ChartImprovementService(new FakeAppLogger()).ImproveProject(project, Config(), 0);

        // A note tied INTO a chord keeps its link AND its sustain — see
        // KeepsANoteLinkedIntoAChord_LinkAndSustainTogether below.
        Assert.True(noteLinkedToChord.LinkNext);
        Assert.False(unpitchedSlide.LinkNext);
        Assert.DoesNotContain(chord.ChordNotes, cn => cn.LinkNext);
        Assert.True(result.TotalChanges > 0);
    }

    [Fact]
    public void ImproveProject_KeepsANoteLinkedIntoAChord_LinkAndSustainTogether()
    {
        // A LINKED CHORD IS LEGAL TO AUTHOR. What RS cannot spell is the
        // LANDING, and that is repaired at the EXPORT boundary
        // (Parsing/Xml/Song/LinkLandingBreakup), never in the chart — which is
        // why ArrangementAutoFixService.UnlinkSoundsLinkedIntoChords was removed
        // in 0.40 after clearing 27 links in one run on the owner's chart. The
        // same pass survived inside RemoveInvalidLinkNextPatterns, reachable
        // from Tools > Improve Chart. It only ever dropped the FLAG, so the tie
        // became a bare sustain running past the end of its own hand shape.
        var project = new ChartProject();
        foreach (var beat in Beats(24)) project.Beats.Add(beat);

        var arrangement = new Arrangement { Name = "Lead" };
        var level = new ChartLevel();

        var tie = new GuitarNote
        {
            Position = new FractionalPosition(1),
            Sustain = new FractionalPosition(1),
            Fret = 5,
            StringIndex = 0,
            LinkNext = true
        };
        level.Notes.Add(tie);

        // The landing: a chord claiming the same string at the continuation.
        var landing = new Chord { Position = new FractionalPosition(2) };
        landing.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 5 });
        landing.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 5 });
        level.Chords.Add(landing);

        arrangement.Levels.Add(level);
        project.Arrangements.Add(arrangement);

        new ChartImprovementService(new FakeAppLogger()).ImproveProject(project, Config(), 0);

        Assert.True(tie.LinkNext);
        Assert.Equal(new FractionalPosition(1), tie.Sustain);
    }

    [Fact]
    public void ImproveProject_DoesNotDeleteADistantNoteBecauseALinkTargetsAChord()
    {
        // RemoveUnnecessaryLinkNextTargets indexed level.Notes ONLY, so the chord
        // occupying the string was invisible: the pass looked straight past it,
        // found the next STANDALONE note bars later, and — because that note
        // happened to have zero sustain — deleted it and cleared the link. The
        // deleted note had nothing to do with the link.
        var project = new ChartProject();
        foreach (var beat in Beats(24)) project.Beats.Add(beat);

        var arrangement = new Arrangement { Name = "Lead" };
        var level = new ChartLevel();

        level.Notes.Add(new GuitarNote
        {
            Position = new FractionalPosition(1),
            Sustain = new FractionalPosition(1),
            Fret = 5,
            StringIndex = 0,
            LinkNext = true
        });

        // The link's REAL target: a chord on the same string at the continuation.
        var chord = new Chord { Position = new FractionalPosition(2) };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 5 });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 5 });
        level.Chords.Add(chord);

        // An innocent bystander many bars later, on the same string.
        var bystander = new GuitarNote { Position = new FractionalPosition(12), Fret = 3, StringIndex = 0 };
        level.Notes.Add(bystander);

        arrangement.Levels.Add(level);
        project.Arrangements.Add(arrangement);

        new ChartImprovementService(new FakeAppLogger()).ImproveProject(project, Config(), 0);

        Assert.Contains(bystander, level.Notes);
    }

    [Fact]
    public void ImproveProject_DoesNotDeleteADistantChordNoteTargetsBystander()
    {
        // The SECOND loop, over level.Chords, had the identical defect.
        var project = new ChartProject();
        foreach (var beat in Beats(24)) project.Beats.Add(beat);

        var arrangement = new Arrangement { Name = "Lead" };
        var level = new ChartLevel();

        var source = new Chord { Position = new FractionalPosition(1) };
        source.ChordNotes.Add(new ChordNote
        {
            StringIndex = 0, Fret = 5, Sustain = new FractionalPosition(1), LinkNext = true
        });
        source.ChordNotes.Add(new ChordNote
        {
            StringIndex = 1, Fret = 5, Sustain = new FractionalPosition(1), LinkNext = true
        });
        level.Chords.Add(source);

        var target = new Chord { Position = new FractionalPosition(2) };
        target.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 5 });
        target.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 5 });
        level.Chords.Add(target);

        var bystander = new GuitarNote { Position = new FractionalPosition(12), Fret = 3, StringIndex = 0 };
        level.Notes.Add(bystander);

        arrangement.Levels.Add(level);
        project.Arrangements.Add(arrangement);

        new ChartImprovementService(new FakeAppLogger()).ImproveProject(project, Config(), 0);

        Assert.Contains(bystander, level.Notes);
    }

    [Fact]
    public void ImproveProject_StillDropsAZeroSustainStubExactlyAtTheContinuation()
    {
        // The pass is not neutered: a real dangling target — a bare zero-sustain
        // note sitting exactly on position + sustain — is still removed.
        var project = new ChartProject();
        foreach (var beat in Beats(24)) project.Beats.Add(beat);

        var arrangement = new Arrangement { Name = "Lead" };
        var level = new ChartLevel();

        var source = new GuitarNote
        {
            Position = new FractionalPosition(3),
            Sustain = new FractionalPosition(1),
            Fret = 7,
            StringIndex = 1,
            LinkNext = true
        };
        level.Notes.Add(source);
        var stub = new GuitarNote { Position = new FractionalPosition(4), Fret = 7, StringIndex = 1 };
        level.Notes.Add(stub);

        arrangement.Levels.Add(level);
        project.Arrangements.Add(arrangement);

        new ChartImprovementService(new FakeAppLogger()).ImproveProject(project, Config(), 0);

        Assert.DoesNotContain(stub, level.Notes);
        Assert.False(source.LinkNext);
    }
}
