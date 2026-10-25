using System.ComponentModel;
using System.Windows.Input;
using DotCharter.Core.Domain.Guitar;

namespace DotCharter.Presentation.Contracts.Editor;

/// <summary>
///     The Shapes panel: every chord shape of the arrangement as a card. A card shows only the
///     grip's diagram, its name and its use count, plus a small attention dot when it has a
///     problem — a duplicate, hand shapes out of sync with their chords, no fingering. The
///     actions (Edit, Select uses, Merge…, Delete), the name's No name switch and the problem's
///     one fix appear on the selected or hovered card alone, and nothing on a card is truncated.
///     The name is typed straight into the card, Edit opens the shape editor with its scope, a
///     card dragged onto another merges the two after one confirmation, and Delete asks only
///     when the shape is in use.
///     <para>
///         Cards are ticked for BULK edits: several shapes edited together in the shape editor
///         (frets or fingering, so same-fret copies can be fingered alike and become one shape),
///         merged into one, or deleted, from one bar over the list. A card offers its tick
///         only while <see cref="IShapePanelRow.CanTick" />.
///     </para>
///     <para>
///         A verb that needs a yes (a merge, deleting a used shape) shows its confirmation in
///         ONE bar over the list, saying what moves where, before anything changes.
///     </para>
/// </summary>
public interface IShapePanelScreen : INotifyPropertyChanged
{
    /// <summary>"18 shapes · 3 need attention".</summary>
    string Summary { get; }

    /// <summary>Filters the rows by name or frets as it is typed.</summary>
    string SearchText { get; set; }

    IReadOnlyList<IShapePanelRow> Rows { get; }

    bool HasRows { get; }

    /// <summary>Why the list is empty: no chart, no shapes, or nothing matches the search.</summary>
    string EmptyText { get; }

    /// <summary>True while Clean up shapes would change something.</summary>
    bool HasCleanUp { get; }

    /// <summary>"Remove unused (3)" while Clean up removes what nothing uses; "Clean up (7)" once it also merges and reviews.</summary>
    string CleanUpLabel { get; }

    /// <summary>Runs Clean up shapes: the shapes nothing uses are removed in one undo step.</summary>
    ICommand CleanUpCommand { get; }

    /// <summary>True while the "Needs attention (N)" toggle is offered: some card has a problem, or the filter is on.</summary>
    bool HasAttentionFilter { get; }

    /// <summary>"Needs attention (3)".</summary>
    string AttentionFilterLabel { get; }

    /// <summary>On: only the cards with a problem are listed. The search still applies.</summary>
    bool ShowAttentionOnly { get; set; }

    /// <summary>Places the shape of the nearest chord before the pen at the pen (the keybind and the palette verb).</summary>
    ICommand RepeatPreviousChordCommand { get; }

    /// <summary>True while a card is being dragged, or Merge… was pressed, and a target is wanted.</summary>
    bool IsChoosingMergeTarget { get; }

    /// <summary>"Drop “A5 (2)” on the shape it should become", or the pick-a-card wording while there is no drag.</summary>
    string MergeHintText { get; }

    /// <summary>True while at least one row is ticked.</summary>
    bool HasSelection { get; }

    /// <summary>"3 shapes selected · 37 chords".</summary>
    string SelectionText { get; }

    /// <summary>Opens the shape editor on the ticked shapes together.</summary>
    ICommand EditSelectedCommand { get; }

    /// <summary>Merges the ticked shapes into the one with the most uses (after the bar's confirmation).</summary>
    ICommand MergeSelectedCommand { get; }

    /// <summary>Deletes the ticked shapes; used ones ask in the bar where their uses go.</summary>
    ICommand DeleteSelectedCommand { get; }

    ICommand ClearSelectionCommand { get; }

    /// <summary>True while a verb waits for its confirmation in the bar.</summary>
    bool HasPending { get; }

    /// <summary>What the pending verb does, in full: "Merge “A5 (2)” into “A5”: 6 chords and 2 hand shapes move; “A5 (2)” is removed."</summary>
    string PendingText { get; }

    /// <summary>"Merge", "Delete and move 4 uses".</summary>
    string PendingConfirmLabel { get; }

    /// <summary>True when the pending verb removes something; the button wears the danger tier.</summary>
    bool IsPendingDestructive { get; }

    ICommand ConfirmPendingCommand { get; }

    /// <summary>Drops the pending verb, or ends the merge-target choice.</summary>
    ICommand CancelPendingCommand { get; }
}

/// <summary>One chord shape in the panel.</summary>
public interface IShapePanelRow : INotifyPropertyChanged
{
    /// <summary>Ticked for a bulk edit.</summary>
    bool IsSelected { get; set; }

    /// <summary>The name being typed in place; it is applied by <see cref="CommitNameCommand" /> (Enter, or the box losing focus).</summary>
    string NameText { get; set; }

    /// <summary>Applies the typed name to the shape, so every chord and hand shape that uses it reads it.</summary>
    ICommand CommitNameCommand { get; }

    /// <summary>True while the card offers its bulk-edit tick, which needs bulk edit wired.</summary>
    bool CanTick { get; }

    /// <summary>True while the card offers Edit, which needs the shape editor wired.</summary>
    bool CanEdit { get; }

    /// <summary>True while the card offers Merge…: the shape is used and another shape has the same frets.</summary>
    bool CanMerge { get; }

    /// <summary>True while a merge target is wanted and this card can take the picked one (same frets): it offers Merge here.</summary>
    bool CanMergeHere { get; }

    /// <summary>The fret pattern the name box shows when the shape has no name.</summary>
    string NamePlaceholder { get; }

    /// <summary>True when the shape deliberately has no name.</summary>
    bool IsNoName { get; set; }

    /// <summary>The frets low string to high, <c>x</c> for an unplayed string.</summary>
    string FretsText { get; }

    /// <summary>A display copy of the shape for the chord diagram; never the chart's own instance.</summary>
    ChordTemplate Diagram { get; }

    Chord DiagramChord { get; }

    int StringCount { get; }

    /// <summary>"14 chords · 3 hand shapes", or "Unused".</summary>
    string UsesText { get; }

    bool IsUnused { get; }

    /// <summary>True when the row names a problem.</summary>
    bool HasIssue { get; }

    /// <summary>"Same grip as “A5”", "2 hand shapes don't match their chords", "No fingering".</summary>
    string IssueText { get; }

    /// <summary>The one fix for the issue: "Merge", "Sync", "Finger".</summary>
    string FixLabel { get; }

    ICommand FixCommand { get; }

    /// <summary>True for the row being dragged (or picked with Merge…).</summary>
    bool IsMergeSource { get; }

    /// <summary>True for the row under the drag; the view lights it.</summary>
    bool IsMergeTarget { get; }

    /// <summary>Opens the shape editor on this shape, its scope on every use. Offered while <see cref="CanEdit" />.</summary>
    ICommand EditCommand { get; }

    /// <summary>Places this shape at the pen, or reshapes the selection onto it.</summary>
    ICommand PlaceCommand { get; }

    /// <summary>Selects every use on the level on screen.</summary>
    ICommand SelectUsesCommand { get; }

    /// <summary>Jumps to the next chord or hand shape on the level that uses this shape, after the playhead, wrapping.</summary>
    ICommand NextUseCommand { get; }

    /// <summary>True while the selection holds a chord or hand shape of this shape: the card offers Split off selection.</summary>
    bool CanSplitSelection { get; }

    /// <summary>Gives the selected uses their own copy of this shape, as one undo step.</summary>
    ICommand SplitSelectionCommand { get; }

    /// <summary>The keyboard path of a drag: picks this row as the one to merge away.</summary>
    ICommand BeginMergeCommand { get; }

    /// <summary>A drag began on this card: it becomes the shape to merge away, and the cards that can take it light up.</summary>
    ICommand BeginDragCommand { get; }

    /// <summary>The drag moved over (parameter <c>true</c>) or off (<c>false</c>) this card; a card that can take the dragged one lights.</summary>
    ICommand DragOverCommand { get; }

    /// <summary>The drag was released over this card: the merge waits in the bar, or the reason it cannot is said.</summary>
    ICommand DropCommand { get; }

    /// <summary>The drag ended wherever it was released (or Esc): a drag that did not land on a card drops the pick.</summary>
    ICommand EndDragCommand { get; }

    /// <summary>While a merge target is wanted: merges the picked row into this one (after the bar's confirmation).</summary>
    ICommand MergeHereCommand { get; }

    /// <summary>Deletes an unused shape at once; a used one asks in the bar where its uses go.</summary>
    ICommand DeleteCommand { get; }
}
