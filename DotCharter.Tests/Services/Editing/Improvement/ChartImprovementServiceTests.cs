namespace DotCharter.Tests.Services.Editing.Improvement;

public sealed class ChartImprovementServiceTests
{
    [Fact]
    public void TemplateOnlyCompatibilityCleanupIsCounted()
    {
        var project = new ChartProject();
        var arrangement = new Arrangement();
        var template = new ChordTemplate { Id = 0, Name = "C-arp" };
        arrangement.ChordTemplates.Add(template);
        project.Arrangements.Add(arrangement);
        var service = new ChartImprovementService();

        var result = service.ImproveProject(project, Config(), 0);

        Assert.Equal("C", template.Name);
        Assert.True(template.ForceArpeggioInRs);
        Assert.True(result.TotalChanges > 0);
        Assert.Equal(0, service.ImproveProject(project, Config(), 0).TotalChanges);
    }

    [Fact]
    public void SpacingOnlyPolishIsCountedAndANormalizedChartIsANoOp()
    {
        var project = new ChartProject();
        foreach (var beat in Beats()) project.Beats.Add(beat);
        var arrangement = new Arrangement();
        var level = new ChartLevel();
        var note = new GuitarNote { Position = new FractionalPosition(1), Fret = 24, StringIndex = 0 };
        level.Notes.Add(note);
        arrangement.Levels.Add(level);
        arrangement.EventPoints.Add(new EventPoint { Position = new FractionalPosition(11), Phrase = "END" });
        project.Arrangements.Add(arrangement);
        var service = new ChartImprovementService();

        var result = service.ImproveProject(project, Config(), 0);

        Assert.True(note.Ignore);
        Assert.True(note.IgnoreDerived);
        Assert.True(result.TotalChanges > 0);
        Assert.Equal(0, service.ImproveProject(project, Config(), 0).TotalChanges);
    }

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
    public void ImproveProject_TrimsTheGridWithoutRetimingRetainedSounds()
    {
        var project = new ChartProject();
        foreach (var beat in Beats()) project.Beats.Add(beat);
        var interpolationBeat = project.Beats[2];
        var arrangement = new Arrangement { Name = "Lead" };
        var level = new ChartLevel();
        var note = new GuitarNote
        {
            Position = new FractionalPosition(1, new Fraction(1, 4)),
            Sustain = new FractionalPosition(new Fraction(1, 2)),
            StringIndex = 0,
            Fret = 5
        };
        var chord = new Chord { Position = new FractionalPosition(1, new Fraction(1, 8)) };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 5 });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 2, Fret = 7 });
        level.Notes.Add(note);
        level.Chords.Add(chord);
        level.Notes.Add(new GuitarNote { Position = new FractionalPosition(3), StringIndex = 0, Fret = 7 });
        arrangement.Levels.Add(level);
        project.Arrangements.Add(arrangement);

        new ChartImprovementService().ImproveProject(project, Config(), 750);

        Assert.Single(level.Notes);
        Assert.Equal(625.0, note.Position.GetPositionMs(project.Beats), 6);
        Assert.Equal(750.0, note.EndPosition.GetPositionMs(project.Beats), 6);
        Assert.Equal(562.5, chord.Position.GetPositionMs(project.Beats), 6);
        Assert.Equal(3, project.Beats.Count);
        Assert.Same(interpolationBeat, project.Beats[^1]);
        Assert.Equal(1000.0, interpolationBeat.Time);
    }

    [Fact]
    public void ImproveProject_DoesNotAppendAnAudioEndBeatThatRetimesExtrapolatedContent()
    {
        var project = new ChartProject();
        foreach (var beat in Beats(2)) project.Beats.Add(beat);
        var arrangement = new Arrangement { Name = "Lead" };
        var level = new ChartLevel();
        var note = new GuitarNote { Position = new FractionalPosition(2), StringIndex = 0, Fret = 5 };
        level.Notes.Add(note);
        arrangement.Levels.Add(level);
        project.Arrangements.Add(arrangement);

        new ChartImprovementService().ImproveProject(project, Config(), 1250);

        Assert.Equal(1000.0, note.Position.GetPositionMs(project.Beats), 6);
        Assert.Equal(2, project.Beats.Count);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(500)]
    [InlineData(750)]
    public void ImproveProject_KeepsEveryLockedBeatUnchanged(double audioEndMs)
    {
        var project = new ChartProject { IsBeatMapLocked = true };
        foreach (var beat in Beats()) project.Beats.Add(beat);
        project.Beats[2].IsAnchor = true;
        project.Beats[2].NoteDenominator = 8;
        var originals = project.Beats.ToArray();
        var snapshots = project.Beats.Select(b => b.Clone()).ToArray();

        var result = new ChartImprovementService().ImproveProject(project, Config(), audioEndMs);

        Assert.Equal(originals.Length, project.Beats.Count);
        for (var i = 0; i < originals.Length; i++)
        {
            Assert.Same(originals[i], project.Beats[i]);
            Assert.Equal(snapshots[i].Time, project.Beats[i].Time);
            Assert.Equal(snapshots[i].Bpm, project.Beats[i].Bpm);
            Assert.Equal(snapshots[i].IsAnchor, project.Beats[i].IsAnchor);
            Assert.Equal(snapshots[i].IsMeasure, project.Beats[i].IsMeasure);
            Assert.Equal(snapshots[i].BeatsInMeasure, project.Beats[i].BeatsInMeasure);
            Assert.Equal(snapshots[i].NoteDenominator, project.Beats[i].NoteDenominator);
        }

        Assert.DoesNotContain("extra beats", result.Changes.Keys);
    }

    [Fact]
    public void PhraseStartAnchors_AreSeededFromTheAnchorInEffect_NotTheLastStored()
    {
        var arrangement = new Arrangement { Name = "Lead" };
        var level = new ChartLevel();
        // Stored out of order, as a level is mid-session: the hand in effect at beat 4 is fret 7.
        level.Anchors.Add(new FretAnchor(new FractionalPosition(2), 7));
        level.Anchors.Add(new FretAnchor(new FractionalPosition(0), 3));
        arrangement.Levels.Add(level);
        arrangement.EventPoints.Add(new EventPoint { Position = new FractionalPosition(4), Phrase = "verse" });

        Assert.Equal(1, ChartImprovementService.EnsurePhraseStartAnchors(arrangement));

        Assert.Equal(7, level.Anchors.Single(a => a.Position == new FractionalPosition(4)).Fret);
    }

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
