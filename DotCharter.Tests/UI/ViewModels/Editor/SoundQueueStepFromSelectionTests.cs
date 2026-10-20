using DotCharter.Services.Editing.Selection;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     <c>CurrentIndex</c> is DEFINED as "position of the CURRENT selection
///     inside the filtered list", and the queue recomputed it only on the
///     inspector's debounced issue refresh (an edit, a history move, a level
///     switch) or a filter change.
///     <para>
///         A highway CLICK does none of those — <c>InspectorViewModel
///         .OnSelectionChanged</c> never schedules an issue refresh — so after
///         picking a different sound the position was whatever the last step had
///         left behind, and the very next <c>««</c>/<c>»»</c> stepped from THERE.
///         The charter lands somewhere unrelated to the sound under the caret,
///         and the queue silently skips or repeats the items in between.
///     </para>
///     <para>
///         The fix re-locates the selection inside the match list <c>Step</c> has
///         already built — one pass over a list that exists either way, no extra
///         seam read — rather than trusting the cached index.
///     </para>
/// </summary>
public class SoundQueueStepFromSelectionTests
{
    [Fact]
    public void SteppingAfterAHighwayClick_ContinuesFromTheClickedSound()
    {
        var h = new Harness();
        for (var beat = 0; beat < 5; beat++) h.Level.Notes.Add(Note(beat));
        var vm = h.Create();

        // Two steps: the queue is on note #2 (1-based index 2, beat 1).
        vm.NextMatch();
        vm.NextMatch();
        Assert.Equal(2, vm.CurrentIndex);

        // The charter now clicks note #4 on the highway. Nothing refreshes the
        // queue — a selection change is not an edit.
        h.SelectOnly(h.Level.Notes[3]);

        vm.NextMatch();

        Assert.Equal(5, vm.CurrentIndex);
        Assert.Same(h.Level.Notes[4], h.Selection.SelectedNote);
    }

    [Fact]
    public void SteppingBackAfterAHighwayClick_ContinuesFromTheClickedSound()
    {
        var h = new Harness();
        for (var beat = 0; beat < 5; beat++) h.Level.Notes.Add(Note(beat));
        var vm = h.Create();

        vm.NextMatch();
        h.SelectOnly(h.Level.Notes[3]);

        vm.PreviousMatch();

        Assert.Equal(3, vm.CurrentIndex);
        Assert.Same(h.Level.Notes[2], h.Selection.SelectedNote);
    }

    /// <summary>
    ///     …and the triage entry point still means "the FIRST match", not "one
    ///     past whatever happens to be selected". It sets the filter and steps
    ///     from position 0 explicitly, so re-locating inside the step must
    ///     not reach it.
    /// </summary>
    [Fact]
    public void TriageFilter_StillLandsOnTheFirstMatch_WithSomethingElseSelected()
    {
        var h = new Harness();
        for (var beat = 0; beat < 5; beat++) h.Level.Notes.Add(Note(beat));
        var vm = h.Create();

        h.SelectOnly(h.Level.Notes[3]);
        vm.TriageFilter(SoundQueueFilter.AnySound);

        Assert.Equal(1, vm.CurrentIndex);
        Assert.Same(h.Level.Notes[0], h.Selection.SelectedNote);
    }

    /// <summary>A sound that is NOT in the queue (a different filter's match set)
    /// leaves the position at 0, and a forward step from 0 lands on the first
    /// match — the wrap contract the queue has always had.</summary>
    [Fact]
    public void ASelectionOutsideTheQueue_StepsFromTheStart()
    {
        var h = new Harness();
        h.Arrangement.ChordTemplates.Add(new ChordTemplate
        {
            Id = 0,
            Frets = new Dictionary<int, int> { { 0, 3 } },
            Fingers = new Dictionary<int, int> { { 0, 1 } },
            Name = "A"
        });
        for (var beat = 0; beat < 3; beat++) h.Level.Notes.Add(Note(beat));
        var untemplated = new Chord { Position = new FractionalPosition(9), TemplateId = -1 };
        h.Level.Chords.Add(untemplated);

        var vm = h.Create();
        vm.SelectedFilter = vm.Filters.Single(f => f.Filter == SoundQueueFilter.Untemplated);
        // A NOTE is selected, and notes are never Untemplated matches.
        h.SelectOnly(h.Level.Notes[1]);

        vm.NextMatch();

        Assert.Equal(1, vm.CurrentIndex);
        Assert.Same(untemplated, h.Selection.SelectedChord);
    }

    private static GuitarNote Note(int beat) => new()
    {
        Position = new FractionalPosition(beat), StringIndex = 0, Fret = 5
    };

    private sealed class Harness
    {
        public readonly Arrangement Arrangement = new();
        public readonly List<ArrangementIssue> Issues = [];
        public readonly SelectionService Selection = new();

        public Harness()
        {
            Arrangement.Levels.Add(new ChartLevel());
            Arrangement.Tuning = ["E", "A", "D", "G", "B", "e"];
        }

        public ChartLevel Level => Arrangement.Levels[0];

        /// <summary>What a highway click does: clear, then select. Nothing here
        /// touches the queue.</summary>
        public void SelectOnly(GuitarNote note)
        {
            Selection.ClearSelections();
            Selection.SelectedNotes.Add(note);
            Selection.SelectedNote = note;
        }

        public SoundQueueViewModel Create() => new(
            () => Arrangement,
            () => Level,
            () => [],
            () => Issues,
            Selection,
            _ => { },
            FakeLocalization.English());
    }
}
