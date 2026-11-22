// The shell's doors onto the dialogs that live on the rewrite's DialogHost
// (IDialogs, target-architecture.md § 3.2): each verb is one ShowAsync, a dialog
// nobody awaits has its fault logged rather than lost, and a verb with nothing
// to act on says so on the status line instead of opening an empty dialog.
using CommunityToolkit.Mvvm.Input;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Dialogs;
using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.Presentation.Shared;
using DotCharter.Services.Editing.Maintenance;
using DotCharter.Services.Editing.Tempo.RecordingAlignment;
using DotCharter.Services.Export;
using DotCharter.Tone.UI.ViewModels.Shell;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.Input;

namespace DotCharter.UI.ViewModels.Shell;

/// <summary>
///     Every menu, palette and keybind verb that opens a hosted dialog — Edit,
///     File, Tools, Tempo map, Arrangement and Vocals — plus the editor tools'
///     doors onto the fret-hand position, event and beat editors, exposed as
///     <c>MainWindowViewModel.ShellDialogs</c>. Tools ▸ Calibrate latency reaches
///     the hosted wizard through the injected <see cref="ILatencyCalibrationPrompt" />,
///     the same seam Settings ▸ Audio and the first-run wizard take.
/// </summary>
public sealed partial class ShellDialogsViewModel : ViewModelBase, IFhpEditLauncher
{
    private readonly ProjectState _projectState;
    private readonly ChartEditorViewModel _chartEditor;
    private readonly IDialogs _hostedDialogs;
    private readonly ILatencyCalibrationPrompt _latencyCalibration;
    private readonly IAppLogger _logger;
    private readonly ILocalizationService _localization;
    private readonly IPlaybackTransport _playback;
    private readonly ChartIssuesDoor _issuesDoor;
    private Action? _showChartIssues;

    public ShellDialogsViewModel(
        ProjectState projectState,
        ChartEditorViewModel chartEditor,
        IDialogs hostedDialogs,
        ILatencyCalibrationPrompt latencyCalibration,
        IAppLogger logger,
        ILocalizationService localization,
        IPlaybackTransport playback,
        ChartIssuesDoor issuesDoor,
        SelectionEditsViewModel selectionEdits,
        InlineEditViewModel inlineEdit,
        AiChartViewModel? aiChart = null,
        Editor.AiChatDockViewModel? aiChat = null)
    {
        _projectState = projectState;
        _chartEditor = chartEditor;
        _hostedDialogs = hostedDialogs;
        _latencyCalibration = latencyCalibration;
        _logger = logger;
        _localization = localization;
        _playback = playback;
        _issuesDoor = issuesDoor;
        SelectionEdits = selectionEdits;
        InlineEdit = inlineEdit;
        inlineEdit.OpenEventDialog = request => OpenEvent(request.TimeMs, request.Target, request.SectionName);
        AiChart = aiChart;
        AiChat = aiChat;
    }

    /// <summary>
    ///     Select similar, saved patterns, technique toggles and double / half time: the Edit menu's
    ///     selection verbs, hung here because the shell's own constructor is at its dependency budget.
    /// </summary>
    public SelectionEditsViewModel SelectionEdits { get; }

    /// <summary>The timeline's in-place fret-hand position and section editors (bound by the overlay over the timeline).</summary>
    public InlineEditViewModel InlineEdit { get; }

    /// <summary>
    /// Tools ▸ Fix &amp; clean up's AI chart pass verbs and their door to
    ///     Settings ▸ AI. They run on the status rail rather than in a dialog; they hang
    ///     here because the shell's own constructor is at its dependency budget
    ///     (<c>MainWindowViewModelDependencyBudgetTests</c>). Null only in a host built
    ///     without them.
    /// </summary>
    public AiChartViewModel? AiChart { get; }

    /// <summary>
    /// The AI chat pane's dock Context. It hangs here for the same reason
    ///     <see cref="AiChart" /> does: the shell's constructor is at its dependency budget.
    ///     Null only in a host built without it.
    /// </summary>
    public Editor.AiChatDockViewModel? AiChat { get; }

    /// <summary>Edit ▸ Paste special, from the menu, the palette and its keybind.</summary>
    [RelayCommand]
    private void OpenPasteSpecial() => Launch(() => _hostedDialogs.ShowAsync<IPasteSpecialScreen, bool>(), "paste special");

    /// <summary>Help ▸ Keyboard shortcuts.</summary>
    [RelayCommand]
    private void OpenKeyboardShortcuts() => Launch(() => _hostedDialogs.ShowAsync<IKeyboardShortcutsScreen, bool>(), "keyboard shortcuts");

    /// <summary>Vocals ▸ Track settings; there is nothing to edit without an active vocal track.</summary>
    [RelayCommand]
    private void OpenVocalTrackSettings()
    {
        if (_chartEditor.ActiveVocalTrack == null)
        {
            return;
        }

        Launch(() => _hostedDialogs.ShowAsync(new VocalTrackSettingsRequest()), "vocal track settings");
    }

    /// <summary>Tools: Fix chart (and the palette's Improve chart / Auto-fix arrangements): lists what the groups would change, then applies the ticked ones as one undo step.</summary>
    internal void OpenFixChart(FixChartGroups preselect = FixChartGroups.Both) =>
        Launch(() => _hostedDialogs.ShowAsync(new FixChartRequest(preselect)), "fix chart");

    /// <summary>Tools: Fix chart...: both groups ticked.</summary>
    [RelayCommand]
    private void FixChart() => OpenFixChart();

    /// <summary>The palette's Improve chart: the Polish group ticked.</summary>
    [RelayCommand]
    private void ImproveChart() => OpenFixChart(FixChartGroups.Polish);

    /// <summary>The palette's Auto-fix arrangements: the Errors group ticked.</summary>
    [RelayCommand]
    private void AutoFixChart() => OpenFixChart(FixChartGroups.Errors);

    /// <summary>The fret-hand position editor at <paramref name="timeMs" /> (the playhead when negative).</summary>
    internal void OpenFhp(double timeMs = -1, FretAnchor? anchor = null) => Launch(() => _hostedDialogs.ShowAsync(new FhpRequest(timeMs, anchor)), "fret-hand position");

    /// <summary>The event marker at <paramref name="timeMs" /> of <paramref name="arrangement" /> (the active one when null).</summary>
    internal void OpenEvent(double timeMs, Arrangement? arrangement = null) => OpenEvent(timeMs, arrangement, null);

    /// <summary>The event marker dialog with <paramref name="sectionName" /> already typed (the popover's "More...").</summary>
    internal void OpenEvent(double timeMs, Arrangement? arrangement, string? sectionName) =>
        Launch(() => _hostedDialogs.ShowAsync(new EventRequest(timeMs, arrangement, sectionName)), "event");

    /// <summary>
    ///     The fret-hand position editor ON the timeline row of <paramref name="viewport" />; the dialog when that
    ///     timeline cannot host it (another timeline's click, or none on screen).
    /// </summary>
    internal void EditFhpInPlace(object? viewport, double timeMs, FretAnchor? anchor) =>
        ((IFhpEditLauncher)this).EditFhpInPlace(viewport, new FhpRequest(timeMs, anchor));

    void IFhpEditLauncher.EditFhpInPlace(object? viewport, FhpRequest request)
    {
        if (InlineEdit.Hosts(viewport)) InlineEdit.ShowFhp(request);
        else Launch(() => _hostedDialogs.ShowAsync(request), "fret-hand position");
    }

    /// <summary>The Edit fret-hand position key: the in-place editor at the playhead, the dialog when no timeline can host it.</summary>
    internal void EditFhpAtPlayheadInPlace() => EditFhpInPlace(InlineEdit.Host, -1, null);

    /// <summary>The section popover on the Sections row of <paramref name="viewport" />; the event dialog when it cannot be shown.</summary>
    internal void AddSectionInPlace(object? viewport, double timeMs, Arrangement? arrangement)
    {
        if (InlineEdit.Hosts(viewport)) InlineEdit.ShowSection(timeMs, arrangement);
        else OpenEvent(timeMs, arrangement);
    }

    /// <summary>The beat editor on the grid's beat at <paramref name="index" />.</summary>
    internal void OpenBeat(BeatMarker beat, int index) => Launch(() => _hostedDialogs.ShowAsync(new BeatRequest(beat, index)), "beat");

    /// <summary>File ▸ Song metadata; says so instead when no project is open.</summary>
    [RelayCommand]
    private void OpenSongMetadata()
    {
        if (_projectState.ActiveProject?.Chart == null)
        {
            _projectState.SetMessage(_localization["Status_SongMetadata_NoProject"]);
            return;
        }

        Launch(() => _hostedDialogs.ShowAsync<ISongMetadataScreen, bool>(), "song metadata");
    }

    /// <summary>Tools ▸ Generate difficulty levels; says so instead when no arrangement is open.</summary>
    [RelayCommand]
    private void OpenLevelGeneration()
    {
        if (_projectState.ActiveArrangement == null)
        {
            _projectState.SetMessage(_localization["Status_NoArrangementSelected"]);
            return;
        }

        Launch(() => _hostedDialogs.ShowAsync<ILevelGenerationScreen, bool>(), "level generation");
    }

    /// <summary>Tools ▸ Tuner, from the menu, the toolbar, the Tools popover and Play Mode's menu; says so
    /// instead when there is no guitar or bass arrangement to tune against.</summary>
    [RelayCommand]
    private void OpenTuner()
    {
        if (!DotCharter.Services.Audio.Tuning.LiveTuner.CanTuneAgainst(_projectState.ActiveArrangement))
        {
            _projectState.SetMessage(_localization["Tuner_NoArrangement"]);
            return;
        }

        Launch(() => _hostedDialogs.ShowAsync<ITunerScreen, bool>(), "tuner");
    }

    /// <summary>Arrangement ▸ Settings, from the menu, the Tracks popover and the palette; nothing to edit
    /// without an active arrangement.</summary>
    [RelayCommand]
    private void OpenArrangementSettings()
    {
        if (_projectState.ActiveArrangement == null)
        {
            return;
        }

        Launch(() => _hostedDialogs.ShowAsync(new ArrangementSettingsRequest()), "arrangement settings");
    }

    /// <summary>Edit ▸ History, the menu's and the undo/redo popovers' "full history".</summary>
    [RelayCommand]
    private void OpenHistory() => Launch(() => _hostedDialogs.ShowAsync<IHistoryScreen, bool>(), "history");

    /// <summary>Tools ▸ Linked measures; says so instead when no chart is loaded.</summary>
    [RelayCommand]
    private void OpenLinkGroups()
    {
        if (_projectState.ActiveProject == null)
        {
            _projectState.SetMessage(_localization["Status_NoChartLoaded"]);
            return;
        }

        Launch(() => _hostedDialogs.ShowAsync(new LinkGroupsRequest([])), "linked measures");
    }

    /// <summary>Edit ▸ Checkpoints.</summary>
    [RelayCommand]
    private void OpenCheckpoints() => Launch(() => _hostedDialogs.ShowAsync<ICheckpointsScreen, bool>(), "checkpoints");

    /// <summary>Edit ▸ Bookmarks…: the ten slots with their times.</summary>
    [RelayCommand]
    private void OpenBookmarks() => Launch(() => _hostedDialogs.ShowAsync<IBookmarksScreen, bool>(), "bookmarks");

    /// <summary>Tools ▸ Showlights; says so instead when no chart is loaded.</summary>
    [RelayCommand]
    private void OpenShowlights()
    {
        if (_projectState.ActiveProject == null)
        {
            _projectState.SetMessage(_localization["Status_NoChartLoaded"]);
            return;
        }

        Launch(ShowShowlightsAsync, "showlights");
    }

    private async Task ShowShowlightsAsync()
    {
        // The 3D preview's show-light chip shows with nothing active only while this editor is open.
        _projectState.EditorUi.IsShowlightsEditorOpen = true;
        try { await _hostedDialogs.ShowAsync<IShowlightsScreen, bool>(); }
        finally { _projectState.EditorUi.IsShowlightsEditorOpen = false; }
    }

    /// <summary>Tools ▸ Chart diff; there is nothing to compare without an open arrangement.</summary>
    [RelayCommand]
    private void OpenChartDiff()
    {
        if (_projectState.ActiveArrangement == null)
        {
            _projectState.SetMessage(_localization["Status_NoArrangementSelected"]);
            return;
        }

        Launch(() => _hostedDialogs.ShowAsync<IChartDiffScreen, bool>(), "chart diff");
    }

    /// <summary>Edit ▸ Pattern builder, from the menu, its keybind and the palette.</summary>
    [RelayCommand]
    private void OpenPatternBuilder() => Launch(() => _hostedDialogs.ShowAsync<IPatternBuilderScreen, bool>(), "pattern builder");

    /// <summary>Tools ▸ Calibrate latency, from the palette; Settings ▸ Audio and the first-run wizard open the same flow.</summary>
    [RelayCommand]
    private void OpenLatencyWizard() => Launch(() => _latencyCalibration.OpenAsync(), "latency calibration");

    /// <summary>Tools ▸ Tone designer, from the menu, the palette and the export dialog; nothing to design without a project.</summary>
    [RelayCommand]
    private void OpenToneDesigner()
    {
        if (_projectState.ActiveProject == null)
        {
            _projectState.SetMessage(_localization["Status_Export_NoProject"]);
            return;
        }

        Launch(() => _hostedDialogs.ShowAsync<IToneDesignerScreen, bool>(), "tone designer");
    }

    /// <summary>Play-only content is never exported: the export dialogs do not open.
    /// Their exporters (<c>RocksmithPackageBuilder</c>, <c>DcSongWriter</c>) refuse again.</summary>
    private bool RefusedAsPlayOnly()
    {
        if (!PlayOnlyExportGuard.IsPlayOnly(_projectState.ActiveProject)) return false;
        _projectState.SetMessage(_localization[PlayOnlyExportGuard.MessageKey], StatusSeverity.Error);
        return true;
    }

    /// <summary>File ▸ Export Rocksmith package, from the menu and the palette; nothing to export without a project.</summary>
    [RelayCommand]
    private void OpenRocksmithExport()
    {
        if (_projectState.ActiveProject == null)
        {
            _projectState.SetMessage(_localization["Status_Export_NoProject"]);
            return;
        }

        if (RefusedAsPlayOnly()) return;
        _chartEditor.CommitPendingEdits();
        Launch(() => _hostedDialogs.ShowAsync<IRocksmithExportScreen, bool>(), "Rocksmith export");
    }

    /// <summary>File ▸ Export ▸ DotCharter song, from the menu and the palette; nothing to package without a project.</summary>
    [RelayCommand]
    private void OpenDcSongExport()
    {
        if (_projectState.ActiveProject == null)
        {
            _projectState.SetMessage(_localization["Status_Export_NoProject"]);
            return;
        }

        if (RefusedAsPlayOnly()) return;
        _chartEditor.CommitPendingEdits();
        Launch(() => _hostedDialogs.ShowAsync<IDcSongExportScreen, bool>(), "DotCharter song export");
    }

    /// <summary>
    ///     Set by the window: brings the Inspector's Issues tab up. The Chart quality report dialog is gone; everything it showed lives on that tab.
    /// </summary>
    public Action? ShowChartIssues
    {
        get => _showChartIssues;
        set
        {
            _showChartIssues = value;
            _issuesDoor.Open = value; // the Rocksmith export's Show in Issues tab reaches the shell through the door
        }
    }

    /// <summary>Tools ▸ Review ▸ Chart quality report: opens the Issues tab, which lists every check; needs a chart.</summary>
    [RelayCommand]
    private void OpenQualityReport()
    {
        if (_projectState.ActiveProject == null)
        {
            _projectState.SetMessage(_localization["Status_NoChartLoaded"]);
            return;
        }

        ShowChartIssues?.Invoke();
    }

    /// <summary>Tools ▸ Batch lint.</summary>
    [RelayCommand]
    private void OpenBatchLint() => Launch(() => _hostedDialogs.ShowAsync<IBatchLintScreen, bool>(), "batch lint");

    /// <summary>Tempo map ▸ Set BPM for all measures; nothing to retime without a beat grid.</summary>
    [RelayCommand]
    private void OpenSetBpmDialog()
    {
        if (_chartEditor.Beats.Count == 0)
        {
            _logger.Info(AppLogCategory.Events, "Set-BPM-for-all-measures ignored; no beats present.");
            return;
        }

        _logger.Info(AppLogCategory.Events, "Opening Set-BPM-for-all-measures dialog.");
        Launch(() => _hostedDialogs.ShowAsync<ISetBpmScreen, bool>(), "set BPM");
    }

    /// <summary>Tempo map ▸ Align beat grid to notes; needs a grid of two beats, and refuses a locked one up front.</summary>
    [RelayCommand]
    private void OpenAlignBeatGridDialog()
    {
        var project = _projectState.ActiveProject;
        if (project == null || project.Beats.Count < 2)
        {
            _projectState.SetMessage(_localization["Status_NoChartLoaded"]);
            return;
        }

        if (project.IsBeatMapLocked)
        {
            _projectState.SetMessage(_localization["Status_BeatMapLocked"]);
            return;
        }

        _logger.Info(AppLogCategory.Events, "Opening Align-beat-grid dialog.");
        Launch(() => _hostedDialogs.ShowAsync<IAlignBeatGridScreen, bool>(), "align beat grid");
    }

    /// <summary>Tempo map ▸ Estimate BPM; needs a chart, an unlocked map and the song audio, and says which is missing.</summary>
    [RelayCommand]
    private void OpenEstimateBpmDialog()
    {
        var reason = EstimateBpmDisabledReason();
        if (reason != null)
        {
            _projectState.SetMessage(reason);
            return;
        }

        _logger.Info(AppLogCategory.Events, "Opening Estimate-BPM dialog.");
        Launch(() => _hostedDialogs.ShowAsync<IEstimateBpmScreen, bool>(), "estimate BPM");
    }

    /// <summary>Why Estimate BPM cannot run now, or null when it can.</summary>
    public string? EstimateBpmDisabledReason()
    {
        var project = _projectState.ActiveProject;
        if (project == null || project.Beats.Count < 2) return _localization["Status_NoChartLoaded"];
        if (project.IsBeatMapLocked) return _localization["Status_BeatMapLocked"];
        return string.IsNullOrEmpty(_playback.CurrentAudioPath) ? _localization["Status_EstimateBpmNeedsAudio"] : null;
    }

    /// <summary>Tempo map ▸ Fit the tab's tempo to the recording; needs a grid of two beats, an unlocked map
    ///     and notes to match against the audio (the menu row and the palette show the same reason up front).</summary>
    [RelayCommand]
    private void OpenAlignToRecordingDialog()
    {
        var project = _projectState.ActiveProject;
        if (project == null || project.Beats.Count < 2)
        {
            _projectState.SetMessage(_localization["Status_NoChartLoaded"]);
            return;
        }

        if (project.IsBeatMapLocked)
        {
            _projectState.SetMessage(_localization["Status_BeatMapLocked"]);
            return;
        }

        if (!RecordingAlignmentPlan.HasNotes(project))
        {
            _projectState.SetMessage(_localization[EditingAvailability.KeyAlignNeedsNotes], StatusSeverity.Warning);
            return;
        }

        _logger.Info(AppLogCategory.Events, "Opening Align-to-recording dialog.");
        Launch(() => _hostedDialogs.ShowAsync<IAlignToRecordingScreen, bool>(), "align to recording");
    }

    /// <summary>Tempo map ▸ Start &amp; end…: the one dialog over the five operations below, opened on the first.</summary>
    [RelayCommand]
    private void OpenStartEndDialog() => ShowTempoMapOperation(TempoMapOperation.DefaultStartSilence);

    [RelayCommand]
    private void OpenSetDefaultStartSilenceDialog() => ShowTempoMapOperation(TempoMapOperation.DefaultStartSilence);

    [RelayCommand]
    private void OpenAddSilenceAtStartDialog() => ShowTempoMapOperation(TempoMapOperation.AddSilenceAtStart);

    [RelayCommand]
    private void OpenAddSilenceAtEndDialog() => ShowTempoMapOperation(TempoMapOperation.AddSilenceAtEnd);

    [RelayCommand]
    private void OpenAddMeasuresAtStartDialog() => ShowTempoMapOperation(TempoMapOperation.AddMeasuresAtStart);

    [RelayCommand]
    private void OpenAddMeasuresAtEndDialog() => ShowTempoMapOperation(TempoMapOperation.AddMeasuresAtEnd);

    /// <summary>Tempo map ▸ Insert measures at playhead…: the dialog alone, on that verb.</summary>
    [RelayCommand]
    private void OpenInsertMeasuresAtPlayheadDialog() => ShowTempoMapOperation(TempoMapOperation.InsertMeasuresAtPlayhead);

    /// <summary>Tempo map ▸ Sync arrangements.</summary>
    [RelayCommand]
    private void OpenArrangementSyncDialog() => Launch(() => _hostedDialogs.ShowAsync<IArrangementSyncScreen, bool>(), "arrangement sync");

    private void ShowTempoMapOperation(TempoMapOperation operation) => Launch(() => _hostedDialogs.ShowAsync(new TempoMapOperationRequest(operation)), "tempo-map operation");

    /// <summary>Opens a dialog nobody awaits. With no host attached it cannot open at all; that is logged too.</summary>
    private void Launch(Func<Task> show, string what) => DialogLaunch.Run(_logger, show, what);
}
