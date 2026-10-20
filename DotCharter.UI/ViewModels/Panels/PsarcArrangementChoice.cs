using DotCharter.Services.Library;

namespace DotCharter.UI.ViewModels.Panels;

/// <summary>
///     One selectable PATH of the selected archive in the detail pane, with its
///     REAL note count — the manifest's <c>NotesHard</c>, carried through
///     <see cref="LibraryArrangementEntry.NoteCount" /> from the <c>.hsan</c> the
///     library scan already reads, so it costs no extra archive work.
///     <para>
///         <see cref="HasNoteCount" /> is false when the package carried none (a
///         vocals arrangement, a hand-built package). The row then shows the path
///         alone rather than a zero: an arrangement with genuinely no notes does
///         not ship, so a printed 0 would be a claim the data never made.
///     </para>
///     <para>
///         Picking a path is not decoration — it is what
///         <see cref="SongBrowserViewModel.LoadSongItem" /> opens the editor on
///         (<c>ProjectState.ActiveArrangement</c>). The whole archive is still
///         loaded; the pick decides which arrangement is in front of you.
///     </para>
/// </summary>
public sealed class PsarcArrangementChoice
{
    private readonly ILocalizationService _localization;

    public PsarcArrangementChoice(ILocalizationService localization, string label, string arrangementName, int noteCount,
        string tuningSummary = "", int capo = 0)
    {
        _localization = localization;
        Label = label;
        ArrangementName = arrangementName;
        NoteCount = noteCount;
        TuningLabel = SongRowFacts.TuningCell(tuningSummary, capo, localization);
    }

    /// <summary>THIS arrangement's tuning with its capo folded in ("Drop D ·
    /// Capo 2"), per <see cref="SongRowFacts.TuningCell" />. Empty when the
    /// index row predates schema v8 or the package carries none — the pane then
    /// prints the path alone rather than repeating the song-level tuning as if
    /// it were this arrangement's.</summary>
    public string TuningLabel { get; }

    public bool HasTuning => TuningLabel.Length > 0;

    /// <summary>Rocksmith path name — Lead / Rhythm / Bass / Combo / Vocals.</summary>
    public string Label { get; }

    /// <summary>The manifest's arrangement name, which is how the pick is matched
    /// back to an <c>Arrangement</c> in the loaded project.</summary>
    public string ArrangementName { get; }

    public int NoteCount { get; }
    public bool HasNoteCount => NoteCount > 0;

    public string NoteCountLabel => NoteCount > 0
        ? _localization.Plural("SongBrowser_NoteCountOne", "SongBrowser_NoteCount", NoteCount)
        : "";
}
