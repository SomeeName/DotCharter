using DotCharter.Services.Editing.Fixing;

namespace DotCharter.Tests.Services.Editing.Fixing;

public sealed class ArrangementAutoFixServiceTests
{
    private static (Arrangement arr, ArrangementFixResult result) Run(Action<Arrangement> setup)
    {
        var arr = new Arrangement { ArrangementType = ArrangementType.Lead };
        arr.Levels.Add(new ChartLevel());
        setup(arr);
        var result = new ArrangementFixResult();
        new ArrangementAutoFixService().Fix(arr, result);
        return (arr, result);
    }

    private static GuitarNote Note(int beat, int fret, int stringIdx = 0, int sustainBeats = 0) => new()
    {
        Position = new FractionalPosition(beat),
        Sustain = sustainBeats == 0 ? FractionalPosition.Zero : new FractionalPosition(sustainBeats),
        Fret = fret,
        StringIndex = stringIdx
    };

    [Fact]
    public void EmptyArrangement_NoChanges()
    {
        var (_, result) = Run(_ => { });
        Assert.Equal(0, result.TotalChanges);
    }

    [Fact]
    public void UnsortedNotes_AreSorted()
    {
        var (arr, result) = Run(a =>
        {
            a.Levels[0].Notes.Add(Note(4, 5));
            a.Levels[0].Notes.Add(Note(1, 3));
        });

        Assert.True(result.Changes.ContainsKey("sortedCollections"));
        Assert.Equal(new FractionalPosition(1), arr.Levels[0].Notes[0].Position);
    }

    [Fact]
    public void ChordWithoutNotes_IsRemoved()
    {
        var (arr, result) = Run(a =>
            a.Levels[0].Chords.Add(new Chord { Position = new FractionalPosition(1) }));

        Assert.Empty(arr.Levels[0].Chords);
        Assert.Equal(1, result.Changes["invalidChordsFixed"]);
    }

    [Fact]
    public void ChordWithMissingTemplate_KeepsItsSounds_AndGetsATemplateForTheShapeItPlays()
    {
        // This used to DELETE the chord: authored sounds thrown away to repair a
        // broken pointer, while the shape sat in ChordNotes the whole time.
        // Dangling ids are ordinary chart state (importer idempotency skips, a
        // cross-arrangement paste, the pre-0.41 template-undo bug).
        var (arr, result) = Run(a =>
        {
            var chord = new Chord { Position = new FractionalPosition(1), TemplateId = 99 };
            chord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 2 });
            chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 4 });
            a.Levels[0].Chords.Add(chord);
        });

        var kept = Assert.Single(arr.Levels[0].Chords);
        Assert.Equal(2, kept.ChordNotes.Count);
        var template = Assert.Single(arr.ChordTemplates);
        Assert.Equal(template.Id, kept.TemplateId);
        Assert.Equal(new Dictionary<int, int> { [0] = 2, [1] = 4 }, template.Frets);
        Assert.Equal(1, result.Changes["invalidChordsFixed"]);
    }

    [Fact]
    public void ChordWithMissingTemplate_AdoptsATemplateTheChartAlreadyDeclaresForThatShape()
    {
        var (arr, _) = Run(a =>
        {
            a.ChordTemplates.Add(new ChordTemplate { Id = 0, Name = "B5", Frets = { [0] = 2, [1] = 4 } });
            var chord = new Chord { Position = new FractionalPosition(1), TemplateId = 99 };
            chord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 2 });
            chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 4 });
            a.Levels[0].Chords.Add(chord);
        });

        Assert.Single(arr.ChordTemplates);
        Assert.Equal(0, Assert.Single(arr.Levels[0].Chords).TemplateId);
    }

    [Fact]
    public void RunningTwice_ChangesNothingTheSecondTime_AfterADanglingTemplateIdIsRepaired()
    {
        var arr = new Arrangement { ArrangementType = ArrangementType.Lead };
        arr.Levels.Add(new ChartLevel());
        var chord = new Chord { Position = new FractionalPosition(1), TemplateId = 99 };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 2 });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 4 });
        arr.Levels[0].Chords.Add(chord);

        var service = new ArrangementAutoFixService();
        var first = new ArrangementFixResult();
        service.Fix(arr, first);
        Assert.True(first.TotalChanges > 0);

        var second = new ArrangementFixResult();
        service.Fix(arr, second);
        Assert.Equal(0, second.TotalChanges);
    }

    [Fact]
    public void DuplicateNoteOnSameString_IsRemoved()
    {
        var (arr, result) = Run(a =>
        {
            a.Levels[0].Notes.Add(Note(2, 3));
            a.Levels[0].Notes.Add(Note(2, 5));
        });

        Assert.Single(arr.Levels[0].Notes);
        Assert.Equal(1, result.Changes["duplicatesRemoved"]);
    }

    [Fact]
    public void SameName_DifferentString_IsKept()
    {
        var (arr, _) = Run(a =>
        {
            a.Levels[0].Notes.Add(Note(2, 3, stringIdx: 0));
            a.Levels[0].Notes.Add(Note(2, 5, stringIdx: 1));
        });

        Assert.Equal(2, arr.Levels[0].Notes.Count);
    }

    [Fact]
    public void LinkedNotes_SameFret_AreJoined()
    {
        var (arr, result) = Run(a =>
        {
            var first = Note(1, 5, sustainBeats: 1);
            first.LinkNext = true;
            var second = Note(2, 5, sustainBeats: 1);
            a.Levels[0].Notes.Add(first);
            a.Levels[0].Notes.Add(second);
        });

        Assert.Single(arr.Levels[0].Notes);
        Assert.Equal(new FractionalPosition(2), arr.Levels[0].Notes[0].Sustain);
        Assert.False(arr.Levels[0].Notes[0].LinkNext);
        Assert.Equal(1, result.Changes["linkedNotesJoined"]);
    }

    [Fact]
    public void LinkedNotes_DifferentFret_AreNotJoined()
    {
        var (arr, _) = Run(a =>
        {
            var first = Note(1, 5, sustainBeats: 1);
            first.LinkNext = true;
            a.Levels[0].Notes.Add(first);
            a.Levels[0].Notes.Add(Note(2, 7));
        });

        Assert.Equal(2, arr.Levels[0].Notes.Count);
    }

    [Fact]
    public void OverlappingSustain_IsClampedToNextNote()
    {
        var (arr, result) = Run(a =>
        {
            a.Levels[0].Notes.Add(Note(1, 3, sustainBeats: 4));
            a.Levels[0].Notes.Add(Note(3, 5));
        });

        Assert.Equal(new FractionalPosition(2), arr.Levels[0].Notes[0].Sustain);
        Assert.Equal(1, result.Changes["noteLengthsFixed"]);
    }

    [Fact]
    public void PassOtherNotes_SustainIsNotClamped()
    {
        var (arr, _) = Run(a =>
        {
            var note = Note(1, 3, sustainBeats: 4);
            note.PassOtherNotes = true;
            a.Levels[0].Notes.Add(note);
            a.Levels[0].Notes.Add(Note(3, 5));
        });

        Assert.Equal(new FractionalPosition(4), arr.Levels[0].Notes[0].Sustain);
    }

    [Fact]
    public void BendOutsideNote_IsRemoved()
    {
        var (arr, result) = Run(a =>
        {
            var note = Note(1, 3, sustainBeats: 1);
            note.BendValues.Add(new BendValue { Position = new FractionalPosition(1), Step = 1 });
            note.BendValues.Add(new BendValue { Position = new FractionalPosition(5), Step = 2 });
            a.Levels[0].Notes.Add(note);
        });

        Assert.Single(arr.Levels[0].Notes[0].BendValues);
        Assert.Equal(1, result.Changes["bendsOutOfBoundsRemoved"]);
    }

    [Fact]
    public void NegativeSlide_IsCleared()
    {
        var (arr, result) = Run(a =>
        {
            var note = Note(1, 3);
            note.SlideTo = -2;
            note.UnpitchedSlide = true;
            a.Levels[0].Notes.Add(note);
        });

        Assert.Null(arr.Levels[0].Notes[0].SlideTo);
        Assert.False(arr.Levels[0].Notes[0].UnpitchedSlide);
        Assert.Equal(1, result.Changes["slidesFixed"]);
    }

    [Fact]
    public void SlideOntoOwnFret_IsCleared()
    {
        var (arr, _) = Run(a =>
        {
            var note = Note(1, 5);
            note.SlideTo = 5;
            a.Levels[0].Notes.Add(note);
        });

        Assert.Null(arr.Levels[0].Notes[0].SlideTo);
    }

    [Fact]
    public void FretBelowCapo_IsRaisedToCapo()
    {
        var (arr, result) = Run(a =>
        {
            a.Capo = 3;
            a.Levels[0].Notes.Add(Note(1, 2));
            a.Levels[0].Notes.Add(Note(2, 0));
        });

        Assert.Equal(3, arr.Levels[0].Notes[0].Fret);
        Assert.Equal(0, arr.Levels[0].Notes[1].Fret); // open strings untouched
        Assert.Equal(1, result.Changes["capoFretsRaised"]);
    }

    [Fact]
    public void PhraseStartWithoutAnchor_GetsAnchorClonedFromPrevious()
    {
        var (arr, result) = Run(a =>
        {
            a.EventPoints.Add(new EventPoint { Phrase = "riff1", Position = new FractionalPosition(4) });
            a.Levels[0].Notes.Add(Note(5, 7));
            a.Levels[0].Anchors.Add(new FretAnchor
            { Position = new FractionalPosition(1), Fret = 5, Width = 4 });
        });

        Assert.Equal(2, arr.Levels[0].Anchors.Count);
        var inserted = arr.Levels[0].Anchors[1];
        Assert.Equal(new FractionalPosition(4), inserted.Position);
        Assert.Equal(5, inserted.Fret);
        Assert.Equal(1, result.Changes["anchorsAddedAtPhrases"]);
    }

    [Fact]
    public void StructuralPhrases_DoNotGetAnchors()
    {
        var (arr, _) = Run(a =>
        {
            a.EventPoints.Add(new EventPoint { Phrase = "COUNT", Position = new FractionalPosition(0) });
            a.EventPoints.Add(new EventPoint { Phrase = "END", Position = new FractionalPosition(7) });
            a.EventPoints.Add(new EventPoint { Phrase = "noguitar", Position = new FractionalPosition(6) });
            a.Levels[0].Notes.Add(Note(5, 7));
        });

        Assert.Empty(arr.Levels[0].Anchors);
    }

    // ---- idempotence: a fixer that is not a fixed point EATS CHARTS ----------

    /// <summary>
    ///     <b>The pipeline must be a fixed point.</b> Running auto-fix on its own
    ///     output has to change nothing — anything else means the first run left
    ///     the chart in a state the pipeline itself calls broken, and the charter
    ///     sees a second, unexplained round of edits (or a "1 change applied"
    ///     toast) every time they press the button.
    ///     <para>
    ///         The break this pins: <c>FixSlides</c> runs BEFORE
    ///         <c>FixCapoFrets</c>, and the capo raise moves a note's fret without
    ///         touching its slide target. A note at fret 3 sliding to fret 5 under
    ///         a capo at 5 is raised to fret 5 and is now sliding onto its own
    ///         fret — exactly what <c>FixSlides</c> exists to clear, one step too
    ///         late. Run two clears it.
    ///     </para>
    /// </summary>
    [Fact]
    public void RunningTwice_ChangesNothingTheSecondTime_WhenACapoRaiseLandsANoteOnItsOwnSlideTarget()
    {
        var arr = new Arrangement { ArrangementType = ArrangementType.Lead, Capo = 5 };
        arr.Levels.Add(new ChartLevel());
        var note = Note(1, 3, sustainBeats: 1);
        note.SlideTo = 5;
        arr.Levels[0].Notes.Add(note);

        new ArrangementAutoFixService().Fix(arr, new ArrangementFixResult());

        var second = new ArrangementFixResult();
        new ArrangementAutoFixService().Fix(arr, second);

        Assert.Equal(0, second.TotalChanges);
    }

    /// <summary>The same break through a CHORD note, which has its own slide field.</summary>
    [Fact]
    public void RunningTwice_ChangesNothingTheSecondTime_WhenACapoRaiseLandsAChordNoteOnItsOwnSlideTarget()
    {
        var arr = new Arrangement { ArrangementType = ArrangementType.Lead, Capo = 5 };
        arr.Levels.Add(new ChartLevel());
        var chord = new Chord { Position = new FractionalPosition(1), TemplateId = -1 };
        chord.ChordNotes.Add(new ChordNote
        {
            StringIndex = 0, Fret = 3, SlideTo = 5, Sustain = new FractionalPosition(1)
        });
        arr.Levels[0].Chords.Add(chord);

        new ArrangementAutoFixService().Fix(arr, new ArrangementFixResult());

        var second = new ArrangementFixResult();
        new ArrangementAutoFixService().Fix(arr, second);

        Assert.Equal(0, second.TotalChanges);
    }

    /// <summary>
    ///     A capo raise must carry the SLIDE TARGET with it. Leaving it behind
    ///     writes a slide DOWN to a fret the capo makes unplayable — a chart the
    ///     inspector then flags (L01) and the export gate blocks, invented by the
    ///     fixer rather than authored.
    /// </summary>
    [Fact]
    public void CapoRaise_CarriesTheSlideTargetWithTheFret()
    {
        var (arr, _) = Run(a =>
        {
            a.Capo = 5;
            var note = Note(1, 3, sustainBeats: 1);
            note.SlideTo = 2;
            a.Levels[0].Notes.Add(note);
        });

        var fixedNote = arr.Levels[0].Notes[0];
        Assert.Equal(5, fixedNote.Fret);
        // Sliding to fret 2 under a capo at 5 is not playable; the target has to
        // rise with the fret, and a slide onto its own fret is then cleared.
        Assert.Null(fixedNote.SlideTo);
    }

    /// <summary>
    ///     A whole-pipeline fixed-point proof over a chart that trips EVERY
    ///     fixer at once. The first run may change as much as it likes; the
    ///     second must be a no-op.
    /// </summary>
    [Fact]
    public void RunningTwice_ChangesNothingTheSecondTime_OverAChartThatTripsEveryFixer()
    {
        var arr = new Arrangement { ArrangementType = ArrangementType.Lead, Capo = 2 };
        arr.Levels.Add(new ChartLevel());
        var level = arr.Levels[0];

        var template = new ChordTemplate { Id = 0, Name = "X" };
        template.Frets[0] = 1;
        template.Fingers[0] = 9;
        arr.ChordTemplates.Add(template);

        // out of order + a duplicate + an overlapping sustain
        level.Notes.Add(Note(6, 4, stringIdx: 1));
        level.Notes.Add(Note(1, 1, sustainBeats: 6));
        level.Notes.Add(Note(3, 8));
        level.Notes.Add(Note(3, 9));

        // a linked pair to join, with a bend past its own end
        var linked = Note(8, 7, stringIdx: 2, sustainBeats: 1);
        linked.LinkNext = true;
        linked.BendValues.Add(new BendValue { Position = new FractionalPosition(20), Step = 1 });
        level.Notes.Add(linked);
        level.Notes.Add(Note(9, 7, stringIdx: 2, sustainBeats: 1));

        // a slide the capo raise will make degenerate
        var sliding = Note(12, 1, stringIdx: 3, sustainBeats: 1);
        sliding.SlideTo = 2;
        level.Notes.Add(sliding);

        level.Chords.Add(new Chord { Position = new FractionalPosition(14) }); // no notes
        level.Anchors.Add(new FretAnchor { Position = new FractionalPosition(20), Fret = 5, Width = 4 });
        level.Anchors.Add(new FretAnchor { Position = new FractionalPosition(20), Fret = 7, Width = 4 });
        arr.EventPoints.Add(new EventPoint { Phrase = "riff1", Position = new FractionalPosition(5) });

        var first = new ArrangementFixResult();
        new ArrangementAutoFixService().Fix(arr, first);
        Assert.True(first.TotalChanges > 0, "fixture must actually trip the pipeline");

        var second = new ArrangementFixResult();
        new ArrangementAutoFixService().Fix(arr, second);

        Assert.Equal(0, second.TotalChanges);
    }

    /// <summary>
    ///     <b>An auto-fix must never invent an error the editor never showed.</b>
    ///     The phrase-start anchor was inserted at fret 1 whenever no earlier
    ///     anchor existed to copy — and under a capo the lowest legal fret-hand
    ///     position is <c>capo + 1</c>, so the fixer manufactured an
    ///     <c>AnchorOnOrBelowCapo</c> (L02) finding at every phrase it touched.
    /// </summary>
    [Fact]
    public void PhraseStartAnchor_IsNeverPlacedOnOrBelowTheCapo()
    {
        var (arr, _) = Run(a =>
        {
            a.Capo = 3;
            a.EventPoints.Add(new EventPoint { Phrase = "riff1", Position = new FractionalPosition(4) });
            a.Levels[0].Notes.Add(Note(5, 7));
        });

        var inserted = Assert.Single(arr.Levels[0].Anchors);
        Assert.True(inserted.Fret > arr.Capo,
            $"auto-fix placed a fret-hand position on fret {inserted.Fret}, at or below the capo (fret {arr.Capo})");
    }

    /// <summary>The same claim stated as the inspector would: auto-fix adds no L02.</summary>
    [Fact]
    public void AutoFix_AddsNoAnchorOnOrBelowCapoFinding_ThatTheChartDidNotAlreadyHave()
    {
        var beats = Enumerable.Range(0, 16)
            .Select(i => new BeatMarker { Time = i * 500.0, Bpm = 120 })
            .ToList();

        var arr = new Arrangement { ArrangementType = ArrangementType.Lead, Capo = 3 };
        arr.Levels.Add(new ChartLevel());
        arr.EventPoints.Add(new EventPoint { Phrase = "riff1", Position = new FractionalPosition(4) });
        arr.Levels[0].Notes.Add(Note(5, 7));

        var validator = new ArrangementValidationService(new FakeAppLogger());
        var before = validator.Validate(arr, beats)
            .Count(i => i.Type == ArrangementIssueType.AnchorOnOrBelowCapo);

        new ArrangementAutoFixService().Fix(arr, new ArrangementFixResult());

        var after = validator.Validate(arr, beats)
            .Count(i => i.Type == ArrangementIssueType.AnchorOnOrBelowCapo);

        Assert.Equal(before, after);
    }

    [Fact]
    public void InvalidTemplateFinger_IsRemoved()
    {
        var (arr, result) = Run(a =>
        {
            var template = new ChordTemplate { Id = 0, Name = "X" };
            template.Frets[0] = 3;
            template.Fingers[0] = 7;
            a.ChordTemplates.Add(template);
        });

        Assert.Empty(arr.ChordTemplates[0].Fingers);
        Assert.Equal(1, result.Changes["templateFingersCleaned"]);
    }

    /// <summary>
    ///     A LINKED CHORD IS LEGAL TO AUTHOR — tying or sliding one shape into
    ///     the next is ordinary guitar, and it is how the tab importers and the
    ///     clipboard paste spell a strum ringing into the next one. What RS
    ///     cannot spell is the LANDING, and that is repaired at the export
    ///     boundary by <c>LinkLandingBreakup</c>, never in the chart.
    ///     <para>
    ///         This asserts the INVERSE of what it used to. An
    ///         <c>UnlinkSoundsLinkedIntoChords</c> fixer cleared the flag while
    ///         leaving the sustain, so the tie became a bare sustain running
    ///         past its own hand shape and every linked slide chord came apart
    ///         at once — 27 of them in a single auto-fix run on a real chart.
    ///         Auto-fix must leave an authored link alone.
    ///     </para>
    /// </summary>
    [Fact]
    public void AChordNoteLinkedIntoAChord_KeepsBothItsLinkAndItsSustain()
    {
        ChordNote linked = null!;
        var (_, result) = Run(a =>
        {
            var first = new Chord { Position = new FractionalPosition(1) };
            linked = new ChordNote
            {
                StringIndex = 0, Fret = 5, Sustain = new FractionalPosition(1), LinkNext = true
            };
            first.ChordNotes.Add(linked);
            var second = new Chord { Position = new FractionalPosition(2) };
            second.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 5 });
            a.Levels[0].Chords.Add(first);
            a.Levels[0].Chords.Add(second);
        });

        Assert.True(linked.LinkNext);
        Assert.Equal(new FractionalPosition(1), linked.Sustain);
        Assert.False(result.Changes.ContainsKey("linksIntoChordsCleared"));
    }

    [Fact]
    public void ASoundLinkedIntoANoteOnThatString_IsLeftLinked()
    {
        GuitarNote first = null!;
        var (_, result) = Run(a =>
        {
            first = Note(1, 5, sustainBeats: 1);
            first.LinkNext = true;
            a.Levels[0].Notes.Add(first);
            // A DIFFERENT fret, so JoinSimilarLinkedNotes cannot merge the pair
            // and consume the flag before this fixer is even reached.
            a.Levels[0].Notes.Add(Note(2, 7));
            // A chord at the same time on a DIFFERENT string must not count.
            var chord = new Chord { Position = new FractionalPosition(2) };
            chord.ChordNotes.Add(new ChordNote { StringIndex = 3, Fret = 7 });
            a.Levels[0].Chords.Add(chord);
        });

        Assert.True(first.LinkNext);
        Assert.False(result.Changes.ContainsKey("linksIntoChordsCleared"));
    }
}
