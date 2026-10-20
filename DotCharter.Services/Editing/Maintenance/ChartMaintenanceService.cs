// ChartMaintenanceService root: the IChartMaintenanceService contract, the injected services, the constructor and the shared snapshot-undo actions.
using DotCharter.Services.Editing.Sync;
using DotCharter.Services.Editing.Fixing;
using DotCharter.Services.Editing.Phrases;
using DotCharter.Services.Editing.Refret;
using DotCharter.Services.Parsing.Midi;
using DotCharter.Services.Editing.Improvement;
using DotCharter.Services.State;
using DotCharter.Services.Session;
using DotCharter.Services.Import.Audio;
using DotCharter.Localization;

namespace DotCharter.Services.Editing.Maintenance;

public interface IChartMaintenanceService
{
    void ImproveChart();
    void RemoveDoubleStopChordNames();

    /// <summary>
    ///     Runs <paramref name="mutate" /> against the LIVE project and commits
    ///     whatever it changed as ONE undo node.
    ///     <para>
    ///         For bulk operations that already write straight into the project
    ///         graph through other services — the tab importers above all —
    ///         restructuring them to mutate a clone would be a large and risky
    ///         change, while snapshotting AROUND them is exact: the before/after
    ///         pair captures everything, whatever they touched.
    ///     </para>
    ///     <para>
    ///         Returns false when there is no project. Exceptions from
    ///         <paramref name="mutate" /> propagate — the caller reports its own
    ///         failure — but no history node is pushed for a run that threw.
    ///     </para>
    /// </summary>
    bool RunAsSingleUndoStep(Action mutate);

    /// <summary>Deletes the active arrangement as one undo node. Refuses to delete the last one.</summary>
    void DeleteActiveArrangement();

    /// <summary>Deletes the active vocal track as one undo node.</summary>
    void DeleteActiveVocalTrack();
    void AutoFixChart();
    void SquashLevels();

    /// <summary>
    /// "Apply DLC Builder fixes" (the chart's context menu): runs exactly what every Rocksmith
    ///     export runs on its copy — DLC Builder's fixes, then dynamic difficulty for an arrangement
    ///     with one level (<see cref="Export.DynamicDifficulty.DlcBuilderBuildSteps" />) — on
    ///     <paramref name="arrangements" /> (default: the active one) IN the project, as ONE undo
    ///     node. The number of arrangements it changed; the status line says what happened.
    /// </summary>
    int ApplyDlcBuilderFixes(IReadOnlyList<Arrangement>? arrangements = null);

    /// <summary>Runs the chart verb that repairs an export finding on <paramref name="arrangement" />
    /// as ONE undo node; the number of changes. A tone fix is the Tone designer, not a chart edit.</summary>
    int FixExportFinding(Export.Validation.ExportFixKind kind, Arrangement arrangement);

    /// <summary>Applies <see cref="Tones.ToneKeyRepair" />'s rename for <paramref name="undefinedKey" /> as ONE
    /// undo node. Null, and nothing changed, when the project leaves the repair in doubt.</summary>
    Tones.ToneKeyRepairProposal? RepairUndefinedToneKey(string undefinedKey);

    // Charting power tools — see ChartMaintenanceService.PowerTools.cs.
    bool GenerateLevels(int levelCount);

    /// <summary>
    ///     <see cref="GenerateLevels" /> with the one expensive phase on a worker
    ///     and a per-level report. The clone and the history node still land on
    ///     the UI thread — see the remarks on the implementation for why that
    ///     split is the safe one. Still ONE undo node.
    /// </summary>
    Task<bool> GenerateLevelsAsync(int levelCount, IProgress<LevelGenerationProgress>? progress = null);
    int PropagatePhraseAtPlayhead();

    /// <summary>
    ///     Copies the active arrangement's section / phrase markers onto every
    ///     other arrangement. MERGE, not replace — safe to run twice.
    /// </summary>
    int CopyStructureToOtherArrangements();

    /// <summary>
    ///     Carries one arrangement's charting work onto another wherever the two
    ///     play the same sounds. OVERWRITES the target's shared sounds; one undo
    ///     step. See <see cref="ArrangementSoundSync" /> for the matching rule.
    /// </summary>
    ArrangementSyncSummary SyncArrangement(Arrangement source, Arrangement target);
    bool ApplyMidiDraft(MidiChartDraft draft, bool replaceBeatGrid);
    bool Refret();

    void GenerateFhp();
    void GenerateFhpForSelection();

    /// <summary>Covers every chord no hand shape covers, on every level of the active
    /// arrangement, as one undo step.</summary>
    void GenerateHandShapesForArrangement();
    void ApplyHandShapeTemplateToSelectedChords();
    void SetDefaultStartSilence(double seconds);
    Task AddSilenceAtBeginningAsync(double seconds);
    Task AddSilenceAtEndAsync(double seconds);
    /// <summary>Prepends whole measures before the first beat and shifts the
    /// chart. Measures, not beats: this moves EVERYTHING, so a partial bar puts
    /// every downbeat in the song on an off-beat.</summary>
    void AddMeasuresAtStart(int measureCount);

    /// <summary>Appends whole measures after the last beat so the RS END
    /// phrase has a bar of its own. Shifts nothing.</summary>
    void AddMeasuresAtEnd(int measureCount);
    void InsertBeatAtPlayhead();
    void RemoveBeatAtPlayhead();

    /// <summary>Tempo map ▸ Align beat grid to notes: moves every beat by
    /// <paramref name="offsetMs" /> while every timed element keeps its absolute
    /// time, as one undo step. False (and a status message) when refused — no
    /// chart, a locked beat map, nothing to move.</summary>
    bool AlignBeatGridToNotes(double offsetMs);

    /// <summary>Flips <see cref="ChartProject.IsBeatMapLocked" /> and returns the
    /// new state. Marks the project dirty — the flag is persisted, so the toggle
    /// is a document change the undo history cannot represent.</summary>
    bool ToggleBeatMapLock();
    void InsertVocalAtPlayhead();
    void ToggleSelectedChordSplit();
    void ToggleSelectedChordOnlyBox();
    void ToggleSelectedChordPassOtherNotes();
    void ConvertSelectedNotesToPowerChord(bool big);

    /// <summary>
    ///     Switches every two-fret octave shape of the ACTIVE arrangement between
    /// <c>1·4</c> and <c>1·3</c> (<see cref="TwoFretPairFingering" />) as
    ///     one undo node, and reports how many chords changed.
    /// </summary>
    void ToggleTwoFretOctaveFingering();

    /// <summary>
    ///     The same for every two-string power chord (root + fifth on adjacent
    /// strings, the fifth two frets up): first press <c>1·4</c>, the
    ///     next back to <c>1·3</c>.
    /// </summary>
    void TogglePowerChordFingering();
    void MergeSelectedNotesIntoChords();
    /// <summary>
    /// "Muted strums → muted chord": every fret-hand-muted single note after a chord, on
    ///     that chord's strings, becomes a muted copy of the chord on its template, and the chord's
    ///     hand shape grows over them (<see cref="ChordShapes.MutedStrumChords" />) — ONE undo node.
    ///     The selection, or with nothing selected the phrase under the playhead.
    /// </summary>
    void ConvertMutedStrumsToChords();
    void SplitSelectedChordsIntoNotes();

    /// <summary>EOF's Ctrl+G, ghost gems: <see cref="ChordShapes.GhostGemCommands.Toggle" />.</summary>
    void ToggleGhostGems();

    /// <summary>The tapped-harmonic shortcut: <see cref="ChordShapes.GhostGemCommands.TappedHarmonic" />.</summary>
    void MakeTappedHarmonic();
}

public sealed partial class ChartMaintenanceService : IChartMaintenanceService
{
    private readonly IChartEditorSurface _chartEditor;
    private readonly IChartImprovementService _chartImprovementService;
    private readonly IConfigurationService _configService;
    private readonly IFretAnchorGenerationService _fretAnchorGenerationService;
    private readonly IHistoryService _historyService;
    private readonly ILevelGenerationService _levelGenerationService;
    private readonly ILevelSquashService _levelSquashService;
    private readonly IAppLogger _logger;
    private readonly IPhraseIterationPropagationService _phrasePropagationService;
    private readonly IProjectSessionService _projectSession;
    private readonly ProjectState _projectState;
    private readonly IRefretService _refretService;

    private readonly IArrangementAutoFixService _autoFixService;
    private readonly ILocalizationService _localization;

    /// <summary>Tempo map ▸ Add silence, with its audio reload.</summary>
    private readonly ProjectSilencePadder _silencePadder;

    /// <summary>Notes to chords and back, chord flags, two-fret fingering and muted strums.</summary>
    private readonly ChordConversionCommands _chordConversion;

    /// <summary>Generate FHP for the arrangement and for the selection.</summary>
    private readonly FhpGenerationCommands _fhpGeneration;

    public ChartMaintenanceService(
        IConfigurationService configService,
        IChartImprovementService chartImprovementService,
        ILevelSquashService levelSquashService,
        ILevelGenerationService levelGenerationService,
        IPhraseIterationPropagationService phrasePropagationService,
        IFretAnchorGenerationService fretAnchorGenerationService,
        IProjectSessionService projectSession,
        IHistoryService historyService,
        IAppLogger logger,
        ProjectState projectState,
        IChartEditorSurface chartEditor,
        IArrangementAutoFixService autoFixService,
        IRefretService refretService,
        ILocalizationService localization,
        IAudioSilenceSplicer? silenceSplicer = null)
    {
        _configService = configService;
        _chartImprovementService = chartImprovementService;
        _levelSquashService = levelSquashService;
        _levelGenerationService = levelGenerationService;
        _phrasePropagationService = phrasePropagationService;
        _fretAnchorGenerationService = fretAnchorGenerationService;
        _projectSession = projectSession;
        _historyService = historyService;
        _logger = logger;
        _projectState = projectState;
        _chartEditor = chartEditor;
        _autoFixService = autoFixService;
        _refretService = refretService;
        _localization = localization;
        _chordConversion = new ChordConversionCommands(projectState, chartEditor, historyService, localization, logger);
        _fhpGeneration = new FhpGenerationCommands(projectState, chartEditor, historyService, localization, logger,
            fretAnchorGenerationService);
        _silencePadder = new ProjectSilencePadder(projectState, projectSession, historyService, logger, localization,
            silenceSplicer, (before, after) => new ProjectSnapshotAction(this, before, after, GetActiveArrangementIndex(),
                GetActiveLevelIndex(), GetActiveVocalTrackIndex()));
    }

    private string FormatPosition(FractionalPosition position)
        => position.FormatWithMs(_chartEditor.Beats);

    private int GetActiveArrangementIndex()
    {
        var project = _projectState.ActiveProject;
        var arrangement = _projectState.ActiveArrangement;
        return project != null && arrangement != null ? project.Arrangements.IndexOf(arrangement) : -1;
    }

    private int GetActiveLevelIndex()
    {
        var arrangement = _projectState.ActiveArrangement;
        var level = _projectState.ActiveLevel;
        return arrangement != null && level != null ? arrangement.Levels.IndexOf(level) : -1;
    }

    private int GetActiveVocalTrackIndex()
    {
        var project = _projectState.ActiveProject;
        var vocalTrack = _chartEditor.ActiveVocalTrack;
        return project != null && vocalTrack != null ? project.VocalTracks.IndexOf(vocalTrack) : -1;
    }

    /// <summary>
    ///     Puts a whole-project snapshot back on screen from inside an undoable
    ///     action.
    /// </summary>
    /// <remarks>
    ///     This must not clone the snapshot and hand the fresh graph to
    ///     <c>ApplyProject</c>: that would detach the ENTIRE undo history, because every
    ///     other action holds live references — <c>AddNotesAction</c> the
    ///     <c>ObservableCollection</c>, <c>MutateNotesAction</c> the individual
    ///     <c>GuitarNote</c> instances — so after Refret / level generation / a
    ///     tempo tool, Ctrl+Z would hit the orphaned graph and do nothing the user
    ///     could see, and redoing the tool would throw away every edit made after it.
    ///     <para>
    ///         <c>resetHistory: true</c> is not available here — this runs INSIDE
    ///         a history action, so clearing the stack would destroy the very
    ///         history being replayed. Instead the snapshot is reconciled INTO
    ///         the live graph (<see cref="ChartProjectRestorer" />), which keeps
    ///         the collection and element instances the captured references
    ///         point at. <c>ApplyProject</c> is therefore not called on
    ///         this path at all, and its documented <c>resetHistory</c> contract
    ///         is untouched.
    ///     </para>
    /// </remarks>
    private void RestoreProjectSnapshot(
        ChartProject snapshot,
        int arrangementIndex,
        int levelIndex,
        int vocalTrackIndex)
    {
        var project = _projectState.ActiveProject;
        if (project == null)
        {
            // No live graph to reconcile into (nothing is open) — the swap is
            // all there is, and no history can be pointing anywhere yet.
            project = CloneProject(snapshot);
            _chartEditor.ApplyProject(project);
        }
        else
        {
            ChartProjectRestorer.Restore(project, snapshot);
        }

        ReresolveViewState(project, arrangementIndex, levelIndex, vocalTrackIndex);
    }

    /// <summary>
    /// The arrangement-scoped sibling of <see cref="RestoreProjectSnapshot" />: for a
    ///     tool that changes ONE arrangement and nothing else, the step holds that arrangement
    ///     twice instead of the whole project twice, so it costs a fraction of the history's byte
    ///     budget. Reconciled into the live arrangement (its instance survives, like the project's
    ///     does on the whole-project path), then the same view-state re-resolve.
    /// </summary>
    private void RestoreArrangementSnapshot(Arrangement snapshot, int arrangementIndex, int levelIndex,
        int vocalTrackIndex)
    {
        var project = _projectState.ActiveProject;
        if (project == null || arrangementIndex < 0 || arrangementIndex >= project.Arrangements.Count)
        {
            // The history only replays this step against the graph it was recorded on, whose
            // arrangement list the tool did not change; anything else is a bug, not a state.
            throw new InvalidOperationException(
                $"Arrangement snapshot {arrangementIndex} has no arrangement to restore into.");
        }

        ChartProjectRestorer.RestoreArrangement(project.Arrangements[arrangementIndex], snapshot);
        ReresolveViewState(project, arrangementIndex, levelIndex, vocalTrackIndex);
    }

    private void ReresolveViewState(ChartProject project, int arrangementIndex, int levelIndex, int vocalTrackIndex)
    {
        // WHICH ARRANGEMENT IS ON SCREEN IS VIEW STATE, NOT CHART STATE.
        // The recorded indices are a FALLBACK, used only when what the charter
        // is currently looking at did not survive the restore. Applying them
        // unconditionally teleported the user: switch Lead → Rhythm, press
        // Ctrl+Z, and undoing a tempo tool made minutes earlier on Lead threw
        // the view back to Lead — an arrangement switch is not an undo step,
        // and undo must never make one.
        //
        // The re-resolve below is a separate and necessary job: a SHRINKING
        // snapshot (undoing level generation, undoing a MIDI draft) drops the
        // instances the reconcile removes, and they would otherwise stay
        // selected as detached objects that every subsequent edit silently
        // writes into.
        var arrangement = _projectState.ActiveArrangement;
        if (arrangement == null || !project.Arrangements.Contains(arrangement))
        {
            arrangement = arrangementIndex >= 0 && arrangementIndex < project.Arrangements.Count
                ? project.Arrangements[arrangementIndex]
                : project.Arrangements.FirstOrDefault();
            _projectState.ActiveArrangement = arrangement;
        }

        if (arrangement != null)
        {
            var level = _projectState.ActiveLevel;
            if (level == null || !arrangement.Levels.Contains(level))
            {
                level = levelIndex >= 0 && levelIndex < arrangement.Levels.Count
                    ? arrangement.Levels[levelIndex]
                    : arrangement.Levels.FirstOrDefault();
            }

            if (level != null) _projectState.ActiveLevel = level;
        }

        var vocalTrack = _chartEditor.ActiveVocalTrack;
        if (vocalTrack == null || !project.VocalTracks.Contains(vocalTrack))
        {
            _chartEditor.ActiveVocalTrack = vocalTrackIndex >= 0 && vocalTrackIndex < project.VocalTracks.Count
                ? project.VocalTracks[vocalTrackIndex]
                : project.VocalTracks.FirstOrDefault();
        }

        _chartEditor.Selection.ClearAll();
        _chartEditor.RequestRedraw();
    }

    // Snapshotting for undo lives in the shared cloner — it also serves the
    // save pipeline, and keeping one copy means new project collections
    // can't silently vanish from undo history (Annotations and
    // PhraseIterations both did exactly that while this class had its own).
    private static ChartProject CloneProject(ChartProject source) => ChartProjectCloner.Clone(source);

    private string L(string key) => _localization[key];

    private string L(string key, params object[] args) => _localization.Format(key, args);

    /// <summary>The arrangement the ACTIVE arrangement index names in the live project, or null.</summary>
    private Arrangement? LiveArrangementAt(ChartProject project, int arrangementIndex) =>
        arrangementIndex >= 0 && arrangementIndex < project.Arrangements.Count
            ? project.Arrangements[arrangementIndex]
            : null;

    /// <summary>
    ///     The ONE undo node of a whole-arrangement level operation (Squash levels, Generate
    ///     levels): the arrangement snapshot, plus the chord templates the operation left with no
    /// user (<see cref="LevelTemplateOrphans" /> holds the rule and the renumbering).
    ///     <para>
    ///         <see cref="LevelTemplateOrphans.Apply" /> writes the final ids into
    ///         <paramref name="after" /> and PARKS each declared orphan in its slot; the live
    ///         instances it returns are removed after the restore, by instance
    ///         (<see cref="RemoveChordTemplatesAction" />), and come back to their own slots on
    ///         undo — never pulled out of the snapshot, whose index-wise reconcile would shift
    ///         every later template's values into a different live instance.
    ///     </para>
    /// </summary>
    private IChartAction LevelOperationAction(Arrangement live, int arrangementIndex, Arrangement before,
        Arrangement after, LevelTemplateOrphans templates, int levelIndex, int vocalTrackIndex)
    {
        var stale = templates.Apply(after, live.ChordTemplates);
        IChartAction snapshot = new ArrangementSnapshotAction(this, arrangementIndex, before, after, levelIndex,
            vocalTrackIndex);
        if (stale.Count == 0) return snapshot;

        _logger.Info(AppLogCategory.Editing,
            $"Level operation on '{live.Name}' drops {stale.Count} chord template(s) it left with no user.");
        return new CompositeAction([snapshot, new RemoveChordTemplatesAction(live.ChordTemplates, stale)]);
    }

    /// <summary>See <see cref="RestoreArrangementSnapshot" />. Only for a tool that changes nothing
    /// outside the one arrangement: <c>ArrangementSnapshotUndoTests</c> deep-compares the whole
    /// project across do/undo/redo for each tool that records one.</summary>
    private sealed class ArrangementSnapshotAction(
        ChartMaintenanceService service,
        int arrangementIndex,
        Arrangement oldArrangement,
        Arrangement newArrangement,
        int levelIndex,
        int vocalTrackIndex)
        : IChartAction
    {
        public bool HasEffect => true;

        public long EstimatedBytes { get; } =
            ChartProjectFootprint.EstimateBytes(oldArrangement) + ChartProjectFootprint.EstimateBytes(newArrangement);

        public void Execute() =>
            service.RestoreArrangementSnapshot(newArrangement, arrangementIndex, levelIndex, vocalTrackIndex);

        public void Undo() =>
            service.RestoreArrangementSnapshot(oldArrangement, arrangementIndex, levelIndex, vocalTrackIndex);
    }

    private sealed class ProjectSnapshotAction(
        ChartMaintenanceService service,
        ChartProject oldProject,
        ChartProject newProject,
        int arrangementIndex,
        int levelIndex,
        int vocalTrackIndex)
        : IChartAction
    {
        public bool HasEffect => true;

        /// <summary>Both clones stay alive for as long as the step is in the history.</summary>
        public long EstimatedBytes { get; } =
            ChartProjectFootprint.EstimateBytes(oldProject) + ChartProjectFootprint.EstimateBytes(newProject);

        public void Execute() => service.RestoreProjectSnapshot(newProject, arrangementIndex, levelIndex, vocalTrackIndex);

        public void Undo() => service.RestoreProjectSnapshot(oldProject, arrangementIndex, levelIndex, vocalTrackIndex);
    }
}
