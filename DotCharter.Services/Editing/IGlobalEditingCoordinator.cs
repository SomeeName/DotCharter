using DotCharter.Services.Editing.Catalog;
using DotCharter.Services.Editing.Selection.Patterns;
using DotCharter.Services.Editing.Tracks;
using DotCharter.Services.State;

namespace DotCharter.Services.Editing;

public interface IGlobalEditingCoordinator
{
    void Undo();
    void Redo();
    void CopySelected(EditorFocusArea focusArea);
    void Paste(double currentTimeMs, int gridDivisor);
    void PasteSpecial(double currentTimeMs, int gridDivisor, PasteSpecialMode mode);

    /// <summary>Stores the current highway selection in the phrase catalog.
    /// Null when there is no track or nothing selected.</summary>
    PhraseCatalogEntry? CapturePhrase(EditorFocusArea focusArea, string name);

    /// <summary>Pastes a catalog phrase at the grid-snapped playhead as ONE
    /// undoable step. Refuses (changing nothing) when the phrase does not fit
    /// the destination — see <see cref="PhrasePasteCompatibility" />.</summary>
    PhrasePasteOutcome PastePhrase(PhraseCatalogEntry entry, double currentTimeMs, int gridDivisor);
    void SelectAllEvents();
    void SelectAllNotes();
    void SelectLikePattern();

    /// <summary>
    ///     Runs a user-authored <see cref="MatchPattern" /> over the focused
    ///     track and ADDS every occurrence to the selection. Returns how many
    ///     elements it added — the pattern builder shows it, and a pattern that
    ///     reports 0 on a chart full of the thing it describes is how a bad
    ///     definition announces itself.
    /// </summary>
    int SelectByPattern(MatchPattern pattern);

    /// <summary>
    ///     Moves to the next (<paramref name="direction" /> &gt; 0) or previous
    ///     occurrence of <paramref name="pattern" /> relative to
    ///     <paramref name="fromMs" />, REPLACING the selection with that
    ///     occurrence so a second press steps on from there. Returns the chart
    ///     time to seek to, or null when there is no further match — the caller
    ///     owns the seek, exactly as it does for the EOF seek family.
    /// </summary>
    double? MoveToPatternMatch(MatchPattern pattern, double fromMs, int direction);

    /// <summary>How many elements <paramref name="pattern" /> matches right now,
    ///     without touching the selection — the builder's live preview.</summary>
    int CountPatternMatches(MatchPattern pattern);

    /// <summary>
    ///     True when the SEED (the app-wide selection a pattern is derived from)
    ///     holds nothing that belongs to the FOCUSED track's level — i.e. the
    ///     seed and the source are two different arrangements.
    ///     <para>
    ///         The seed comes from <c>ISelectionService</c> and the source from
    ///         the focused track, and without this check nothing compares them. Select a
    ///         three-note shape on strings 3/4/5 of a six-string Lead, focus the
    ///         Bass, and <c>StringRule.Exact</c> compares literal string indices
    ///         against a four-string arrangement while <c>StringRule.Relative</c>
    ///         computes offsets from a six-string origin: silent over- or (much
    ///         more often) under-matching, reported to the user as nothing but a
    ///         smaller number.
    ///     </para>
    ///     <para>False when there is no seed at all — that state has its own
    ///     readout and this one must not compete with it.</para>
    /// </summary>
    bool PatternSeedIsFromAnotherArrangement();
    void GenerateHandShapeFromSelection();

    /// <summary>Generate hand shape from selection with every bar's grip resolved
    /// as an ARPEGGIO template.</summary>
    void GenerateArpeggioFromSelection();

    /// <summary>Accepts every suggested hand shape that holds a selected sound, as
    /// ONE undo node — plain bars, or arpeggio bars when <paramref name="arpeggio" />.</summary>
    void AcceptSuggestedHandShapesInSelection(bool arpeggio);

    /// <summary>Splits the hand shape covering each selected sound AT that
    /// sound, so a bar that spans a hand position change becomes two.</summary>
    void SplitHandShapeAtSelection();
    void DeleteSelected(EditorFocusArea focusArea);
    void DeleteSelectedRelated(EditorFocusArea focusArea);
    void SendSelectionToArrangement(Arrangement target);
    void SnapSelectedNotes(int gridDivisor);
    void SnapAllNotes(int gridDivisor);
    void ApplyDoubleTimeToSelection();
    void ApplyHalfTimeToSelection();
}
