using Avalonia;
using DotCharter.Services.Editing.Fingering;
using DotCharter.Services.Editing.HandShapes;
using DotCharter.Services.Editing.Tracks;
using DotCharter.Tests.Services.Editing.Highway;
using DotCharter.UI.Input.Tools;

namespace DotCharter.Tests.UI.Input.Tools;

/// <summary>
///     Drawing a bar on the hand-shape lane (#481). The right-click used to place a bar
///     exactly ONE BEAT long, named "Chord" whatever it covered, which the charter then had
///     to resize. Now a drag draws the span and a plain click spans to the next fret change
///     or chord — through <see cref="HandShapeDrawGesture" />, the part of
///     <c>HandShapeInteractionTool</c> that needs no <c>ToolContext</c>, against the real
///     editing service and history.
/// </summary>
public sealed class HandShapeDrawGestureTests
{
    private static readonly Point Press = new(100, 10);
    private static readonly Point FarAway = new(300, 10);

    [Fact]
    public void ADrag_DrawsTheSpanBetweenItsSnappedEnds_InOneHistoryNode()
    {
        var h = new Rig();
        h.Mutation.AddNote(3, 0, 3);
        h.Mutation.AddNote(4, 1, 5);

        h.Gesture.Arm(h.Track, Press, Beat(2));
        var bar = h.Gesture.Release(FarAway, Beat(6));

        Assert.NotNull(bar);
        var placed = Assert.Single(h.Mutation.State.HandShapes);
        Assert.Equal(Beat(2), placed.Position);
        Assert.Equal(Beat(6), placed.EndPosition);

        // The bar AND the template its grip minted are one undo node.
        var template = Assert.Single(h.Mutation.Arrangement.ChordTemplates);
        Assert.Equal(template.Id, placed.TemplateId);
        Assert.True(template.HasFrets(new Dictionary<int, int> { [0] = 3, [1] = 5 }));
        Assert.Single(h.Mutation.History.GetUndoSteps());

        h.Mutation.History.Undo();
        Assert.Empty(h.Mutation.State.HandShapes);
        Assert.Empty(h.Mutation.Arrangement.ChordTemplates);
    }

    [Fact]
    public void ADragToTheLeft_DrawsTheSameSpan()
    {
        var h = new Rig();
        h.Gesture.Arm(h.Track, FarAway, Beat(6));
        h.Gesture.Release(Press, Beat(2));

        var placed = Assert.Single(h.Mutation.State.HandShapes);
        Assert.Equal(Beat(2), placed.Position);
        Assert.Equal(Beat(6), placed.EndPosition);
    }

    [Fact]
    public void ADrag_StopsAtTheNextBar_InsteadOfOverlappingIt()
    {
        var h = new Rig();
        var existing = new HandShape { Position = Beat(5), EndPosition = Beat(7), Name = string.Empty };
        h.Mutation.State.HandShapes.Add(existing);

        h.Gesture.Arm(h.Track, Press, Beat(2));
        h.Gesture.Release(FarAway, Beat(9));

        var drawn = Assert.Single(h.Mutation.State.HandShapes, s => !ReferenceEquals(s, existing));
        Assert.Equal(Beat(2), drawn.Position);
        // Clamped to the next bar's start; ChartSpacingEnforcer then opens the usual gap.
        Assert.True(drawn.EndPosition.CompareTo(Beat(5)) <= 0, $"bar ends at {drawn.EndPosition}, inside the next bar");
        Assert.True(drawn.EndPosition.CompareTo(Beat(4)) > 0, $"bar ends at {drawn.EndPosition}, short of the drag");
    }

    /// <summary>The issue's click case: a run of identical-shape sounds is ONE grip, and the
    /// bar ends before the first sound that plays the hand somewhere else.</summary>
    [Fact]
    public void APlainClick_OverARunOfTheSameShape_EndsBeforeTheFirstDifferentSound()
    {
        var h = new Rig();
        h.Mutation.AddNote(1, 0, 3);
        h.Mutation.AddNote(2, 1, 5);
        var lastOfTheShape = h.Mutation.AddNote(3, 0, 3);
        var fretChange = h.Mutation.AddNote(5, 0, 7);

        h.Gesture.Arm(h.Track, Press, Beat(1));
        h.Gesture.Release(Press, Beat(1));

        var placed = Assert.Single(h.Mutation.State.HandShapes);
        Assert.Equal(Beat(1), placed.Position);
        Assert.True(placed.Covers(lastOfTheShape.Position), $"bar ends at {placed.EndPosition}, before its own shape");
        Assert.True(placed.EndPosition.CompareTo(fretChange.Position) <= 0,
            $"bar ends at {placed.EndPosition}, past the fret change at {fretChange.Position}");
        Assert.Single(h.Mutation.History.GetUndoSteps());
    }

    [Fact]
    public void APlainClick_OverRepeatedStrums_EndsBeforeTheNextChord()
    {
        var h = new Rig();
        h.Mutation.AddChord(1, -1, (0, 3), (1, 5), (2, 5));
        var lastStrum = h.Mutation.AddChord(2, -1, (0, 3), (1, 5), (2, 5));
        var nextChord = h.Mutation.AddChord(4, -1, (0, 5), (1, 7), (2, 7));

        h.Gesture.Arm(h.Track, Press, Beat(1));
        h.Gesture.Release(Press, Beat(1));

        var placed = Assert.Single(h.Mutation.State.HandShapes);
        Assert.True(placed.Covers(lastStrum.Position));
        Assert.True(placed.EndPosition.CompareTo(nextChord.Position) <= 0);
    }

    [Fact]
    public void APlainClick_EndsARunOfNotesAtTheNextChord()
    {
        var notes = new List<GuitarNote>
        {
            new() { Position = Beat(1), StringIndex = 0, Fret = 3 },
            new() { Position = Beat(2), StringIndex = 1, Fret = 5 }
        };
        var chord = new Chord { Position = Beat(3) };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 3 });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 5 });

        var end = HandShapeLaneSpan.ClickEnd(new Arrangement(), notes, [chord], [], [], Beat(1));

        Assert.Equal(Beat(3), end);
    }

    [Fact]
    public void APlainClick_OnAnEmptyLane_StillPlacesTheOneBeatBar()
    {
        var h = new Rig();
        h.Gesture.Arm(h.Track, Press, Beat(2));
        h.Gesture.Release(new Point(Press.X + 1, Press.Y), Beat(2));

        var placed = Assert.Single(h.Mutation.State.HandShapes);
        Assert.Equal(Beat(3), placed.EndPosition);
        Assert.Equal(-1, placed.TemplateId);
        Assert.Equal(string.Empty, placed.Name);
    }

    /// <summary>The preview prints the grip it will carry, and commits nothing.</summary>
    [Fact]
    public void TheDragPreview_ShowsTheGrip_AndLeavesTheChartAlone()
    {
        var h = new Rig();
        h.Mutation.AddNote(3, 0, 3);
        h.Mutation.AddNote(4, 1, 5);

        h.Gesture.Arm(h.Track, Press, Beat(2));
        Assert.Null(h.Gesture.Preview(new Point(Press.X + 1, Press.Y), Beat(2))); // still a click
        var preview = h.Gesture.Preview(FarAway, Beat(6));

        Assert.NotNull(preview);
        Assert.Equal(Beat(2), preview.Position);
        Assert.Equal(Beat(6), preview.EndPosition);
        Assert.Contains("35", preview.Name, StringComparison.Ordinal);
        Assert.Empty(h.Mutation.State.HandShapes);
        Assert.Empty(h.Mutation.Arrangement.ChordTemplates);
        Assert.Empty(h.Mutation.History.GetUndoSteps());
    }

    /// <summary>A new bar is named after its template — never the literal "Chord".</summary>
    [Fact]
    public void ADrawnBar_IsNamedAfterItsTemplate()
    {
        var h = new Rig();
        var g5 = h.Mutation.AddTemplate(0, (0, 3), (1, 5));
        g5.Name = "G5";
        h.Mutation.AddNote(3, 0, 3);
        h.Mutation.AddNote(4, 1, 5);

        h.Gesture.Arm(h.Track, Press, Beat(2));
        h.Gesture.Release(FarAway, Beat(6));

        var placed = Assert.Single(h.Mutation.State.HandShapes);
        Assert.Equal(g5.Id, placed.TemplateId);
        Assert.Equal("G5", placed.Name);
    }

    private static FractionalPosition Beat(int beat) => new(beat);

    private sealed class Rig
    {
        public Rig()
        {
            Gesture = new HandShapeDrawGesture(new FingeringGenerationService(), Mutation.Service);
            Track = new TrackEditorContext(Mutation.Arrangement, Mutation.Selection)
            {
                Project = Mutation.State.ActiveProject,
                Level = Mutation.Arrangement.Levels[0]
            };
        }

        public ChordKeyboardMutationTests.Harness Mutation { get; } = new();
        public HandShapeDrawGesture Gesture { get; }
        public TrackEditorContext Track { get; }
    }
}
