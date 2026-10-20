using BeatMarker = DotCharter.Core.Domain.Events.BeatMarker;
using Chord = DotCharter.Core.Domain.Guitar.Chord;
using DotCharter.Services.Editing.Anchors;
using DotCharter.Services.State;
using Fraction = DotCharter.Core.Domain.Primitives.Fraction;
using FretAnchor = DotCharter.Core.Domain.Events.FretAnchor;
using GuitarNote = DotCharter.Core.Domain.Guitar.GuitarNote;
using HandShape = DotCharter.Core.Domain.Guitar.HandShape;

namespace DotCharter.Services.Editing.Highway;

public interface IHighwayEditingService
{
    Action GetLinkNextUpdater();

    /// <summary>The same spacing / link-next pass for ANOTHER level of the arrangement — an
    /// edit that reaches every level (the inspector's "Apply to" ladder) runs it on each
    /// level it changed, not only on the one on screen.</summary>
    Action GetLevelSpacingUpdater(ChartLevel level);

    // Placement & Removal
    void HandleRightClickChord(Chord hitChord, int stringIndex);
    void RemoveNote(GuitarNote hitNote);
    void ApplyPlacement(FractionalPosition targetPos, int stringIndex);

    // Handshapes & Unified Drag and Drop
    void RemoveHandShape(HandShape handShape);

    /// <summary>
    ///     <paramref name="mintedTemplates" /> are templates the CALLER resolved
    ///     for this shape but has NOT added to the arrangement — they are added
    ///     inside the same history node, ahead of the shape itself.
    ///     <para>
    ///         Adding to `arr.ChordTemplates` directly, outside any `IChartAction`,
    ///         would leave a right-click hand-shape
    ///         create only half-undoable: the shape would come back out, the
    ///         template would stay forever.
    ///     </para>
    /// </summary>
    void AddHandShape(HandShape hs, IEnumerable<ChordTemplate>? mintedTemplates = null);

    /// <summary>
    ///     <c>incomingTemplates</c> carries the templates the incoming chords /
    ///     hand shapes' <c>TemplateId</c>s refer to when they come from ANOTHER
    ///     arrangement (a paste), keyed by the SOURCE id. Each is
    ///     resolved-or-created in the target arrangement and the element
    ///     repointed; a same-arrangement paste resolves back onto the same
    ///     template, so it stays a no-op. Null for in-place drag/drop.
    ///     <para>
    ///         <c>mintedTemplates</c> are templates the caller already resolved
    ///         and which must be added inside this same history node, BEFORE the
    ///         elements referencing them.
    ///     </para>
    /// </summary>
    bool CommitHandShapeDragAndDrop(
        List<GuitarNote> targetNotes, List<GuitarNote> newNotes,
        List<Chord> targetChords, List<Chord> newChords,
        List<HandShape> targetHandShapes, List<HandShape> newHandShapes,
        List<FretAnchor> targetAnchors, List<FretAnchor> newAnchors,
        IEnumerable<IChartAction>? additionalActions = null,
        IReadOnlyDictionary<int, ChordTemplate>? incomingTemplates = null,
        IEnumerable<ChordTemplate>? mintedTemplates = null);

    // Modifiers
    void MoveSelectedStringUp(int maxStrings);
    void MoveSelectedStringDown();

    /// <summary>
    ///     Moves the selection <paramref name="delta" /> lanes while keeping the
    ///     PITCH — fret 5 on the low E becomes fret 0 on the A. Guitar Pro's
    ///     Alt+Up / Alt+Down. A sound that cannot sound its pitch on the new
    ///     string stays put; a chord moves whole or not at all.
    /// </summary>
    void MoveSelectedStringPreservingPitch(int delta, int maxStrings);
    void MoveSelectedFretUp();
    void MoveSelectedFretDown();

    /// <summary>
    ///     Shifts the selection by <paramref name="direction" /> OCTAVES (±12
    ///     frets) in ONE history node.
    ///     <para>
    ///         Spelling this as
    ///         <c>for (i &lt; 12) MoveSelectedFretUp()</c> would make one keystroke on a
    ///         barre chord cost twelve of the 500 history nodes (twelve Ctrl+Z to
    ///         undo), twelve full <c>ChartSpacingEnforcer</c> passes, and up to
    ///         eleven intermediate <c>ChordTemplate</c>s — nothing garbage-collects
    ///         templates, so those would stay in the arrangement.
    ///     </para>
    ///     Behaviour is unchanged: the per-string clamp makes twelve ±1 steps and
    ///     one ±12 step agree for every fret.
    /// </summary>
    void MoveSelectedFretByOctave(int direction);

    void SetSelectedFret(int fret);

    /// <summary>
    /// How far the TECHNIQUE verbs below reach on a chord: the inspector's "Apply to"
    ///     ladder, set by the UI. Null — and on the ladder's "selected" rung, for an implied target
    ///     or with no ladder on screen — every verb acts on the selection. The fret and string verbs
    ///     never ask: a shape change is the chord panel's own edit.
    /// </summary>
    IChordTechniqueReach? TechniqueReach { get; set; }

    void ToggleSelectedHarmonic();
    void ToggleSelectedHopo();
    void ToggleSelectedMute();
    void ToggleSelectedLinkNext();
    void ToggleSelectedVibrato();
    void ToggleSelectedTremolo();
    void ToggleSelectedPicking();
    void ToggleSelectedAccent();
    void ToggleSelectedIgnore();
    void ToggleSelectedPassOtherNotes();

    // Group ("set all to first.next") variants — see SelectedNoteMutationCommands
    // for the rationale (mirrors RsCharter cyclicalToggleNotes vs the independent
    // form used by the methods above).
    void ToggleSelectedHarmonicGroup();
    void ToggleSelectedHopoGroup();
    void ToggleSelectedMuteGroup();
    void ToggleSelectedLinkNextGroup();
    void ToggleSelectedVibratoGroup();
    void ToggleSelectedTremoloGroup();
    void ToggleSelectedPickingGroup();
    void ToggleSelectedAccentGroup();
    void ToggleSelectedIgnoreGroup();
    void ToggleSelectedPassOtherNotesGroup();

    // Sustain
    bool AdjustSelectedNotesSustain(int deltaGridSteps, double totalAudioLengthMs, int gridDivisor,
        IList<BeatMarker> beats);

    /// <summary>
    ///     Nudges every selected FHP anchor's fret (or width) by
    ///     <paramref name="delta" />, without a dialog.
    ///     <para>
    ///         The dialog is not the ONLY way to change an existing anchor: as the only way,
    ///         one owner session meant 16 modal round trips,
    ///         12 of which changed the fret by exactly one — including two
    ///         consecutive ±1 edits on the SAME anchor a second apart, which is one
    ///         intent ("move it down two") spelled as two dialogs.
    ///     </para>
    ///     <para>
    ///         A run of nudges coalesces into ONE undo node
    ///         (<see cref="MergeVerbs.AnchorFret" /> /
    ///         <see cref="MergeVerbs.AnchorWidth" />), so holding the key to walk an
    ///         anchor down four frets is still a single Ctrl+Z.
    ///     </para>
    ///     Returns false when no anchor is selected or the nudge would change
    ///     nothing (already at a clamp).
    /// </summary>
    bool AdjustSelectedAnchors(int delta, AnchorNudgeTarget target, int capo = 0);

    // Notation duration (sheet-music authoring hint; NOTES and CHORDS in ONE undo
    // step). These write GuitarNote/Chord.NotationDuration (quarter-note units) —
    // the authored-rhythm hint the sheet exporter honours over sustain/onset fill.
    // Notation-only; never gameplay-relevant.

    /// <summary>Sets the notation-duration hint on every selected note/chord to
    /// <paramref name="duration"/> (null clears it, reverting to the derived
    /// rhythm). One undo step. Returns false when nothing is selected.</summary>
    bool SetSelectedNotationDuration(Fraction? duration);

    /// <summary>Multiplies every selected note/chord's notation duration by
    /// <paramref name="factor"/> (a null hint counts as a quarter = 1/1), clamped
    /// to the whole..64th range. Backs the GP-style +/- halve/double keys. One
    /// undo step. Returns false when nothing is selected.</summary>
    bool ScaleSelectedNotationDuration(Fraction factor);
}
