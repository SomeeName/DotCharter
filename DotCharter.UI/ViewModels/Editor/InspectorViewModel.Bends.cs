// InspectorViewModel: the two BendEditorViewModel factories and the chord-bend target rebuild - every bend write goes through the same undoable mutation funnel.

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DotCharter.UI.ViewModels.Editor.Inspector;
using DotCharter.UI.ViewModels.Panels;

namespace DotCharter.UI.ViewModels.Editor;

public partial class InspectorViewModel
{
    private void RebuildChordBendEditor()
    {
        if (SelectedChord == null || BendTargetChordNote == null)
        {
            ChordBendEditor = null;
            return;
        }

        ChordBendEditor = BuildChordBendEditor(SelectedChord, BendTargetChordNote);
    }

    // --- Whether the selected sound bends ---

    /// <summary>
    ///     The one selected note or chord carries a bend, so the Sound rail's Bends entry wears a
    ///     dot. Bends is a section of its own: a disclosure that opens by
    ///     itself for a bent sound would steal the section the
    ///     user is working in, so the rail says it instead.
    /// </summary>
    [ObservableProperty] private bool _selectionHasBends;

    /// <summary>Re-read on every note or chord sync (a bend edit re-syncs too).</summary>
    private void FollowSelectionBends(bool hasBends) => SelectionHasBends = hasBends;

    // --- The chord-bend string picker, named like every other string ---

    /// <summary>One label per <see cref="ChordBendStrings" /> entry, in the same
    /// order, through <see cref="GuitarStringLabel" />. The picker printed the
    /// model's 0-based index ("String 0") before.</summary>
    public IReadOnlyList<string> ChordBendStringLabels
    {
        get
        {
            var format = _localization["Inspector_StringLabelFmt"];
            return ChordBendStrings
                .Select(n => GuitarStringLabel.Format(n.StringIndex, NoteStringCount, NoteTuningNames, format))
                .ToArray();
        }
    }

    /// <summary>The picker's selected row: the position of
    /// <see cref="BendTargetChordNote" /> in <see cref="ChordBendStrings" />. A
    /// negative write (the picker clearing itself while its items are replaced)
    /// is ignored.</summary>
    public int ChordBendStringPosition
    {
        get => BendTargetChordNote == null ? -1 : ChordBendStrings.IndexOf(BendTargetChordNote);
        set
        {
            if (value < 0 || value >= ChordBendStrings.Count) return;
            BendTargetChordNote = ChordBendStrings[value];
        }
    }

    partial void OnChordBendStringsChanged(ObservableCollection<ChordNote> value)
    {
        _ = value;
        OnPropertyChanged(nameof(ChordBendStringLabels));
        OnPropertyChanged(nameof(ChordBendStringPosition));
    }

    // --- Bend editor factories ---
    //
    // The editor needs a reader for the current BendValues, a writer that
    // replaces them atomically, a pair of "start / end position" getters
    // for slot projection, and a commit callback. All writes go through
    // ApplyNoteMutation / ApplyChordMutation so the full bend-value list
    // swap is part of a single undoable action.

    private BendEditorViewModel BuildNoteBendEditor(GuitarNote note)
    {
        return new BendEditorViewModel(
            () => note.BendValues.ToList(),
            values =>
            {
                ApplyNoteMutation(n =>
                {
                    n.BendValues.Clear();
                    foreach (var v in values)
                        n.BendValues.Add(new BendValue(v.Position, v.Step));
                });
            },
            () => note.Position,
            () => note.EndPosition,
            () =>
            {
                /* writer already pushed to history */
            });
    }

    /// <summary>
    ///     A chord's curve is written through <see cref="ApplyChordMutation" />, so it follows the
    /// "Apply to" ladder pinned over the Bends section exactly as a technique does: the
    ///     rung's chords on every level, one undo node. A bend is per string, and the picker's string
    ///     is its mask: the curve lands on that STRING (by index) of every target chord, and a target
    ///     that does not play it is left alone.
    /// </summary>
    private BendEditorViewModel BuildChordBendEditor(Chord chord, ChordNote target)
    {
        var stringIdx = target.StringIndex;
        return new BendEditorViewModel(
            () => target.BendValues.ToList(),
            values => ApplyChordMutation(ChordBendWrite(stringIdx, values)),
            () => chord.Position,
            () => chord.Position + target.Sustain,
            () => { });
    }

    /// <summary>
    ///     What one chord-bend commit does to each chord it reaches: replaces the curve on the
    ///     string at <paramref name="stringIndex" />, and on no other; a chord without that string
    ///     is left as it is. Pure, so the default gate writes it per rung through the inspector's
    ///     own builder (<c>ChordBendApplyToTests</c>).
    /// </summary>
    internal static Action<Chord> ChordBendWrite(int stringIndex, IReadOnlyList<BendValue> values)
    {
        return chord =>
        {
            var note = chord.ChordNotes.FirstOrDefault(n => n.StringIndex == stringIndex);
            if (note == null) return;
            note.BendValues.Clear();
            foreach (var v in values)
                note.BendValues.Add(new BendValue(v.Position, v.Step));
        };
    }
}
