namespace DotCharter.Services.Session;

public interface IProjectSessionService : IDisposable
{
    AppConfig AppConfig { get; }

    /// <summary>True while the session holds edits that are not on disk.
    /// Derived from the undo history (see <c>ProjectDirtyTracker</c>),
    /// so undoing back to the last-saved state reads clean again.</summary>
    bool IsProjectDirty { get; }

    /// <summary>Raised when <see cref="IsProjectDirty" /> flips.</summary>
    event Action? ProjectDirtyChanged;

    /// <summary>What the chart editor has open right now, named as the window title
    /// names it (<see cref="ProjectDisplayName" />: "Artist - Title"), or empty when
    /// nothing named is loaded. A song played from the library is loaded here too,
    /// so this is not the most recently SAVED project.</summary>
    string LoadedChartName => "";

    /// <summary>
    ///     Asks the user whether it is alright to REPLACE the open project.
    ///     Returns true to proceed, false to abandon the operation.
    ///     <para>
    ///         A delegate rather than an injected dialog service on purpose: the
    ///         prompt lives on <c>IAppDialogService</c>, which is constructed from
    ///         thirty view models that in turn reach back here, so injecting it
    ///         would close a dependency cycle. The shell assigns this once.
    ///     </para>
    ///     <para>
    ///         Null means "no shell is listening" — every path then behaves
    ///         exactly as it did before, which is what keeps tests and headless
    ///         hosts working.
    ///     </para>
    /// </summary>
    Func<Task<bool>>? ConfirmDiscardAsync { get; set; }

    /// <summary>
    ///     True when the caller may replace the open project — clean, or the user
    ///     said so. Every path that calls <c>ApplyProject(resetHistory: true)</c>
    ///     must go through this: that call clears the undo history, so the
    ///     previous project's edits become unreachable the instant it runs.
    /// </summary>
    Task<bool> MayReplaceOpenProjectAsync();

    /// <summary>
    ///     Re-baselines the unsaved-changes flag: the session state now IS the
    ///     state on disk (or in the library it was loaded from).
    ///     <para>
    ///         <b>Every path that calls <c>ApplyProject(resetHistory: true)</c> has
    ///         to decide between this and <see cref="MarkProjectDirty" />, and it
    ///         is not optional.</b> <c>HistoryService.Clear</c> mints a NEW root
    ///         node id, and the dirty flag is "current node id ≠ the baseline id" —
    ///         so a replace that says nothing leaves a baseline no node can ever
    ///         match again and the session reads dirty forever. That shipped: five
    ///         of the six replace paths were silent, so merely loading a song from
    ///         the library and closing raised "The project has unsaved changes",
    ///         and the recovery snapshotter (which watches the same flag) wrote a
    ///         snapshot for a chart nobody had touched.
    ///     </para>
    /// </summary>
    void MarkProjectClean();

    /// <summary>
    ///     Marks the session dirty until the next save — for a project that is NOT
    ///     on disk in this state, whatever the undo history says. Two replace paths
    ///     need it: a new project (never written) and a checkpoint restore (the
    ///     restored graph deliberately differs from the <c>.dcproj</c>). It is also
    ///     how <see cref="AttachAudioAsync" /> records a change the history cannot
    ///     represent.
    /// </summary>
    void MarkProjectDirty();

    void Initialize();
    void LoadAppConfig();
    void SaveAppConfig();
    void FreeAudio();
    Task LoadFile(string filePath);
    Task LoadAudioFile(string filePath, bool generateBeats = true, bool preferMemoryPlayback = false);

    /// <summary>
    ///     ATTACHES <paramref name="sourcePath" /> to the open project as its song
    ///     audio, so the reference survives save/reopen — as opposed to
    ///     <see cref="LoadAudioFile" />, which only auditions a file through the
    ///     engine and leaves <see cref="ChartProject.AudioFileName" /> untouched.
    ///     Copies the file into the project folder (mirroring the new-project
    ///     flow), sets both the project's and the session's audio reference, and
    ///     marks the project dirty. Returns false when the file is unusable or the
    ///     user declined replacing existing audio.
    /// </summary>
    Task<bool> AttachAudioAsync(string sourcePath);

    /// <summary>
    /// Loads a cached silent WAV of <paramref name="projectLengthMs"/> into the
    /// transport so an audio-less chart still has a working playhead, scrubbing,
    /// count-in and Play Mode. The silent file is loaded into the engine only —
    /// it is NEVER stored as the song audio identity
    /// (<c>ProjectState.CurrentAudioFileName</c>), so stem separation and
    /// other audio-dependent features stay disabled for an audio-less chart.
    /// </summary>
    Task LoadSilentTransportAsync(double projectLengthMs);
    Task OpenProject();

    /// <summary>Saves to the session's project path; a session without one
    /// (fresh/default project, PSARC import) routes through the Save As picker
    /// instead of silently writing a relative "project.dcproj". Returns false
    /// when the user canceled the picker or the save failed.</summary>
    Task<bool> SaveProjectAsync();

    /// <summary>Always shows the save-file picker and remembers the chosen
    /// path as the session's project path. Returns false on cancel/failure.</summary>
    Task<bool> SaveProjectAsAsync();

    /// <summary>
    ///     The persisted "Open Recent" list, most recent first, with entries
    ///     whose files do not exist already dropped. Maintained by the ONE
    ///     open/save funnel: a successful project open or save touches it.
    /// </summary>
    IReadOnlyList<RecentProjectEntry> GetRecentProjects();

    /// <summary>
    ///     The persisted list AS RECORDED, with no filesystem probe at all.
    ///     <para>
    ///         For the boot path: <c>MainWindowViewModel</c>'s constructor runs
    ///         before <c>new MainWindow()</c>, and <see cref="GetRecentProjects" />
    ///         does one <c>File.Exists</c> per entry (ten of them) on the UI
    ///         thread there. On a local SSD that is under a millisecond, but an
    ///         MRU row is routinely a cloud-synced or network path, where a
    ///         single <c>File.Exists</c> blocks for the share's timeout — before
    ///         the window exists. Render these first, then replace them with
    ///         the pruned list from a worker.
    ///     </para>
    /// </summary>
    /// <remarks>Default-implemented so the many test stubs of this interface
    /// keep compiling with only the synchronous member; the real session
    /// service overrides both.</remarks>
    IReadOnlyList<RecentProjectEntry> GetRecentProjectsUnverified() => GetRecentProjects();

    /// <summary>The pruned list, with every <c>File.Exists</c> on a worker
    /// thread. The async half of <see cref="GetRecentProjects" />.</summary>
    Task<IReadOnlyList<RecentProjectEntry>> GetRecentProjectsAsync() => Task.FromResult(GetRecentProjects());

    /// <summary>Raised (on whatever thread recorded the entry) after the
    /// recent-projects list changed.</summary>
    event Action? RecentProjectsChanged;
}
