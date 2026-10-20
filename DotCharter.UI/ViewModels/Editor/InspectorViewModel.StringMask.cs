// InspectorViewModel: what the string-edit mask TARGETS in the selection - the chord technique readout's source string, the MaskedChordNotes rule, and the scope sentences. The mask verbs and keyboard readouts are Inspector/StringMaskViewModel.

using DotCharter.UI.ViewModels.Editor.Inspector;

namespace DotCharter.UI.ViewModels.Editor;

public partial class InspectorViewModel
{
    partial void OnBendTargetChordNoteChanged(ChordNote? value)
    {
        _ = value;
        OnPropertyChanged(nameof(ChordBendStringPosition));
        RebuildChordBendEditor();
        SyncChordNoteTechniques();
    }

    private void SyncChordNoteTechniques()
    {
        // The slide reads EVERY masked string, not the one technique source:
        // a chord whose strings slide differently has no single value.
        SyncChordSlide();
        var src = ActiveTechniqueSource();
        if (src == null) return;
        _isSyncing = true;
        ChordNoteMute = (int)src.Mute;
        ChordNoteHarmonic = (int)src.Harmonic;
        ChordNoteHopo = (int)src.Hopo;
        ChordNoteBassPicking = (int)src.BassPicking;
        ChordNoteVibrato = src.Vibrato;
        ChordNoteLinkNext = src.LinkNext;
        ChordNoteTremolo = src.Tremolo;
        ChordNoteUnpitchedSlide = src.UnpitchedSlide;
        ChordNoteIgnore = src.Ignore;
        _isSyncing = false;
    }

    /// <summary>
    ///     The chord note a per-string technique READOUT reflects: the lowest
    ///     masked string of the selected chord, else the bend target.
    ///     <para>
    ///         Reads <see cref="ProjectState.StringEditMask" /> — the SAME
    ///         selector the lane headers draw and <c>Ctrl+1..9</c> sets. Before
    ///         0.36 this read the inspector's own <c>ChordStringTargets</c>,
    ///         a second per-string selector with the OPPOSITE default (every
    ///         string picked, vs. the mask's empty), so masking strings 4–5 on
    ///         the lane headers and then clicking a technique chip applied it to
    ///         ALL strings.
    ///     </para>
    /// </summary>
    private ChordNote? ActiveTechniqueSource()
    {
        var firstMasked = MaskedChordNotes(ChordBendStrings, _projectState.StringEditMask).FirstOrDefault();
        return firstMasked ?? BendTargetChordNote;
    }

    /// <summary>
    ///     THE per-string targeting rule for a chord, in one place: the notes
    ///     whose string the mask includes, in string order.
    ///     <para>
    ///         <c>internal</c> so it is unit-testable without the full inspector
    ///         view-model graph — the same reason <see cref="AllSameNullable{T}" />
    ///         is (<c>NoteTechniqueChipBehaviorTests</c>).
    ///     </para>
    ///     <para>
    ///         An EMPTY mask includes every string, which is what makes the mask
    ///         a strict no-op until the user sets one. The inspector must not
    ///         ask a DIFFERENT selector (<c>ChordStringTargets</c>, defaulting
    ///         to every string SELECTED) and fall back to the bend target's
    ///         single string when it was empty — the two would disagree in both
    ///         directions and a mask set on the lane headers would do nothing here.
    ///     </para>
    /// </summary>
    internal static IReadOnlyList<ChordNote> MaskedChordNotes(
        IEnumerable<ChordNote> chordNotes, StringEditMask mask) =>
        chordNotes.Where(n => mask.Includes(n.StringIndex))
            .OrderBy(n => n.StringIndex)
            .ToArray();

    /// <summary>
    ///     What the per-string technique controls will actually touch, in words.
    ///     Principle 2 of the redesign: no edit may be broader than the sentence
    ///     above it, so this is rendered on the technique card's scope bar
    ///     rather than left as invisible modal state.
    /// </summary>
    public string ChordStringScopeLabel
    {
        get
        {
            var loc = _localization;
            var total = ChordBendStrings.Count;
            var mask = _projectState.StringEditMask;
            if (mask.IsEmpty)
                return loc.Format("Inspector_Scope_AllStrings", total);

            // Named the way the lane gutter and the string chips name them ("A (5)").
            var targeted = ChordBendStrings.Where(n => mask.IsExplicit(n.StringIndex))
                .Select(n => n.StringIndex)
                .ToList();
            return targeted.Count == 0
                ? loc["Inspector_Scope_NoStringInMask"]
                : loc.Format("Inspector_Scope_SomeStrings",
                    GuitarStringLabel.List(targeted, NoteStringCount, NoteTuningNames, loc["Inspector_StringLabelFmt"]),
                    targeted.Count, total);
        }
    }

    /// <summary>True when a SINGLE selected note sits on a string the mask
    /// excludes — the keyboard verbs skip it, so the panel says so and offers
    /// the clear inline instead of letting the user wonder.</summary>
    public bool NoteStringIsOutsideMask =>
        HasSingleNoteSelection && SelectedNote != null &&
        !_projectState.StringEditMask.IsEmpty &&
        !_projectState.StringEditMask.Includes(SelectedNote.StringIndex);

    private void NotifyMaskScopeChanged()
    {
        OnPropertyChanged(nameof(ChordStringScopeLabel));
        OnPropertyChanged(nameof(NoteStringIsOutsideMask));
        StringMask.SyncChips(SelectedChords.SelectMany(c => c.ChordNotes).Select(n => n.StringIndex), NoteStringCount,
            NoteTuningNames);
        StringMask.NotifyMaskChanged();
    }
}
