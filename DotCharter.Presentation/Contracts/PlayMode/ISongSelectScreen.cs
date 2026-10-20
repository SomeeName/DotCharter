using System.ComponentModel;
using System.Windows.Input;
using DotCharter.Presentation.Contracts.Library;

namespace DotCharter.Presentation.Contracts.PlayMode;

/// <summary>
///     What Play Mode's Song Select needs, and nothing else: the Library's list in the play
///     presentation, with your record beside each song and Play instead of Load.
///     <para>
///         It speaks the Library's language on purpose (wave 5): the same column headers
///         (<see cref="ILibraryColumn" /> — the header is the ONE sort control and carries the
///         column's funnel), artist headings while the list is in artist order, a column that
///         prints only what differs from the common case, and "—" for an unknown value. What it
///         adds is the player's: the record column, the tuning check and the best run.
///     </para>
///     <para>
///         Every string is already FORMATTED, so the view binds no converter chain, and
///         <c>FakeSongSelectScreen</c> drives every state — first run, a library still loading,
///         no match, the long-text stress case — with no services behind it.
///     </para>
/// </summary>
public interface ISongSelectScreen : INotifyPropertyChanged
{
    /// <summary>Leaves Song Select for Home.</summary>
    ICommand BackCommand { get; }

    /// <summary>The search box's text. Writing it re-filters <see cref="Rows" />.</summary>
    string SearchText { get; set; }

    /// <summary>True while <see cref="SearchText" /> is non-empty — the clear affordance's gate.</summary>
    bool HasSearchText { get; }

    /// <summary>Empties <see cref="SearchText" /> and nothing else.</summary>
    ICommand ClearSearchCommand { get; }

    /// <summary>"38 of 40 songs": what the search and filters leave, of all there are.</summary>
    string CountText { get; }

    /// <summary>True while the library is still being read.</summary>
    bool IsLoading { get; }

    /// <summary>"Reading your library…" while <see cref="IsLoading" />; empty otherwise.</summary>
    string LoadingText { get; }

    /// <summary>Points the app at a different song folder.</summary>
    ICommand ChooseLibraryFolderCommand { get; }

    ILibraryColumn SongColumn { get; }

    ILibraryColumn PathsColumn { get; }

    ILibraryColumn TuningColumn { get; }

    ILibraryColumn LengthColumn { get; }

    ILibraryColumn DifficultyColumn { get; }

    /// <summary>Your record: sorts by the accuracy the cell shows, best first; filters by played / full combo.</summary>
    ILibraryColumn RecordColumn { get; }

    /// <summary>One chip per active filter value, each carrying its own remove verb.</summary>
    IReadOnlyList<IFilterChip> ActiveFilters { get; }

    bool HasActiveFilters { get; }

    /// <summary>Drops every chip and the search term in one gesture.</summary>
    ICommand ClearFiltersCommand { get; }

    /// <summary>The filtered, sorted rows; an artist's heading is stamped on the first row of its run.</summary>
    IReadOnlyList<ISongRow> Rows { get; }

    /// <summary>
    ///     True when the list has something to show — the exact complement of the two empty
    ///     states, so the column heads go with the rows.
    /// </summary>
    bool HasRows { get; }

    /// <summary>The row the detail rail is describing, or null.</summary>
    ISongRow? SelectedRow { get; set; }

    bool HasSelection { get; }

    /// <summary>The selected song's cover as the platform draws it, or null while it streams in.</summary>
    object? DetailCover { get; }

    /// <summary>The selected song's arrangements, as the rail's tiles.</summary>
    IReadOnlyList<IArrangementTile> Arrangements { get; }

    /// <summary>
    ///     The tile Play would launch. A ONE-WAY selection: the screen republishes the picked tile
    ///     even when the value did not change.
    /// </summary>
    IArrangementTile? SelectedArrangement { get; set; }

    /// <summary>True when this profile has a recorded run on the selected song.</summary>
    bool HasBestRun { get; }

    /// <summary>The best run's letter grade, e.g. "A".</summary>
    string BestGradeLabel { get; }

    /// <summary>"96.2 %".</summary>
    string BestAccuracyLabel { get; }

    /// <summary>The best run's score, already formatted.</summary>
    string BestScoreLabel { get; }

    /// <summary>When the best run happened, already humanised ("4 days ago").</summary>
    string LastPlayedLabel { get; }

    bool IsBestFullCombo { get; }

    /// <summary>The tuning the selected arrangement expects, by NAME ("Drop C"), capo included.</summary>
    string ExpectedTuningLabel { get; }

    /// <summary>The note letters behind <see cref="ExpectedTuningLabel" />, for the tooltip.</summary>
    string ExpectedTuningNotes { get; }

    /// <summary>
    ///     What the tuner last heard against that tuning, in a sentence: "Your guitar is in
    ///     tune for this", "Retune from E Standard", or "Not checked yet".
    /// </summary>
    string TuningCheckLabel { get; }

    /// <summary>The guitar was last heard in a different tuning.</summary>
    bool TuningNeedsChange { get; }

    /// <summary>The guitar was last heard in this tuning.</summary>
    bool TuningIsVerified { get; }

    /// <summary>Starts the selected song on the selected arrangement.</summary>
    ICommand PlayCommand { get; }

    /// <summary>Whether <see cref="PlayCommand" /> would do anything; bound to the button's <c>IsEnabled</c>.</summary>
    bool CanPlay { get; }

    /// <summary>No song folder yet: the list is replaced by the one place to choose it.</summary>
    bool ShowFirstRun { get; }

    /// <summary>There are songs, and the search or the filters leave none of them.</summary>
    bool ShowNoMatch { get; }

    /// <summary>The setlist panel at the foot of the rail.</summary>
    ISetlistPanel Setlists { get; }
}

/// <summary>
///     What the view tells its screen about the list it is drawing, so covers stream in for the
///     rows on screen only and filter counts are computed when a funnel opens. Optional, as the
///     Library's <see cref="ILibraryViewHooks" /> is.
/// </summary>
public interface ISongSelectViewHooks
{
    void RowShown(ISongRow row);

    void RowHidden(ISongRow row);

    void FiltersOpening();
}

/// <summary>One row in the song list. Everything here is already FORMATTED.</summary>
public interface ISongRow : INotifyPropertyChanged
{
    /// <summary>The artist, on the first row of the artist's run; empty on the others.</summary>
    string GroupHeader { get; }

    bool HasGroupHeader { get; }

    string Title { get; }

    string Artist { get; }

    string Album { get; }

    /// <summary>The album under an artist heading; "artist · album" in any other order.</summary>
    string Subtitle { get; }

    /// <summary>One or two letters for the art tile while the cover is not drawn.</summary>
    string Monogram { get; }

    /// <summary>The cover as the platform draws it, or null while it streams in (or there is none).</summary>
    object? Cover { get; }

    /// <summary>The paths this song HAS, one initial each ("L", "R", "B").</summary>
    IReadOnlyList<string> Paths { get; }

    /// <summary>The same paths in words ("Lead · Rhythm · Bass"), for the tooltip and a screen reader.</summary>
    string PathsSummary { get; }

    /// <summary>The tuning when it is not E Standard without a capo ("Drop D", "E Std · capo 2"); empty when it is.</summary>
    string TuningLabel { get; }

    /// <summary>The note letters, for the tooltip.</summary>
    string TuningNotes { get; }

    /// <summary>The tuning or capo means work on the guitar first.</summary>
    bool TuningNeedsAttention { get; }

    /// <summary>"4:12".</summary>
    string LengthText { get; }

    /// <summary>"7.4", or "—" when unrated.</summary>
    string DifficultyLabel { get; }

    /// <summary>True when this profile has played it; an unplayed row's record cell is empty.</summary>
    bool HasBest { get; }

    /// <summary>The letter grade badge, e.g. "S".</summary>
    string BestGradeLabel { get; }

    /// <summary>"96.2 %".</summary>
    string BestAccuracyLabel { get; }

    bool IsFullCombo { get; }
}

/// <summary>An arrangement tile in the detail rail.</summary>
public interface IArrangementTile : INotifyPropertyChanged
{
    /// <summary>The arrangement's own name ("Lead 1").</summary>
    string Name { get; }

    /// <summary>Its tuning letters.</summary>
    string TuningLabel { get; }

    /// <summary>"612 notes", already formatted.</summary>
    string NoteCountLabel { get; }

    bool IsSelected { get; }

    /// <summary>Picks this tile. Always republishes, even when already picked.</summary>
    ICommand SelectCommand { get; }
}

/// <summary>One active filter, with the ✕ that drops exactly it.</summary>
public interface IFilterChip : INotifyPropertyChanged
{
    string Label { get; }

    ICommand RemoveCommand { get; }
}
