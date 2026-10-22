using DotCharter.Services.Audio.Waveform;
using DotCharter.Tone.Diagnostics;
using DotCharter.Tone.UI.ViewModels.Shell;
using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DotCharter.Services.Audio.Coordination;
using DotCharter.Services.Audio.StemSeparation;
using DotCharter.Services.Audio.StemSeparation.GuitarRoles;

namespace DotCharter.UI.ViewModels.Editor;

/// <summary>
/// Tracks the DAW-style mixer panel. Rebuilds itself whenever the active
/// project's stems change, pushes the loaded set into the audio engine,
/// and exposes per-stem channel strips for binding.
/// </summary>
// ViewModelBase (not plain ObservableObject) so the app ViewLocator can
// resolve StemMixerView when this VM is a dock pane Context.
public partial class StemMixerViewModel : ViewModelBase, IDisposable
{
    private const string ReleasedMessage = "The stem mixer was disposed; its pending rebuild stops here.";

    private readonly ProjectState _state;
    private readonly IStemMixer _audio;
    private readonly IAppLogger? _logger;
    private readonly IUiDispatcher _ui;
    private readonly StemWaveformCoordinator? _waveform;
    private readonly IGuitarRoleStems? _guitarRoleStems;
    private readonly ILocalizationService _localization;
    private readonly Dictionary<string, string> _stemPaths = new(StringComparer.OrdinalIgnoreCase);
    private bool _loading;
    private bool _rebuildRequested;
    private bool _waveformRefreshInFlight;
    private bool _waveformRefreshPending;
    private int _released;
    private int _dispatchesInFlight;

    public StemMixerViewModel(ProjectState state, IStemMixer audio, ILocalizationService localization,
        IAppLogger? logger = null, IUiDispatcher? ui = null, StemWaveformCoordinator? waveform = null,
        IGuitarRoleStems? guitarRoleStems = null)
    {
        _state = state;
        _audio = audio;
        _logger = logger;
        _waveform = waveform;
        _guitarRoleStems = guitarRoleStems;
        _localization = localization;
        _ui = ui ?? new Tone.UI.Platform.AvaloniaUiDispatcher();
        GuitarRoles = new GuitarRolesViewModel(guitarRoleStems, localization, new ReleaseAwareDispatcher(this),
            Rebuild, logger);
        Tracks = [];
        _state.PropertyChanged += OnStateChanged;
        Rebuild();
    }

    public ObservableCollection<StemTrackViewModel> Tracks { get; }

    /// <summary>
    ///     Ends this mixer: no rebuild or waveform refresh still in flight may reach
    ///     the dispatcher once this returns.
    ///     <para>
    ///         The rebuild loop and the waveform refresh resume on POOL threads
    ///         (<c>ConfigureAwait(false)</c> after the worker probe and the engine
    ///         load) and only then dispatch their next step. A singleton disposed
    ///         with its container would keep going: the pool thread would call
    ///         <c>Dispatcher.UIThread</c> after the app was released, and in the
    ///         headless session — one app and dispatcher per dispatch — that read
    ///         would CLAIM the next case's UI thread (the thief named by
    ///         <c>headless-uithread-claims.log</c>).
    ///     </para>
    ///     <para>
    ///         Every dispatch goes through <see cref="TryEnterDispatch" />, which
    ///         counts itself in BEFORE reading the released flag; this sets the flag
    ///         and then waits for the count to drain. Either the caller sees the
    ///         flag and stops, or this sees the caller and waits for its call to
    ///         return — so after <c>Dispose</c> no new dispatcher call can BEGIN.
    ///         The wait covers the call only (an enqueue, on Avalonia), never the
    ///         dispatched work. <b>Never call this from inside work this view model
    ///         dispatched</b>: it would wait for itself. Pinned by
    ///         <c>UI/ViewModels/Editor/StemMixerReleaseTests</c>.
    ///     </para>
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) != 0) return;
        _state.PropertyChanged -= OnStateChanged;
        var spin = new SpinWait();
        while (Volatile.Read(ref _dispatchesInFlight) != 0) spin.SpinOnce();
    }

    private bool IsReleased => Volatile.Read(ref _released) != 0;

    private bool TryEnterDispatch()
    {
        Interlocked.Increment(ref _dispatchesInFlight);
        if (Volatile.Read(ref _released) == 0) return true;
        Interlocked.Decrement(ref _dispatchesInFlight);
        return false;
    }

    private void ExitDispatch() => Interlocked.Decrement(ref _dispatchesInFlight);

    /// <summary>
    ///     What <see cref="GuitarRoles" /> dispatches through: its stereo check and its
    ///     split finish on pool threads, so they obey the same released check as this
    /// type's own rebuild — a posted result after <see cref="Dispose" /> is
    ///     dropped, never handed to a dispatcher that may belong to the next app.
    /// </summary>
    private sealed class ReleaseAwareDispatcher(StemMixerViewModel owner) : IUiDispatcher
    {
        public bool CheckAccess() => false;
        public void Post(Action action) => owner.PostWhileAlive(action);
        public void PostNormal(Action action) => owner.PostWhileAlive(action);
        public Task InvokeAsync(Action action) => owner.InvokeWhileAliveAsync(action);
        public Task<T> InvokeAsync<T>(Func<T> func) => owner.InvokeWhileAliveAsync(func);

        public Task<T> InvokeAsync<T>(Func<Task<T>> func) =>
            throw new NotSupportedException("Guitar roles never dispatches async work.");
    }

    private void ThrowIfReleased()
    {
        if (IsReleased) throw new OperationCanceledException(ReleasedMessage);
    }

    /// <summary>Every <c>_ui.InvokeAsync</c> of this type: throws
    /// <see cref="OperationCanceledException" /> once disposed, which ends the
    /// rebuild loop quietly (see <see cref="Dispose" />).</summary>
    private Task InvokeWhileAliveAsync(Action action)
    {
        if (!TryEnterDispatch()) throw new OperationCanceledException(ReleasedMessage);
        try { return _ui.InvokeAsync(action); }
        finally { ExitDispatch(); }
    }

    /// <inheritdoc cref="InvokeWhileAliveAsync(Action)" />
    private Task<T> InvokeWhileAliveAsync<T>(Func<T> func)
    {
        if (!TryEnterDispatch()) throw new OperationCanceledException(ReleasedMessage);
        try { return _ui.InvokeAsync(func); }
        finally { ExitDispatch(); }
    }

    /// <summary>Every <c>_ui.PostNormal</c> of this type: dropped once disposed.</summary>
    private void PostWhileAlive(Action action)
    {
        if (!TryEnterDispatch()) return;
        try { _ui.PostNormal(action); }
        finally { ExitDispatch(); }
    }

    /// <summary>The Guitar roles switch.</summary>
    public GuitarRolesViewModel GuitarRoles { get; }

    /// <summary>
    ///     The two strips that stand in for the guitar while Guitar roles is on, per
    ///     project. Their <see cref="Stem" /> objects are the mixer's own, never added
    ///     to <see cref="ChartProject.Stems" />: the project lists what the separation
    ///     produced, and a derived stem is a way of LISTENING to one of those. Kept
    ///     across rebuilds of the same project so a toggle or a reload of the set does
    ///     not reset their faders.
    /// </summary>
    private (ChartProject Project, Stem Lead, Stem Rhythm)? _roleStems;

    [ObservableProperty] private bool _hasStems;

    /// <summary>
    ///     Draw the waveform of what the MIXER is playing rather than of the
    ///     audio file.
    ///     <para>
    ///         Charting to a stem you cannot see is the problem this removes:
    ///         separate out the guitar, and the timeline still draws the whole
    ///         band's transients, which tells you nothing about where the guitar
    ///         actually is. Session-scoped — it is a way of LOOKING, not chart
    ///         data, so it is not written to the project.
    ///     </para>
    /// </summary>
    [ObservableProperty] private bool _waveformFollowsMix;

    partial void OnWaveformFollowsMixChanged(bool value) => RefreshWaveform();

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Rebuild whenever either the project itself OR its on-disk location
        // changes. Project loading sets ActiveProject first (which triggers
        // a Rebuild before CurrentProjectPath is known), so the first pass
        // can't resolve relative stem paths and silently produces zero tracks.
        // The second event for CurrentProjectPath gives us a working dir.
        // The audio file name too: Add silence swaps the song AND its stems
        // in one step, and its undo swaps them back. An unchanged stem set costs a
        // probe and no engine load.
        if (e.PropertyName is (nameof(ProjectState.ActiveProject)) or
            (nameof(ProjectState.CurrentProjectPath)) or
            (nameof(ProjectState.CurrentAudioFileName)))
        {
            PostWhileAlive(Rebuild);
        }
    }

    /// <summary>
    /// Refreshes the tracks collection from <see cref="ProjectState.ActiveProject"/>
    /// and pushes the resulting stem set into the audio engine. Call from the
    /// UI thread; the disk work happens on a worker and the result is applied
    /// back here.
    /// </summary>
    /// <remarks>
    /// A rebuild requested while one is in flight is COALESCED, not dropped.
    /// Dropping it meant: load project A (long stem load starts) → open project
    /// B → B's ActiveProject change posts a rebuild that is discarded → the
    /// mixer keeps showing A's stems and the engine keeps A's stems loaded,
    /// until some later unrelated ProjectState change happens to fire. Same
    /// coalesce-don't-queue shape as ProjectSessionService.SchedulePsarcCompanion.
    /// </remarks>
    /// <summary>
    ///     True from the moment a rebuild starts until the LAST coalesced one has
    ///     finished — <c>FinishRebuild</c> included, which re-derives the master's
    ///     solo-scoped mute. The strips appear BEFORE that, so a test that settles
    ///     on the strips alone and then races a counted number of yields can press
    ///     Solo inside the rebuild and read a wrong answer; it settles on this
    ///     instead. Read on the UI thread.
    /// </summary>
    internal bool IsRebuilding => _loading;

    public void Rebuild()
    {
        if (_loading)
        {
            _rebuildRequested = true;
            return;
        }

        _ = RunRebuildLoopAsync();
    }

    /// <summary>
    ///     <b>The catch is the outer half of a two-layer guard, and it is not
    ///     redundant.</b> This runs as a DISCARDED task
    ///     (<c>_ = RunRebuildLoopAsync()</c>), so anything that escapes here
    ///     faults a task nobody observes — no log, no status, and the mixer AND
    ///     the engine keep the previous project's stems, which is exactly the
    ///     outcome the coalescing above exists to prevent, arriving by a
    ///     different door.
    ///     <para>
    ///         <see cref="RebuildOnceAsync" /> catches its own body and logs it,
    ///         so the ordinary failure (a locked or corrupt WAV, an engine error)
    ///         never reached here. What did: a throw from inside THAT catch
    ///         handler. It sets <c>HasStems = false</c> outside any try, and that
    ///         raises <c>PropertyChanged</c> into arbitrary subscribers — so one
    ///         throwing binding target turned a logged, degraded rebuild into a
    ///         silent one.
    ///     </para>
    /// </summary>
    private async Task RunRebuildLoopAsync()
    {
        _loading = true;
        _rebuildRequested = false;
        try
        {
            bool again;
            do
            {
                await RebuildOnceAsync().ConfigureAwait(false);
                again = await InvokeWhileAliveAsync(TakePendingRebuildOrRelease).ConfigureAwait(false);
            } while (again);
        }
        catch (OperationCanceledException) when (IsReleased)
        {
            // Disposed mid-rebuild (see Dispose): the loop ends here, touching
            // neither the dispatcher nor the engine again. Not a failure.
        }
        catch (Exception ex)
        {
            _logger?.Error(AppLogCategory.Audio,
                $"Stem mixer rebuild failed; the mixer and the engine may still hold the " +
                $"previous project's stems. {ex.GetType().Name}: {ex.Message}", ex);
            // The latch has to come back even when the loop died, or every later
            // project switch is coalesced into a run that is already over. Safe
            // to write unconditionally here: no second loop can have started
            // while `_loading` was true.
            await InvokeWhileAliveAsync(() => _loading = false).ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     One indivisible UI-thread step: either take the rebuild that arrived
    ///     while this one was in flight, or release the in-flight latch.
    /// </summary>
    /// <remarks>
    ///     They cannot be two dispatched steps. <see cref="Rebuild" /> runs on
    ///     the UI thread and decides between "start a loop" and "flag the
    ///     running one" purely from <c>_loading</c>, so a <c>Rebuild()</c>
    ///     landing between a "nothing pending" read and a later
    ///     <c>_loading = false</c> would set <c>_rebuildRequested</c> on a loop
    ///     that has already exited — the dropped-rebuild bug the coalescing
    ///     exists to prevent, arriving through the gap between two posts.
    /// </remarks>
    private bool TakePendingRebuildOrRelease()
    {
        if (!_rebuildRequested)
        {
            _loading = false;
            return false;
        }

        _rebuildRequested = false;
        return true;
    }

    /// <summary>
    ///     Drops the engine's stem set AND the waveform cache built from it. The two
    ///     are one decision, and splitting them leaked across every project switch:
    ///     <see cref="StemWaveformCoordinator" /> is an app-wide singleton whose
    ///     per-stem pyramids are keyed by PATH, so a song whose stems are cleared
    ///     leaves a full pyramid per stem behind for the life of the process — and
    ///     its <c>_fileWaveform</c> keeps pointing at the CLOSED song, so turning
    ///     "waveform follows mix" back off on the NEXT project drew the previous
    ///     song's waveform over this one's timeline. Two of the four clear sites
    ///     below (the no-project and no-stems early returns) reset only the engine.
    ///     <para>
    ///         Every clear goes through here — pinned by
    ///         <c>UI/StemWaveformCacheLifetimeTests</c>, which fails on a bare
    ///         <c>_audio.ClearStems()</c> anywhere else in this type.
    ///     </para>
    /// </summary>
    // What the engine currently holds, so an identical rebuild does not reload it.
    private ChartProject? _loadedStemsProject;
    private string? _loadedStemsSignature;

    private void ClearStemsAndTheirWaveforms()
    {
        // The solo baseline describes the strips that are going away.
        _masterMutedBeforeSolo = null;
        _loadedStemsProject = null;
        _loadedStemsSignature = null;
        _audio.ClearStems();
        _waveform?.Reset();
    }

    /// <summary>
    ///     <b><see cref="Tracks" /> is an <see cref="ObservableCollection{T}" />
    ///     bound to the mixer, so every mutation of it — and of the properties
    ///     the strips raise — is dispatched through <see cref="_ui" />, never
    ///     left to the ambient <c>SynchronizationContext</c>.</b>
    ///     <para>
    ///         This method awaits a worker probe and an engine load, and resuming
    ///         with <c>ConfigureAwait(true)</c> and then
    ///         clearing and refilling <c>Tracks</c> inline is only correct
    ///         while a context is installed to come back to. In the app there
    ///         is one (Avalonia's), so it worked; anywhere else — and that
    ///         includes every unit test — the continuation lands on the pool and
    ///         the bound collection is rebuilt off the UI thread, which is a
    ///         data race against anything reading it. The dispatcher this type
    ///         is CONSTRUCTED with is the explicit answer, and
    ///         <c>StemMixerTracksThreadAffinityTests</c> pins it.
    ///     </para>
    /// </summary>
    private async Task RebuildOnceAsync()
    {
        try
        {
            var project = _state.ActiveProject;
            if (project == null)
            {
                await InvokeWhileAliveAsync(() =>
                {
                    Tracks.Clear();
                    HasStems = false;
                    ClearStemsAndTheirWaveforms();
                    GuitarRoles.Update(null, false, null, false);
                }).ConfigureAwait(false);
                return;
            }

            // Resolve relative stem paths against the project file's directory —
            // the base every persisted relative name in the .dcproj uses (the cover
            // art does too; see parsing-psarc.md).
            // Pure string work — no disk access, safe on the UI thread.
            var projectDir = !string.IsNullOrWhiteSpace(_state.CurrentProjectPath)
                ? Path.GetDirectoryName(_state.CurrentProjectPath) ?? string.Empty
                : string.Empty;

            // …and the AUDIO's directory, which is the base every build up to
            // 0.49.6 actually WROTE against (RegisterAndPersistStemsAsync relativized
            // against ProjectAudioLocator.ResolveOutputDirectory). The two agree
            // whenever the audio sits beside the project — which is why this went
            // unnoticed — and diverge for a rooted AudioFileName, a PSARC session,
            // or a chart in a per-arrangement subfolder, where every stem then
            // silently failed to load. Read-side fallback only: nothing writes it.
            var legacyDir = ProjectAudioLocator.ResolveOutputDirectory(
                _state.CurrentProjectPath, _state.CurrentAudioFileName) ?? string.Empty;

            // Snapshot the declared stems before handing anything to a worker:
            // ProjectState mutates its collections on the UI thread.
            var declared = project.Stems.ToArray();
            // Read on the UI thread with the rest of the snapshot; the derived pair is
            // looked up on the worker below.
            var rolesWanted = project.GuitarRolesSplit;

            // EVERY filesystem probe runs off the UI thread, in the Services-side
            // StemFolderProbe. Directory.Exists,
            // Directory.EnumerateFiles and one File.Exists per stem must not run
            // inline here, on every project load AND every project-path change:
            // a project on a network share, a cloud placeholder folder that
            // has to hydrate, or a cold spinning disk would freeze the whole window for
            // the duration of the enumeration.
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var probe = await Task.Run(() => StemFolderProbe.Probe(projectDir, legacyDir, declared, rolesWanted, _guitarRoleStems))
                .ConfigureAwait(false);
            var probeMs = sw.Elapsed.TotalMilliseconds;

            // Back to the UI thread to touch Tracks — explicitly, not by hoping
            // for a captured context.
            var plan = await InvokeWhileAliveAsync(() => BuildStrips(project, probe, declared))
                .ConfigureAwait(false);
            if (plan == null) return;
            var stripsMs = sw.Elapsed.TotalMilliseconds;

            // A disposed mixer pushes nothing into an engine its container is
            // tearing down.
            ThrowIfReleased();
            if (plan.NeedsEngineLoad)
                await _audio.LoadStemsAsync(plan.Requests).ConfigureAwait(false);
            var engineMs = sw.Elapsed.TotalMilliseconds;

            await InvokeWhileAliveAsync(() => FinishRebuild(project, plan)).ConfigureAwait(false);
            _logger?.Info(AppLogCategory.Audio | AppLogCategory.Performance,
                $"Stem mixer rebuild: probe {probeMs:0} ms, strips at {stripsMs:0} ms, engine load at {engineMs:0} ms (load={plan.NeedsEngineLoad}), finished at {sw.Elapsed.TotalMilliseconds:0} ms.");
        }
        catch (OperationCanceledException) when (IsReleased)
        {
            // Disposed: not a stem failure, so no "continuing without stems"
            // degrade — the loop above ends quietly.
            throw;
        }
        catch (Exception ex)
        {
            // A stems folder with a locked or corrupt WAV must degrade to
            // "no stems", never propagate: the rebuild loop is fire-and-forget
            // from the constructor and from ProjectState change notifications.
            _logger?.Error(AppLogCategory.Audio, "Stem mixer rebuild failed; continuing without stems.", ex);
            // Degrading is a UI-visible write too: `HasStems` raises
            // PropertyChanged into bound targets, so it goes through the
            // dispatcher like every other one. Awaited, so a throwing subscriber
            // still escapes to the loop's outer catch (that is the whole gap
            // AThrowFromTheFailureHandlerIsStillReported pins).
            await InvokeWhileAliveAsync(() =>
            {
                HasStems = false;
                try { ClearStemsAndTheirWaveforms(); }
                catch (Exception clearEx)
                {
                    _logger?.Error(AppLogCategory.Audio, "Failed to clear stems after rebuild failure.", clearEx);
                }
            }).ConfigureAwait(false);
        }
    }

    /// <summary>
    ///     What one rebuild still owes the engine once the strips are built.
    ///     <c>null</c> from <see cref="BuildStrips" /> means "stop": the project
    ///     was swapped under us, or it declares no stems at all.
    /// </summary>
    private sealed record RebuildPlan(
        List<StemLoadRequest> Requests, string Signature, bool NeedsEngineLoad, string? GuitarPath, bool RolesActive);

    /// <summary>
    ///     Rebuilds the channel strips from a finished probe. <b>UI thread only</b>
    ///     — it clears and refills the bound <see cref="Tracks" /> collection.
    /// </summary>
    private RebuildPlan? BuildStrips(ChartProject project, StemProbe probe, Stem[] declared)
    {
        // The project may have been swapped while we were on the worker.
        // Applying stale results would flash the wrong stems; the coalesced
        // rebuild the swap queued will produce the right ones.
        if (!ReferenceEquals(_state.ActiveProject, project)) return null;

        Tracks.Clear();

        // Recovery path: when the .dcproj doesn't reference any stems but a
        // `stems/` folder exists next to it, adopt the WAV files there. For
        // projects produced by older builds (or by separation runs that
        // finished before we started saving the project automatically), so
        // the user doesn't have to regenerate just to see what is on disk.
        foreach (var (name, relativePath) in probe.Recovered)
        {
            project.Stems.Add(new Stem
            {
                Name = name,
                Path = relativePath,
                Local = true,
                Volume = 1.0,
                Offset = 0.0
            });
        }

        var stems = probe.Recovered.Count > 0 ? project.Stems.ToArray() : declared;
        if (stems.Length == 0)
        {
            HasStems = false;
            ClearStemsAndTheirWaveforms();
            GuitarRoles.Update(project, false, null, false);
            return null;
        }

        var requests = new List<StemLoadRequest>(stems.Length);
        _stemPaths.Clear();
        _waveform?.Reset();
        foreach (var s in stems)
        {
            // The probe already decided which base each stored path resolves
            // against, ON THE WORKER — recomputing it here would put a
            // File.Exists per stem back on the UI thread.
            if (!probe.Resolved.TryGetValue(s.Path, out var abs)) continue;
            if (string.IsNullOrWhiteSpace(abs) || !probe.Existing.Contains(abs)) continue;
            // A project stem that happens to carry a derived stem's name would share
            // its engine key; the derived pair wins while it is playing.
            if (probe.RolePair != null && GuitarRoleStems.IsRoleStem(s.Name)) continue;
            if (probe.RolePair is { } pair &&
                string.Equals(abs, probe.GuitarPath, StringComparison.OrdinalIgnoreCase))
            {
                // Guitar roles: the guitar plays as its two halves, in its place.
                var (lead, rhythm) = RoleStemsFor(project, s);
                AddStrip(requests, lead, pair.LeadPath);
                AddStrip(requests, rhythm, pair.RhythmPath);
                continue;
            }

            AddStrip(requests, s, abs);
        }

        // Master mix channel always sits at the front so users can A/B the
        // separated stems against the untouched original. Its mute comes from
        // the PROJECT — a hard-coded mute would be half of why
        // a project could open silent.
        var master = new StemTrackViewModel(_audio, "Original", project.OriginalMixMuted, _localization);
        master.AttachOwner(this);
        Tracks.Insert(0, master);

        // …and the other half: never OPEN a project with nothing audible. Every
        // stem muted plus the original muted is a reachable, saveable state that
        // plays silence over a perfectly healthy engine, with nothing in the log
        // and nothing in the UI to explain it. Load-time only — muting everything
        // while you work is your business, but a project you just opened has to
        // make a sound. The original is what comes back, because it is the one
        // channel that is always there.
        if (master.IsMuted && Tracks.All(t => t.IsMasterMix || t.IsMuted))
        {
            _logger?.Info(AppLogCategory.Audio,
                $"Every mixer channel was muted on load ({Tracks.Count - 1} stem(s)); " +
                "un-muting the original mix so the project is not silent.");
            master.IsMuted = false;
        }

        HasStems = requests.Count > 0;

        // Project loading fires ActiveProject and then CurrentProjectPath, so
        // every load pushes the SAME resolved stem set into the engine
        // twice unless guarded — and an engine load is the expensive half (six 183 s stems
        // measured at ~37 s per pass, so the whole project
        // would wait 75 s). The tracks above are rebuilt regardless (cheap); the
        // engine is asked only when the resolved (name, path) sequence differs
        // from what it already holds for this project.
        var signature = string.Join("\n", requests.Select(r => r.Name + "\0" + r.Path));
        var engineHasThisSet = ReferenceEquals(_loadedStemsProject, project) &&
                               _loadedStemsSignature == signature;

        if (requests.Count == 0) ClearStemsAndTheirWaveforms();

        return new RebuildPlan(requests, signature,
            NeedsEngineLoad: requests.Count > 0 && !engineHasThisSet,
            probe.GuitarPath, RolesActive: probe.RolePair != null);
    }

    private void AddStrip(List<StemLoadRequest> requests, Stem stem, string path)
    {
        requests.Add(new StemLoadRequest(stem.Name, path));
        _stemPaths[stem.Name] = path;
        var trk = new StemTrackViewModel(_audio, stem, _localization);
        trk.AttachOwner(this);
        Tracks.Add(trk);
    }

    /// <summary>The derived strips' stems for this project, seeded from the guitar's
    /// own fader, mute and solo the first time.</summary>
    private (Stem Lead, Stem Rhythm) RoleStemsFor(ChartProject project, Stem guitar)
    {
        if (_roleStems is { } known && ReferenceEquals(known.Project, project)) return (known.Lead, known.Rhythm);
        var lead = SeededFrom(guitar, GuitarRoleStems.LeadName);
        var rhythm = SeededFrom(guitar, GuitarRoleStems.RhythmName);
        _roleStems = (project, lead, rhythm);
        return (lead, rhythm);
    }

    private static Stem SeededFrom(Stem guitar, string name) => new()
    {
        Name = name,
        Path = string.Empty,
        Local = true,
        Volume = guitar.Volume,
        Muted = guitar.Muted,
        Solo = guitar.Solo,
    };

    /// <summary>
    ///     The tail of one rebuild, after the engine has (or has not) been given
    ///     the new stem set. <b>UI thread only</b> — it reads <see cref="Tracks" />
    ///     and writes engine-facing strip state.
    /// </summary>
    private void FinishRebuild(ChartProject project, RebuildPlan plan)
    {
        if (plan.NeedsEngineLoad)
        {
            _loadedStemsProject = project;
            _loadedStemsSignature = plan.Signature;
        }

        // Push per-stem state back into the engine after loading.
        foreach (var t in Tracks) t.ApplyToEngine();
        // …and re-derive the solo-scoped master mute for THIS project's strips. The
        // baseline belongs to the mixer contents: carrying the previous song's over
        // would restore ITS original-mix mute onto this one.
        _masterMutedBeforeSolo = null;
        SyncMasterToSoloState();
        RefreshWaveform();
        GuitarRoles.Update(project, HasStems, plan.GuitarPath, plan.RolesActive);
    }

    /// <summary>
    /// Called by a track when its mute state changes. When the user un-mutes
    /// the master "Original" channel we silence every stem (otherwise the
    /// original mix would play on top of the stems and audibly double). When
    /// the user un-mutes a stem we mute the original.
    /// </summary>
    internal void OnTrackMuteChanged(StemTrackViewModel changed, bool nowMuted)
    {
        if (nowMuted) return; // muting one track never forces another open
        if (changed.IsMasterMix)
        {
            foreach (var t in Tracks)
                if (!t.IsMasterMix) t.SetMutedSilently(true);
        }
        else
        {
            foreach (var t in Tracks)
                if (t.IsMasterMix) t.SetMutedSilently(true);
        }
    }

    /// <summary>
    ///     What the master "Original" strip's mute was before the current run of solos
    ///     started, so releasing the last solo puts it back. Null while no stem is
    ///     soloed.
    /// </summary>
    private bool? _masterMutedBeforeSolo;

    /// <summary>
    ///     Soloing a stem also silences the master "Original" mix — otherwise the
    ///     soloed stem would be drowned out by the original — and releasing the LAST
    ///     solo brings the strip back to whatever the user had.
    /// </summary>
    /// <remarks>
    ///     This must not be a ONE-WAY DOOR: muting the master through
    ///     <see cref="StemTrackViewModel.SetMutedSilently" /> and doing nothing at all on
    ///     release (<c>if (!nowSoloed) return;</c>) would mean one click on Solo turned
    ///     the original mix off for good and — because that path also calls
    ///     <see cref="PersistOriginalMixMuted" /> — writes it into the project, so it
    ///     survives a reload. The engine's own solo handling is deliberately transient
    ///     ("the toggle is restored as soon as the solo is released",
    ///     <c>JuceAudioPlaybackService.ApplyGain</c>); the UI would be the half that makes it
    ///     stick, which is why the symptom reads as "un-soloing never gives me the
    ///     original back".
    ///     <para>
    ///         The early return would also skip <see cref="RefreshWaveform" />, so with
    ///         "waveform follows mix" on, releasing a solo would leave the timeline drawing the
    ///         soloed stem alone.
    ///     </para>
    /// </remarks>
    internal void OnTrackSoloChanged(StemTrackViewModel changed, bool nowSoloed)
    {
        _ = changed;
        _ = nowSoloed;
        SyncMasterToSoloState();
        RefreshWaveform();
    }

    /// <summary>
    ///     Silences the master strip while ANY stem is soloed and restores the user's
    ///     own mute once none is. Solo-scoped: nothing here reaches the project.
    /// </summary>
    private void SyncMasterToSoloState()
    {
        var master = Tracks.FirstOrDefault(t => t.IsMasterMix);
        if (master == null) return;

        if (Tracks.Any(t => !t.IsMasterMix && t.IsSolo))
        {
            _masterMutedBeforeSolo ??= master.IsMuted;
            master.SetMutedForSolo(true);
        }
        else if (_masterMutedBeforeSolo is { } restore)
        {
            _masterMutedBeforeSolo = null;
            master.SetMutedForSolo(restore);
        }
    }

    /// <summary>Any change to what is audible re-draws the waveform, when it is
    /// following the mix.</summary>
    internal void OnTrackAudibilityChanged() => RefreshWaveform();

    /// <summary>
    ///     Writes the "Original" channel's mute back to the project, the way a stem
    ///     channel writes <see cref="Stem.Muted" />.
    /// </summary>
    /// <remarks>
    ///     That channel has no backing <see cref="Stem" />, so it has nowhere of its
    ///     own to persist to and went unsaved entirely — the mixer re-derived it on
    ///     load as "muted, because stems exist". Saving only half a mutually
    ///     exclusive pair is what turned "I want the original, not the stems" into
    ///     total silence.
    /// </remarks>
    internal void PersistOriginalMixMuted(bool muted)
    {
        if (_state.ActiveProject is { } project) project.OriginalMixMuted = muted;
    }

    /// <summary>
    ///     Re-derives the drawn waveform from the current channel state.
    ///     <para>
    ///         COALESCED, not queued: a fader drag raises a change per tick and
    ///         each mix is a walk over every audible stem's level 0. One run at a
    ///         time with a single pending re-run collapses a drag into two
    ///         passes — the same shape <see cref="Rebuild" /> uses for the stem
    ///         load it cannot afford to repeat either.
    ///     </para>
    /// </summary>
    private void RefreshWaveform()
    {
        if (_waveform == null) return;
        if (_waveformRefreshInFlight)
        {
            _waveformRefreshPending = true;
            return;
        }

        var follow = WaveformFollowsMix;
        var channels = Tracks
            .Select(t => (
                // The master strip's fader is NOT a per-channel gain: it writes
                // `IAudioPlaybackService.SongVolume`, which the engine applies to
                // the original mix AND to every stem
                // (`ApplyStemGainsLocked`: `UserVolume * master`). It is therefore
                // a common factor that cancels out of the RELATIVE balance a
                // drawing shows, and feeding it in as this channel's gain shrinks
                // the original against stems the same slider just made equally
                // quiet. The drawing is a picture of the BALANCE, not a meter.
                Channel: new StemMixChannel(t.Name, t.IsMasterMix,
                    t.IsMasterMix ? 1.0 : t.VolumeLinear, t.IsMuted, t.IsSolo),
                Path: t.IsMasterMix ? null : _stemPaths.GetValueOrDefault(t.Name)))
            .ToList();

        _waveformRefreshInFlight = true;
        _ = Task.Run(async () =>
        {
            try
            {
                await _waveform.ApplyAsync(follow, channels).ConfigureAwait(false);
            }
            finally
            {
                PostWhileAlive(() =>
                {
                    _waveformRefreshInFlight = false;
                    if (!_waveformRefreshPending) return;
                    _waveformRefreshPending = false;
                    RefreshWaveform();
                });
            }
        });
    }
}
