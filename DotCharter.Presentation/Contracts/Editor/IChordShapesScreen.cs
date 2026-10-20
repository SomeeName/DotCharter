using System.ComponentModel;
using System.Windows.Input;
using DotCharter.Core.Domain.Guitar;

namespace DotCharter.Presentation.Contracts.Editor;

/// <summary>
/// The Chord shapes list: every chord TEMPLATE of the active arrangement as a row of
///     its own — a mini chord box, its name, its finger state, how many chords and hand shapes
///     use it on the level on screen and on every level, and which other templates have the
///     same frets. Hosted as an inspector tab.
///     <para>
/// It is also the chord-shape PALETTE: a click on a row's chord box places that whole
///         chord at the pen, or reshapes the selection onto it, as one undo step; the shapes placed
///         last sit in a strip over the list.
///     </para>
///     <para>
///         What a row action did, or why it was refused, goes to the app's status rail
///         (<c>ProjectState.SetMessage</c>), the same line the placement service writes — not a
/// line in the pane, which pushed the list down.
///     </para>
/// </summary>
public interface IChordShapesScreen : INotifyPropertyChanged
{
    /// <summary>One line over the list: how many shapes, duplicates and missing fingerings.</summary>
    string Summary { get; }

    /// <summary>The rows the active filter lets through, in the arrangement's template order.</summary>
    IReadOnlyList<IChordShapeRow> Rows { get; }

    bool HasRows { get; }

    /// <summary>Why the list is empty: no arrangement, no templates, or nothing matches the filter.</summary>
    string EmptyText { get; }

    /// <summary>The filter captions, each carrying its count ("Duplicates 4").</summary>
    string AllFilterLabel { get; }

    string DuplicatesFilterLabel { get; }

    string MissingFingeringFilterLabel { get; }

    bool IsShowingAll { get; }

    bool IsShowingDuplicates { get; }

    bool IsShowingMissingFingering { get; }

    ICommand ShowAllCommand { get; }

    /// <summary>Only templates that share their frets (and arpeggio flavour) with another.</summary>
    ICommand ShowDuplicatesCommand { get; }

    /// <summary>Only templates with a fretted string that has no finger.</summary>
    ICommand ShowMissingFingeringCommand { get; }

    /// <summary>The shapes placed most recently on this arrangement, newest first — the same row objects as <see cref="Rows" />.</summary>
    IReadOnlyList<IChordShapeRow> RecentRows { get; }

    bool HasRecentRows { get; }

    /// <summary>Places the shape of the nearest chord before the pen at the pen.</summary>
    ICommand RepeatPreviousChordCommand { get; }
}

/// <summary>One chord template in the list.</summary>
public interface IChordShapeRow : INotifyPropertyChanged
{
    /// <summary>The template's name, or its fret pattern ("x x 8 6 x x") when it has none.</summary>
    string Name { get; }

    /// <summary>True when <see cref="Name" /> is the fret pattern standing in for a missing name; the view mutes it.</summary>
    bool IsUnnamed { get; }

    /// <summary>The frets low string to high, <c>x</c> for an unused string ("x 3 2 0 1 0").</summary>
    string FretsText { get; }

    /// <summary>A display copy of the template for the mini chord box (its fingers); never the chart's own instance.</summary>
    ChordTemplate Diagram { get; }

    /// <summary>The template's frets as a chord, which is what the mini chord box draws its dots from.</summary>
    Chord DiagramChord { get; }

    int StringCount { get; }

    bool IsArpeggio { get; }

    /// <summary>Complete / suggested / missing, as a word.</summary>
    string FingerStateText { get; }

    bool IsFingeringMissing { get; }

    bool IsFingeringSuggested { get; }

    /// <summary>"12 chords · 3 hand shapes" on the level on screen.</summary>
    string LevelUsesText { get; }

    /// <summary>"All levels: 40 chords · 9 hand shapes"; empty when no other level uses the shape.</summary>
    string AllUsesText { get; }

    bool IsUnused { get; }

    /// <summary>"Same frets as 2 others" when the row has duplicates, else empty.</summary>
    string DuplicateText { get; }

    bool IsDuplicate { get; }

    /// <summary>
    ///     Places this whole chord at the pen, pointing at this template, as one undo step; with
    ///     sounds selected it reshapes each of them onto this template instead.
    /// </summary>
    ICommand PlaceCommand { get; }

    /// <summary>Selects every chord and hand shape on the level on screen that uses this template.</summary>
    ICommand SelectUsesCommand { get; }

    /// <summary>Seeks to the next use after the playhead on the level on screen, wrapping round.</summary>
    ICommand NextUseCommand { get; }

    bool IsRenaming { get; }

    /// <summary>The name being typed while <see cref="IsRenaming" />.</summary>
    string NameDraft { get; set; }

    ICommand BeginRenameCommand { get; }

    ICommand CommitRenameCommand { get; }

    ICommand CancelRenameCommand { get; }

    /// <summary>The other templates with the same frets in the same arpeggio flavour; empty when none.</summary>
    IReadOnlyList<IChordShapeMergeTarget> MergeTargets { get; }

    /// <summary>True while the row lists its <see cref="MergeTargets" /> to pick from.</summary>
    bool IsChoosingMergeTarget { get; }

    /// <summary>Opens or closes the list of merge targets.</summary>
    ICommand ChooseMergeTargetCommand { get; }

    /// <summary>Moves the selected chords and hand shapes that use this template onto a copy of it.</summary>
    ICommand SplitSelectionCommand { get; }

    /// <summary>True while the selection holds at least one use of this template.</summary>
    bool CanSplitSelection { get; }
}

/// <summary>A template the row can be merged into.</summary>
public interface IChordShapeMergeTarget : INotifyPropertyChanged
{
    /// <summary>"Am · 14 uses".</summary>
    string Label { get; }

    /// <summary>Repoints every user of the row's template onto this one, as one undo step.</summary>
    ICommand MergeCommand { get; }
}
