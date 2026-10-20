using System.ComponentModel;
using System.Diagnostics;
using Avalonia.Threading;
using DotCharter.Rendering.Data;
using DotCharter.Rendering.Diagnostics;
using DotCharter.Rendering.Engines;
using DotCharter.Rendering.Vulkan;
using DotCharter.Services.Export.Rocksmith;
using DotCharter.UI.Constants;
using DotCharter.UI.Controls.Themes;
using DotCharter.UI.ViewModels.Editor;
using Microsoft.Extensions.DependencyInjection;
using DotCharter.Core.Graphics;
using ProjectState = DotCharter.Services.State.ProjectState;

namespace DotCharter.UI.Controls;

/// <summary>
///     Core of the 3D preview render loop: render thread + snapshot staging +
///     idle gating (<see cref="Preview3DFrameGate" />) + fps cap/trickle
///     (<see cref="Preview3DPacing" />) + <see cref="FrameStats" />. Its one
///     host is <see cref="Preview3DComposedCanvas" /> (Avalonia-composed
///     output); the host supplies HOW the engine initializes — the
///     <c>initializeEngine</c> delegate passed to <see cref="Start" />.
/// </summary>
internal sealed class Preview3DRenderDriver
{
    private readonly object _dataLock = new();
    private readonly Stopwatch _fpsStopwatch = Stopwatch.StartNew();

    /// <summary>
    ///     3D frame-time percentiles over the last ~512 frames. Fed on the
    ///     render thread only; read on demand (HUD, env-gated perf gate).
    /// </summary>
    public FrameTimeRing FrameStats { get; } = new();

    // Per-phase cost rings (render thread only): CPU time inside
    // RunSnapshotUpdate and inside Fretboard3DEngine.RenderFrame (the latter
    // INCLUDES the present wait under FIFO). interval − snapshot − render ≈
    // pacing sleep, so the env-gated [3DStats] line can attribute the loop's
    // milliseconds without a profiler attach.
    private readonly FrameTimeRing _snapshotTimes = new();
    private readonly FrameTimeRing _renderTimes = new();

    private readonly HashSet<(long timeMs, int stringIdx)> _linkedTargetsBuffer = [];
    private readonly HashSet<(long timeMs, int stringIdx)> _techniqueOriginsBuffer = [];
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

    private readonly SnapshotUpdateContext _updateContext = new();
    private readonly RepeatChordLatch _repeatChords = new();

    private CancellationTokenSource? _cancellationTokenSource;
    private int _capoSafe;
    private string _currentDebugText = "Initializing...";

    private ChartEditorViewModel? _editorSafe;
    private Fretboard3DEngine? _engine;

    private int _fpsFrameCount;
    // How many of the last second's frames were idle-trickle frames — frames
    // that came out of a park, carry a substituted delta and contribute NOTHING
    // to FrameStats. When that is all of them the HUD must say "idle N Hz", not
    // "N fps" beside percentiles from the last time the loop really ran
    // (Preview3DStatsLine).
    private int _idleFrameCount;
    private bool _pendingFrameWasIdle;
    // Attempts that produced no frame in the last second (composed
    // back-pressure — the compositor owns every pool image). They are the
    // usual reason a healthy loop presents well under its cap, and printing
    // the cap alone leaves "34 fps at cap 63" unexplained.
    private int _declinedFrameCount;
    private volatile bool _isRenderable = true;
    // Immersive game-run latch: while set, the corner render-stats line is a
    // diagnostic gated behind the detection monitor. Editor mode (false) keeps
    // the line always-on, unchanged. Flipped rarely (gameplay enter/exit), so a
    // plain volatile read on the render thread is enough — mirrors _isRenderable.
    private volatile bool _gameplayPresentation;
    private int _visibilityProbePending;
    private double _lastDeltaTimeMs;

    /// <summary>
    ///     Time since the last frame that actually REACHED THE SCREEN — the
    ///     interval <see cref="FrameStats" /> records.
    ///     <para>
    ///         <b>It must NOT record <c>_stopwatch</c>, the interval between
    ///         render ATTEMPTS, or the HUD's two numbers
    ///         would disagree in the ACTIVE case</b> (as in
    ///         <c>34 fps · p50 7,0 / p99 48,4 ms</c>). A sample taken
    ///         before <c>RenderFrame</c> would let every DECLINED attempt — the
    ///         composed pump back-pressuring to the compositor's rate —
    ///         contribute a short interval to the percentiles while counting
    ///         for nothing in the fps tally. p50 would then be the spin rate of
    ///         the decline loop, not a frame time. Sampling on the success path
    ///         makes <c>fps ≈ 1000 / p50</c> true by construction.
    ///     </para>
    /// </summary>
    private readonly Stopwatch _sincePresent = Stopwatch.StartNew();
    private int _renderHeight = 1;
    private FrameSnapshot _renderSnapshot = new();

    private Task? _renderTask;
    private Task? _drainingTask;

    private int _renderWidth = 1;

    private FrameSnapshot _stagingSnapshot = new();
    private TransportViewModel? _transportSafe;
    private LivePlayViewModel? _livePlaySafe;
    private IVulkanRenderService? _vulkanService;

    // Live Play feedback (render thread only): the crowd meter, the sticky
    // EARLY/LATE + tier cues and the feedback sequence, drained into the
    // staging snapshot once per frame.
    private readonly LivePlayFeedbackDrain _livePlayFeedback = new();

    // Latch for whole-chart font-atlas pre-registration: re-register only when
    // the lyric/event collections change (instance or count), so the atlas
    // rebuild happens at load time instead of hitching mid-playback when an
    // unseen character scrolls into view. Text edits inside an existing entry
    // are still caught by the per-frame visible scan in Fretboard3DEngine.
    // It holds those two collections WEAKLY — see ChartTextRegistrationLatch.
    private readonly ChartTextRegistrationLatch _textRegistration = new();

    // Per-frame ferry fields for RunSnapshotUpdate. Kept as fields (not
    // method parameters) so RunSnapshotUpdate stays parameterless and we
    // could re-introduce a cached-delegate dispatch later if required.
    private double _pendingTimeMs;
    private int _pendingCapo;
    private double _pendingViewStartMs;
    private double _pendingViewEndMs;
    private ChartEditorViewModel? _pendingEditor;
    private LivePlayViewModel? _pendingLivePlay;
    private bool _lastSnapshotSuccess;

    // ---- Idle gating (0.14 P6) ----
    // While the transport is stopped and nothing changed, the render loop
    // parks (Preview3DFrameGate) instead of re-rendering at full rate. These
    // wake signals mirror the 2D ChartRenderer's RequestNextFrameRendering
    // sources: playhead (ProjectState.CurrentTimeMs + direct clock polling),
    // edits (ChartEditorViewModel.ChartDataChanged), selection
    // (ISelectionService.SelectionChanged), theme (ChartTheme.ThemeChanged)
    // and the host's styled properties. Starts dirty so the very first
    // frame renders unconditionally.
    private int _renderRequests = 1;
    private double _lastRenderedTimeMs = double.NaN;

    /// <summary>
    ///     The UNLED transport clock of the last rendered frame — the value the
    ///     idle gate compares. It must NOT be the led clock: on the composed
    ///     path every completed present publishes a latency sample into
    ///     <see cref="VisualLeadTiming" />'s EMA, so the lead moves a little on
    ///     every frame. Feeding a led clock into the gate made
    ///     present → EMA → "time changed" → render → present self-sustaining,
    ///     and the 0.14 P6 park never triggered — a paused editor burned the GPU
    ///     at the fps cap forever. The transport clock is the thing that
    ///     genuinely stops when playback does.
    /// </summary>
    private double _lastRenderedRawTimeMs = double.NaN;

    // The render clock's lead and its pause ease-out (render thread only; shared
    // rule with the 2D timeline).
    private readonly RenderLeadEase _leadEase = new();
    private int _lastRenderedWidth = -1;
    private int _lastRenderedHeight = -1;
    private bool _idleSkipped;
    private readonly Stopwatch _sinceLastRenderAttempt = Stopwatch.StartNew();
    // Since the last WOKEN frame: the trickle relaxes to 1 Hz past the settle window.
    private readonly Stopwatch _sinceLastWake = Stopwatch.StartNew();
    private ProjectState? _projectState;
    private ISelectionService? _selectionService;
    private ChartEditorViewModel? _subscribedEditor;

    /// <summary>Reads the host control's IsEffectivelyVisible — called on the
    /// UI thread by the parked loop's self-heal probe.</summary>
    private readonly Func<bool> _visibilityProbe;
    // The editor hands over its localization service; null until an editor is set.
    private volatile ShowlightChipLabelSource? _showlightChipLabels;

    private Action<Fretboard3DEngine>? _initializeEngine;

    // ---- Headless measurement hooks (0.16 composed-path benchmarking) ----
    // DOTCHARTER_3D_STRESS=1 renders continuously (the idle gate treats the
    // transport as always playing) so fps is measurable without loading a
    // chart or clicking play; DOTCHARTER_3D_STATS_LOG=1 writes the 1 Hz HUD
    // summary (+ composed present latency) to the app log every 5 s. Both
    // are read once, off in normal runs, and shared by both presentation
    // paths so native-vs-composed numbers come from the same code.
    private static readonly bool StressRender =
        Environment.GetEnvironmentVariable("DOTCHARTER_3D_STRESS") == "1";

    private static readonly bool StatsLogEnabled =
        Environment.GetEnvironmentVariable("DOTCHARTER_3D_STATS_LOG") == "1";

    private int _statsLogCountdown = 5;
    private bool _loggedRefreshResolve;

    /// <summary>The render clock leads the playhead by
    /// <see cref="VisualLeadTiming.EffectiveLeadMs" /> so the composed
    /// presentation latency (submit → compositor commit → scanout) cancels
    /// against the audio — mirror of the audio-latency-compensated
    /// playhead.</summary>
    public Preview3DRenderDriver(Func<bool> visibilityProbe, Func<nint>? windowHandleProbe = null)
    {
        _visibilityProbe = visibilityProbe;
        _windowHandleProbe = windowHandleProbe;
    }

    // "Cap 0 = the display's own rate", not "free-run". The composed path
    // never blocks on vsync (no swapchain) and the native path only blocks
    // under FIFO, so an uncapped loop renders hundreds of fps the panel can
    // never show (owner report: 600 fps burn). The monitor's refresh rate is
    // re-resolved lazily (window can move between monitors); the probe returns
    // a UI-thread-cached HWND, and the Win32 query itself is thread-safe.
    private readonly Func<nint>? _windowHandleProbe;
    private int _displayRefreshFps = 60;
    // NOT long.MinValue: TickCount64 - long.MinValue wraps NEGATIVE, so the
    // TTL check below never fired and the refresh probe NEVER ran — every
    // "cap 0 = display rate" user was silently pinned to the 60 Hz fallback
    // (v0.32.5 find). -Ttl guarantees the first call resolves.
    private long _displayRefreshResolvedAt = -DisplayRefreshTtlMs;
    private const long DisplayRefreshTtlMs = 5000;

    private int EffectiveMaxFps()
    {
        var cap = Preview3DPacing.MaxFps;
        if (cap != 0) return cap;
        // The DOTCHARTER_3D_STRESS benchmark hook exists to measure the
        // uncapped loop — keep it genuinely unlimited.
        if (StressRender) return 0;
        var now = Environment.TickCount64;
        if (now - _displayRefreshResolvedAt >= DisplayRefreshTtlMs)
        {
            var hwnd = _windowHandleProbe?.Invoke() ?? 0;
            _displayRefreshFps = Platform.DisplayRefresh.ForWindowOrDefault(hwnd);
            _displayRefreshResolvedAt = now;
            if (StatsLogEnabled && !_loggedRefreshResolve)
            {
                _loggedRefreshResolve = true;
                App.Services?.GetService<IAppLogger>()?.Info(AppLogCategory.Rendering,
                    $"[3DStats] display-refresh probe: hwnd=0x{hwnd:X} -> {_displayRefreshFps} Hz.");
            }
        }
        // Pace slightly ABOVE refresh: the cap wait sleeps in whole
        // milliseconds, so a cap of exactly the refresh rate lands ~5% under
        // it (measured 57 fps on a 60 Hz panel) and the compositor repeats a
        // frame every few vblanks. A ~5% headroom guarantees a fresh frame
        // per vblank while still eliminating the free-run (600 fps → ~63).
        return _displayRefreshFps + Math.Max(2, _displayRefreshFps / 20);
    }

    /// <summary>
    ///     Wakes the parked render loop for one immediate frame. Safe from any
    ///     thread; coalesces (a set flag stays set until the next frame).
    /// </summary>
    public void RequestRender() => Interlocked.Exchange(ref _renderRequests, 1);

    private void OnChartDataChanged() => RequestRender();

    private void OnSelectionChanged(object? sender, EventArgs e) => RequestRender();

    private void OnThemeChanged() => RequestRender();

    private void OnProjectStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Every change wakes, the playhead and each 3D setting the snapshot reads
        // alike (all are written through ProjectState), so a parked loop whose
        // trickle has relaxed to 1 Hz still shows a toggle at once.
        RequestRender();

        // A project switch must FORGET the project it closed. Fretboard3DEngine
        // is an app-wide singleton and its glyph sheet is only ever ADDED to, so
        // every lyric, section and phrase name of every song opened in the
        // session stayed packed into a fixed 2048² sheet — which eventually
        // overflows and renders the excess glyphs BLANK. Same ActiveProject hook
        // TrackContextRegistry prunes on; see docs/agents/ui/project-lifecycle.md.
        if (e.PropertyName == nameof(ProjectState.ActiveProject)) _engine?.ResetTextRegistrations();
    }

    private void SyncEditorSubscription(ChartEditorViewModel? editor)
    {
        if (ReferenceEquals(_subscribedEditor, editor)) return;
        _subscribedEditor?.ChartDataChanged -= OnChartDataChanged;
        _subscribedEditor = editor;
        _subscribedEditor?.ChartDataChanged += OnChartDataChanged;
    }

    // ---- Host input setters (UI thread) ----------------------------------

    public void SetEditor(ChartEditorViewModel? editor)
    {
        lock (_dataLock)
        {
            _editorSafe = editor;
        }

        _showlightChipLabels = editor is null ? null : new ShowlightChipLabelSource(editor.Localization);

        SyncEditorSubscription(editor);
        RequestRender();
    }

    public void SetTransport(TransportViewModel? transport)
    {
        lock (_dataLock)
        {
            _transportSafe = transport;
        }

        RequestRender();
    }

    public void SetLivePlay(LivePlayViewModel? livePlay)
    {
        lock (_dataLock)
        {
            _livePlaySafe = livePlay;
        }

        RequestRender();
    }

    public void SetCapo(int capo)
    {
        lock (_dataLock)
        {
            _capoSafe = capo;
        }

        RequestRender();
    }

    /// <summary>Immersive game-run latch. While active the bottom-left render-
    /// stats line only draws when the detection monitor is open — it is a
    /// diagnostic, not game HUD. Editor mode always shows it (unchanged).</summary>
    public void SetGameplayPresentation(bool active)
    {
        _gameplayPresentation = active;
        RequestRender();
    }

    /// <summary>Render size in PIXELS (the composed canvas measures it through
    /// <c>ComposedSurfaceGeometry</c>).</summary>
    public void SetSize(int width, int height)
    {
        _renderWidth = Math.Max(1, width);
        _renderHeight = Math.Max(1, height);
    }

    /// <summary>Hard renderability latch (attach/detach). Never depends on
    /// change notifications — see the host's attach override.</summary>
    public void SetRenderable(bool renderable) => _isRenderable = renderable;

    /// <summary>IsEffectivelyVisible change: coming back into view must not
    /// wait for the idle trickle.</summary>
    public void NotifyEffectiveVisibility(bool visible)
    {
        _isRenderable = visible;
        if (visible) RequestRender();
    }

    /// <summary>
    ///     Subscribes the idle-gate wake sources that live on singletons
    ///     (theme, project state, selection) + the given editor. UI thread;
    ///     idempotent (unsubscribe-then-subscribe, as the canvas always did).
    /// </summary>
    public void AttachWakeSources(ChartEditorViewModel? editor)
    {
        SyncEditorSubscription(editor);
        ChartTheme.ThemeChanged += OnThemeChanged;
        _projectState ??= App.Services?.GetService<ProjectState>();
        if (_projectState != null)
        {
            _projectState.PropertyChanged -= OnProjectStateChanged;
            _projectState.PropertyChanged += OnProjectStateChanged;
        }

        _selectionService ??= App.Services?.GetService<ISelectionService>();
        if (_selectionService != null)
        {
            _selectionService.SelectionChanged -= OnSelectionChanged;
            _selectionService.SelectionChanged += OnSelectionChanged;
        }

        RequestRender();
    }

    /// <summary>
    ///     Unsubscribes every singleton wake source — a detached host (and
    ///     its Vulkan snapshot graph) must never stay rooted for the process
    ///     lifetime (ViewSubscriptionLeakGuardTests contract).
    /// </summary>
    public void DetachWakeSources()
    {
        SyncEditorSubscription(null);
        ChartTheme.ThemeChanged -= OnThemeChanged;
        _projectState?.PropertyChanged -= OnProjectStateChanged;
        _selectionService?.SelectionChanged -= OnSelectionChanged;
    }

    // ---- Loop lifecycle ---------------------------------------------------

    public void Start(Action<Fretboard3DEngine> initializeEngine)
    {
        if (_renderTask != null && !_renderTask.IsCompleted) return;
        _initializeEngine = initializeEngine;
        _cancellationTokenSource = new CancellationTokenSource();
        var token = _cancellationTokenSource.Token;

        // A destroy/create pair (pop-out or re-parent of the host) can start
        // the next loop while the previous one is still finishing its last
        // frame. Chain onto the draining task so two loops never touch the
        // Vulkan renderer concurrently.
        var draining = _drainingTask;
        _renderTask = Task.Run(async () =>
        {
            if (draining is { IsCompleted: false })
            {
                try { await draining.ConfigureAwait(false); }
                catch { /* the old loop's failure is its own log entry */ }
            }

            RenderLoop(token);
        }, CancellationToken.None);
    }

    public void Stop()
    {
        var cts = _cancellationTokenSource;
        var task = _renderTask;
        if (cts == null) return;

        _cancellationTokenSource = null;
        _renderTask = null;
        _drainingTask = task;

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed elsewhere — nothing to do.
        }

        // Always dispose the CTS, but defer until the render task actually
        // observes the cancellation so we don't yank it out from under the
        // worker thread (which would surface as ObjectDisposedException
        // inside the loop). Falling through synchronously when the task is
        // already done keeps the common shutdown path allocation-free.
        if (task == null || task.IsCompleted)
        {
            cts.Dispose();
        }
        else
        {
            task.ContinueWith(_ =>
            {
                try { cts.Dispose(); }
                catch (ObjectDisposedException) { }
            }, TaskScheduler.Default);
        }
    }

    /// <summary>
    ///     Waits (bounded) for the stopped loop to actually exit — the host's
    ///     detach must let the last frame retire before it tears its present
    ///     pump down. The render loop never blocks on the UI thread
    ///     (snapshots are built on the render thread; the visibility probe is
    ///     a fire-and-forget post), so waiting here cannot deadlock.
    /// </summary>
    public void WaitForDrain(TimeSpan timeout)
    {
        var draining = _drainingTask;
        if (draining is { IsCompleted: false })
        {
            try
            {
                draining.Wait(timeout);
            }
            catch (AggregateException)
            {
                // The old loop's failure is its own log entry.
            }
        }
    }

    private void RenderLoop(CancellationToken token)
    {
        // Consecutive-failure counter: a transient Vulkan hiccup (surface
        // re-target, device busy) must not kill the whole loop — a dead loop
        // is a permanently frozen 3D view. Persistent failure logs loudly
        // and gives up so the error is visible instead of a silent freeze.
        var consecutiveFailures = 0;
        const int maxConsecutiveFailures = 120;

        // Declines are counted separately from exceptions, and are NEVER fatal: a
        // minimized window declines legitimately and indefinitely, so giving up on them
        // would permanently freeze the 3D view for anyone who minimizes the app.
        // Their only job is to stop a stall being SILENT and to stop it spinning.
        var consecutiveDeclines = 0;
        var declineStallReported = false;
        const int declineStallThreshold = 2000; // ~2 s of nothing but declines
        var logger = App.Services?.GetService<IAppLogger>();

        try
        {
            _vulkanService = App.Services!.GetRequiredService<IVulkanRenderService>();
            _engine = App.Services!.GetRequiredService<Fretboard3DEngine>();

            // --- Grab the Audio Service for direct time polling ---
            var audioService = App.Services!.GetRequiredService<IPlaybackTransport>();

            _initializeEngine!(_engine);

            _stopwatch.Restart();
            _fpsStopwatch.Restart();

            while (!token.IsCancellationRequested)
            {
                if (!_isRenderable)
                {
                    // Self-heal the visibility latch: a dock/pane re-layout can
                    // deliver a transient IsEffectivelyVisible=false that never
                    // gets a matching true notification (Avalonia only notifies
                    // on change, and the flip back can be swallowed when no
                    // re-parent happens). Re-read the real value on the UI
                    // thread while parked so a stale false can't freeze the
                    // view forever — the "frozen even after redocking" bug.
                    if (Interlocked.Exchange(ref _visibilityProbePending, 1) == 0)
                    {
                        Dispatcher.UIThread.Post(() =>
                        {
                            // Through NotifyEffectiveVisibility, not a bare field
                            // write: coming back into view must also ARM a frame.
                            // The watcher's path always did; this one did not —
                            // and this is precisely the path taken when the
                            // watcher's notification is the thing that went
                            // missing, so the heal woke the loop and then PARKED
                            // it, leaving the stale frame the heal exists to
                            // replace on screen until the idle trickle (100 ms by
                            // default, up to 1 s at the 1 Hz floor).
                            NotifyEffectiveVisibility(_visibilityProbe());
                            Interlocked.Exchange(ref _visibilityProbePending, 0);
                        }, DispatcherPriority.Background);
                    }

                    Thread.Sleep(33);
                    continue;
                }

                var w = _renderWidth;
                var h = _renderHeight;
                ChartEditorViewModel? localEditor;
                TransportViewModel? localTransport;
                LivePlayViewModel? localLivePlay;
                double localTimeMs;
                int localCapo;

                lock (_dataLock)
                {
                    localEditor = _editorSafe;
                    localTransport = _transportSafe;
                    localLivePlay = _livePlaySafe;
                    localCapo = _capoSafe;
                }

                // --- Fetch exact time directly from the MasterClock ---
                localTimeMs = audioService.CurrentTimeMs;
                // Keep the UNLED value: it is what the idle gate compares.
                var rawTimeMs = localTimeMs;
                // Composed path: lead the render clock by the present-latency
                // estimate so the frame lands on glass in sync with the audio
                // (a constant presentation delay is compensable; the pump's
                // jitter metric tracks what is not). Applied to the WHOLE
                // frame — snapshot window, camera, gate compare — so the
                // scene stays self-consistent at the led time.
                // Applied on BOTH presentation paths. The native swapchain still
                // queues a frame, waits for vblank and scans out — less latency than
                // the composed path, but not none, so zero compensation would be wrong.
                // Native publishes no measurement, so its lead is purely the user's
                // trim (default 0 = unchanged).
                // The lead is WALL ms (present latency); the clock is SONG ms,
                // which runs PlaybackSpeed times faster — scale, or 2× plays
                // half a lead behind the audio (VisualLeadTiming.LeadScale).
                // ONLY while playing, through the rule the 2D timeline uses: a
                // parked playhead is drawn at its own time. Added unconditionally, the
                // lead moved between idle frames (the present pump feeds its EMA after
                // every frame), so the paused highway crept and flickered.
                var isPlayingNow = audioService.IsPlaying || (localTransport?.IsPlaying ?? false);
                localTimeMs += _leadEase.Resolve(isPlayingNow,
                    VisualLeadTiming.EffectiveLeadMs * VisualLeadTiming.LeadScale(audioService.PlaybackSpeed),
                    Environment.TickCount64);

                if (w <= 0 || h <= 0 || localEditor == null || localTransport == null)
                {
                    Thread.Sleep(16);
                    continue;
                }

                // ---- Idle gating (0.14 P6) + fps cap (0.14.2) ----
                // Not playing, clock/size unchanged since the last rendered
                // frame, camera settled and no wake event pending → park at a
                // 4 ms signal poll instead of re-rendering at full rate. A
                // ≤10 Hz trickle inside the gate (Settings → Graphics) covers
                // state with no wake signal (settings toggles, in-place text
                // edits). Every wake renders its first frame immediately
                // (worst case one poll). A user fps cap additionally spaces
                // frames that WOULD render: a cap wait keeps the wake flags
                // unconsumed and does NOT count as idle — the elapsed time is
                // a genuine frame interval, so camera easing and FrameStats
                // stay fed with real deltas when the capped frame lands.
                var msSinceLastRender = _sinceLastRenderAttempt.Elapsed.TotalMilliseconds;
                var effectiveMaxFps = EffectiveMaxFps();
                var woke = Preview3DFrameGate.IsWake(
                    audioService.IsPlaying || localTransport.IsPlaying || StressRender,
                    // A pause ease-out keeps drawing until it settles at the parked time.
                    Volatile.Read(ref _renderRequests) != 0 || _leadEase.IsEasing,
                    // The UNLED clock: the lead is an EMA the composed present
                    // pump feeds on every frame, so comparing the LED clock made
                    // present → EMA → render self-sustaining and the park
                    // unreachable while paused (see _lastRenderedRawTimeMs).
                    rawTimeMs != _lastRenderedRawTimeMs,
                    w != _lastRenderedWidth || h != _lastRenderedHeight,
                    _engine.IsCameraAnimating);
                var frameDecision = Preview3DFrameGate.Decide(woke, msSinceLastRender, effectiveMaxFps,
                    Preview3DPacing.IdleFps, _sinceLastWake.Elapsed.TotalMilliseconds);

                if (frameDecision == Preview3DFrameGate.FrameDecision.Park)
                {
                    _idleSkipped = true;
                    Thread.Sleep(Preview3DFrameGate.IdlePollMs);
                    continue;
                }

                if (frameDecision == Preview3DFrameGate.FrameDecision.WaitForFpsCap)
                {
                    var remainingMs = Preview3DFrameGate.MsUntilNextCappedFrame(
                        msSinceLastRender, effectiveMaxFps);
                    // Sub-ms wait (v0.32.5): Thread.Sleep's ~1-1.6 ms quantum
                    // overshot every cap interval — a 294 fps cap (3.4 ms)
                    // paced ~5 ms ≈ 200 fps. The precise wait holds the cap to
                    // ~±0.3 ms. Still chunked at IdlePollMs so Stop/teardown
                    // stays as responsive as the old 4 ms poll.
                    Platform.PreciseSleep.Sleep(Math.Min(remainingMs, Preview3DFrameGate.IdlePollMs));
                    continue;
                }

                Interlocked.Exchange(ref _renderRequests, 0);
                _sinceLastRenderAttempt.Restart();
                if (woke) _sinceLastWake.Restart(); // never on a trickle frame

                var frameTime = (float)_stopwatch.Elapsed.TotalSeconds;
                _stopwatch.Restart();
                _pendingFrameWasIdle = _idleSkipped;
                if (_idleSkipped)
                {
                    // Coming out of an idle park: the stopwatch gap is idle
                    // time, not a frame time — recording it would poison the
                    // FrameStats percentiles, and feeding it to the camera
                    // easing would snap the pan. Substitute a nominal 60 Hz
                    // delta and skip the HUD sample for this one frame.
                    frameTime = 1f / 60f;
                }
                else
                {
                    _lastDeltaTimeMs = frameTime * 1000.0;
                }

                if (_fpsStopwatch.ElapsedMilliseconds >= 1000)
                {
                    var framesLastSecond = _fpsFrameCount;
                    var idleFramesLastSecond = _idleFrameCount;
                    var declinedLastSecond = _declinedFrameCount;
                    _fpsFrameCount = 0;
                    _idleFrameCount = 0;
                    _declinedFrameCount = 0;
                    _fpsStopwatch.Restart();
                    // Compact one-line stats string; HudComponent tucks it into
                    // the bottom-left corner at low opacity instead of the old
                    // multi-line debug block that dominated the viewport.
                    var stats = FrameStats.Summarize();
                    // Publish the 1 Hz summary for the Session Health dashboard (read-only).
                    Diagnostics.RenderDiagnostics.PublishPreview3D(stats);
                    _currentDebugText = Preview3DStatsLine.Format(
                        framesLastSecond,
                        idleFramesLastSecond,
                        Preview3DFrameGate.TrickleHz(Preview3DPacing.IdleFps, _sinceLastWake.Elapsed.TotalMilliseconds),
                        stats,
                        effectiveMaxFps,
                        declinedLastSecond,
                        _engine.GpuSubmitTimeMs,
                        _vulkanService.LastDrawCallCount,
                        _vulkanService.LastVertexCount);

                    if (StatsLogEnabled && --_statsLogCountdown <= 0)
                    {
                        _statsLogCountdown = 5;
                        var present = Diagnostics.RenderDiagnostics.Preview3DPresentLatency;
                        var interval = Diagnostics.RenderDiagnostics.Preview3DPresentInterval;
                        var validation = VulkanValidationDiagnostics.Active
                            ? $" · validation err={VulkanValidationDiagnostics.ErrorCount} warn={VulkanValidationDiagnostics.WarningCount}"
                            : "";
                        var effLead = VisualLeadTiming.EffectiveLeadMs;
                        var lead = effLead > 0 ? $" · lead {effLead:F2} ms" : "";
                        var snap = _snapshotTimes.Summarize();
                        var render = _renderTimes.Summarize();
                        logger?.Info(AppLogCategory.Rendering,
                            $"[3DStats] {_currentDebugText} · present p50 {present.P50Ms:F2} / p99 {present.P99Ms:F2} ms" +
                            $" · interval p50 {interval.P50Ms:F2} / p99 {interval.P99Ms:F2} / σ {Diagnostics.RenderDiagnostics.Preview3DPresentIntervalStdDevMs:F2} ms{lead}" +
                            $" · snap p50 {snap.P50Ms:F2} / p99 {snap.P99Ms:F2} ms · render p50 {render.P50Ms:F2} / p99 {render.P99Ms:F2} ms" +
                            $" · cap {effectiveMaxFps} fps · mode {_vulkanService.CurrentPresentMode}{validation}");
                    }
                }

                // The feed must cover what the board can actually show. The
                // horizon is user-controlled now (scroll speed x draw distance,
                // up to 12 s), so a fixed 4000 ms look-ahead would simply stop
                // projecting notes that are already supposed to be on screen —
                // they would wink into existence partway down the highway.
                var horizonMs = HighwaySpeedPolicy.HorizonMs(
                    (float)localEditor.UserSettings.HighwayScrollSpeed,
                    (float)localEditor.UserSettings.HighwayDrawDistanceZ);
                var lookAheadMs = Math.Max(ChartEditorConstants.Preview3DLookAheadMs,
                    horizonMs + ChartEditorConstants.Preview3DLookAheadMarginMs);

                var viewStartMs = localTimeMs - ChartEditorConstants.Preview3DLookBehindMs;
                var viewEndMs = localTimeMs + lookAheadMs;

                // Ferry inputs through fields so the cached delegate has nothing
                // to capture — eliminates the per-frame closure (System.Action +
                // <>c__DisplayClass) allocation visible in alloc traces.
                // Build the snapshot directly on the render thread. We used
                // to marshal this to the UI thread via Dispatcher.UIThread.Invoke
                // for thread-safety on the editor's collections, but that
                // allocated a DispatcherJob per frame (~60-144 Hz) and was a
                // primary contributor to Gen-1 collections — every Gen-1 root
                // mark phase suspended the audio pump's managed thread and
                // produced an audible click. The collections are stable during
                // playback (the user isn't editing); during active editing the
                // try/catch in RunSnapshotUpdate absorbs any transient race
                // (a structurally-modified collection just skips that frame
                // and the next frame catches up).
                _pendingEditor = localEditor;
                _pendingLivePlay = localLivePlay;
                _pendingTimeMs = localTimeMs;
                _pendingCapo = localCapo;
                _pendingViewStartMs = viewStartMs;
                _pendingViewEndMs = viewEndMs;
                _lastSnapshotSuccess = false;

                var snapTs = Stopwatch.GetTimestamp();
                RunSnapshotUpdate();
                _snapshotTimes.Add(Stopwatch.GetElapsedTime(snapTs).TotalMilliseconds);

                var snapshotSuccess = _lastSnapshotSuccess;

                if (snapshotSuccess) (_stagingSnapshot, _renderSnapshot) = (_renderSnapshot, _stagingSnapshot);
                // A failed snapshot (transient edit race) re-renders the old
                // one; re-arm the wake flag so the retry happens next
                // iteration instead of waiting for the idle trickle.
                else Interlocked.Exchange(ref _renderRequests, 1);

                if (token.IsCancellationRequested || !_isRenderable)
                {
                    // The wake flag was consumed BEFORE this attempt and the
                    // iteration produced no frame, so re-arm it exactly as the
                    // declined-frame, thrown-frame and failed-snapshot branches
                    // do. A hide landing between the gate and the present used
                    // to eat the edit / selection / theme change that woke us.
                    Interlocked.Exchange(ref _renderRequests, 1);
                    continue;
                }

                try
                {
                    var renderTs = Stopwatch.GetTimestamp();
                    var frameRendered = _engine.RenderFrame(w, h, frameTime, _renderSnapshot);
                    if (frameRendered) _renderTimes.Add(Stopwatch.GetElapsedTime(renderTs).TotalMilliseconds);
                    if (frameRendered)
                    {
                        // Present-to-present, and only on the path that
                        // presented: see _sincePresent. The first frame out of
                        // an idle park is still skipped — that interval is the
                        // park, not a frame.
                        var presentIntervalMs = _sincePresent.Elapsed.TotalMilliseconds;
                        _sincePresent.Restart();
                        if (!_pendingFrameWasIdle) FrameStats.AddFrameInterval(presentIntervalMs);

                        _fpsFrameCount++;
                        if (_pendingFrameWasIdle) _idleFrameCount++;
                        // Idle-gate bookkeeping: what the screen now shows.
                        _idleSkipped = false;
                        _lastRenderedTimeMs = localTimeMs;
                        _lastRenderedRawTimeMs = rawTimeMs;
                        _lastRenderedWidth = w;
                        _lastRenderedHeight = h;

                        consecutiveFailures = 0;
                        consecutiveDeclines = 0;
                        declineStallReported = false;
                    }
                    else
                    {
                        // BeginFrame declined (native: acquire hiccup or
                        // minimized; composed: the compositor still owns
                        // every pool image — its rate back-pressures ours).
                        //
                        // A decline is normally transient, but a WEDGED frame fence also
                        // surfaces as a permanent decline — and this branch must not reset
                        // the failure counter, or a wedge would spin at 1 kHz forever with no
                        // output and no error. It is not fatal (a minimized window
                        // declines legitimately and forever), so: say so once, then back
                        // off from the busy 1 ms yield to a lazy one.
                        _declinedFrameCount++;
                        consecutiveDeclines++;
                        // The wake flag was consumed BEFORE this attempt. A
                        // declined frame produced nothing, so re-arm it exactly
                        // as the failed-snapshot path does — otherwise a paused
                        // editor's edit / selection / theme change that landed
                        // on a decline waited for the idle trickle (100 ms by
                        // default, up to 1 s at the 1 Hz floor) to show.
                        Interlocked.Exchange(ref _renderRequests, 1);
                        if (consecutiveDeclines >= declineStallThreshold && !declineStallReported)
                        {
                            declineStallReported = true;
                            logger?.Warning(AppLogCategory.Rendering,
                                $"3D preview has declined {consecutiveDeclines} consecutive frames. " +
                                "Expected while minimized; otherwise the swapchain or a frame fence is stalled.");
                        }

                        Thread.Sleep(declineStallReported ? 16 : 1);
                    }
                }
                catch (Exception frameEx)
                {
                    // One bad frame (mid re-target, transient device state)
                    // is recoverable; a stream of them is not.
                    consecutiveFailures++;
                    // The wake flag was consumed BEFORE the attempt, and a frame
                    // that THREW produced nothing — so re-arm it exactly as the
                    // declined-frame and failed-snapshot branches do. Without
                    // this, a paused editor's edit / selection / theme change
                    // that landed on a transient device hiccup waited for the
                    // idle trickle (100 ms by default, up to 1 s at the 1 Hz
                    // floor) before it showed, which reads as "the highway did
                    // not pick up my edit".
                    Interlocked.Exchange(ref _renderRequests, 1);
                    if (consecutiveFailures == 1)
                    {
                        logger?.Warning(AppLogCategory.Rendering,
                            $"3D preview frame failed (will retry): {frameEx.Message}");
                    }

                    if (consecutiveFailures >= maxConsecutiveFailures)
                    {
                        logger?.Error(AppLogCategory.Rendering,
                            "3D preview render loop stopped after repeated frame failures.", frameEx);
                        break;
                    }

                    Thread.Sleep(16);
                }
            }
        }
        catch (Exception ex)
        {
            logger?.Error(AppLogCategory.Rendering, "3D preview render loop crashed.", ex);
            Debug.WriteLine($"Render Loop Crashed: {ex}");
        }
    }

    private void RunSnapshotUpdate()
    {
        var localEditor = _pendingEditor;
        if (localEditor == null) return;

        var localTimeMs = _pendingTimeMs;
        var localCapo = _pendingCapo;
        var viewStartMs = _pendingViewStartMs;
        var viewEndMs = _pendingViewEndMs;

        try
        {
            // ONE read of the active arrangement for the whole snapshot. It is a
            // mutable property on a view model the UI thread owns, and this runs
            // on the render thread: read three separate times (tones, Arrangement,
            // ChordTemplates) an arrangement switch landing mid-method described
            // ONE frame with two different parts — notes and levels from the old
            // one against the new one's tuning, string count and chord templates,
            // so frets landed on the wrong strings and template ids resolved
            // against the wrong table (which the catch below then swallowed as a
            // "transient edit race"). The collections above have the same
            // exposure and the same cure: capture, then project.
            var arrangement = localEditor.ActiveArrangement;
            // A dynamic-difficulty run draws the level it judges, phrase by phrase.
            var gameplay = localEditor.GameplayLevel;
            var notes = gameplay?.Notes ?? localEditor.PlacedNotes;
            var chords = gameplay?.Chords ?? localEditor.PlacedChords;
            var anchors = gameplay?.Anchors ?? localEditor.Anchors;
            var beats = localEditor.Beats;
            var handShapes = gameplay?.HandShapes ?? localEditor.HandShapes;

            TimelineMath.GetVisibleRange(notes, viewStartMs, viewEndMs, beats, 0, out var nStart,
                out var nEnd);
            TimelineMath.GetVisibleRange(chords, viewStartMs, viewEndMs, beats, 0, out var cStart,
                out var cEnd);
            TimelineMath.GetVisibleRange(anchors, viewStartMs, viewEndMs, beats, 0, out var aStart,
                out var aEnd);
            TimelineMath.GetVisibleRange(handShapes, viewStartMs, viewEndMs, beats, 0, out var hsStart,
                out var hsEnd);
            // A held note that started before the look-behind still rings across the
            // strike line; the one-element-before rule above dropped it.
            nStart = TimelineMath.ExtendStartToSounding(notes, nStart, viewStartMs, beats, static n => n.EndPosition);
            cStart = TimelineMath.ExtendStartToSounding(chords, cStart, viewStartMs, beats, static c => c.EndPosition);
            hsStart = TimelineMath.ExtendStartToSounding(handShapes, hsStart, viewStartMs, beats,
                static h => h.EndPosition);

            var bStart = Math.Max(0, TimelineMath.BinarySearchBeats(beats, viewStartMs));
            var bEnd = beats.Count - 1;
            if (beats.Count > 0)
            {
                bEnd = TimelineMath.BinarySearchBeats(beats, viewEndMs);
                if (bEnd < beats.Count - 1 && beats[bEnd].Time < viewEndMs) bEnd++;
            }

            _linkedTargetsBuffer.Clear();
            _techniqueOriginsBuffer.Clear();

            static bool NoteHasTechnique(IGuitarSound s)
                => s.SlideTo != null || s.UnpitchedSlide || s.Vibrato || s.Tremolo || s.BendValues.Count > 0;

            var nStartExt = Math.Max(0, nStart - 10);
            var nEndExt = Math.Min(notes.Count - 1, nEnd + 10);
            for (var i = nStartExt; i <= nEndExt; i++)
            {
                if (NoteHasTechnique(notes[i]))
                {
                    _techniqueOriginsBuffer.Add(((long)Math.Round(notes[i].Position.GetPositionMs(beats)),
                        notes[i].StringIndex));
                }
            }

            var cStartExt = Math.Max(0, cStart - 10);
            var cEndExt = Math.Min(chords.Count - 1, cEnd + 10);
            // Link landings: the 2D timeline's own walk, per string and only for a
            // sound with a sustain to land on. This driver kept a copy that
            // still OR-ed the chord-wide link and let a zero-sustain link land on
            // itself, so the highway hid heads the timeline drew.
            FrameSnapshot.CollectLinkTargets(notes, chords, beats, nStartExt, nEndExt, cStartExt, cEndExt,
                _linkedTargetsBuffer);
            for (var i = cStartExt; i <= cEndExt; i++)
            {
                var cTimeMs = (long)Math.Round(chords[i].Position.GetPositionMs(beats));
                for (var j = 0; j < chords[i].ChordNotes.Count; j++)
                {
                    var cn = chords[i].ChordNotes[j];
                    if (NoteHasTechnique(cn))
                        _techniqueOriginsBuffer.Add((cTimeMs, cn.StringIndex));
                }
            }

            var lyricPhrases = localEditor.LyricPhrases;
            var eventPoints = localEditor.EventPoints;
            // Tones are the THIRD latched collection here, as they already were
            // in the 2D path: the HUD draws the tone name, so its glyphs have to
            // be in the atlas before a switch, not after a mid-frame rebuild.
            var toneChanges = (IList<Core.Domain.Events.ToneChange>?)arrangement?.ToneChanges
                              ?? System.Array.Empty<Core.Domain.Events.ToneChange>();
            if (_textRegistration.NeedsRegistration(
                    lyricPhrases, lyricPhrases.Count, eventPoints, eventPoints.Count,
                    toneChanges, toneChanges.Count))
            {
                if (_engine?.PreRegisterChartText(lyricPhrases, eventPoints, toneChanges) == true)
                {
                    _textRegistration.Accept(
                        lyricPhrases, lyricPhrases.Count, eventPoints, eventPoints.Count,
                        toneChanges, toneChanges.Count);
                }
            }

            _updateContext.CurrentTimeMs = localTimeMs;
            // Events/tones/lyrics/showlights window themselves inside
            // FrameSnapshot.Update off this range (buffer 0 — parity with the
            // note windowing above). Leaving these unset collapses those
            // projections to nothing.
            _updateContext.ViewStartMs = viewStartMs;
            _updateContext.ViewEndMs = viewEndMs;
            _updateContext.VisibleRangeBufferMs = 0;
            _updateContext.Capo = localCapo;
            _updateContext.Chart = localEditor.CurrentChart;
            _updateContext.Arrangement = arrangement;
            // The corner render-stats line is a diagnostic, not game HUD (Preview3DStatsLine.Shown).
            _updateContext.DebugText = Preview3DStatsLine.Shown(_gameplayPresentation,
                _pendingLivePlay?.ShowDetectionMonitor == true, Preview3DStatsLine.HiddenByEnvironment)
                ? _currentDebugText
                : string.Empty;
            // The show-light chip is a charting aid: the editor's preview
            // shows it, an immersive Play Mode run does not.
            _updateContext.ShowlightChipLabels = _gameplayPresentation ? null : _showlightChipLabels?.Current();
            _updateContext.EventPoints = localEditor.EventPoints;
            _updateContext.PreviewEventPoints = localEditor.PreviewEventPoints;
            _updateContext.LyricPhrases = localEditor.LyricPhrases;
            _updateContext.ActiveNotes = notes;
            _updateContext.NoteStart = nStart;
            _updateContext.NoteEnd = nEnd;
            _updateContext.ActiveChords = chords;
            _updateContext.ChordStart = cStart;
            _updateContext.ChordEnd = cEnd;
            _updateContext.ActiveTemplates = arrangement?.ChordTemplates ??
                                             (IList<ChordTemplate>)[];
            _updateContext.ActiveBeats = beats;
            _updateContext.BeatStart = bStart;
            _updateContext.BeatEnd = bEnd;
            _updateContext.ActiveAnchors = anchors;
            _updateContext.AnchorStart = aStart;
            _updateContext.AnchorEnd = aEnd;
            _updateContext.ActiveHandShapes = handShapes;
            _updateContext.HandShapeStart = hsStart;
            _updateContext.HandShapeEnd = hsEnd;
            // The repeat box is the export's decision, as data.
            _updateContext.RepeatChords = _repeatChords.Get(_updateContext.ActiveTemplates, notes, chords,
                handShapes, beats, localEditor.HistoryRevision, Environment.TickCount64);
            _updateContext.SelectedNotes = localEditor.SelectedNotes;
            _updateContext.SelectedChords = localEditor.SelectedChords;
            _updateContext.SelectedHandShapes = localEditor.SelectedHandShapes;
            // Version the selection (the 2D renderer does the same): left at
            // the -1 default, FrameSnapshot would re-hash all six lookup sets on
            // EVERY frame — O(selection) × 6 at the fps cap, on the render
            // thread, for as long as anything was selected. Each snapshot keeps
            // its own last-seen version, so the staging/render pair each build
            // once per real selection edit.
            _updateContext.SelectionVersion = localEditor.Selection.SelectionVersion;
            _updateContext.PreviewNotes = localEditor.PreviewNotes;
            _updateContext.PreviewChords = localEditor.PreviewChords;
            _updateContext.PreviewHandShapes = localEditor.PreviewHandShapes;
            _updateContext.LinkedTargets = _linkedTargetsBuffer;
            _updateContext.TechniqueOrigins = _techniqueOriginsBuffer;
            _updateContext.Showlights = localEditor.Showlights;
            // Tone changes drive the HUD active-tone label (base tone + switch
            // flash). The 2D path already feeds these; the 3D highway — where
            // gameplay actually renders — did not, so the tone HUD showed
            // nothing during a run. Mirror the 2D source here.
            _updateContext.Tones = toneChanges;

            _stagingSnapshot.Update(_updateContext);
            _stagingSnapshot.EnableNoteHitExplosions = localEditor.UserSettings.EnableNoteHitExplosions;
            _stagingSnapshot.EnableHighwayNearFade = localEditor.UserSettings.EnableHighwayNearFade;
            _stagingSnapshot.EnableShowlightBeams = localEditor.UserSettings.EnableShowlightBeams;
            _stagingSnapshot.EnableShowlightFog = localEditor.UserSettings.EnableShowlightFog;
            _stagingSnapshot.EnableAnchorPreview = localEditor.UserSettings.EnableAnchorPreview;
            _stagingSnapshot.EnableAnchorSweep = localEditor.UserSettings.EnableAnchorSweep;
            _stagingSnapshot.AnchorPreviewLeadMs = localEditor.UserSettings.AnchorPreviewLeadMs;
            // Clamped here as well as on apply: UserSettings is plain mutable
            // state, and a zero scroll speed would divide into an infinite scale.
            _stagingSnapshot.HighwayScrollSpeed =
                HighwaySpeedPolicy.ClampScrollSpeed((float)localEditor.UserSettings.HighwayScrollSpeed);
            _stagingSnapshot.HighwayDrawDistanceZ =
                HighwaySpeedPolicy.ClampDrawDistanceZ((float)localEditor.UserSettings.HighwayDrawDistanceZ);
            _stagingSnapshot.Preview3DCameraFill =
                GraphicsSettingsPolicy.ClampCameraFillPercent(localEditor.UserSettings.Preview3DCameraFill);
            _stagingSnapshot.EnableBeatPulse = localEditor.UserSettings.EnableBeatPulse;
            _stagingSnapshot.EnableDepthFog = localEditor.UserSettings.EnableDepthFog;
            _stagingSnapshot.EnableVignette = localEditor.UserSettings.EnableVignette;
            _stagingSnapshot.EnableEnhancedHitFeedback = localEditor.UserSettings.EnableEnhancedHitFeedback;
            _stagingSnapshot.EnableCrowdBackground = localEditor.UserSettings.EnableCrowdBackground;
            _stagingSnapshot.EnableHighwayLighting = localEditor.UserSettings.EnableHighwayLighting;
            _stagingSnapshot.EnableBloom = localEditor.UserSettings.EnableBloom;
            _stagingSnapshot.EnableStringShimmer = localEditor.UserSettings.EnableStringShimmer;
            _stagingSnapshot.EnableHitSparks = localEditor.UserSettings.EnableHitSparks;
            _stagingSnapshot.EnableFilmicTonemap = localEditor.UserSettings.EnableFilmicTonemap;
            _stagingSnapshot.LeftyFlip = localEditor.UserSettings.LeftyFlip;
            _stagingSnapshot.InvertStrings = localEditor.UserSettings.InvertStrings;

            // Live Play verdicts: drain the judgement queue into this frame's
            // snapshot. The queue is drained even while inactive so stale
            // events from a stopped session never leak into the next one.
            var localLivePlay = _pendingLivePlay;
            var livePlayActive = localLivePlay?.IsActive == true;
            _stagingSnapshot.LivePlayActive = livePlayActive;
            // Resume-rewind dead-note horizon (see DeadNoteMath). Published by the VM
            // as an atomic bit pattern — this reads it from the render thread.
            _stagingSnapshot.LivePlayResolvedThroughMs = livePlayActive
                ? localLivePlay!.ResolvedThroughMs
                : double.NegativeInfinity;
            // The crowd meter, the feedback markers and the sticky EARLY/LATE +
            // tier cues; a timing GRADE feeds only the cues (LivePlayFeedbackDrain).
            _livePlayFeedback.Drain(localLivePlay?.FeedbackEvents, livePlayActive,
                localLivePlay?.Streak ?? 0, localTimeMs, _stagingSnapshot);

            _lastSnapshotSuccess = true;
        }
        catch (Exception ex)
        {
            // Ignore rendering during rapid UI breakdown states, but log the issue so it doesn't fail silently.
            Debug.WriteLine($"Failed to update rendering snapshot: {ex.Message}");
        }
    }
}
