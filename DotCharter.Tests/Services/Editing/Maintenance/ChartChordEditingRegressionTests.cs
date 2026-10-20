using DotCharter.Core.Domain.Vocals;

namespace DotCharter.Tests.Services.Editing.Maintenance;

/// <summary>
///     Regressions for the faults fixed in
///     <c>ChartMaintenanceService.ChordEditing.cs</c>. They share
///     <see cref="ChartPowerToolsTests.Harness" /> because the property under test
///     is the same one that file's tests assert: the edit lands in the project
///     THROUGH the history service.
/// </summary>
public sealed class ChartChordEditingRegressionTests
{
    /// <summary>
    ///     Hand-shape containment is <c>HandShape.Covers</c> — start inclusive, end
    ///     EXCLUSIVE — and this verb open-coded a CLOSED span instead.
    ///     <para>
    ///         Shapes ABUT by construction (every tab importer emits A=[0,4) B=[4,8)),
    ///         so under a closed span a chord sitting exactly ON a shape's end counted
    ///         as inside it. Because <c>Chord.ApplyTemplate</c> rewrites
    ///         <c>ChordNotes</c> from the template's frets, a chord that belongs to NO
    ///         shape at all was silently re-fretted to the shape that ENDED on it —
    ///         data loss with no visible cause.
    ///     </para>
    /// </summary>
    [Fact]
    public void ApplyHandShapeTemplate_treatsAShapeEndAsExclusive_soAChordOnItIsNotReFretted()
    {
        var h = new ChartPowerToolsTests.Harness();
        var arrangement = h.State.ActiveArrangement!;
        var level = h.State.ActiveLevel!;

        var aTemplate = new ChordTemplate { Id = 0, Name = "A" };
        aTemplate.Frets[0] = 3;
        aTemplate.Frets[1] = 5;
        var bTemplate = new ChordTemplate { Id = 1, Name = "B" };
        bTemplate.Frets[0] = 8;
        bTemplate.Frets[1] = 10;
        arrangement.ChordTemplates.Add(aTemplate);
        arrangement.ChordTemplates.Add(bTemplate);

        // The abutting pair every tab importer emits: A=[0,4), B=[4,8).
        level.HandShapes.Add(new HandShape
        {
            Position = new FractionalPosition(0), EndPosition = new FractionalPosition(4), TemplateId = 0
        });
        level.HandShapes.Add(new HandShape
        {
            Position = new FractionalPosition(4), EndPosition = new FractionalPosition(8), TemplateId = 1
        });

        // Exactly on the A/B boundary: a member of B, the shape that STARTS there.
        var onBoundary = ChordAt(4, 1, 1);
        // Exactly on B's end, where NOTHING starts: a member of no shape at all.
        var pastTheEnd = ChordAt(8, 1, 1);
        level.Chords.Add(onBoundary);
        level.Chords.Add(pastTheEnd);
        h.Selection.SelectedChords.Add(onBoundary);
        h.Selection.SelectedChords.Add(pastTheEnd);

        h.Maintenance.ApplyHandShapeTemplateToSelectedChords();

        // The boundary chord follows B — never A, whose span merely ends there.
        Assert.Equal(1, onBoundary.TemplateId);
        Assert.Equal([8, 10], onBoundary.ChordNotes.OrderBy(n => n.StringIndex).Select(n => n.Fret));

        // The chord past B's end is inside nothing and must be left exactly as it
        // was. Under the closed span it adopted B's template AND B's frets.
        Assert.Equal(-1, pastTheEnd.TemplateId);
        Assert.Equal([1, 1], pastTheEnd.ChordNotes.OrderBy(n => n.StringIndex).Select(n => n.Fret));

        // One chord changed, so exactly one undo node — and undo puts it back.
        Assert.True(h.History.CanUndo);
        h.History.Undo();
        Assert.Equal(-1, onBoundary.TemplateId);
        Assert.Equal([1, 1], onBoundary.ChordNotes.OrderBy(n => n.StringIndex).Select(n => n.Fret));
        Assert.False(h.History.CanUndo);
    }

    /// <summary>
    ///     Inserting a placeholder lyric into an existing phrase stretches that
    ///     phrase's end, and the stretch has to live in the SAME undo node as the
    ///     lyric — a gesture is one node.
    ///     <para>
    ///         It used to be a bare <c>targetPhrase.EndPosition = endPos</c> beside the
    ///         <c>AddElementsAction</c>, so Ctrl+Z removed the placeholder and left the
    ///         phrase permanently longer. Nothing on screen shows a phrase end moving by
    ///         a fraction of a beat, so tap-and-undo crept the phrase forward invisibly,
    ///         one insert at a time.
    ///     </para>
    /// </summary>
    [Fact]
    public void InsertVocalAtPlayhead_putsThePhraseEndStretchInTheSameUndoNodeAsTheLyric()
    {
        var h = new ChartPowerToolsTests.Harness();
        var project = h.State.ActiveProject!;

        // A phrase that is OPEN at the playhead but ENDS before the placeholder's
        // 250 ms tail, so the insert has to stretch it.
        const double tapMs = 700.0;
        var track = new VocalTrack { Name = "Vocals 1" };
        var phrase = new LyricPhrase
        {
            Position = FractionalPosition.Zero,
            EndPosition = TimelineMath.PositionFromMs(tapMs + 100.0, project.Beats)
        };
        track.Phrases.Add(phrase);
        project.VocalTracks.Add(track);

        var endBefore = phrase.EndPosition;
        var stretchedTo = TimelineMath.PositionFromMs(tapMs + 250.0, project.Beats);
        Assert.True(endBefore.CompareTo(stretchedTo) < 0, "fixture must force a stretch");

        h.State.CurrentTimeMs = tapMs;
        h.Maintenance.InsertVocalAtPlayhead();

        Assert.Single(phrase.Lyrics);
        Assert.Equal(stretchedTo, phrase.EndPosition);

        // ONE node: a single Ctrl+Z reverts BOTH halves and empties the stack. The
        // bare assignment left the phrase at `stretchedTo` forever.
        Assert.True(h.History.CanUndo);
        h.History.Undo();
        Assert.Empty(phrase.Lyrics);
        Assert.Equal(endBefore, phrase.EndPosition);
        Assert.False(h.History.CanUndo);
    }

    /// <summary>
    ///     Merging notes must never adopt an ARPEGGIO template that merely shares
    ///     their frets.
    ///     <para>
    ///         Two flags spell arpeggio and each import path sets only one:
    ///         <c>RsXmlSongReader</c> sets <c>ForceArpeggioInRs</c> (from the
    ///         <c>-arp</c> displayName) and never <c>Arpeggio</c>, so on every
    ///         DLC-Builder / EOF chart the hand-rolled <c>!t.Arpeggio</c> filter this
    ///         verb used was TRUE for an arpeggio. The merged chord then exported with
    ///         <c>ChordMask.Arpeggio</c> and an <c>-arp</c> name it never asked for.
    ///     </para>
    /// </summary>
    [Fact]
    public void MergeNotesIntoChord_doesNotAdoptAnArpeggioTemplateSpelledForceArpeggioInRs()
    {
        var h = new ChartPowerToolsTests.Harness();
        var arrangement = h.State.ActiveArrangement!;
        var level = h.State.ActiveLevel!;

        // The RS-XML/EOF spelling: ForceArpeggioInRs set, Arpeggio NOT.
        var arpeggio = new ChordTemplate { Id = 0, Name = "G5-arp", ForceArpeggioInRs = true };
        arpeggio.Frets[0] = 3;
        arpeggio.Frets[1] = 5;
        arrangement.ChordTemplates.Add(arpeggio);

        var low = new GuitarNote { Position = new FractionalPosition(10), StringIndex = 0, Fret = 3 };
        var high = new GuitarNote { Position = new FractionalPosition(10), StringIndex = 1, Fret = 5 };
        level.Notes.Add(low);
        level.Notes.Add(high);
        h.Selection.SelectedNotes.Add(low);
        h.Selection.SelectedNotes.Add(high);

        h.Maintenance.MergeSelectedNotesIntoChords();

        var chord = Assert.Single(level.Chords);
        Assert.NotEqual(arpeggio.Id, chord.TemplateId);

        // A plain template was minted instead — in BOTH spellings — and it arrives
        // charting-ready, exactly like an imported one.
        var minted = Assert.Single(arrangement.ChordTemplates, t => t.Id == chord.TemplateId);
        Assert.False(minted.IsArpeggio);
        Assert.False(minted.ForceArpeggioInRs);
        Assert.NotEmpty(minted.Fingers);
        // The arpeggio template is untouched: templates are SHARED, never mutated.
        Assert.True(arpeggio.ForceArpeggioInRs);
        Assert.Equal("G5-arp", arpeggio.Name);

        Assert.True(h.History.CanUndo);
        h.History.Undo();
        Assert.Empty(level.Chords);
        Assert.Single(arrangement.ChordTemplates);
    }

    /// <summary>An untemplated two-string chord at <paramref name="beat" />.</summary>
    private static Chord ChordAt(int beat, int lowFret, int highFret)
    {
        var chord = new Chord { Position = new FractionalPosition(beat), TemplateId = -1 };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = lowFret });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = highFret });
        return chord;
    }
}
