// The chart editing and maintenance commands the menus, keybinds and the command
// palette invoke: clipboard, selection, snapping, zoom, FHP and hand shapes,
// chord operations, beat/vocal insertion and the tempo-map dialogs.
using CommunityToolkit.Mvvm.Input;
using DotCharter.UI.Constants;
using DotCharter.UI.Input;

namespace DotCharter.UI.ViewModels.Shell;

public partial class MainWindowViewModel
{
    public bool IsBeatMapLocked => ProjectState.ActiveProject?.IsBeatMapLocked == true;

    /// <summary>
    /// The chart's context menu ▸ Apply DLC Builder fixes: DLC Builder's fixes, then dynamic
    ///     difficulty for a single-level arrangement, on the active arrangement as ONE undo step —
    ///     what every Rocksmith export already does to its copy, put into the project.
    /// </summary>
    [RelayCommand]
    private void ApplyDlcBuilderFixes() => _chartMaintenance.ApplyDlcBuilderFixes();

    [RelayCommand]
    private void ImproveChart() => _chartMaintenance.ImproveChart();

    [RelayCommand]
    private void AutoFixChart() => _chartMaintenance.AutoFixChart();

    [RelayCommand]
    private void RemoveDoubleStopChordNames() => _chartMaintenance.RemoveDoubleStopChordNames();

    [RelayCommand]
    private void SquashLevels() => _chartMaintenance.SquashLevels();

    /// <summary>
    ///     "This chorus appears 4× — apply my edit to all of them": replays the
    ///     content of the phrase iteration under the playhead into every other
    ///     iteration of the same phrase. Undoable as one action.
    /// </summary>
    [RelayCommand]
    private void PropagatePhrase() => _chartMaintenance.PropagatePhraseAtPlayhead();

    /// <summary>
    ///     Fills in the structure a chart can be CERTAIN of: the COUNT and END
    ///     phrases (89 of 89 hand-finished arrangements in TestData/EOF carry
    ///     exactly one of each) and a <c>noguitar</c> section over every bracketed
    ///     silence (171 of 171 such sections in the corpus contain no sound at
    ///     all). One undoable step.
    ///     <para>
    ///         It never invents a section BOUNDARY — not one of the 24 GuitarPro
    ///         sources in the corpus carries a section marker, and only 14.8% of
    ///         sections follow a rest, so where a verse becomes a chorus has no
    ///         signal to derive it from and stays the charter's call.
    ///     </para>
    /// </summary>
    [RelayCommand]
    private void GenerateStructure()
    {
        var result = _structureGeneration.Generate();
        ProjectState.MainDisplayMessage = result.IsEmpty
            ? _localization["Status_StructureNoChange"]
            : _localization.Format("Status_StructureGenerated",
                result.NoGuitarSections, result.CountPhrases + result.EndPhrases);
    }

    /// <summary>
    ///     The refret engine: re-assigns string/fret positions for the same
    ///     sounding pitches to minimize hand movement and stretch. Works on the
    ///     selection when notes are selected, else on the whole active
    ///     arrangement. Undoable as one action.
    /// </summary>
    [RelayCommand]
    private void Refret() => _chartMaintenance.Refret();

    [RelayCommand]
    private void GenerateFhp() => _chartMaintenance.GenerateFhp();

    [RelayCommand]
    private void GenerateFhpForSelection() => _chartMaintenance.GenerateFhpForSelection();

    [RelayCommand]
    private void GenerateHandShape() => _globalEditing.GenerateHandShapeFromSelection();

    /// <summary>Tools ▸ Hand shapes ▸ Generate arpeggio from selection: the
    /// same bars as Generate hand shape, each on an arpeggio template.</summary>
    [RelayCommand]
    private void GenerateArpeggio() => _globalEditing.GenerateArpeggioFromSelection();

    /// <summary>Tools ▸ Hand shapes ▸ Generate hand shapes (arrangement): a bar
    /// over every chord no bar covers, on every level, one undo step.</summary>
    [RelayCommand]
    private void GenerateHandShapesForArrangement() => _chartMaintenance.GenerateHandShapesForArrangement();

    /// <summary>Tools ▸ Hand shapes ▸ Accept suggested hand shapes in selection: every
    /// dashed bar holding a selected sound becomes a plain bar, one undo step.</summary>
    [RelayCommand]
    private void AcceptSuggestedHandShapes() => _globalEditing.AcceptSuggestedHandShapesInSelection(arpeggio: false);

    /// <summary>…the same, each bar on an arpeggio template.</summary>
    [RelayCommand]
    private void AcceptSuggestedArpeggios() => _globalEditing.AcceptSuggestedHandShapesInSelection(arpeggio: true);

    /// <summary>Splits the hand shape covering each selected sound AT that
    /// sound — the charting fix for a bar drawn across a fret change.</summary>
    [RelayCommand]
    private void SplitHandShape() => _globalEditing.SplitHandShapeAtSelection();

    [RelayCommand]
    private void ApplyHandShapeTemplateToChords() => _chartMaintenance.ApplyHandShapeTemplateToSelectedChords();

    [RelayCommand]
    private void InsertBeatAtPlayhead() => _chartMaintenance.InsertBeatAtPlayhead();

    [RelayCommand]
    private void RemoveBeatAtPlayhead() => _chartMaintenance.RemoveBeatAtPlayhead();

    [RelayCommand]
    private void InsertVocalAtPlayhead() => _chartMaintenance.InsertVocalAtPlayhead();

    /// <summary>
    ///     Drops the last section marker again at the playhead without the dialog.
    ///     <para>
    ///         Lifted out of <c>AppKeybindRegistrar</c> (where it was inline) so
    ///         the keybind and the command palette run the SAME code — it was
    ///         keybind-only before, i.e. unfindable by anyone who had not already
    ///         learnt the chord.
    ///     </para>
    /// </summary>
    [RelayCommand]
    private void StampSection()
    {
        if (_eventEditing.StampLastMarker(ChartEditor.SnapToGrid(ProjectState.CurrentTimeMs)))
            ChartEditor.RequestRedraw();
    }

    [RelayCommand]
    private void ToggleChordSplit() => _chartMaintenance.ToggleSelectedChordSplit();

    [RelayCommand]
    private void ToggleChordOnlyBox() => _chartMaintenance.ToggleSelectedChordOnlyBox();

    [RelayCommand]
    private void ToggleChordPassOtherNotes() => _chartMaintenance.ToggleSelectedChordPassOtherNotes();

    [RelayCommand]
    private void ConvertToPowerChordSmall() => _chartMaintenance.ConvertSelectedNotesToPowerChord(big: false);

    [RelayCommand]
    private void ConvertToPowerChordBig() => _chartMaintenance.ConvertSelectedNotesToPowerChord(big: true);

    [RelayCommand]
    private void MergeNotesIntoChord() => _chartMaintenance.MergeSelectedNotesIntoChords();

    [RelayCommand]
    private void ToggleOctaveFingering() => _chartMaintenance.ToggleTwoFretOctaveFingering();

    [RelayCommand]
    private void TogglePowerChordFingering() => _chartMaintenance.TogglePowerChordFingering();

    [RelayCommand]
    private void MutedStrumsToChord() => _chartMaintenance.ConvertMutedStrumsToChords();

    [RelayCommand]
    private void ToggleGhostGems() => _chartMaintenance.ToggleGhostGems();

    [RelayCommand]
    private void TappedHarmonic() => _chartMaintenance.MakeTappedHarmonic();

    [RelayCommand]
    private void SplitChordsIntoNotes() => _chartMaintenance.SplitSelectedChordsIntoNotes();

    [RelayCommand]
    private void SelectLikePattern() => _globalEditing.SelectLikePattern();

    [RelayCommand]
    private void Copy() => _globalEditing.CopySelected(ChartEditor.FocusArea);

    [RelayCommand]
    private void Cut()
    {
        _globalEditing.CopySelected(ChartEditor.FocusArea);
        _globalEditing.DeleteSelected(ChartEditor.FocusArea);
    }

    [RelayCommand]
    private void Paste() => _globalEditing.Paste(ProjectState.CurrentTimeMs, Transport.GridDivisor);

    /// <summary>Paste at an explicit chart time — the timeline context menu's
    /// "Paste here" captures its own click time rather than reading
    /// <c>LastPointerTimeMs</c> later, which may have moved while the menu was
    /// open.</summary>
    public void PasteAt(double timeMs) => _globalEditing.Paste(timeMs, Transport.GridDivisor);

    /// <summary>Duplicate = copy + paste at <paramref name="timeMs" /> — ONE
    /// code path for the keybind (paste-at-mouse, <c>AppKeybindRegistrar</c>)
    /// and the timeline context menu's "Duplicate".</summary>
    public void DuplicateSelectionAt(double timeMs)
    {
        _globalEditing.CopySelected(ChartEditor.FocusArea);
        _globalEditing.Paste(timeMs, Transport.GridDivisor);
    }

    [RelayCommand]
    private void SelectAll()
    {
        if (ChartEditor.FocusArea == EditorFocusArea.TimelineEvents)
            _globalEditing.SelectAllEvents();
        else
            _globalEditing.SelectAllNotes();
    }

    [RelayCommand]
    private void DeleteSelection() => _globalEditing.DeleteSelected(ChartEditor.FocusArea);

    [RelayCommand]
    private void SnapSelected() => _globalEditing.SnapSelectedNotes(Transport.GridDivisor);

    [RelayCommand]
    private void SnapAll() => _globalEditing.SnapAllNotes(Transport.GridDivisor);

    [RelayCommand]
    private void ZoomIn() =>
        // Follow ON: historical zoom-around-playhead. Follow OFF: anchors on
        // the viewport center instead of the far-away playhead (0.16 6.6).
        TimelineNavigation.ApplyZoom(ProjectState, ChartEditorConstants.ZoomInFactor);

    [RelayCommand]
    private void ZoomOut() => TimelineNavigation.ApplyZoom(ProjectState, ChartEditorConstants.ZoomOutFactor);

    /// <summary>Toolbar/palette toggle for the decoupled timeline viewport
    /// (0.16 6.6). Turning Follow off freezes the view where it is; turning
    /// it back on snaps the view to the playhead.</summary>
    [RelayCommand]
    private void ToggleTimelineFollow() => ProjectState.TimelineFollowsPlayhead = !ProjectState.TimelineFollowsPlayhead;

    // The keybind, the menu and the palette all come here; the target is chosen by
    // TimelineWheelAdjuster.ApplyToSelection (selected sounds, then hand shapes, then the lyric).
    private DotCharter.UI.Input.TimelineWheelAdjuster? _lengthAdjuster;

    [RelayCommand]
    private void IncreaseLength() => AdjustSelectionLength(1);

    [RelayCommand]
    private void DecreaseLength() => AdjustSelectionLength(-1);

    private void AdjustSelectionLength(int deltaSteps)
    {
        _lengthAdjuster ??= InteractionFactory.CreateWheelAdjuster();
        if (_lengthAdjuster.ApplyToSelection(this, deltaSteps) == DotCharter.UI.Input.WheelAdjustmentResult.Changed)
            ChartEditor.RequestRedraw();
    }

    [RelayCommand]
    private void ToggleBookmark(int index) => ChartEditor.ToggleBookmarkAtPlayhead(index);

    [RelayCommand]
    private void CopyStructureToOtherArrangements() => _chartMaintenance.CopyStructureToOtherArrangements();

    [RelayCommand]
    private void ToggleBeatMapLock()
    {
        // The flag is persisted, so the flip is a document change and has to
        // mark the project dirty — the coordinator owns that, and the menu
        // checkmark binding is refreshed here. See
        // ChartMaintenanceService.ToggleBeatMapLock.
        _chartMaintenance.ToggleBeatMapLock();
        OnPropertyChanged(nameof(IsBeatMapLocked));
    }
}
