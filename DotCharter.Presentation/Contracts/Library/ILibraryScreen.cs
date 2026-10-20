using System.ComponentModel;
using System.Windows.Input;

namespace DotCharter.Presentation.Contracts.Library;

/// <summary>
///     The Library: the song archives under one folder, searched, filtered and sorted, with the
///     selected song's paths and the verb that opens one in the chart editor.
///     <para>
///         One contract for both presentations (wave 5): the charting host shows it as a dense
///         table beside the editor's rail, the play host full-bleed. Member names match
///         <c>ISongSelectScreen</c> where they mean the same thing, so Play Mode's Song Select can
///         become the play host of this data.
///     </para>
///     <para>
///         The table answers the external review's Library items: the column header is the ONE
///         sort control (no sort dropdown beside it), a column only prints what differs from the
///         common case (the paths a song has, an unusual tuning, a problem), an unknown value is
///         "—", and every filterable header carries its funnel on the same side.
///     </para>
/// </summary>
public interface ILibraryScreen : INotifyPropertyChanged
{
    /// <summary>The folder the archives are read from; empty before one is chosen.</summary>
    string FolderPath { get; }

    ICommand ChooseLibraryFolderCommand { get; }

    ICommand RefreshCommand { get; }

    bool IsScanning { get; }

    /// <summary>0..1 while <see cref="IsScanning" />.</summary>
    double ScanProgress { get; }

    /// <summary>"Scanning 412 of 2,160 archives…" while scanning; empty otherwise.</summary>
    string ScanText { get; }

    /// <summary>Stops a scan in flight; the songs read so far stay.</summary>
    ICommand CancelScanCommand { get; }

    /// <summary>An abnormal end — a scan that failed or a load that did not open; empty otherwise.</summary>
    string StatusText { get; }

    /// <summary>
    ///     What the last Refresh of the same folder changed — "Refreshed · 12 new, 1 removed" or
    ///     "Refreshed · no changes" — so a Refresh always answers; empty while scanning, after a
    ///     first scan or a new folder, and once the search changes.
    /// </summary>
    string RefreshResultText { get; }

    /// <summary>"150 of 2,160 songs": what the search and filters leave, of all there are.</summary>
    string CountText { get; }

    /// <summary>Writing it re-filters <see cref="Rows" />.</summary>
    string SearchText { get; set; }

    /// <summary>The song column, sorted by artist (the order that stamps the artist headings).</summary>
    ILibraryColumn SongColumn { get; }

    /// <summary>
    ///     The song column's other two orders, captioned beside <see cref="SongColumn" /> in the same
    ///     header cell: by title, and by the archive's file name, which keeps a pack's songs
    ///     together. Sort only (no filter); with them the header reaches every order the list
    ///     can be put in.
    /// </summary>
    ILibraryColumn TitleSort { get; }

    /// <inheritdoc cref="TitleSort" />
    ILibraryColumn ArchiveSort { get; }

    ILibraryColumn PathsColumn { get; }

    ILibraryColumn TuningColumn { get; }

    ILibraryColumn YearColumn { get; }

    ILibraryColumn DifficultyColumn { get; }

    ILibraryColumn LengthColumn { get; }

    ILibraryColumn StatusColumn { get; }

    IReadOnlyList<ILibraryFilterChip> ActiveFilters { get; }

    bool HasActiveFilters { get; }

    ICommand ClearFiltersCommand { get; }

    /// <summary>Flat and virtualized: an artist's header is stamped on the first row of its run.</summary>
    IReadOnlyList<ILibraryRow> Rows { get; }

    ILibraryRow? SelectedRow { get; set; }

    /// <summary>No folder chosen yet: the list is replaced by the one place to choose it.</summary>
    bool ShowFirstRun { get; }

    /// <summary>There are songs, and the search or the filters leave none of them.</summary>
    bool ShowNoMatch { get; }

    bool HasSelection { get; }

    string DetailTitle { get; }

    string DetailArtist { get; }

    string DetailAlbum { get; }

    /// <summary>The selected song's cover as the platform draws it, or null while it streams in.</summary>
    object? DetailCover { get; }

    /// <summary>The selected song has cover art (it may still be streaming in). Without it the rail shows no cover box at all.</summary>
    bool DetailHasCover { get; }

    /// <summary>Why the selected song cannot be loaded, in a sentence; empty when it can.</summary>
    string DetailProblem { get; }

    /// <summary>The selected song's facts as short tags ("Drop D (D A D G B E)", "capo 2", "1977"); a tuning carries its notes.</summary>
    IReadOnlyList<string> DetailFacts { get; }

    /// <summary>The paths the archive holds (none when it cannot be read); the picked one is what Load opens.</summary>
    IReadOnlyList<ILibraryPath> Paths { get; }

    ILibraryPath? SelectedPath { get; set; }

    /// <summary>"bobmthre_m.psarc · 3.6 MB".</summary>
    string ArchiveText { get; }

    /// <summary>"Ready to load — Three Little Birds · Lead", or why not.</summary>
    string LoadStatusText { get; }

    /// <summary>Loading replaces the project open in the editor: say so before, not after.</summary>
    bool ReplacesOpenProject { get; }

    ICommand CheckLibraryCommand { get; }

    ICommand LoadCommand { get; }

    /// <summary>What Load does when it can, and why it cannot when it cannot.</summary>
    string LoadTip { get; }
}

/// <summary>
///     What a Library view tells its screen about the list it is drawing, so covers stream in
///     for the rows on screen only and filter counts are computed when a funnel opens rather
///     than on every keystroke. Optional: a screen without it simply does not stream.
/// </summary>
public interface ILibraryViewHooks
{
    void RowShown(ILibraryRow row);

    void RowHidden(ILibraryRow row);

    void FiltersOpening();
}

/// <summary>A column header: its caption, the sort it applies and the filter it carries.</summary>
public interface ILibraryColumn : INotifyPropertyChanged
{
    string Title { get; }

    bool IsSortable { get; }

    /// <summary>"↑" / "↓" on the column the list is sorted by; empty on the others.</summary>
    string SortGlyph { get; }

    /// <summary>Sorts by this column; pressing the sorted column again reverses it. Unknown values ("—") sort last either way.</summary>
    ICommand SortCommand { get; }

    bool HasFilter { get; }

    /// <summary>Some option is ticked: the funnel lights.</summary>
    bool IsFiltered { get; }

    IReadOnlyList<ILibraryFilterOption> FilterOptions { get; }

    ICommand ClearFilterCommand { get; }
}

public interface ILibraryFilterOption : INotifyPropertyChanged
{
    string Label { get; }

    /// <summary>How many songs the option would leave, already formatted.</summary>
    string CountLabel { get; }

    bool IsSelected { get; set; }
}

public interface ILibraryFilterChip
{
    string Label { get; }

    ICommand RemoveCommand { get; }
}

public interface ILibraryRow : INotifyPropertyChanged
{
    /// <summary>The artist, on the first row of the artist's run; empty on the others.</summary>
    string GroupHeader { get; }

    bool HasGroupHeader { get; }

    string Title { get; }

    /// <summary>The album under an artist heading (the heading already names the artist); "artist · album" in any other order.</summary>
    string Subtitle { get; }

    /// <summary>One or two letters for the art tile while the cover is not drawn.</summary>
    string Monogram { get; }

    /// <summary>The cover as the platform draws it, or null while it streams in (or there is none).</summary>
    object? Cover { get; }

    /// <summary>The paths this song HAS, one initial each ("L", "R", "B", "V").</summary>
    IReadOnlyList<string> Paths { get; }

    /// <summary>The same paths in words ("Lead · Rhythm · Bass"), for the tooltip and a screen reader.</summary>
    string PathsSummary { get; }

    /// <summary>The tuning when it is not E Standard without a capo; empty when it is.</summary>
    string TuningLabel { get; }

    /// <summary>The tuning or capo means work on the guitar first.</summary>
    bool TuningNeedsAttention { get; }

    /// <summary>
    ///     <see cref="TuningLabel" /> with the strings' notes beside the name ("Drop C (C G C F A D) ·
    ///     capo 3"), for the cell's tooltip: the name alone hides which strings move. Empty when
    ///     <see cref="TuningLabel" /> is.
    /// </summary>
    string TuningTip { get; }

    /// <summary>The year, or "—".</summary>
    string YearLabel { get; }

    /// <summary>Songsterr-style difficulty, or "—".</summary>
    string DifficultyLabel { get; }

    string LengthLabel { get; }

    /// <summary>Empty for a song that loads; the problem, in two words, for one that does not.</summary>
    string StatusLabel { get; }

    bool HasProblem { get; }

    /// <summary>
    ///     "DotCharter song" on a song read from a DotCharter song package (<c>.dcsong</c>); empty on a
    ///     Rocksmith archive. A package and the archive it was exported from can sit side by side,
    ///     and this is how the list tells them apart.
    /// </summary>
    string SourceTag { get; }

    bool HasSourceTag { get; }
}

public interface ILibraryPath
{
    string Label { get; }

    /// <summary>"C# Standard · capo 3 · 325 notes": what differs, already formatted; empty when nothing is known.</summary>
    string Detail { get; }
}
