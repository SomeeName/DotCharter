// InspectorViewModel: the two BendEditorViewModel factories and the chord-bend target rebuild - every bend edit stages on the Bends page and the footer's Apply writes it.

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

    // --- Bend editor factories: a curve edit STAGES on the Bends page; the footer's Apply writes it ---

    private BendEditorViewModel BuildNoteBendEditor(GuitarNote note) =>
        Staged.Bends.BuildEditor(note, null, () => note.Position, () => note.EndPosition);

    /// <summary>A chord's curve is drawn on the picked STRING; Apply lands it on that string of every chord the "Apply
    /// to" rung reaches (a chord without it is passed by), remapped onto each one's onset and sustain.</summary>
    private BendEditorViewModel BuildChordBendEditor(Chord chord, ChordNote target) =>
        Staged.Bends.BuildEditor(chord, target.StringIndex, () => chord.Position, () => chord.Position + target.Sustain);
}
