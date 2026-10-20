using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DotCharter.Tone.UI.ViewModels.Shell;
using DotCharter.UI.Controls.Sheet;

namespace DotCharter.UI.ViewModels.Editor;

/// <summary>
/// Context for the dockable Sheet Music pane. Exposes what the SheetMusicView
/// control needs (project state, selection, history), routes seek requests to
/// the chart editor, owns the persisted sheet zoom (1.0 = fit the strip
/// height), and backs the sheet's EDITING gestures: exact click-select,
/// double-click note insert (quantized to the editor's Raster grid, default
/// fret from context) and GP-style digit fret typing. Every mutation goes
/// through <see cref="IHighwayEditingService"/> — the same commands and undo
/// history the 2D timeline uses, one gesture = one undo step.
/// </summary>
public sealed partial class SheetMusicPaneViewModel : ViewModelBase
{
    private readonly ChartEditorViewModel _chartEditor;
    private readonly IHighwayEditingService _highwayEditing;
    private readonly IConfigurationService _configService;
    private readonly AppConfig _appConfig;

    [ObservableProperty] private double _sheetZoom;

    /// <summary>0 = notation + tab, 1 = notation only, 2 = tab only —
    /// maps straight onto the staves ComboBox's SelectedIndex.</summary>
    [ObservableProperty] private int _sheetStaves;

    /// <summary>false = single horizontal strip (default, synced-follow editing),
    /// true = GP8-style paged multi-system layout (wraps + scrolls vertically).
    /// Persisted in <see cref="AppConfig.SheetMusicPageMode"/>.</summary>
    [ObservableProperty] private bool _sheetPageMode;

    /// <summary>Duration-palette modifiers: dotted (×3/2) and triplet (×2/3) are
    /// mutually exclusive input flags folded into the note-value the palette
    /// buttons apply. They do NOT transform the current selection on their own —
    /// our data stores a single duration fraction, not base+dot, so "make the
    /// selected note dotted" is expressed by re-clicking a note value with the
    /// modifier on.</summary>
    [ObservableProperty] private bool _durationDotted;

    [ObservableProperty] private bool _durationTriplet;

    public SheetMusicPaneViewModel(ProjectState projectState, ChartEditorViewModel chartEditor,
        IHighwayEditingService highwayEditing, IHistoryService history,
        IConfigurationService configService)
    {
        ProjectState = projectState;
        _chartEditor = chartEditor;
        _highwayEditing = highwayEditing;
        History = history;
        _configService = configService;
        _appConfig = configService.LoadAppConfig();
        _sheetZoom = Math.Clamp(_appConfig.SheetMusicZoom,
            SheetMusicView.MinUserScale, SheetMusicView.MaxUserScale);
        _sheetStaves = Math.Clamp(_appConfig.SheetMusicStaves, 0, 2);
        _sheetPageMode = _appConfig.SheetMusicPageMode;
    }

    partial void OnSheetStavesChanged(int value)
    {
        _appConfig.SheetMusicStaves = Math.Clamp(value, 0, 2);
        _configService.SaveAppConfig(_appConfig);
    }

    partial void OnSheetPageModeChanged(bool value)
    {
        _appConfig.SheetMusicPageMode = value;
        _configService.SaveAppConfig(_appConfig);
    }

    // `ToggleLayoutCommand` is DELETED (0.58): the strip/page toggle in the
    // floating stave cluster is `ToggleButton IsChecked="{Binding SheetPageMode,
    // Mode=TwoWay}"`, which also persists through `OnSheetPageModeChanged` above.

    // Dotted and triplet are mutually exclusive (a beat is one or the other).
    partial void OnDurationDottedChanged(bool value)
    {
        if (value) DurationTriplet = false;
    }

    partial void OnDurationTripletChanged(bool value)
    {
        if (value) DurationDotted = false;
    }

    public ProjectState ProjectState { get; }

    /// <summary>The app-wide shared selection model, bound into the sheet strip
    /// so it highlights the same notes/chords selected in the 2D/3D views.</summary>
    public ISelectionService Selection => _chartEditor.Selection;

    /// <summary>The shared undo history, bound into the strip so committed
    /// edits (from any view) re-render the notation.</summary>
    public IHistoryService History { get; }

    public void Seek(double ms) => _chartEditor.ExecuteSeek(ms);

    /// <summary>Click-to-select fallback (no glyph resolved): nearest note/chord.</summary>
    public void SelectAt(double ms) => _chartEditor.SelectNearestToTime(ms);

    /// <summary>
    /// Exact click-select from the sheet: the precise notes/chords the clicked
    /// glyph was rendered from. A plain click REPLACES the selection (empty
    /// payload = clicked a rest → clear). <paramref name="additive"/> (Ctrl+click)
    /// TOGGLES each sound into the existing multi-selection instead of replacing.
    /// </summary>
    public void SelectSounds(IReadOnlyList<GuitarNote> notes, IReadOnlyList<Chord> chords, bool additive = false)
    {
        var selection = _chartEditor.Selection;
        if (!additive)
        {
            selection.ClearSelections();
            SelectionBulkOps.ReplaceWith(selection.SelectedNotes, notes);
            SelectionBulkOps.ReplaceWith(selection.SelectedChords, chords);
            return;
        }

        // Ctrl+click: toggle each clicked sound in/out of the current selection.
        foreach (var n in notes)
            if (!selection.SelectedNotes.Remove(n)) selection.SelectedNotes.Add(n);
        foreach (var c in chords)
            if (!selection.SelectedChords.Remove(c)) selection.SelectedChords.Add(c);
    }

    /// <summary>
    /// Double-click insert: quantizes the clicked time to the editor's Raster
    /// grid (plus the grid's note magnetism), derives the default fret from the
    /// previous note on that string (0 when none), and commits through the same
    /// highway command pipeline the timeline uses — collision-checked, one undo
    /// step, and the new note ends up selected for immediate refinement.
    /// </summary>
    public void AddNoteAt(double timeMs, int stringIndex)
    {
        if (ProjectState.ActiveLevel == null) return;
        var pos = _chartEditor.SnapToGrid(timeMs);

        // Already a sound on that string at that position? Select it instead of
        // stacking a duplicate.
        foreach (var existing in _chartEditor.PlacedNotes)
        {
            if (existing.Position == pos && existing.StringIndex == stringIndex)
            {
                SelectSounds(new[] { existing }, Array.Empty<Chord>());
                return;
            }
        }
        foreach (var chord in _chartEditor.PlacedChords)
        {
            if (chord.Position == pos && chord.GetFretForString(stringIndex) >= 0)
            {
                SelectSounds(Array.Empty<GuitarNote>(), new[] { chord });
                return;
            }
        }

        var note = new GuitarNote
        {
            Position = pos,
            StringIndex = stringIndex,
            Fret = DefaultFretFor(pos, stringIndex),
            Sustain = FractionalPosition.Zero
        };

        _highwayEditing.CommitHandShapeDragAndDrop(
            [], [note],
            [], [],
            [], [],
            [], []);
    }

    /// <summary>GP-style digit fret typing on the current selection.</summary>
    public void SetFret(int fret) => _highwayEditing.SetSelectedFret(fret);

    /// <summary>
    /// Applies the note value with denominator <paramref name="denominator"/>
    /// (1 = whole … 64 = 64th), folded with the current dotted/triplet modifiers,
    /// to the selected note(s)/beat as their authored notation duration. Quarter-
    /// note units: value = 4/denominator, ×3/2 dotted, ×2/3 triplet. One undo
    /// step through the shared highway pipeline.
    /// </summary>
    public void ApplyDuration(int denominator)
    {
        var value = new Fraction(4, Math.Clamp(denominator, 1, 64));
        if (DurationDotted) value *= new Fraction(3, 2);
        else if (DurationTriplet) value *= new Fraction(2, 3);
        _highwayEditing.SetSelectedNotationDuration(value);
    }

    /// <summary>Command wrapper: XAML passes the denominator as a string
    /// (CommandParameter), parsed here so a single command backs every palette
    /// button.</summary>
    [RelayCommand]
    private void ApplyDurationValue(string? denominator)
    {
        if (int.TryParse(denominator, out var d)) ApplyDuration(d);
    }

    /// <summary>Clears the authored duration hint on the selection, reverting to
    /// the sustain/onset-derived rhythm.</summary>
    [RelayCommand]
    private void ClearDuration() => _highwayEditing.SetSelectedNotationDuration(null);

    /// <summary>GP "+" muscle memory: doubles the selected note value (longer —
    /// eighth → quarter). Backs the sheet-focused OemPlus/Num+ key.</summary>
    public void LengthenDuration() => _highwayEditing.ScaleSelectedNotationDuration(new Fraction(2, 1));

    /// <summary>GP "-" muscle memory: halves the selected note value (shorter —
    /// quarter → eighth). Backs the sheet-focused OemMinus/Num- key.</summary>
    public void ShortenDuration() => _highwayEditing.ScaleSelectedNotationDuration(new Fraction(1, 2));

    /// <summary>Previous note on the string at or before <paramref name="pos"/>
    /// (notes and chord notes both count); 0 when the string is untouched.</summary>
    private int DefaultFretFor(FractionalPosition pos, int stringIndex)
    {
        var bestPos = FractionalPosition.Zero;
        var bestFret = -1;

        foreach (var n in _chartEditor.PlacedNotes)
        {
            if (n.StringIndex != stringIndex || n.Position > pos) continue;
            if (bestFret < 0 || n.Position >= bestPos)
            {
                bestPos = n.Position;
                bestFret = n.Fret;
            }
        }
        foreach (var c in _chartEditor.PlacedChords)
        {
            if (c.Position > pos) continue;
            var fret = c.GetFretForString(stringIndex);
            if (fret < 0) continue;
            if (bestFret < 0 || c.Position >= bestPos)
            {
                bestPos = c.Position;
                bestFret = fret;
            }
        }

        return bestFret >= 0 ? bestFret : 0;
    }

    partial void OnSheetZoomChanged(double value)
    {
        var clamped = Math.Clamp(value, SheetMusicView.MinUserScale, SheetMusicView.MaxUserScale);
        if (Math.Abs(clamped - value) > 0.0001)
        {
            SheetZoom = clamped;
            return;
        }

        _appConfig.SheetMusicZoom = clamped;
        _configService.SaveAppConfig(_appConfig);
    }

    [RelayCommand]
    private void ZoomIn() => SheetZoom = Math.Round(SheetZoom + 0.1, 2);

    [RelayCommand]
    private void ZoomOut() => SheetZoom = Math.Round(SheetZoom - 0.1, 2);

    [RelayCommand]
    private void ZoomReset() => SheetZoom = 1.0;
}
