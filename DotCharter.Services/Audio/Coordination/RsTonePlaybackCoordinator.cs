// RsTonePlaybackCoordinator root: ActiveToneInfo and RsToneDriver, the song's tone conversion, the run and editor-preview drivers and the apply worker that switches the rig.
using System.ComponentModel;
using System.Threading.Channels;
using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Services.Library;
using DotCharter.Tone.Audio.Dsp.Tones;
using DotCharter.Tone.Parsing.Psarc;
using ProjectState = DotCharter.Services.State.ProjectState;

namespace DotCharter.Services.Audio.Coordination;

/// <summary>Which tone is live and whether it was reproduced 1:1, for an optional
/// HUD badge. Raised on the apply thread — subscribers marshal to the UI.</summary>
/// <param name="ToneName">The tone as the chart (or the edit) named it.</param>
/// <param name="WasApproximated">The conversion approximated or skipped gear.</param>
/// <param name="SongIdentity">The prepared SONG the tone belongs to, or null when the
/// tone is the open project's own (the editor preview, an own chart) — the key a
/// song tone's saved data (edits, the chart synth's instrument override) uses.</param>
/// <param name="PackageToneKey">The package's own key for the tone (what that data is
/// saved under), or null when it is not a package tone.</param>
public sealed record ActiveToneInfo(
    string ToneName, bool WasApproximated, string? SongIdentity = null, string? PackageToneKey = null);

/// <summary>
///     Who currently owns the DSP tone rig. There is exactly ONE owner at a time
///     and exactly one snapshot of the player's manual chain, which is the whole
///     reason the editor's tone preview lives in this coordinator instead of a
///     second one: two owners would each snapshot "the manual chain" and the
///     second would save the FIRST one's tone rig as if the player had built it.
/// </summary>
public enum RsToneDriver
{
    /// <summary>Nothing is driving; the chain is the player's own board.</summary>
    None,

    /// <summary>A Play Mode run (<see cref="RsTonePlaybackCoordinator.BeginRun" />).</summary>
    PlayModeRun,

    /// <summary>The chart editor's tone preview
    /// (<see cref="RsTonePlaybackCoordinator.BeginPreview" />).</summary>
    EditorPreview,

    /// <summary>The player editing ONE of a song's tones in the rig before the run
    /// (<see cref="RsTonePlaybackCoordinator.BeginToneEdit" />).</summary>
    SongToneEdit
}

/// <summary>
/// Drives the in-house tone-rig from a song's Rocksmith in-game tones during a
/// Play Mode run (Phase 2 of the "Use in-game tones" feature). Sibling of
/// <see cref="AudioPlaybackCoordinator"/>.
///
/// <para><b>How it hooks into playback.</b> <see cref="PrepareForSong"/> (called
/// on the pre-play TuningCheck screen) reads the song's tone JSON once, converts
/// every tone the arrangement references to a cached <see cref="DcRigPreset"/> +
/// <see cref="ConversionReport"/>, and returns a badge summary — it applies
/// NOTHING. <see cref="BeginRun"/> (called from <c>PlayModeViewModel.BeginGameplay</c>
/// when the toggle is on) snapshots the player's manual chain, applies the tone
/// active at the current playhead (pre-roll, so the rig is armed before the first
/// note), then subscribes to <c>ProjectState.CurrentTimeMs</c> — the SAME
/// coalesced ≤60 Hz UI-thread playhead <see cref="AudioPlaybackCoordinator"/>
/// already publishes, so there is NO second 250 Hz timer. On each tick the pure
/// <see cref="RsToneScanner"/> reports whether the active tone changed (cheap,
/// allocation-free on the no-change path); a change is handed to a single-consumer
/// latest-wins worker that runs <see cref="DcRigApplier"/> OFF the UI thread.
/// <see cref="EndRun"/> stops the worker and restores the manual chain. The
/// player's VST chain is never touched (only the dcrig nodes).</para>
///
/// <para><b>The chart editor's tone preview (0.56) is the SAME machinery with a
/// different tone SOURCE.</b> <see cref="PrepareForProject"/> converts the open
/// project's own authored <see cref="ChartProject.ToneDefinitions"/> instead of a
/// package's tone JSON, and <see cref="BeginPreview"/>/<see cref="EndPreview"/>
/// drive and restore through the identical snapshot/worker/scanner path. It is
/// deliberately NOT a second coordinator: there is one manual-chain snapshot and
/// one <see cref="_runGeneration"/>, so a Play Mode run taking over from a live
/// preview (and the still-pending restore of either) is handled by machinery that
/// already exists. <see cref="Driver"/> names the single owner.</para>
///
/// Never throws; a tone with no converted preset keeps the current rig (logged).
/// </summary>
public sealed partial class RsTonePlaybackCoordinator
{
    private readonly IDspChainService _chain;
    private readonly DcRigApplier _applier;
    private readonly ProjectState _projectState;
    private readonly IConfigurationService _config;
    private readonly DotCharter.Tone.Audio.Dsp.Presets.IDspTonePresetService? _tonePresets;
    private readonly IAppLogger? _logger;
    private readonly IGameplayToneHud? _toneHud;
    private readonly RsToneConverter _converter = new();
    private readonly PropertyChangedEventHandler _onProjectStateChanged;
    private readonly object _applyGate = new();

    // Test seam: how tone definitions are read for a package path. Defaults to the
    // plugin's importer (PackageTones; none in a build without one: no package tones);
    // a unit test overrides it to inject tone JSON without a package.
    internal Func<string, IReadOnlyList<PsarcToneDefinition>> ToneDefinitionSource =
        DotCharter.Services.Plugins.PackageTones.Read;

    // Test seam: how a song's EDITED tones are read, by song identity.
    internal Func<string, IReadOnlyDictionary<string, DcRigPreset>> SongTonesSource =
        identity => Services.Audio.Dsp.Tones.SongToneEditStore.Default.Read(identity);

    // The package's conversions WITH the prepared song's edits laid over them
    // (OverlaySongEdits, ToneEdit partial). The bare conversions are cached per
    // package path beside them, because one package can hold hundreds of songs.
    private Dictionary<string, DcRigPreset> _presets = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, ConversionReport> _reports = new(StringComparer.OrdinalIgnoreCase);
    private string? _convertedPsarcPath;

    /// <summary>The arrangement the last <see cref="PrepareForSong"/> was handed,
    /// held WEAKLY and read back through <see cref="PreparedArrangement"/>.
    /// <para>Identity without ownership: this coordinator is an app-wide
    /// singleton, so a strong field here pinned the whole chart graph — levels,
    /// notes, chords, hand shapes, anchors — of every arrangement the session ever
    /// prepared, for the life of the process. What is handed in is always
    /// <see cref="ProjectState.ActiveArrangement"/>, so the target stays
    /// resolvable for exactly as long as the arrangement is the loaded one, and
    /// the closed project becomes collectable the moment it is switched away.</para>
    /// <para>Clearing it at a lifecycle point is NOT the fix: <see cref="EndRun"/>
    /// is reachable from the <c>UseInGameTones</c> toggle
    /// (<c>PlayModeViewModel.Tones.cs</c>), which turns the tones off and on again
    /// mid-run and needs <see cref="BeginRun"/> to still find the prepare.</para>
    /// </summary>
    private WeakReference<Arrangement>? _preparedArrangement;

    private string? _preparedSongIdentity;

    // ---- editor tone preview (its own tone SOURCE, the same rig) ----
    // Kept apart from the psarc cache above rather than sharing it: a
    // PrepareForSong while a preview drives would otherwise swap the maps under
    // the live scanner and every apply would silently no-op ("no converted
    // preset"), which is exactly the class of failure this file is full of.
    private Dictionary<string, DcRigPreset> _previewPresets = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, ConversionReport> _previewReports = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The editor preview's own arrangement — same weak-identity rule as
    /// <see cref="_preparedArrangement"/>, for the same reason.</summary>
    private WeakReference<Arrangement>? _previewArrangement;

    /// <summary>The prepared arrangement, or null once it has been collected —
    /// which can only happen after the project that owned it was closed.</summary>
    private Arrangement? PreparedArrangement => Resolve(_preparedArrangement);

    /// <summary>The preview's arrangement, or null once it has been collected.</summary>
    private Arrangement? PreviewArrangement => Resolve(_previewArrangement);

    private static WeakReference<Arrangement>? WeakRef(Arrangement? arrangement) =>
        arrangement is null ? null : new WeakReference<Arrangement>(arrangement);

    private static Arrangement? Resolve(WeakReference<Arrangement>? weak) =>
        weak is not null && weak.TryGetTarget(out var arrangement) ? arrangement : null;

    /// <summary>True when the last prepare converted the OPEN PROJECT's own
    /// authored tone bodies rather than a package's — an own chart played from
    /// Play Mode. <see cref="BeginRun"/> reads it to point at the right maps;
    /// written on whichever thread <c>BuildSummary</c> ran on.</summary>
    private volatile bool _preparedFromProject;

    /// <summary>The maps the CURRENTLY driving source applies from — the psarc
    /// cache for a run, the project's own tones for the editor preview. Assigned
    /// before <see cref="StartDriving"/> and read by the apply worker.</summary>
    private volatile Dictionary<string, DcRigPreset> _activePresets =
        new(StringComparer.OrdinalIgnoreCase);

    private volatile Dictionary<string, ConversionReport> _activeReports =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>True while <see cref="_activePresets"/> is the open PROJECT's tones (the
    /// preview maps), false for a package's. Set with every assignment of the maps
    /// and stable for the life of a driver — unlike the maps' identity, which the
    /// levelling republishes mid-run, so an apply can never ask "is this the
    /// preview's map?" by reference and catch the old one.</summary>
    private volatile bool _activeIsProjectSource;

    private RsToneDriver _driver = RsToneDriver.None;
    private readonly object _convertGate = new();
    // The in-flight background conversion started by PrepareForSongAsync (BUG 2:
    // tone conversion moved off the UI thread). BeginRun skips gracefully if a
    // run somehow begins before it finishes — never blocks the UI thread on it.
    private volatile Task? _prepareInFlight;

    private RsToneScanner? _scanner;
    private IReadOnlyList<DspNode>? _userChainSnapshot;
    /// <summary>The player's scenes beside <see cref="_userChainSnapshot" />: they are
    /// managed state on the chain, the run's tones clear them, and the restore puts
    /// them back with the board.</summary>
    private DotCharter.Tone.Audio.Dsp.Scenes.RigSceneSet? _userScenesSnapshot;
    private string _activeToneKey = "";

    private Channel<ApplyRequest>? _applyChannel;
    private Task? _worker;
    private CancellationTokenSource? _workerCts;

    // ---- run teardown (see EndRun) ----
    // EndRun is called on the UI thread from every run-exit path, so it must not
    // wait on the apply worker and must not take _applyGate: both block for the
    // length of a native DSP apply. The restore therefore runs on a background
    // finisher, which opens a window in which a NEW run can begin before the old
    // chain is back. These three fields close it:
    private Task? _restoreTask;
    private int _runGeneration;
    /// <summary>The manual chain a still-pending restore will put back. A BeginRun
    /// inside that window adopts it instead of re-snapshotting the live chain,
    /// which is still the previous run's TONE rig.</summary>
    private volatile IReadOnlyList<DspNode>? _pendingRestore;
    private volatile DotCharter.Tone.Audio.Dsp.Scenes.RigSceneSet? _pendingRestoreScenes;

    /// <summary>Test seam: completes when the background restore started by the
    /// last <see cref="EndRun"/> has finished (or immediately if none is in
    /// flight). Production code never needs to await it.</summary>
    internal Task RestoreCompletion => _restoreTask ?? Task.CompletedTask;

    public RsTonePlaybackCoordinator(
        IDspChainService chain,
        DcRigApplier applier,
        ProjectState projectState,
        IConfigurationService config,
        DotCharter.Tone.Audio.Dsp.Presets.IDspTonePresetService? tonePresets = null,
        IAppLogger? logger = null,
        IGameplayToneHud? toneHud = null,
        IToneLoudnessMeter? loudnessMeter = null)
    {
        _toneHud = toneHud;
        _levelling = new RsToneLevelling(loudnessMeter);
        _chain = chain;
        _applier = applier;
        _projectState = projectState;
        _config = config;
        _tonePresets = tonePresets;
        _logger = logger;
        _onProjectStateChanged = OnProjectStatePropertyChanged;
    }

    /// <summary>The summary from the last <see cref="PrepareForSong"/>.</summary>
    public RsToneConversionSummary LastSummary { get; private set; } = RsToneConversionSummary.None;

    /// <summary>The summary from the last <see cref="PrepareForProject"/> — the
    /// editor tone preview's availability answer.</summary>
    public RsToneConversionSummary LastPreviewSummary { get; private set; } = RsToneConversionSummary.None;

    /// <summary>True between <see cref="BeginRun"/>/<see cref="BeginPreview"/> and
    /// the matching stop. <see cref="Driver"/> says WHICH of the two.</summary>
    public bool IsDriving { get; private set; }

    /// <summary>Who owns the rig right now. There is only ever one.</summary>
    public RsToneDriver Driver => _driver;

    /// <summary>True while the chart editor's tone preview is driving.</summary>
    public bool IsPreviewing => _driver == RsToneDriver.EditorPreview;

    // Set BEFORE a drive's first apply and cleared only once the player's board is
    // back on the chain. Volatile: the tone session's persist timer reads it.
    private volatile bool _boardDisplaced;

    /// <summary>
    ///     True from the moment a song's tone takes the rig until the player's own
    ///     board is restored onto it — through the background restore after a stop,
    ///     and for good after <see cref="AbandonPreview" /> (shutdown), which restores
    ///     nothing. The player's tone session persists nothing while this is true
    ///     (<c>ToneSessionService.BoardDisplaced</c>), so a song tone is never saved
    /// as the player's rig.
    /// </summary>
    public bool IsPlayersBoardDisplaced => _boardDisplaced;

    /// <summary>True while the off-UI-thread tone conversion started by
    /// <see cref="PrepareForSongAsync"/> is still running. <see cref="BeginRun"/>
    /// skips (does not drive) in that window — the VM surfaces it as a small
    /// 'Tones loading…' badge instead of a silent no-op (plan 2.5).</summary>
    public bool IsPrepareInFlight => _prepareInFlight is { IsCompleted: false };

    /// <summary>The tone the rig is currently rebuilt as (for a HUD badge).</summary>
    public string CurrentToneName => _activeToneKey;

    /// <summary>Raised on each successful tone apply (including the initial
    /// take-over) — optional live HUD badge. Fired on the apply thread.</summary>
    public event Action<ActiveToneInfo>? ActiveToneChanged;

    /// <summary>
    ///     Raised when nothing is driving the rig any more (either driver), so a
    ///     live tone read-out can CLEAR rather than freeze on the last tone it saw.
    ///     <para>
    ///         A separate event from <see cref="ActiveToneChanged" /> on purpose:
    ///         an apply of "" is not a thing, and a stop is not an apply. Fired on
    ///         the UI thread (every stop path is), but subscribers must not assume
    ///         it — <see cref="StopDriving" /> is reachable from teardown.
    ///     </para>
    /// </summary>
    public event Action? DrivingStopped;

    // -------------------------------------------------------------- pre-play

    /// <summary>Dry-convert (applies nothing) the tones of the song/arrangement
    /// about to be played and return a summary for the pre-play badge.
    /// <para><paramref name="psarcPath"/> picks the tone SOURCE, and BOTH are
    /// real: a path converts from the PACKAGE, null/empty converts the open
    /// project's own <see cref="ChartProject.ToneDefinitions"/> (see
    /// <see cref="BuildSummary"/>). It does NOT mean "no tones" (a stale
    /// claim to the contrary once got a fixed bug re-diagnosed as a live one).
    /// An edited CDLC saved as a <c>.dcproj</c> takes the second
    /// branch: its bodies came from the package, but the package is gone.</para>
    /// <paramref name="songIdentity"/> is the practice-profile song key
    /// (<c>"lib:&lt;SongKey&gt;"</c> / project path) that keys the lookup of the
    /// per-song <see cref="SongTonePreference"/> at <see cref="BeginRun"/>.
    /// A <c>.dcsong</c> path counts as null: its tone bodies are its chart's, which
    /// the open project already holds (<see cref="PackageToneSource"/>).
    /// Never throws.</summary>
    public RsToneConversionSummary PrepareForSong(string? psarcPath, Arrangement? arrangement, string? songIdentity = null)
    {
        psarcPath = PackageToneSource(psarcPath);
        _preparedArrangement = WeakRef(arrangement);
        _preparedSongIdentity = songIdentity;
        LastSummary = BuildSummary(psarcPath, arrangement, songIdentity);
        return LastSummary;
    }

    /// <summary>Off-UI-thread variant of <see cref="PrepareForSong"/> (BUG 2 /
    /// 0.15.1): the expensive part — reading + parsing + converting EVERY tone in
    /// the package — runs on a background thread so the pre-play screen never
    /// stalls the UI. The prepared arrangement is recorded synchronously (so
    /// <see cref="BeginRun"/> sees the right one immediately); the badge
    /// <see cref="LastSummary"/> updates when the conversion finishes. Only a song
    /// with NO tone bodies to convert at all short-circuits inline (no thread
    /// hop) — an own chart with authored tones has real conversion work and takes
    /// the same background path a package does. Never throws.</summary>
    public Task<RsToneConversionSummary> PrepareForSongAsync(string? psarcPath, Arrangement? arrangement, string? songIdentity = null)
    {
        psarcPath = PackageToneSource(psarcPath);
        _preparedArrangement = WeakRef(arrangement);
        _preparedSongIdentity = songIdentity;

        // Nothing to convert from either source — compute inline, no thread hop.
        if (arrangement is null ||
            (string.IsNullOrWhiteSpace(psarcPath) &&
             (_projectState.ActiveProject?.ToneDefinitions.Count ?? 0) == 0))
        {
            LastSummary = BuildSummary(psarcPath, arrangement, songIdentity);
            _prepareInFlight = null;
            return Task.FromResult(LastSummary);
        }

        var task = Task.Run(() =>
        {
            var summary = BuildSummary(psarcPath, arrangement, songIdentity);
            // Publish inside the background work, BEFORE the task completes, so a
            // BeginRun that observes _prepareInFlight.IsCompleted also sees the
            // matching LastSummary (no torn read between the two).
            LastSummary = summary;
            return summary;
        });
        _prepareInFlight = task;
        return task;
    }

    /// <summary>The archive to read tone bodies from: a Rocksmith <c>.psarc</c>, or null
    /// for anything else — a <c>.dcsong</c> carries its tones in its chart, which is the
    /// open project's own, so it takes the own-chart branch.</summary>
    internal static string? PackageToneSource(string? path) =>
        LibraryArchiveFiles.KindOf(path) == LibrarySourceKind.Psarc ? path : null;

    private RsToneConversionSummary BuildSummary(string? psarcPath, Arrangement? arrangement, string? songIdentity)
    {
        var dspAvailable = _chain is not NullDspChainService;
        _preparedFromProject = false;

        if (arrangement is null)
            return new RsToneConversionSummary(false, dspAvailable, 0, 0, 0, Array.Empty<string>());

        if (string.IsNullOrWhiteSpace(psarcPath))
        {
            // An OWN chart. Its tone bodies are the project's own authored ones —
            // the same source the chart editor's preview drives from. Play Mode
            // must not read packages only, or a chart authored HERE,
            // with tones dialled in the Tone Designer and tone changes placed on
            // its timeline, would play completely dry: the one song whose tones the
            // player definitely meant.
            var project = _projectState.ActiveProject;
            if (project is null || project.ToneDefinitions.Count == 0)
                return new RsToneConversionSummary(false, dspAvailable, 0, 0, 0, Array.Empty<string>());

            var own = BuildPreviewSummary(project, arrangement, bodyEdits: null);
            _preparedFromProject = own.HasTones;
            return own;
        }

        EnsureConverted(psarcPath!);
        LevelPackageTones(arrangement, songIdentity);

        return WithSongEdits(
            Summarise(System.IO.Path.GetFileName(psarcPath), arrangement, _presets, _reports, dspAvailable),
            songIdentity);
    }

    /// <summary>The half of a summary that is the same whichever SOURCE the tone
    /// bodies came from: resolve the arrangement's referenced keys against the
    /// converted presets and roll up their conversion notes.</summary>
    private RsToneConversionSummary Summarise(
        string source,
        Arrangement arrangement,
        Dictionary<string, DcRigPreset> presets,
        Dictionary<string, ConversionReport> reports,
        bool dspAvailable)
    {
        var referenced = ReferencedToneKeys(arrangement);
        var resolved = new List<string>();
        foreach (var key in referenced)
        {
            if (presets.ContainsKey(NormalizeKey(key)))
            {
                resolved.Add(key);
            }
            else
            {
                _logger?.Info(AppLogCategory.Audio,
                    $"RsTone: chart tone '{key}' has no converted preset (available: {DescribeKeys(presets)}).");
            }
        }

        _logger?.Info(AppLogCategory.Audio,
            $"RsTone: summary for '{source}' — dspAvailable={dspAvailable}, " +
            $"referencedTones={referenced.Count}, resolved={resolved.Count}, presetKeys={presets.Count}.");

        if (resolved.Count == 0)
            return new RsToneConversionSummary(false, dspAvailable, 0, 0, 0, Array.Empty<string>());

        var approx = 0;
        var skipped = 0;
        var notes = new List<string>();
        foreach (var key in resolved)
        {
            if (!reports.TryGetValue(NormalizeKey(key), out var rep)) continue;
            approx += rep.ApproximatedCount;
            skipped += rep.SkippedCount;
            foreach (var note in rep.Notes)
            {
                if (note.Kind is ConversionNoteKind.Approximated or ConversionNoteKind.Skipped &&
                    notes.Count < 16)
                {
                    notes.Add($"{key}: {note.Message}");
                }
            }
        }

        return new RsToneConversionSummary(true, dspAvailable, resolved.Count, approx, skipped, notes)
        {
            // The RESOLVED keys, not the referenced ones: a chip must name a tone
            // the rig will really play.
            ToneNames = resolved
        };
    }

    /// <summary>Read + parse + convert every tone in the package once, cached by
    /// psarc path so an arrangement switch on the same song doesn't re-read.</summary>
    private void EnsureConverted(string psarcPath)
    {
        // Serialize conversions: PrepareForSongAsync may run this on a background
        // thread while another prepare (or a re-entrant summary) is in flight. The
        // maps are built into LOCALS and published by a single atomic reference
        // assignment at the end, so an unsynchronized reader (BuildSummary /
        // ApplyToneKey) always sees a fully-built dictionary, never a half-populated one.
        lock (_convertGate)
        {
            if (string.Equals(_convertedPsarcPath, psarcPath, StringComparison.Ordinal)) return;

            var presets = new Dictionary<string, DcRigPreset>(StringComparer.OrdinalIgnoreCase);
            var reports = new Dictionary<string, ConversionReport>(StringComparer.OrdinalIgnoreCase);
            var volumes = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);

            IReadOnlyList<PsarcToneDefinition> defs;
            try
            {
                defs = ToneDefinitionSource(psarcPath);
            }
            catch (Exception ex)
            {
                _logger?.Warning(AppLogCategory.Audio, $"RsTone: reading tones from '{psarcPath}' failed.", ex);
                // Publish the (empty) maps + mark converted so we don't re-read every tick.
                PublishPackage(presets, reports, []);
                _packageConverted = presets;
                _packageVolumes = volumes;
                _convertedPsarcPath = psarcPath;
                return;
            }

            ConvertInto(defs, presets, reports, volumes);

            PublishPackage(presets, reports, defs);
            _packageConverted = presets;
            _packageVolumes = volumes;
            _convertedPsarcPath = psarcPath;

            _logger?.Info(AppLogCategory.Audio,
                $"RsTone: converted {defs.Count} tone(s) from '{System.IO.Path.GetFileName(psarcPath)}' " +
                $"→ {presets.Count} preset key(s).");
        }
    }

    /// <summary>Tone JSON → <see cref="DcRigPreset"/>, keyed the way every lookup
    /// in this file expects. Shared by the package cache and the editor preview so
    /// the two sources cannot drift on key normalization (the one thing that makes
    /// a tone silently fail to apply).</summary>
    private void ConvertInto(
        IReadOnlyList<PsarcToneDefinition> defs,
        Dictionary<string, DcRigPreset> presets,
        Dictionary<string, ConversionReport> reports,
        Dictionary<string, float> volumes)
    {
        foreach (var def in defs)
        {
            RsTone tone;
            DcRigPreset preset;
            ConversionReport report;
            try
            {
                tone = RsToneJsonParser.Parse(def);
                // ConvertFilled, not Convert: omitted params must be pinned to
                // catalogue defaults or a mid-song switch inherits the previous
                // tone's values on reused nodes (see RsToneConverter.ConvertFilled).
                preset = _converter.ConvertFilled(tone, out report);
                // The game's own input stage: every tone it plays sits
                // behind a gate that follows the player's noise floor.
                preset = RsInputGate.Prepend(preset);
            }
            catch (Exception ex)
            {
                _logger?.Warning(AppLogCategory.Audio, $"RsTone: converting tone '{def.Key}' failed; skipped.", ex);
                continue;
            }

            // Index by both Key and Name (Key wins) so ToneChange.ToneName resolves
            // whichever the chart used. Keys are trimmed (and the dictionary is
            // case-insensitive) so a stray space / case difference between the
            // chart's tone reference and the manifest key still matches.
            var normKey = NormalizeKey(def.Key);
            if (normKey.Length > 0)
            {
                presets[normKey] = preset;
                reports[normKey] = report;
                volumes[normKey] = tone.VolumeDb;
            }

            var normName = NormalizeKey(def.Name);
            if (normName.Length > 0)
            {
                if (presets.TryAdd(normName, preset)) volumes[normName] = tone.VolumeDb;
                reports.TryAdd(normName, report);
            }
        }
    }

    // ------------------------------------------------------------------ run

    /// <summary>Take over the DSP chain for a run. No-op (returns false) when the
    /// toggle is off, the DSP is unavailable, or the arrangement exposes no tones.
    /// Consults the per-song <see cref="SongTonePreference"/> first: 'My tone'
    /// applies the chosen user preset once and never drives; 'In-game tones'
    /// forces driving even when the global toggle is off.</summary>
    public bool BeginRun()
    {
        if (_driver == RsToneDriver.PlayModeRun) return true;

        // A run OWNS the rig. Without this the editor's tone preview — which is
        // driving the SAME chain through the same scanner — would satisfy the old
        // `if (IsDriving) return true` and the run would silently play the whole
        // song on the preview's schedule, having never built its own.
        // The same for a song-tone edit: the edit was of THIS song's tone, and the
        // run restores-then-drives from the one snapshot of the player's board.
        if (_driver is RsToneDriver.EditorPreview or RsToneDriver.SongToneEdit)
        {
            _logger?.Info(AppLogCategory.Audio,
                $"RsTone: a Play Mode run is taking the rig over from {_driver}.");
            StopDriving();
        }

        var pref = ResolveSongPreference();
        if (pref?.Mode == SongTonePreference.MyToneMode)
        {
            // Per-song "My tone": the player's saved profile IS the tone for
            // this song — apply it once (same as picking it in the pedalboard
            // browser) and do NOT drive switches or touch the snapshot.
            ApplyMyTonePreset(pref.PresetId);
            return false;
        }

        var forceInGame = pref?.Mode == SongTonePreference.InGameMode;

        var arr = PreparedArrangement;
        if (arr is null)
        {
            _logger?.Info(AppLogCategory.Audio, "RsTone: BeginRun skipped — no prepared arrangement.");
            return false;
        }
        if (_prepareInFlight is { IsCompleted: false })
        {
            // Prepare (the off-UI-thread conversion) hasn't finished — skip this
            // run gracefully rather than block the UI thread waiting for it.
            _logger?.Info(AppLogCategory.Audio, "RsTone: BeginRun skipped — tone conversion still in progress.");
            return false;
        }
        if (_chain is NullDspChainService)
        {
            _logger?.Info(AppLogCategory.Audio,
                "RsTone: BeginRun skipped — no native DSP rig (NullDspChainService). " +
                "The native DLL was built without the dca_engine_dsp_* ABI; rebuild it to enable in-game tones.");
            return false;
        }
        if (!LastSummary.HasTones)
        {
            _logger?.Info(AppLogCategory.Audio,
                "RsTone: BeginRun skipped — the prepared arrangement exposes no convertible tones.");
            return false;
        }
        if (!forceInGame && !_config.LoadAppConfig().UseInGameTones)
        {
            _logger?.Info(AppLogCategory.Audio, "RsTone: BeginRun skipped — 'Use in-game tones' is off in config.");
            return false;
        }

        // Whichever source the prepare actually converted from — the package cache
        // for a library song, the project's own authored bodies for an own chart.
        _activePresets = _preparedFromProject ? _previewPresets : _presets;
        _activeIsProjectSource = _preparedFromProject;
        _activeReports = _preparedFromProject ? _previewReports : _reports;
        StartDriving(BuildSchedule(arr), RsToneDriver.PlayModeRun);
        return true;
    }

    /// <summary>The arrangement's tone timeline resolved to ms on the CURRENT beat
    /// map: base tone (StartingTone / ToneBase / ToneA) plus every ToneChange.</summary>
    private RsToneSchedule BuildSchedule(Arrangement arr)
    {
        var baseKey = FirstNonEmpty(arr.StartingTone, arr.ToneBase, arr.ToneA);
        var beats = _projectState.Beats;
        var boundaries = new List<(double, string)>(arr.ToneChanges.Count);
        foreach (var tc in arr.ToneChanges)
            boundaries.Add((tc.GetTimeMs(beats), tc.ToneName));
        return new RsToneSchedule(baseKey, boundaries);
    }

    /// <summary>Take the rig over: snapshot the manual chain, start the apply
    /// worker, apply the tone at the playhead and subscribe to it. Shared by
    /// <see cref="BeginRun"/> and <see cref="BeginPreview"/> — the two differ only
    /// in their gating and their tone SOURCE, never in how the chain is driven.
    /// <see cref="_activePresets"/>/<see cref="_activeReports"/> must already point
    /// at that source.</summary>
    private void StartDriving(RsToneSchedule schedule, RsToneDriver driver)
    {
        _scanner = new RsToneScanner(schedule);

        // From here on the chain is the song's, not the player's: the tone session
        // must not save it. Raised before the first apply below.
        _boardDisplaced = true;

        // Take over from any still-pending restore of the PREVIOUS run (quit →
        // immediately re-enter the song): bumping the generation makes that
        // restore drop out, and its snapshot is the manual chain we want — the
        // live chain right now would still be the last run's tone rig.
        Interlocked.Increment(ref _runGeneration);

        // Snapshot the player's manual chain so the stop can restore it verbatim.
        _userScenesSnapshot = _pendingRestore is not null ? _pendingRestoreScenes : _chain.Scenes;
        _userChainSnapshot = _pendingRestore ?? _chain.GetNodes();
        _pendingRestore = null;
        _pendingRestoreScenes = null;

        // Start the off-UI-thread apply worker (latest-wins, coalesces rapid
        // ToneChange clusters into one apply).
        _workerCts = new CancellationTokenSource();
        _applyChannel = Channel.CreateBounded<ApplyRequest>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
        var reader = _applyChannel.Reader;
        var token = _workerCts.Token;
        _worker = Task.Run(() => RunApplyWorkerAsync(reader, token));

        // Prime the scanner to the current playhead, then apply the active tone
        // NOW (pre-roll, on the UI thread) so the rig is armed before the first
        // note — never leaves a freshly-added stage at default gain.
        _scanner.TryAdvance(_projectState.CurrentTimeMs, out _);
        _logger?.Info(AppLogCategory.Audio,
            $"RsTone: {driver} driving. baseTone='{schedule.BaseToneKey}', " +
            $"activeAtPlayhead='{_scanner.ActiveKey}', hasToneChanges={!schedule.IsEmpty}, " +
            $"presetKeys={_activePresets.Count}, manualChainNodes={_userChainSnapshot?.Count ?? 0}.");
        ApplyToneKey(_scanner.ActiveKey);

        _projectState.PropertyChanged += _onProjectStateChanged;
        _driver = driver;
        IsDriving = true;
    }

    /// <summary>Restore the player's manual chain snapshot and unsubscribe.
    /// Idempotent — safe to call from every run-exit path.
    ///
    /// <para>Returns IMMEDIATELY: this runs on the UI thread (quit, pause-quit,
    /// song end), and both waiting on the apply worker and taking
    /// <c>_applyGate</c> block for the length of a native DSP apply — a song whose
    /// chart has a late tone change would freeze the window on the way to the
    /// results screen. The stop is signalled here; the wait + the restore apply
    /// happen on a background finisher (<see cref="RestoreCompletion"/>).</para></summary>
    public void EndRun()
    {
        // Only a RUN. Play Mode's teardown paths call this unconditionally, and
        // an editor tone preview is not theirs to stop.
        if (_driver != RsToneDriver.PlayModeRun) return;
        StopDriving();
    }

    /// <inheritdoc cref="EndRun" />
    /// <param name="restoreBoard">False only for <see cref="AbandonPreview" />: stop
    /// driving without rebuilding the charter's board.</param>
    private void StopDriving(bool restoreBoard = true)
    {
        if (!IsDriving) return;
        IsDriving = false;
        _driver = RsToneDriver.None;
        _projectState.PropertyChanged -= _onProjectStateChanged;

        var channel = _applyChannel;
        var worker = _worker;
        var workerCts = _workerCts;
        var snapshot = _userChainSnapshot;
        var scenes = _userScenesSnapshot;
        var generation = Interlocked.Increment(ref _runGeneration);

        _worker = null;
        _workerCts = null;
        _applyChannel = null;
        _scanner = null;
        _userChainSnapshot = null;
        _userScenesSnapshot = null;
        _pendingRestoreScenes = scenes;
        _pendingRestore = snapshot;
        _activeToneKey = "";
        // Drop the HUD rig override so the tone label falls back to the chart's
        // own tone (or nothing) once the run ends.
        _toneHud?.ClearRig();
        // …and the same for any managed read-out, which would otherwise freeze on
        // the last tone it saw and claim a rig nobody is driving. Swallowed: a
        // read-out is never allowed to take a teardown down with it.
        try { DrivingStopped?.Invoke(); }
        catch (Exception ex)
        {
            _logger?.Warning(AppLogCategory.Audio, "RsTone: a DrivingStopped handler threw; ignored.", ex);
        }

        // Stop the worker before restoring so no apply races the restore.
        try { channel?.Writer.TryComplete(); } catch { /* already completed */ }
        try { workerCts?.Cancel(); } catch { /* disposed */ }

        _restoreTask = Task.Run(async () =>
        {
            if (worker != null)
            {
                try { await worker.ConfigureAwait(false); }
                catch { /* cancellation / faults are non-fatal */ }
            }

            workerCts?.Dispose();

            if (snapshot is null || !restoreBoard) return;
            var restore = SnapshotToPreset(snapshot, scenes);

            // The gate stays HERE, and only here: it makes the generation check
            // and the restore apply one indivisible step. The applier's own
            // single-flight (0.62) makes each PLAN atomic but says nothing about
            // a check taken just before one.
            lock (_applyGate)
            {
                // Checked INSIDE the gate: a BeginRun that already took over has
                // bumped the generation, and its pre-roll tone apply contends for
                // this same lock — restoring now would stomp the new run's tone.
                if (Volatile.Read(ref _runGeneration) != generation) return;
                _applier.Apply(restore);
                _pendingRestore = null;
                _pendingRestoreScenes = null;
                // The player's board is on the chain again: theirs to save.
                _boardDisplaced = false;
            }
        });
    }

    // ------------------------------------------------- editor tone preview

    /// <summary>
    ///     Dry-convert the OPEN PROJECT's own authored tone bodies
    ///     (<see cref="ChartProject.ToneDefinitions" />, what the Tone Designer
    ///     writes and the rscp <c>dcToneDefinitions</c> node carries) and report
    ///     whether the chart editor's tone preview can drive. Applies nothing.
    ///     <para>
    ///         Deliberately NOT cached: a project has a handful of tones, this is
    ///         called on a project/arrangement/tone-definition change rather than
    ///         per tick, and a cache key over live, editable JSON is a staleness
    ///         bug waiting to be written.
    ///     </para>
    ///     Never throws.
    /// </summary>
    public RsToneConversionSummary PrepareForProject(ChartProject? project, Arrangement? arrangement,
        IReadOnlyDictionary<string, string>? bodyEdits = null)
    {
        _previewArrangement = WeakRef(arrangement);
        LastPreviewSummary = BuildPreviewSummary(project, arrangement, bodyEdits);
        return LastPreviewSummary;
    }

    // bodyEdits: tone bodies the Tone designer holds unsaved, by tone key,
    // converted in place of the committed body so an edit is heard live.
    private RsToneConversionSummary BuildPreviewSummary(ChartProject? project, Arrangement? arrangement,
        IReadOnlyDictionary<string, string>? bodyEdits)
    {
        var dspAvailable = _chain is not NullDspChainService;

        if (project is null || arrangement is null || project.ToneDefinitions.Count == 0)
        {
            _previewPresets = new Dictionary<string, DcRigPreset>(StringComparer.OrdinalIgnoreCase);
            _previewReports = new Dictionary<string, ConversionReport>(StringComparer.OrdinalIgnoreCase);
            return new RsToneConversionSummary(false, dspAvailable, 0, 0, 0, Array.Empty<string>());
        }

        var defs = new List<PsarcToneDefinition>(project.ToneDefinitions.Count);
        foreach (var tone in project.ToneDefinitions)
        {
            var json = bodyEdits is not null && bodyEdits.TryGetValue(tone.Key, out var edited) ? edited : tone.Json;
            defs.Add(new PsarcToneDefinition(tone.Key, tone.Name, json));
        }

        var presets = new Dictionary<string, DcRigPreset>(StringComparer.OrdinalIgnoreCase);
        var reports = new Dictionary<string, ConversionReport>(StringComparer.OrdinalIgnoreCase);
        var volumes = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        lock (_convertGate) ConvertInto(defs, presets, reports, volumes);

        // Published by a single reference assignment, like the package cache: an
        // unsynchronized reader must never see a half-built dictionary. Levelled
        // from the meter's cache; the rest is measured off-thread (Levelling partial).
        lock (_convertGate) _previewPresets = LevelPreviewTones(presets, volumes, arrangement);
        _previewReports = reports;

        return Summarise("the open project", arrangement, presets, reports, dspAvailable);
    }

    /// <summary>
    ///     Start the chart editor's tone preview: route the live guitar input
    ///     through the tone the PLAYHEAD sits in, and re-apply as the playhead
    ///     crosses each <c>ToneChange</c>. Returns false (and logs the reason)
    ///     when a Play Mode run owns the rig, when nothing was prepared, when the
    ///     build has no native DSP rig, or when the arrangement references no tone
    ///     this project defines — the toggle reads that as "unavailable" and shows
    ///     the reason rather than pretending to be on.
    /// </summary>
    public bool BeginPreview()
    {
        if (_driver == RsToneDriver.EditorPreview) return true;
        if (_driver is RsToneDriver.PlayModeRun or RsToneDriver.SongToneEdit)
        {
            _logger?.Info(AppLogCategory.Audio,
                $"RsTone: tone preview skipped — {_driver} owns the rig.");
            return false;
        }

        var arr = PreviewArrangement;
        if (arr is null)
        {
            _logger?.Info(AppLogCategory.Audio, "RsTone: tone preview skipped — no prepared arrangement.");
            return false;
        }
        if (_chain is NullDspChainService)
        {
            _logger?.Info(AppLogCategory.Audio,
                "RsTone: tone preview skipped — no native DSP rig (NullDspChainService). " +
                "The native DLL was built without the dca_engine_dsp_* ABI.");
            return false;
        }
        if (!LastPreviewSummary.HasTones)
        {
            _logger?.Info(AppLogCategory.Audio,
                "RsTone: tone preview skipped — this project defines no tone the arrangement references.");
            return false;
        }

        _activePresets = _previewPresets;
        _activeIsProjectSource = true;
        _activeReports = _previewReports;
        StartDriving(BuildSchedule(arr), RsToneDriver.EditorPreview);
        return true;
    }

    /// <summary>Stop the editor tone preview and put the charter's own board back.
    /// Idempotent, and a no-op while anything else owns the rig.</summary>
    public void EndPreview()
    {
        if (_driver != RsToneDriver.EditorPreview) return;
        StopDriving();
    }

    /// <summary>
    /// Stops the editor preview WITHOUT restoring the charter's board. Only
    ///     for SHUTDOWN: the preview's owner is disposed after the audio engine, so a
    ///     restore there rebuilt the board into detached chains, every module refused
    ///     and logged twice — 76 % of a real log's warnings. The board the preview
    ///     replaced lives in the player's profile store, not in the chain, so nothing
    ///     is lost by not rebuilding a chain that is about to be destroyed. That is
    ///     only true because the tone session saves nothing while a song tone
    /// drives: <see cref="IsPlayersBoardDisplaced" /> stays true after
    ///     this, so the shutdown flush cannot save the song's tone in its place.
    /// </summary>
    public void AbandonPreview()
    {
        if (_driver != RsToneDriver.EditorPreview) return;
        StopDriving(restoreBoard: false);
    }

    /// <summary>
    ///     Re-arm the running editor preview against the chart as it stands NOW —
    ///     without touching the chain. Returns true when a tone was queued.
    ///
    ///     <para>
    ///         <b>Why this is not a stop + start.</b> A run's schedule is fixed
    ///         (nobody edits a chart mid-song), but the editor's is the thing being
    ///         AUTHORED: every marker placed, dragged or tempo-shifted changes it.
    ///         Going through <see cref="EndPreview" /> + <see cref="BeginPreview" />
    ///         to pick that up restores the charter's manual chain and rebuilds the
    ///         tone rig on top of it for every one of those gestures — audible, and
    ///         exactly the teardown <see cref="DcRigApplier" /> exists to avoid. All
    ///         that actually goes stale is the ms-resolved schedule, so all that is
    ///         rebuilt here is the scanner.
    ///     </para>
    ///     <para>
    ///         A rebuild that resolves to the tone already on the rig queues
    ///         nothing, so the common case (a marker moved somewhere the playhead
    ///         is not) costs one small list and no native call at all.
    ///     </para>
    ///     <para>
    ///         <paramref name="reapplyCurrentTone" /> is for the case the key
    ///         comparison cannot see: a tone BODY edited under an unchanged NAME.
    ///         The caller must have re-run <see cref="PrepareForProject" /> first —
    ///         that publishes new preset maps, and the apply worker reads the
    ///         reference captured at <see cref="BeginPreview" />, so this re-points
    ///         it. Without the re-point an edited tone converts perfectly and
    ///         reaches nothing.
    ///     </para>
    /// </summary>
    public bool RefreshPreviewSchedule(bool reapplyCurrentTone = false)
    {
        if (_driver != RsToneDriver.EditorPreview) return false;
        if (PreviewArrangement is not { } arr) return false;

        _activePresets = _previewPresets;
        _activeIsProjectSource = true;
        _activeReports = _previewReports;

        var scanner = new RsToneScanner(BuildSchedule(arr));
        _scanner = scanner;
        scanner.TryAdvance(_projectState.CurrentTimeMs, out var key);

        if (string.IsNullOrEmpty(key)) return false;
        // Compare against what is ACTUALLY on the rig, not against the fresh
        // scanner's own base key — a new scanner starts at the base tone, so
        // every rebuild would otherwise re-apply while the playhead sits past a
        // tone change.
        if (!reapplyCurrentTone && string.Equals(key, _activeToneKey, StringComparison.Ordinal))
            return false;

        RequestApply(key); // latest-wins, same as a boundary crossing
        return true;
    }

    // ----------------------------------------------------------------- tick

    private void OnProjectStatePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!IsDriving) return;
        if (!string.Equals(e.PropertyName, nameof(ProjectState.CurrentTimeMs), StringComparison.Ordinal)) return;

        if (_scanner is { } scanner && scanner.TryAdvance(_projectState.CurrentTimeMs, out var key))
            RequestApply(key); // latest-wins; superseded requests dropped
    }

    private async Task RunApplyWorkerAsync(ChannelReader<ApplyRequest> reader, CancellationToken token)
    {
        try
        {
            await foreach (var request in reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                if (request.Glide) await GlideAsync(request.Key, reader, token).ConfigureAwait(false);
                else ApplyToneKey(request.Key);
            }
        }
        catch (OperationCanceledException) { /* EndRun cancelled us */ }
        catch (Exception ex)
        {
            _logger?.Warning(AppLogCategory.Audio, "RsTone: apply worker faulted.", ex);
        }
    }

    internal void ApplyToneKey(string key)
    {
        // The maps of whoever is DRIVING (a run's package tones, or the editor
        // preview's project tones) — never the package cache directly, which the
        // other source may have replaced under us.
        var presets = _activePresets;
        var reports = _activeReports;

        if (string.IsNullOrEmpty(key) || !presets.TryGetValue(NormalizeKey(key), out var preset))
        {
            _logger?.Info(AppLogCategory.Audio,
                $"RsTone: no converted preset for tone '{key}'; keeping the current rig " +
                $"(available: {DescribeKeys(presets)}).");
            return;
        }

        // _applyGate is NOT redundant with DcRigApplier's own single-flight
        // (0.62): it is what ORDERS this apply against StopDriving's
        // check-then-restore. Dropped, an outgoing run's restore can evaluate its
        // generation check while this apply is mid-flight and then land AFTER it,
        // leaving the new run wearing the old run's tone. The applier's lock makes
        // each PLAN atomic; this gate decides which plan happens first.
        DcRigApplyResult result;
        var previousKey = _activeToneKey;
        lock (_applyGate) result = _applier.Apply(preset);
        _activeToneKey = key;
        _appliedGainDb = RsToneLevelling.TrailingGainDb(preset);
        _appliedAmpLevelDb = RsToneLevelling.AmpLevelDb(preset);

        var approximated = reports.TryGetValue(NormalizeKey(key), out var rep) &&
                           rep.ApproximatedCount + rep.SkippedCount > 0;
        // Publish the applied preset to the gameplay HUD so a switch is visible
        // even without input monitoring — the live proof that switching fires.
        _toneHud?.SetRig(key, approximated);

        if (result.NoOp && string.Equals(previousKey, key, StringComparison.OrdinalIgnoreCase))
        {
            // The same tone again (an arrangement switch that keeps it): correct, and quiet.
            _logger?.Debug(AppLogCategory.Audio, $"RsTone: '{key}' is already on the rig (unchanged).");
        }
        else if (result.NoOp)
        {
            _logger?.Info(AppLogCategory.Audio,
                $"RsTone: applied '{key}' but the DSP chain did not change (NoOp). If the rig should be " +
                "non-empty here, every native op no-oped — the DSP chain is likely not attached to a live engine.");
        }
        else
        {
            _logger?.Info(AppLogCategory.Audio,
                $"RsTone: applied '{key}' (+{result.Added}/-{result.Removed} nodes, {result.Moved} moved, {result.ParamsSet} params).");
        }

        // A package tone (a run's or a song-tone edit's) names its song; the
        // project's own tones (the preview maps) belong to no song.
        var song = _activeIsProjectSource ? null : _preparedSongIdentity;
        ActiveToneChanged?.Invoke(new ActiveToneInfo(key, approximated, song,
            song == null ? null : ResolvePackageToneKey(key)));
    }

    // -------------------------------------------------------------- helpers

    /// <summary>The per-song tone preference for the prepared song, or null
    /// (= follow the global toggle). Unknown modes read as null. Never throws.</summary>
    private SongTonePreference? ResolveSongPreference()
    {
        var key = _preparedSongIdentity;
        if (string.IsNullOrWhiteSpace(key)) return null;

        try
        {
            if (!_config.LoadAppConfig().SongTonePreferences.TryGetValue(key!, out var pref)) return null;
            return pref?.Mode is SongTonePreference.InGameMode or SongTonePreference.MyToneMode ? pref : null;
        }
        catch (Exception ex)
        {
            _logger?.Warning(AppLogCategory.Audio, "RsTone: reading the per-song tone preference failed.", ex);
            return null;
        }
    }

    private void ApplyMyTonePreset(string presetId)
    {
        if (_tonePresets is null || string.IsNullOrWhiteSpace(presetId))
        {
            _logger?.Info(AppLogCategory.Audio,
                "RsTone: per-song 'My tone' preference set but no preset chosen (or no preset service) — keeping the current rig.");
            return;
        }

        var result = _tonePresets.ApplyById(presetId);
        _logger?.Info(AppLogCategory.Audio, result is null
            ? $"RsTone: per-song 'My tone' preset '{presetId}' no longer exists — keeping the current rig."
            : $"RsTone: per-song 'My tone' applied preset '{presetId}' (+{result.Value.Added}/-{result.Value.Removed} nodes, {result.Value.ParamsSet} params); in-game tone switching disabled for this song.");
    }

    /// <summary>The manual chain as a rig to put back. Goes through the shared
    /// <see cref="DcRigSnapshot" /> because a restore has to carry the node's
    /// STRING params and BRANCH tag too: copying only type /
    /// params / bypass would hand a player whose own board had a NAM capture loaded
    /// a MODELLESS namamp — a silent amp — back at the end of every run
    /// whose tones did not happen to contain one, and a split board back
    /// flattened onto one lane.</summary>
    private static DcRigPreset SnapshotToPreset(IReadOnlyList<DspNode> nodes,
        DotCharter.Tone.Audio.Dsp.Scenes.RigSceneSet? scenes)
    {
        var rig = DcRigSnapshot.FromNodes(nodes);
        return rig with { Scenes = DotCharter.Tone.Audio.Dsp.Scenes.RigScenes.Fold(scenes, rig.Modules) };
    }

    private static List<string> ReferencedToneKeys(Arrangement arr)
    {
        var keys = new List<string>();
        void Add(string? key)
        {
            if (!string.IsNullOrWhiteSpace(key) && !keys.Contains(key, StringComparer.OrdinalIgnoreCase))
                keys.Add(key!);
        }

        Add(arr.StartingTone);
        Add(arr.ToneBase);
        Add(arr.ToneA);
        Add(arr.ToneB);
        Add(arr.ToneC);
        Add(arr.ToneD);
        foreach (var name in arr.ToneNames) Add(name);
        foreach (var tc in arr.ToneChanges) Add(tc.ToneName);
        return keys;
    }

    private static string FirstNonEmpty(params string[] candidates)
    {
        foreach (var c in candidates)
        {
            if (!string.IsNullOrWhiteSpace(c))
                return c;
        }

        return "";
    }

    // Case-insensitive (dictionary comparer) + whitespace-tolerant tone-key
    // matching: chart tone references and manifest keys occasionally differ by
    // a stray leading/trailing space, which would otherwise silently no-op.
    private static string NormalizeKey(string? key) => key?.Trim() ?? "";

    // Preset keys for diagnostics (the map holds both Key and Name aliases for
    // the same preset — good enough to eyeball an unmatched-name mismatch).
    // Capped: a multi-song package (RS1 compatibility disc) converts hundreds
    // of tones, and listing all of them printed multi-KB log lines per lookup.
    private static string DescribeKeys(Dictionary<string, DcRigPreset> presets)
    {
        const int maxListed = 24;
        var keys = presets.Keys;
        return keys.Count <= maxListed
            ? string.Join(", ", keys)
            : string.Join(", ", keys.Take(maxListed)) + $", … +{keys.Count - maxListed} more";
    }
}
