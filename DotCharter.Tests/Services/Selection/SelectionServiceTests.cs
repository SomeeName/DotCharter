using DotCharter.Services.Editing.Selection;
using ToneChange = DotCharter.Core.Domain.Events.ToneChange;

namespace DotCharter.Tests.Services.Selection;

public class SelectionServiceTests
{
    private static GuitarNote Note(int beat, int s = 0) => new() { Position = new FractionalPosition(beat), StringIndex = s };

    [Fact]
    public void AddNote_UpdatesSelectedNoteToFirst()
    {
        var svc = new SelectionService();
        var n1 = Note(1);
        svc.SelectedNotes.Add(n1);
        Assert.Same(n1, svc.SelectedNote);
    }

    [Fact]
    public void ClearingCollection_ResetsSelectedNote()
    {
        var svc = new SelectionService();
        svc.SelectedNotes.Add(Note(1));
        svc.SelectedNotes.Clear();
        Assert.Null(svc.SelectedNote);
    }

    [Fact]
    public void ClearHovers_ResetsAllHoverFields()
    {
        var svc = new SelectionService
        {
            HoveredNote = Note(0),
            HoveredStringIndex = 3,
            HoveredTimeMs = 1234.5
        };

        svc.ClearHovers();

        Assert.Null(svc.HoveredNote);
        Assert.Equal(-1, svc.HoveredStringIndex);
        Assert.Equal(-1.0, svc.HoveredTimeMs);
    }

    [Fact]
    public void ClearSelections_EmptiesAllCollections()
    {
        var svc = new SelectionService();
        svc.SelectedNotes.Add(Note(0));
        svc.SelectedChords.Add(new Chord());
        svc.SelectedTones.Add(new ToneChange { Position = new FractionalPosition(1), ToneName = "lead" });
        svc.SelectedHandShapes.Add(new HandShape());

        svc.ClearSelections();

        Assert.Empty(svc.SelectedNotes);
        Assert.Empty(svc.SelectedChords);
        Assert.Empty(svc.SelectedTones);
        Assert.Empty(svc.SelectedHandShapes);
    }

    [Fact]
    public void ClearAll_EmptiesSelectionsPreviewsAndHovers()
    {
        var svc = new SelectionService();
        svc.SelectedNotes.Add(Note(0));
        svc.PreviewNotes.Add(Note(1));
        svc.HoveredNote = Note(2);

        svc.ClearAll();

        Assert.Empty(svc.SelectedNotes);
        Assert.Empty(svc.PreviewNotes);
        Assert.Null(svc.HoveredNote);
    }

    [Fact]
    public void ClearSelections_AlsoDropsTheSelectedLyric()
    {
        // The one scalar content selection. It used to survive every clear, so
        // after a project swap it still pointed into the CLOSED project's vocal
        // track — and the mouse-wheel adjuster mutates it.
        var svc = new SelectionService { SelectedLyric = new DotCharter.Core.Domain.Vocals.LyricEvent() };

        svc.ClearSelections();

        Assert.Null(svc.SelectedLyric);
    }

    /// <summary>
    ///     Selecting note-lane content drops the lyric, whichever gesture does it (click, marquee,
    ///     select-all-in-view, the context menu's target). A note click left the lyric selected and
    ///     highlighted, and once Delete removes a lone selected lyric (2026-09-25) a second Delete
    ///     after deleting that note deleted the lyric the user no longer thought was selected.
    /// </summary>
    [Fact]
    public void SelectingNoteLaneContent_DropsTheSelectedLyric()
    {
        var lyric = new DotCharter.Core.Domain.Vocals.LyricEvent();

        var notes = new SelectionService { SelectedLyric = lyric };
        notes.SelectedNotes.Add(new GuitarNote());
        Assert.Null(notes.SelectedLyric);

        var chords = new SelectionService { SelectedLyric = lyric };
        SelectionBulkOps.ReplaceWith(chords.SelectedChords, [new Chord()]);
        Assert.Null(chords.SelectedLyric);

        var shapes = new SelectionService { SelectedLyric = lyric };
        shapes.SelectedHandShapes.Add(new HandShape());
        Assert.Null(shapes.SelectedLyric);

        var anchors = new SelectionService { SelectedLyric = lyric };
        anchors.SelectedAnchors.Add(new FretAnchor());
        Assert.Null(anchors.SelectedLyric);
    }

    /// <summary>…but CLEARING or shrinking the note selection is not selecting something else.</summary>
    [Fact]
    public void ClearingOrRemovingNotes_KeepsTheSelectedLyric()
    {
        var lyric = new DotCharter.Core.Domain.Vocals.LyricEvent();
        var svc = new SelectionService();
        var a = new GuitarNote();
        var b = new GuitarNote();
        svc.SelectedNotes.Add(a);
        svc.SelectedNotes.Add(b);
        svc.SelectedLyric = lyric;

        svc.SelectedNotes.Remove(a);
        Assert.Same(lyric, svc.SelectedLyric);

        svc.SelectedNotes.Clear();
        SelectionBulkOps.ReplaceWith(svc.SelectedChords, Array.Empty<Chord>());
        Assert.Same(lyric, svc.SelectedLyric);
    }

    [Fact]
    public void ClearAll_AlsoDropsThePreviewArrangement()
    {
        // ClearPreviews() keeps the tag on purpose (per-gesture), but ClearAll is
        // the project-swap hook and must not hold a detached Arrangement alive.
        var svc = new SelectionService { PreviewArrangement = new Arrangement() };

        svc.ClearAll();

        Assert.Null(svc.PreviewArrangement);
    }

    [Fact]
    public void ClearPreviews_KeepsThePreviewArrangementTag()
    {
        var arrangement = new Arrangement();
        var svc = new SelectionService { PreviewArrangement = arrangement };

        svc.ClearPreviews();

        Assert.Same(arrangement, svc.PreviewArrangement);
    }

    [Fact]
    public void SelectionChanged_FiresOnNoteAddChordAddAndClear()
    {
        var svc = new SelectionService();
        var count = 0;
        svc.SelectionChanged += (_, _) => count++;

        svc.SelectedNotes.Add(Note(1)); // add
        svc.SelectedChords.Add(new Chord()); // add
        svc.ClearSelections(); // two non-empty collections cleared

        Assert.True(count >= 3, $"expected >=3 SelectionChanged raises, got {count}");
    }

    [Fact]
    public void SelectionChanged_FiresOnSelectedLyricChange()
    {
        var svc = new SelectionService();
        var fired = false;
        svc.SelectionChanged += (_, _) => fired = true;

        svc.SelectedLyric = new DotCharter.Core.Domain.Vocals.LyricEvent();

        Assert.True(fired);
    }

    [Fact]
    public void SelectionChanged_DoesNotFireForPreviewOrHoverChanges()
    {
        var svc = new SelectionService();
        var count = 0;
        svc.SelectionChanged += (_, _) => count++;

        svc.PreviewNotes.Add(Note(1));
        svc.HoveredNote = Note(2);
        svc.HoveredTimeMs = 500;

        Assert.Equal(0, count);
    }

    // ---- an empty clear is not a selection change ---------------------------

    /// <summary>
    ///     A clear that removes nothing raises nothing. Every one of the six
    ///     collections fans its own <c>CollectionChanged</c> into the coarse
    ///     <c>SelectionChanged</c>, so an unconditional <c>Clear()</c> ran the
    ///     whole subscriber set — the inspector's two O(selection) syncs, the
    ///     minimap repaint, the sheet strip's highlight pass — for a selection
    ///     that did not move. <c>ClearSelections</c> clears all six.
    /// </summary>
    [Fact]
    public void ClearSelections_OnAnEmptySelection_RaisesNothing()
    {
        var svc = new SelectionService();
        var count = 0;
        svc.SelectionChanged += (_, _) => count++;

        svc.ClearSelections();
        svc.ClearSelections();

        Assert.Equal(0, count);
        Assert.Equal(0, svc.SelectionVersion);
    }

    /// <summary>
    ///     The plain-click path (<c>HighwayInteractionTool</c>) clears notes,
    ///     chords, hand shapes and anchors before selecting the hit sound. On an
    ///     ordinary note click the last two are already empty, so two of the four
    ///     were pure notification.
    /// </summary>
    [Fact]
    public void ClearingTheFourSelections_OnlyRaisesForTheOnesThatHadContent()
    {
        var svc = new SelectionService();
        svc.SelectedNotes.Add(Note(1));
        var count = 0;
        svc.SelectionChanged += (_, _) => count++;

        svc.SelectedNotes.Clear();
        svc.SelectedChords.Clear();
        svc.SelectedHandShapes.Clear();
        svc.SelectedAnchors.Clear();

        Assert.Equal(1, count);
    }

    /// <summary>
    ///     …and a clear that really empties a selection still raises, so the
    ///     early-out cannot be mistaken for "a clear stopped notifying".
    /// </summary>
    [Fact]
    public void ClearSelections_WithContent_StillRaisesOncePerNonEmptyCollection()
    {
        var svc = new SelectionService();
        svc.SelectedNotes.Add(Note(1));
        svc.SelectedTones.Add(new ToneChange());
        var count = 0;
        svc.SelectionChanged += (_, _) => count++;

        svc.ClearSelections();

        Assert.Equal(2, count);
        Assert.Empty(svc.SelectedNotes);
        Assert.Empty(svc.SelectedTones);
        Assert.Null(svc.SelectedNote);
    }
}
