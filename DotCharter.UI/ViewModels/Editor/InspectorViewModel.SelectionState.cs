// InspectorViewModel: what the Sound tab SHOWS - SelectionKind, the card-visibility matrix derived from it, the selection/shape/bend summaries and the empty-state playhead line.

using System.Collections.ObjectModel;
using System.ComponentModel;
using DotCharter.UI.ViewModels.Editor.Inspector;

namespace DotCharter.UI.ViewModels.Editor;

public partial class InspectorViewModel
{
    /// <summary>Where the playhead is, for the empty state's block: with the pen
    /// on, a charter spends whole minutes with nothing selected, so the block shows more than
    /// a grey "select a note" hint.
    /// <para>
    ///     It must raise a change itself, or it reads the time the
    ///     view first bound (0:00.000 in the live app, with the playhead at 1:37).
    ///     It follows the playhead while stopped, catches up once when playback
    ///     stops and follows the grid; during playback it holds still rather than
    ///     re-formatting every frame (the status rail runs the live clock).
    /// </para></summary>
    public string PlayheadLabel
    {
        get
        {
            var ms = _projectState.CurrentTimeMs;
            var time = TimeSpan.FromMilliseconds(Math.Max(0, ms));
            return _localization.Format("Inspector_Empty_PlayheadAt",
                $"{(int)time.TotalMinutes}:{time.Seconds:00}.{time.Milliseconds:000}",
                Transport.GridDivisor);
        }
    }

    private static readonly PropertyChangedEventArgs PlayheadLabelChangedArgs = new(nameof(PlayheadLabel));

    /// <summary>A <see cref="ProjectState" /> change moves <see cref="PlayheadLabel" /> when it is
    /// the playhead moving while stopped.</summary>
    internal static bool ProjectStateChangeRefreshesPlayheadLabel(string? propertyName, bool isPlaying) =>
        !isPlaying && propertyName == nameof(ProjectState.CurrentTimeMs);

    /// <summary>A transport change moves <see cref="PlayheadLabel" /> when playback starts or stops
    /// (it catches up with where the playhead stopped) or the grid it prints changes.</summary>
    internal static bool TransportChangeRefreshesPlayheadLabel(string? propertyName) =>
        propertyName is nameof(TransportViewModel.IsPlaying) or nameof(TransportViewModel.GridDivisor);

    // ----- Selection summary header (UX1) -----

    public string SelectionSummary
    {
        get
        {
            var n = SelectedNotes.Count;
            var c = SelectedChords.Count;
            var loc = _localization;
            if (n == 0 && c == 0) return loc["Inspector_Selection_None"];
            if (n > 0 && c == 0)
            {
                return n == 1
                    ? loc["Inspector_Selection_NoteSingular"]
                    : loc.Format("Inspector_Selection_NotesPlural", n);
            }

            if (c > 0 && n == 0)
            {
                return c == 1
                    ? loc["Inspector_Selection_ChordSingular"]
                    : loc.Format("Inspector_Selection_ChordsPlural", c);
            }

            return loc.Format("Inspector_Selection_Mixed", n, c);
        }
    }

    public bool IsMultiSelect => SelectedNotes.Count + SelectedChords.Count > 1;

    public ObservableCollection<HandShape> SelectedHandShapes => _chartEditor.Selection.SelectedHandShapes;

    /// <summary>
    ///     THE one switch the Sound tab reads (04-inspector-redesign.md §3).
    ///     Derived from <c>ISelectionService</c> in a fixed priority order, so
    ///     the tab's content is a function of the SELECTION rather than of which
    ///     tab header the user last clicked.
    /// </summary>
    public SoundSelectionKind SelectionKind
    {
        get
        {
            var notes = SelectedNotes.Count;
            var chords = SelectedChords.Count;
            var shapes = SelectedHandShapes.Count;
            var sounds = notes + chords;

            if (shapes > 0) return sounds > 0 ? SoundSelectionKind.ShapeAndSounds : SoundSelectionKind.HandShape;
            if (sounds == 0) return SoundSelectionKind.Empty;
            if (sounds == 1) return chords == 1 ? SoundSelectionKind.Chord : SoundSelectionKind.Note;
            return notes > 0 && chords > 0 ? SoundSelectionKind.MultiMixed : SoundSelectionKind.MultiSame;
        }
    }

    // Card visibility, straight off the matrix. Kept as named booleans rather
    // than converters in the view so the matrix is readable in ONE place.

    /// <summary>The chord box renders for states 1–3 and 6 only. A
    /// multi-selection gets a shape SUMMARY — an ambiguous diagram over eight
    /// different shapes is a lie.</summary>
    public bool ShowShapeCard => SelectionKind is SoundSelectionKind.Chord
        or SoundSelectionKind.HandShape or SoundSelectionKind.ShapeAndSounds;

    public bool ShowPlacementCard => SelectionKind == SoundSelectionKind.Note;

    public bool ShowSpanCard => SelectionKind is SoundSelectionKind.HandShape
        or SoundSelectionKind.ShapeAndSounds;

    public bool ShowBulkCard => SelectionKind is SoundSelectionKind.MultiSame
        or SoundSelectionKind.MultiMixed;

    public bool ShowNoteTechnique => SelectionKind is SoundSelectionKind.Note
        or SoundSelectionKind.MultiSame or SoundSelectionKind.MultiMixed;

    public bool ShowChordTechnique => SelectionKind is SoundSelectionKind.Chord
        or SoundSelectionKind.ShapeAndSounds;

    /// <summary>Bends only ever render for exactly ONE sound. rscharter does the
    /// same, and it is the honest reading — a curve editor over eight different
    /// curves cannot be told the truth about any of them. States 4–6 get the
    /// one-line <see cref="BendSummary" /> readout instead.</summary>
    public bool ShowNoteBends => SelectionKind == SoundSelectionKind.Note;

    public bool ShowChordBends => SelectionKind == SoundSelectionKind.Chord;

    public bool ShowBendSummary => SelectionKind is SoundSelectionKind.MultiSame
        or SoundSelectionKind.MultiMixed;

    public bool IsEmptySelection => SelectionKind == SoundSelectionKind.Empty;

    public bool HasAnySoundSelection => SelectedNotes.Count + SelectedChords.Count > 0;

    /// <summary>"3 of 8 selected sounds carry bends" — what states 4–6 show in
    /// place of a curve editor they could not honestly drive.</summary>
    public string BendSummary
    {
        get
        {
            var fold = SelectionShapeFold.Over(SelectedNotes, SelectedChords);
            return _localization.Format("Inspector_BendSummary",
                fold.WithBends, fold.Total);
        }
    }

    /// <summary>Fret range / strings used / distinct shapes — the multi-selection's
    /// honest substitute for a diagram.</summary>
    public string SelectionShapeSummary
    {
        get
        {
            var fold = SelectionShapeFold.Over(SelectedNotes, SelectedChords);
            if (!fold.HasSounds) return string.Empty;
            return _localization.Format("Inspector_SelectionShapeSummary",
                fold.MinFret, fold.MaxFret,
                fold.MinString + 1, fold.MaxString + 1,
                fold.DistinctShapes);
        }
    }

    /// <summary>"applies to N sounds" — the scope sentence every card header
    /// carries. Replaces the old WarningFaint "applies to all" strip, which
    /// used the WARNING colour for an entirely normal state.</summary>
    public string AppliesToLabel =>
        _localization.Plural("Inspector_Scope_SoundsOne", "Inspector_Scope_Sounds",
            SelectedNotes.Count + SelectedChords.Count);

    private void NotifySelectionKindChanged()
    {
        Sections.Kind = SelectionKind;
        OnPropertyChanged(nameof(SelectionKind));
        OnPropertyChanged(nameof(ShowShapeCard));
        OnPropertyChanged(nameof(ShowPlacementCard));
        OnPropertyChanged(nameof(ShowSpanCard));
        OnPropertyChanged(nameof(ShowBulkCard));
        OnPropertyChanged(nameof(ShowNoteTechnique));
        OnPropertyChanged(nameof(ShowChordTechnique));
        OnPropertyChanged(nameof(ShowNoteBends));
        OnPropertyChanged(nameof(ShowChordBends));
        OnPropertyChanged(nameof(ShowBendSummary));
        OnPropertyChanged(nameof(IsEmptySelection));
        OnPropertyChanged(nameof(HasAnySoundSelection));
        OnPropertyChanged(nameof(BendSummary));
        OnPropertyChanged(nameof(SelectionShapeSummary));
        OnPropertyChanged(nameof(AppliesToLabel));
    }

    /// <summary>True when at least one note is selected. Drives the Note tab's
    /// empty-state hint (friendly prompt instead of dead zeroed controls).</summary>
    public bool HasNoteSelection => SelectedNote != null || SelectedNotes.Count > 0;

    /// <summary>True when exactly one note is selected — gates the interactive
    /// placement fretboard (a single dot to move). A multi-selection hides it
    /// and shows the numeric editors with an "applies to all" hint instead.</summary>
    public bool HasSingleNoteSelection => HasNoteSelection && !IsMultiSelect;

    /// <summary>True when at least one chord is selected. Drives the Chord tab's
    /// empty-state hint.</summary>
    public bool HasChordSelection => SelectedChord != null || SelectedChords.Count > 0;

    partial void OnSelectedNoteChanged(GuitarNote? value)
    {
        _ = value;
        OnPropertyChanged(nameof(SelectionSummary));
        OnPropertyChanged(nameof(IsMultiSelect));
        OnPropertyChanged(nameof(HasNoteSelection));
        OnPropertyChanged(nameof(HasSingleNoteSelection));
    }

    partial void OnSelectedChordChanged(Chord? value)
    {
        _ = value;
        OnPropertyChanged(nameof(SelectionSummary));
        OnPropertyChanged(nameof(IsMultiSelect));
        OnPropertyChanged(nameof(HasChordSelection));
        OnPropertyChanged(nameof(HasSingleNoteSelection));
    }
}
