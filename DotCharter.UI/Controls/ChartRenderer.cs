using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Rendering;
using DotCharter.Rendering.Data;
using DotCharter.Rendering.Diagnostics;
using DotCharter.Rendering.Timeline2D;
using DotCharter.Tone.UI.Controls;
using DotCharter.UI.Constants;
using DotCharter.UI.Controls.Layers;
using DotCharter.UI.Controls.Layers.Helpers;
using DotCharter.UI.Controls.Timeline2D;
using DotCharter.UI.Editing.Linking;
using DotCharter.UI.Editing.Tracks;
using DotCharter.UI.Input;
using DotCharter.UI.ViewModels.Editor;
using Microsoft.Extensions.DependencyInjection;
using ProjectState = DotCharter.Services.State.ProjectState;

namespace DotCharter.UI.Controls;

/// <summary>
///     The 2D chart timeline viewport. This control owns the
///     frame CONTENT only (snapshot staging, ribbon layout, the layer loop,
///     glyph registration) and presents through the composed-Vulkan
///     <see cref="VulkanTimelineBackend" /> — D3D11 keyed-mutex shared
///     images Avalonia's compositor composes in-airspace. There is no other
///     backend (the OpenGL path was removed with v0.33). Input, hit-testing
///     (<see cref="ICustomHitTest" /> — the control itself is always the
///     pointer source) and the <see cref="IChartViewport" /> contract are
///     presentation-independent. Still a <see cref="Decorator" />: the Child
///     slot hosts the localized error surface when composed-Vulkan init
///     fails on a real compositor (never a silent blank pane); under the
///     headless test platform the control degrades to a no-op render.
/// </summary>
public partial class ChartRenderer : Decorator, ICustomHitTest, IChartViewport
{
    public static readonly StyledProperty<ChartEditorViewModel?> EditorProperty =
        AvaloniaProperty.Register<ChartRenderer, ChartEditorViewModel?>(nameof(Editor));

    public static readonly StyledProperty<TransportViewModel?> TransportProperty =
        AvaloniaProperty.Register<ChartRenderer, TransportViewModel?>(nameof(Transport));

    public static readonly StyledProperty<double> PixelsPerMsProperty =
        AvaloniaProperty.Register<ChartRenderer, double>(nameof(PixelsPerMs), 0.2);

    public static readonly StyledProperty<WaveformSamples?> WaveformDataProperty =
        AvaloniaProperty.Register<ChartRenderer, WaveformSamples?>(nameof(WaveformData));

    public static readonly StyledProperty<bool> ShowWaveformProperty =
        AvaloniaProperty.Register<ChartRenderer, bool>(nameof(ShowWaveform), true);

    public static readonly StyledProperty<bool> DrawLabelsProperty =
        AvaloniaProperty.Register<ChartRenderer, bool>(nameof(DrawLabels), true);

    /// <summary>
    /// When set, the renderer draws the notes/chords/handshapes/anchors of
    /// this arrangement instead of the active arrangement. Used by the
    /// stacked multi-track view to render every arrangement in its own full
    /// timeline. Selection/hover/edit interactions are suppressed for
    /// non-active overrides — clicks fall through to the wrapper which
    /// switches the active arrangement.
    /// </summary>
    public static readonly StyledProperty<Arrangement?> OverrideArrangementProperty =
        AvaloniaProperty.Register<ChartRenderer, Arrangement?>(nameof(OverrideArrangement));

    private readonly TimelineBatcher _batcher = new();
    private readonly ChartRenderContext _renderCtx = new();
    private readonly Stopwatch _fpsStopwatch = Stopwatch.StartNew();
    private readonly Stopwatch _frameStopwatch = Stopwatch.StartNew();

    /// <summary>
    ///     2D frame-to-frame interval percentiles over the last ~512 frames.
    ///     During continuous playback rendering, p50 IS the compositor tick
    ///     cadence — the number that decides whether the 144 fps 2D target is
    ///     reachable without an Avalonia render-timer change.
    /// </summary>
    public FrameTimeRing FrameStats { get; } = new();
    private readonly List<ITimelineRenderLayer> _layers = [];
    private readonly HashSet<(long timeMs, int stringIdx)> _linkedTargetsBuffer = [];
    private readonly FrameSnapshot _snapshot = new();
    private readonly SnapshotUpdateContext _snapshotCtx = new();
    private readonly RibbonEventContentLatch _ribbonEvents = new();

    // ---- Env-gated per-phase frame stats (DOTCHARTER_2D_STATS_LOG=1) ----
    // The [2DStats] sibling of the 3D driver's [3DStats] hook: accumulates
    // per-phase UI-thread cost inside RenderTimelineFrame and logs a 5 s
    // summary, so "where do the frame's milliseconds go" is answerable on a
    // live run without a profiler attach. Inert (a bool check) when unset.
    private static readonly bool StatsLogEnabled =
        Environment.GetEnvironmentVariable("DOTCHARTER_2D_STATS_LOG") == "1";

    private const int PhaseRibbon = 0;
    private const int PhaseSnapshot = 1;
    private const int PhaseAtlas = 2;
    private const int PhaseBegin = 3;
    private const int PhaseLayers = 4;
    private const int PhaseEnd = 5;
    private const int PhaseCount = 6;
    private static readonly string[] PhaseNames = ["ribbon", "snap", "atlas", "begin", "layers", "end"];
    private readonly double[] _phaseSumMs = new double[PhaseCount];
    private readonly double[] _phaseMaxMs = new double[PhaseCount];
    private double[] _layerSumMs = [];
    private int _statsFrames;
    private int _statsBeginSkips;
    private int _statsLogCountdown = 5;
    private long _statsLastFenceSkips;
    private long _statsLastPoolSkips;

    private IPlaybackTransport? _audioService;
    private ProjectState? _projectState;
    private IGhostArrangementsService? _ghostArrangements;
    private readonly List<GuitarNote> _ghostNotesBuffer = [];
    private readonly List<Chord> _ghostChordsBuffer = [];
    private int _currentFps;
    private VulkanTimelineBackend? _backend;
    private Control? _errorSurface;
    private bool _attachedToTree;
    private bool _effectivelyVisible;
    private int _frameCount;

    public ChartRenderer()
    {
        // Frame requests are gated on EFFECTIVE visibility. The editor subtree
        // is IsVisible-gated (MainWindow.axaml), i.e. HIDDEN, not detached —
        // and Avalonia leaves Bounds at its last arranged value when IsVisible
        // goes false, so RenderTimelineFrame's Bounds guard never fired. While
        // hidden, OnProjectStateChanged kept firing on CurrentTimeMs at 60 Hz
        // during playback and the whole pipeline (snapshot -> ribbon layout ->
        // 10 layers -> vertex upload -> Vulkan submit -> compositor import) ran
        // at compositor rate. Worst case is Play Mode, where the invisible 2D
        // timeline competes with the 3D highway for the same UI thread and GPU.
        // Same mechanism as Preview3DComposedCanvas /
        // MenuBackdropControl — never an IsEffectivelyVisible property branch,
        // which Avalonia 12 does not raise (see EffectiveVisibilityWatcher).
        _ = new EffectiveVisibilityWatcher(this, OnEffectiveVisibilityFlip);

        // Layers that don't need DI are added here. EventLayer depends on the
        // shared PinnedLabelHitRegistry (so pinned-label clicks can reach
        // EventInteractionTool), so it's constructed in OnBackendReady once
        // App.Services is reachable. The ordering is preserved by inserting
        // EventLayer at the correct index later.
        _layers.Add(new BackgroundLayer { IncludeBeatGrid = false });
        _layers.Add(new WaveformLayer());
        // Beat / measure / subdivision grid sits ON TOP of the waveform so
        // the grid stays visible against the (otherwise opaque) peaks.
        _layers.Add(new BeatGridLayer());
        _layers.Add(new LaneLayer());
        // (EventLayer gets inserted at index 4 in OnBackendReady)
        _layers.Add(new CursorLayer());
        _layers.Add(new HandShapeLayer());
        _layers.Add(new FretAnchorLayer());
        _layers.Add(new NoteLayer());
        _layers.Add(new LinkLayer());
        _layers.Add(new PlayheadLayer());
    }

    public ChartEditorViewModel? Editor
    {
        get => GetValue(EditorProperty);
        set => SetValue(EditorProperty, value);
    }

    public TransportViewModel? Transport
    {
        get => GetValue(TransportProperty);
        set => SetValue(TransportProperty, value);
    }

    public double PixelsPerMs
    {
        get => GetValue(PixelsPerMsProperty);
        set => SetValue(PixelsPerMsProperty, value);
    }

    public WaveformSamples? WaveformData
    {
        get => GetValue(WaveformDataProperty);
        set => SetValue(WaveformDataProperty, value);
    }

    public bool ShowWaveform
    {
        get => GetValue(ShowWaveformProperty);
        set => SetValue(ShowWaveformProperty, value);
    }

    public bool DrawLabels
    {
        get => GetValue(DrawLabelsProperty);
        set => SetValue(DrawLabelsProperty, value);
    }

    public Arrangement? OverrideArrangement
    {
        get => GetValue(OverrideArrangementProperty);
        set => SetValue(OverrideArrangementProperty, value);
    }

    public double ViewportWidth => Bounds.Width;
    public double ViewportHeight => Bounds.Height;

    /// <summary>
    /// Top-ribbon layout for the arrangement this renderer is bound to (override
    /// arrangement on stacked rows, otherwise the active one): its track's lane
    /// settings through <see cref="RibbonLaneRules" /> — empty lanes give their
    /// height to the note lanes. The hit tester reads this same property, so rows
    /// hide/show consistently for drawing and clicking.
    /// </summary>
    public RibbonLayout TopRibbon =>
        CurrentTrack is { } ctx ? RibbonLaneRules.Compose(ctx.Ribbon, RibbonContentOf(ctx)) : RibbonLayout.Default;

    private TrackEditorContext? CurrentTrack =>
        (OverrideArrangement ?? Editor?.ActiveArrangement) is { } arr && Editor != null
            ? Editor.TrackContexts.GetOrCreate(arr)
            : null;

    /// <summary>The lane settings the lane menu edits, for this renderer's track.</summary>
    internal RibbonVisibility? CurrentRibbon => CurrentTrack?.Ribbon;

    /// <summary>What this renderer's track has per lane — the lane menu's hints read it.</summary>
    internal RibbonContent? CurrentRibbonContent() => CurrentTrack is { } ctx ? RibbonContentOf(ctx) : null;

    private RibbonContent RibbonContentOf(TrackEditorContext ctx)
    {
        if (Editor == null) return default;
        // Latched: a phrases-only chart walked the whole list per call.
        var (hasSections, hasPhrases) =
            _ribbonEvents.Get(ctx.EventPoints, Editor.HistoryRevision, Environment.TickCount64);
        return new RibbonContent(RibbonLaneRules.DistinctTones(ctx.Arrangement.ToneBase, ctx.Tones),
            hasSections, hasPhrases, Editor.LyricPhrases.Count > 0, LinkLaneModel.HasLinks(Editor.ActiveProject, ctx.Arrangement));
    }

    public double TopMargin => TopRibbon.TotalHeight;

    /// <summary>v0.34 lefty flip (IChartViewport): the string→lane order this
    /// renderer draws with — hit-testing reads it from here so both always
    /// agree. Sourced from the profile-fed ProjectState channel.</summary>
    public bool LeftyFlip => _projectState?.LeftyFlip ?? false;

    /// <summary>Inverted string layout (tab convention, IChartViewport): a
    /// vertical string flip that composes (XOR) with <see cref="LeftyFlip"/> in
    /// the 2D lanes. Sourced from the AppConfig-fed ProjectState channel.</summary>
    public bool InvertStrings => _projectState?.InvertStrings ?? false;
    public double BottomMargin => UiLayoutConstants.HighwayBottomMargin;

    /// <summary>
    ///     The vertical layout this renderer drew its LAST frame with, and an
    ///     event that fires only when its SHAPE changed.
    ///     <para>
    ///         <see cref="TimelineGutterControl" /> labels these lanes down the
    ///         left edge and has to move with them. It cannot subscribe to the
    ///         inputs instead: the ribbon's real height depends on the user
    ///         toggles AND on whether the track has any tones / sections /
    ///         phrases / lyrics at all (rows auto-collapse), i.e. on the chart's
    ///         CONTENT, which no single property change announces. Publishing
    ///         the ANSWER is the only signal that cannot go stale.
    ///     </para>
    ///     <para>
    ///         A frame is built per compositor tick and almost every one of them
    ///         produces an identical layout, so the event is gated — through
    ///         <see cref="GutterPublishGate" />, which compares the layout SHAPE
    ///         <b>and</b> the arrangement's tuning. Comparing the shape
    ///         only would mean a same-shape tuning change (any six-string song replacing
    ///         any other) never reached the gutter, which would go on
    ///         printing the previous chart's string names. Stacked override rows do
    ///         NOT publish (`DrawLabels` is false there and they have no gutter),
    ///         the same carve-out `TimelineRenderedAnchorMs` makes.
    ///     </para>
    /// </summary>
    public TimelineGutterLayout GutterLayout { get; private set; }

    public event EventHandler? GutterLayoutChanged;

    private readonly GutterPublishGate _gutterGate = new();

    private void PublishGutterLayout(TimelineGutterLayout layout, Arrangement? arrangement)
    {
        if (OverrideArrangement != null || !DrawLabels) return;
        if (!_gutterGate.ShouldPublish(layout, arrangement)) return;
        GutterLayout = layout;
        GutterLayoutChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool HitTest(Point point) =>
        // Stacked rows render full, fully-interactive timelines too. The
        // wrapper promotes the row's arrangement to active on pointer-press
        // (so the shared editor tools operate on it) and the renderer keeps
        // the input — no fall-through is needed any more.
        true;

    private Rect? _marqueeRect;

    /// <summary>Current marquee-selection rubber band in viewport pixels,
    /// null when no marquee drag is active. Drawn by the sibling
    /// <see cref="MarqueeSelectionOverlay" /> — deliberately NOT part of the
    /// GL scene, so updating it per pointer move never re-renders the chart.</summary>
    public Rect? MarqueeRect => _marqueeRect;

    /// <summary>Raised when <see cref="MarqueeRect" /> changed (UI thread).</summary>
    public event EventHandler? MarqueeChanged;

    public void SetMarquee(Rect? marquee)
    {
        if (_marqueeRect == marquee) return;
        _marqueeRect = marquee;
        MarqueeChanged?.Invoke(this, EventArgs.Empty);
    }

    private RibbonVisibility? _subscribedRibbon;

    private int _continuousRenderRequests;

    /// <summary>
    /// Pin the render loop to its continuous-frame cadence (vsync
    /// aligned) for the duration of an interaction such as a minimap drag.
    /// Without this, while playback is paused each property-change-driven
    /// frame arrives at irregular intervals and the playhead motion looks
    /// stuttery even though the underlying seek throttle is 60 Hz. Each
    /// Begin must be balanced by End; nesting is safe.
    /// </summary>
    public void BeginContinuousRender()
    {
        Interlocked.Increment(ref _continuousRenderRequests);
        RequestNextFrameRendering();
    }

    public void EndContinuousRender()
    {
        if (Interlocked.Decrement(ref _continuousRenderRequests) < 0)
            Interlocked.Exchange(ref _continuousRenderRequests, 0);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == EditorProperty ||
            change.Property == PixelsPerMsProperty ||
            change.Property == ShowWaveformProperty ||
            change.Property.Name == "Bounds" ||
            change.Property == WaveformDataProperty ||
            change.Property == DrawLabelsProperty ||
            change.Property == OverrideArrangementProperty)
        {
            if (change.Property == OverrideArrangementProperty || change.Property == EditorProperty)
                SyncRibbonSubscription();
            RequestNextFrameRendering();
        }
    }

    private void SyncRibbonSubscription()
    {
        var arr = OverrideArrangement ?? Editor?.ActiveArrangement;
        var target = arr != null && Editor != null ? Editor.TrackContexts.GetOrCreate(arr).Ribbon : null;
        if (ReferenceEquals(_subscribedRibbon, target)) return;
        _subscribedRibbon?.PropertyChanged -= OnRibbonChanged;
        _subscribedRibbon = target;
        _subscribedRibbon?.PropertyChanged += OnRibbonChanged;
        SyncLinkHighlight(Editor?.LinkHighlight);
    }

    private LinkHighlight? _highlight;

    private void SyncLinkHighlight(LinkHighlight? target)
    {
        if (ReferenceEquals(_highlight, target)) return;
        _highlight?.Changed -= RequestNextFrameRendering;
        _highlight = target;
        _highlight?.Changed += RequestNextFrameRendering;
    }

    private void OnRibbonChanged(object? sender, PropertyChangedEventArgs e)
    {
        RibbonChangeNotifications++;
        RequestNextFrameRendering();
    }

    /// <summary>Test seam: how many ROWS-ribbon toggles have reached this
    /// renderer. It must keep moving after a detach + re-attach (pop-out /
    /// redock), which is what <c>SyncRibbonSubscription</c> in
    /// <c>OnAttachedToVisualTree</c> guarantees.</summary>
    internal int RibbonChangeNotifications { get; private set; }

    private void OnGhostArrangementsChanged() => RequestNextFrameRendering();

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attachedToTree = true;
        // The playhead deliberately does NOT arrive through an Avalonia
        // binding: every bound update costs ~10 KB on the compositor thread
        // (measured — see AudioPlaybackCoordinator). ProjectState raises
        // CurrentTimeMs with a cached PropertyChangedEventArgs at ≤60 Hz on
        // the UI thread, so subscribing directly is allocation-free.
        _projectState ??= App.Services?.GetService<ProjectState>();
        _projectState?.PropertyChanged += OnProjectStateChanged;

        // The ribbon subscription is torn down on DETACH, and it was only ever
        // rebuilt from OnPropertyChanged(Editor/OverrideArrangement) — but a
        // re-parent raises no property change, and the chart timeline composite
        // must stay a SINGLE instance across layout toggles and pop-out
        // (DockPaneContractTests), so a pop-out/redock IS detach + attach on the
        // same object. Without this call, toggling a ROWS ribbon row produced no
        // frame for the rest of the session. Idempotent (ReferenceEquals).
        SyncRibbonSubscription();
        EnsureBackend();
    }

    private void OnProjectStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        // A project switch must FORGET the project it closed, and the glyph
        // sheet is one of the things that would remember it: every lyric,
        // section name, phrase name and tone name ever registered would stay in the
        // atlas for the life of the process (the set is only ever added to), and
        // the fixed 2048² sheet eventually overflows — at which point the extra
        // glyphs render BLANK. Same hook TrackContextRegistry prunes on; see
        // docs/agents/ui/project-lifecycle.md.
        if (e.PropertyName == nameof(ProjectState.ActiveProject))
        {
            _backend?.FontAtlas?.ResetToSeedCharacters();
            // The per-frame registration is skipped while this signature is
            // unchanged, so it has to be invalidated with the set — otherwise
            // the NEW project's text is never re-registered.
            _lastRegisterSig = 0;
            _textRegistration.Reset();
            RequestNextFrameRendering();
        }

        // ViewAnchor/Follow drive the decoupled-viewport mapping (0.16 6.6):
        // a pan or a Follow flip moves the visible window exactly like a
        // playhead move does, so it needs the same redraw.
        if (e.PropertyName is (nameof(ProjectState.CurrentTimeMs)) or
            (nameof(ProjectState.TimelineViewAnchorMs)) or
            (nameof(ProjectState.TimelineFollowsPlayhead)) or
            // The string-edit mask is drawn on the lane headers, and Ctrl+1..9
            // is a keyboard-only gesture — nothing else invalidates for it, so
            // without this the mask would only appear on the next frame some
            // other change happened to produce.
            (nameof(ProjectState.StringEditMask)) or
            // Same reason as the mask: the loop region is punched in from the
            // keyboard while the transport may be paused, so nothing else would
            // produce the frame that shows it.
            (nameof(ProjectState.LoopRegion)) or
            (nameof(ProjectState.LoopEnabled)) or
            (nameof(ProjectState.LoopPendingStartMs)))
        {
            RequestNextFrameRendering();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _projectState?.PropertyChanged -= OnProjectStateChanged;

        // The ghost-arrangements service and per-track RibbonVisibility live
        // on long-lived singletons. Without explicit unsubscription here the
        // captured ChartRenderer (and its GL resources) would be rooted for
        // the rest of the process lifetime. The stacked multi-track view
        // creates and removes these renderers frequently, so the leak adds
        // up quickly.
        _ghostArrangements?.Changed -= OnGhostArrangementsChanged;
        _ghostArrangements = null;
        _subscribedRibbon?.PropertyChanged -= OnRibbonChanged;
        _subscribedRibbon = null;
        SyncLinkHighlight(null);

        // The pinned-label registry is a DI singleton keyed by viewport; a
        // stacked row that goes away must not leave its rects (and itself) in
        // it. Those renderers are created and removed frequently.
        App.Services?.GetService<PinnedLabelHitRegistry>()?.Forget(this);

        // The composed-Vulkan backend is torn down per detach and rebuilt on
        // the next attach (a pop-out re-targets the new TopLevel's
        // compositor).
        _attachedToTree = false;
        _backend?.Detach();
        _backend = null;

        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>The timeline's accessibility face; see the peer.</summary>
    protected override AutomationPeer OnCreateAutomationPeer() => new ChartRendererAutomationPeer(this);

    /// <summary>IChartViewport: schedules one redraw (coalesced) on the next
    /// compositor animation tick. Damage-driven — an idle timeline renders
    /// nothing; no-op while presentation is unavailable (init failed) or while
    /// the viewport is not EFFECTIVELY VISIBLE (the editor tab is hidden, not
    /// detached, so nothing else stops the 60 Hz playback frames).</summary>
    public void RequestNextFrameRendering()
    {
        if (!_effectivelyVisible) return;
        RequestFrameFromBackend();
    }

    private void RequestFrameFromBackend()
    {
        BackendFrameRequests++;
        _backend?.RequestFrame();
    }

    /// <summary>Test seam: frame requests that passed the visibility gate and
    /// reached the presentation backend. It must not move while the viewport is
    /// hidden.</summary>
    internal int BackendFrameRequests { get; private set; }

    /// <summary>True while this viewport's own IsVisible AND every ancestor's
    /// are set — maintained by <see cref="EffectiveVisibilityWatcher" />.</summary>
    internal bool IsEffectivelyVisibleForRender => _effectivelyVisible;

    private void OnEffectiveVisibilityFlip(bool visible)
    {
        _effectivelyVisible = visible;
        // Nothing rendered while hidden, so the surface still holds the frame it
        // was hidden with — one frame on the false->true flip, bypassing the
        // gate that is only just now open.
        if (visible) RequestFrameFromBackend();
    }

    private void EnsureBackend()
    {
        if (_backend != null) return;
        // A re-attach retries init from scratch (self-healing after e.g. a
        // driver hiccup); the error surface only returns if init fails again.
        ClearErrorSurface();
        var backend = new VulkanTimelineBackend(this);
        _backend = backend;
        _ = backend.InitializeAsync();
    }

    /// <summary>Composed-Vulkan init failed for this viewport. On a real
    /// compositor the timeline area shows a visible, localized error surface
    /// (never a silent blank pane); under the Avalonia.Headless test platform
    /// (<paramref name="showErrorSurface" /> false) the control degrades to a
    /// no-op render — every RequestNextFrameRendering call is simply ignored.
    /// A later re-attach (pop-out/redock) retries initialization.</summary>
    internal void OnComposedBackendFailed(VulkanTimelineBackend failed, bool showErrorSurface)
    {
        if (ReferenceEquals(_backend, failed)) _backend = null;
        if (showErrorSurface && _attachedToTree) ShowErrorSurface();
    }

    /// <summary>Puts the localized "timeline rendering unavailable" surface
    /// into the Decorator's Child slot. Theme-token styled (DynamicResource
    /// equivalents via resource observables — code-built because this control
    /// has no XAML); hit-test invisible so pointer input still reaches the
    /// ChartRenderer (keybind/marquee routing is unaffected).</summary>
    private void ShowErrorSurface()
    {
        if (_errorSurface == null)
        {
            var title = new TextBlock
            {
                Text = LocalizationService.Instance["Timeline2D_RenderInitFailedTitle"],
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                FontWeight = FontWeight.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            title.Bind(TextBlock.ForegroundProperty, title.GetResourceObservable("WarningColor"));
            title.Bind(TextBlock.FontSizeProperty, title.GetResourceObservable("FontSizeLg"));

            var body = new TextBlock
            {
                Text = LocalizationService.Instance["Timeline2D_RenderInitFailedBody"],
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            body.Bind(TextBlock.ForegroundProperty, body.GetResourceObservable("TextSecondary"));
            body.Bind(TextBlock.FontSizeProperty, body.GetResourceObservable("FontSizeSm"));

            var stack = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 560,
                Children = { title, body }
            };
            stack.Bind(StackPanel.SpacingProperty, stack.GetResourceObservable("SpaceSm"));

            var panel = new Border { IsHitTestVisible = false, Child = stack };
            panel.Bind(Border.BackgroundProperty, panel.GetResourceObservable("PanelBackground"));
            _errorSurface = panel;
        }

        Child = _errorSurface;
    }

    private void ClearErrorSurface()
    {
        if (Child != null && ReferenceEquals(Child, _errorSurface))
            Child = null;
    }

    /// <summary>Called by the backend once it can render (composed device +
    /// pump ready after attach). Configures the atlas and resolves
    /// DI-dependent services/layers.</summary>
    internal void OnBackendReady(VulkanTimelineBackend backend)
    {
        if (!ReferenceEquals(backend, _backend)) return;
        ClearErrorSurface();
        _batcher.FontAtlas = backend.FontAtlas;
        backend.FontAtlas?.SetChartFont("Default", ChartEditorConstants.RenderMetrics.DefaultFontSize, false);

        // Resolve DI-dependent services / layers now that App.Services is
        // reachable. The pinned-label registry is the same singleton instance
        // shared with EventInteractionTool, so rect updates on one side are
        // visible to hit-testing on the other.
        if (App.Services != null)
        {
            _audioService = App.Services.GetRequiredService<IPlaybackTransport>();
            // Defensive: drop any live subscription first so the handler can
            // never stack across attach/ready cycles.
            _ghostArrangements?.Changed -= OnGhostArrangementsChanged;
            _ghostArrangements = App.Services.GetRequiredService<IGhostArrangementsService>();
            _ghostArrangements.Changed += OnGhostArrangementsChanged;

            // Only insert EventLayer once, and only if one isn't already
            // in the list (defensive — readiness fires once per attach and
            // the layer list survives detach/re-attach).
            var hasEventLayer = false;
            for (var i = 0; i < _layers.Count; i++)
            {
                if (_layers[i] is EventLayer)
                {
                    hasEventLayer = true;
                    break;
                }
            }

            if (!hasEventLayer)
            {
                var pinnedRegistry = App.Services.GetRequiredService<PinnedLabelHitRegistry>();
                _layers.Insert(4, new EventLayer(pinnedRegistry, this));
            }
        }

        RequestNextFrameRendering();
    }

    /// <summary>
    ///     Builds and renders one timeline frame through
    ///     <paramref name="backend" />. Invoked by the backend's
    ///     animation-frame tick — always the UI thread (the frame reads live
    ///     view-model state, so there is no render thread by design).
    /// </summary>
    internal void RenderTimelineFrame(VulkanTimelineBackend backend)
    {
        if (!ReferenceEquals(backend, _backend) || !backend.IsReady) return;
        if (Editor == null || Transport == null) return;
        // Cache Bounds once: it goes through Avalonia's StyledProperty system
        // and was being read 6+ times per frame.
        var bounds = Bounds;
        var boundsW = bounds.Width;
        var boundsH = bounds.Height;
        if (boundsW <= 0 || boundsH <= 0) return;

        _frameCount++;
        // AddFrameInterval, not Add: this renderer is damage-driven, so the
        // stopwatch interval after an idle period measures the PAUSE, not a
        // frame — recording it made Session Health report 100+ second "frames".
        FrameStats.AddFrameInterval(_frameStopwatch.Elapsed.TotalMilliseconds);
        _frameStopwatch.Restart();
        if (_fpsStopwatch.ElapsedMilliseconds >= 1000)
        {
            _currentFps = _frameCount;
            _frameCount = 0;
            _fpsStopwatch.Restart();
            // Publish the 1 Hz summary for the Session Health dashboard (read-only).
            Diagnostics.RenderDiagnostics.PublishTimeline2D(FrameStats.Summarize());
            if (StatsLogEnabled && --_statsLogCountdown <= 0)
            {
                _statsLogCountdown = 5;
                EmitStatsLog(backend);
            }
        }

        var uiTimeMs = _projectState?.CurrentTimeMs ?? 0;
        var audioTimeMs = _audioService?.CurrentTimeMs ?? uiTimeMs;
        var isPlayingNow = _audioService?.IsPlaying == true;

        // The 2D timeline is Avalonia-composited, so it eats the same DWM commit →
        // scanout latency the 3D highway does and gets the same lead. Only while
        // PLAYING: leading a parked playhead would just draw the wrong time and make
        // a click land on the wrong chart position.
        //
        // The resolution is the PURE TimelineViewMath.ResolveRenderPlayheadMs so the
        // hit-test side can be proven to agree with it, and the result is published
        // to ProjectState below — hit-testers must not map through the coalesced
        // Playback.CurrentTimeMs, which trails this by the de-coalescing gap plus
        // the lead (~100 ms, i.e. 100 px at 1 px/ms) for the whole of playback.
        // The speed goes in too: the trust budget is wall-clock and the two
        // clocks are song time (see TimelineViewMath.SpeedFactor).
        var exactTimeMs = TimelineViewMath.ResolveRenderPlayheadMs(
            uiTimeMs, audioTimeMs, isPlayingNow, VisualLeadTiming.EffectiveLeadMs,
            _audioService?.PlaybackSpeed ?? 1.0);

        //
        // On the playing→paused transition the lead does NOT drop in a single
        // frame — that read as the playhead "jittering back" on every pause
        // (the lead reaches up to 50 ms ≈ many px).
        // Instead the residual eases out over ~120 ms wall-clock; the loop
        // stays alive until it settles. Clicks during the decay map through
        // the unled anchor, an error bounded by the shrinking residual for a
        // tenth of a second — invisible in practice, unlike the snap.
        // The shared rule (RenderLeadEase): while playing the lead is already
        // inside ResolveRenderPlayheadMs above; paused, only the easing residual.
        var appliedLeadMs = _leadEase.Resolve(isPlayingNow, VisualLeadTiming.EffectiveLeadMs, Environment.TickCount64);
        if (!isPlayingNow) exactTimeMs += appliedLeadMs;

        // (The former TotalTimeMs>0 "fit whole song" branch is gone: nothing
        // ever set ChartRenderer.TotalTimeMs — the whole-song overview lives
        // in MinimapOverviewControl with its own cached static-layer bitmap.)
        var currentZoom = PixelsPerMs;
        var centerScreenX = ChartEditorConstants.PlayheadX;
        // Decoupled viewport (0.16 6.6): while Follow is ON the anchor IS the
        // exact playback time (pixel-identical to the historical locked view);
        // while OFF it is the pan anchor. Hit-testing maps through the same
        // value via ProjectState.TimelineViewAnchorEffectiveMs.
        var followPlayhead = _projectState == null || _projectState.EditorUi.TimelineFollowsPlayhead;
        var viewAnchorMs = TimelineViewMath.ResolveViewAnchorMs(
            exactTimeMs, followPlayhead, _projectState?.EditorUi.TimelineViewAnchorMs ?? 0);
        // Device pixels per layout unit: the swapchain, the glyph atlas and the
        // scroll-origin snap below all need it.
        // NOT `(VisualRoot as TopLevel).RenderScaling` alone: the global UI scale wraps the
        // whole shell in a LayoutTransformControl, so this control's logical
        // size is in that transform's INNER units while the pixels it occupies
        // are inner x uiScale x RenderScaling. Sizing the swapchain (and the
        // glyph atlas) from RenderScaling alone therefore rendered the timeline
        // at 1/uiScale of its composited resolution and let the compositor
        // upscale it — a soft 2D timeline at 125 % and up.
        var renderScaling = VisualPixelScale.For(this);
        // The DRAWN view start snaps to a whole device pixel so the frame
        // scrolls as one rigid strip (zoomed out, each note would otherwise snap
        // to its own pixel on a different frame). The anchor itself stays exact.
        var viewStartMs = TimelineViewMath.SnappedViewStartMs(viewAnchorMs, currentZoom, renderScaling);
        var viewEndMs = viewStartMs + boundsW / currentZoom;

        // Publish the anchor this frame DREW so every hit-tester maps through the
        // same number (ProjectState.TimelineViewAnchorEffectiveMs). Only the main
        // renderer is canonical — stacked override rows must not publish — and only
        // while playing: paused, the coalesced playhead IS the drawn time (bar the
        // decaying pause residual above), and a stale published value would survive
        // a seek until the next frame.
        if (_projectState != null && OverrideArrangement == null)
            _projectState.TimelineRenderedAnchorMs = isPlayingNow ? exactTimeMs : double.NaN;

        // Publish the main timeline's width so viewport-center zoom anchoring
        // works from visual-tree-less commands (keybinds/toolbar). Stacked
        // override rows skip this — the main renderer is canonical.
        if (_projectState != null && OverrideArrangement == null &&
            // ReSharper disable once CompareOfFloatsByEqualityOperator
            _projectState.EditorUi.TimelineViewportWidthPx != boundsW)
        {
            _projectState.EditorUi.TimelineViewportWidthPx = boundsW;
        }

        var ts = StatsLogEnabled ? Stopwatch.GetTimestamp() : 0L;
        UpdateSnapshotContext(Editor);
        FrameSnapshot.BuildSnapshot(viewStartMs, viewEndMs, exactTimeMs, _snapshot, _snapshotCtx,
            _linkedTargetsBuffer);
        ts = StatsAccum(PhaseSnapshot, ts);

        // Cache TopRibbon: this getter calls TrackContexts.GetOrCreate +
        // BuildLayout — it was being invoked twice per frame (once via
        // TopMargin, once directly).
        var ribbonLayout = TopRibbon;
        ts = StatsAccum(PhaseRibbon, ts);
        var arrForLayout = OverrideArrangement ?? Editor.ActiveArrangement;
        var stringCount = StringDigitTargeting.StringCountOf(arrForLayout);

        // THE vertical layout, from the one shared formula. The gutter beside
        // this control labels these exact lanes, and a caption one pixel off
        // its lane names the wrong string — so neither side owns a copy.
        var layout = TimelineGutterLayout.For(
            boundsH, ribbonLayout, stringCount, LeftyFlip ^ InvertStrings, BottomMargin, DrawLabels);
        PublishGutterLayout(layout, arrForLayout);

        var topMargin = layout.TopMargin;
        var bottomMargin = layout.BottomMargin;
        var laneAreaHeight = layout.LaneAreaHeight;
        var laneHeight = layout.LaneHeight;
        var fhpY = topMargin + laneAreaHeight + ChartEditorConstants.RenderMetrics.FhpYOffset;

        var rc = _renderCtx;
        rc.Width = boundsW;
        rc.Height = boundsH;
        rc.CurrentZoom = currentZoom;
        rc.ViewStartMs = viewStartMs;
        rc.PixelScale = renderScaling;
        rc.ViewEndMs = viewEndMs;
        rc.CenterScreenX = centerScreenX;
        // Exactly the anchor column while following (byte-identical frames);
        // the playhead's true screen position (possibly off-screen) when the
        // viewport is panned away from it.
        rc.PlayheadScreenX = followPlayhead ? centerScreenX : (exactTimeMs - viewStartMs) * currentZoom;
        rc.TopMargin = topMargin;
        rc.BottomMargin = bottomMargin;
        rc.LaneAreaHeight = laneAreaHeight;
        rc.LaneHeight = laneHeight;
        rc.FhpY = fhpY;
        rc.StringCount = stringCount;
        rc.LeftyFlip = LeftyFlip;
        rc.InvertStrings = InvertStrings;
        rc.CurrentTimeMs = exactTimeMs;
        rc.DrawLabels = DrawLabels;
        rc.WaveformData = ShowWaveform ? WaveformData : null;
        rc.Editor = Editor;
        rc.Transport = Transport;
        rc.Snapshot = _snapshot;
        rc.RendererPixelsPerMs = PixelsPerMs;
        rc.HoveredTimeMs = Editor.Selection.HoveredTimeMs;
        rc.HoveredStringIndex = Editor.Selection.HoveredStringIndex;
        rc.StringEditMask = _projectState?.StringEditMask ?? StringEditMask.Empty;
        rc.LoopRegion = _projectState?.LoopRegion;
        rc.LoopEnabled = _projectState?.LoopEnabled ?? false;
        rc.LoopPendingStartMs = _projectState?.LoopPendingStartMs;
        rc.IsOverrideView = OverrideArrangement != null && OverrideArrangement != Editor.ActiveArrangement;
        rc.Ribbon = ribbonLayout;
        rc.Project = Editor.ActiveProject;
        rc.Arrangement = OverrideArrangement ?? Editor.ActiveArrangement;
        // 0.14.3 B playhead feedback: both flags only go true on frames the
        // continuous render loop produces anyway (playback / pinned scrub),
        // so the effects never require extra invalidation and a frozen
        // paused frame stays byte-identical.
        rc.IsPlaybackActive = _audioService?.IsPlaying == true || Transport.IsPlaying;
        rc.IsScrubbing = !rc.IsPlaybackActive && _continuousRenderRequests > 0;

        // Density LOD: one x-column per note/chord, counted over the
        // VIEWPORT. Counting over the whole snapshot window — view
        // span plus both ±5 s culling buffers — would make, at PixelsPerMs = 1.0, roughly
        // 84% of the decision come from chart the user cannot see, and a sparse
        // passage on screen would lose its fret labels because of a shred section two
        // seconds off-screen.
        var lodColumns =
            NoteLod.CountInWindow(_snapshot.Notes.AsSpan(0, _snapshot.NoteCount), viewStartMs, viewEndMs) +
            NoteLod.CountInWindow(_snapshot.Chords.AsSpan(0, _snapshot.ChordCount), viewStartMs, viewEndMs) +
            NoteLod.CountInWindow(_snapshot.GhostNotes.AsSpan(0, _snapshot.GhostNoteCount), viewStartMs, viewEndMs) +
            NoteLod.CountInWindow(_snapshot.GhostChords.AsSpan(0, _snapshot.GhostChordCount), viewStartMs, viewEndMs);
        // With hysteresis against the level the previous frame drew (rc is this
        // renderer's own, so rc.NoteLod still holds it): scrolling alone moves the
        // count by one, and without the band a constant-density passage blinked
        // between gems and marks on every such step.
        rc.NoteLod = NoteLod.Compute(viewEndMs - viewStartMs, currentZoom, lodColumns, rc.NoteLod);

        // HiDPI text: the GPU viewport is in PHYSICAL pixels while every quad
        // we emit is in logical units, so a glyph sheet rasterized at 1.0 gets
        // stretched by the scale factor on a HiDPI monitor (blurry labels). Hand
        // the atlas the real surface scale — it rasterizes at fontSize × scale and
        // divides its metrics back down, so nothing else in the batcher changes.
        // No-op unless the value actually changed (e.g. dragged to another monitor).
        var atlas = backend.FontAtlas;
        _batcher.FontAtlas = atlas;
        atlas?.SetRenderScale((float)renderScaling);

        // Ensure newly visible characters are injected into the font atlas before drawing.
        // Cheap content signature filters out the (very common) frame-to-frame
        // case where the visible set is unchanged, in which case there's no
        // possible new character to register; a per-char
        // HashSet probe over hundreds of strings every frame would be wasteful.
        if (atlas != null)
        {
            // Whole-chart pre-registration, latched on the three collections'
            // instance + count (ChartTextRegistrationLatch): the one rebuild it
            // costs lands on the first frame after a load or a text edit — with
            // the transport parked — instead of mid-playback when a word with a
            // non-seed glyph scrolls into the window. The windowed scan below
            // stays as the safety net.
            var lyricPhrases = _snapshotCtx.LyricPhrases;
            var eventPoints = _snapshotCtx.EventPoints;
            var tones = _snapshotCtx.Tones;
            if (_textRegistration.NeedsRegistration(lyricPhrases, lyricPhrases.Count,
                    eventPoints, eventPoints.Count, tones, tones.Count))
            {
                ChartTextPreRegistration.Register(atlas, lyricPhrases, eventPoints, tones);
                _textRegistration.Accept(lyricPhrases, lyricPhrases.Count,
                    eventPoints, eventPoints.Count, tones, tones.Count);
            }

            var sig = ComputeRegisterSignature();
            if (sig != _lastRegisterSig)
            {
                for (var i = 0; i < _snapshot.LyricWordCount; i++)
                    atlas.RegisterCharacters(_snapshot.LyricWords[i].Text);

                atlas.RegisterCharacters(_snapshot.DebugText);

                // Sections render UPPERCASED (RenderStringCache.Upper in
                // EventLayer), so register the drawn form as well — otherwise
                // non-ASCII uppercase glyphs (Ä, É, …) never enter the atlas.
                for (var i = 0; i < _snapshot.EventPointCount; i++)
                {
                    atlas.RegisterCharacters(_snapshot.EventPoints[i].Phrase);
                    atlas.RegisterCharacters(_snapshot.EventPoints[i].Section);
                    atlas.RegisterCharacters(RenderStringCache.Upper(_snapshot.EventPoints[i].Section));
                }

                for (var i = 0; i < _snapshot.PreviewEventPointCount; i++)
                {
                    atlas.RegisterCharacters(_snapshot.PreviewEventPoints[i].Phrase);
                    atlas.RegisterCharacters(_snapshot.PreviewEventPoints[i].Section);
                    atlas.RegisterCharacters(RenderStringCache.Upper(_snapshot.PreviewEventPoints[i].Section));
                }

                // Tone badges (EventLayer.DrawTones + the pinned tone label)
                // draw ToneName but were never registered — idiomatic tone keys
                // like "Gtr_Lead!" silently lost every non-seed character.
                for (var i = 0; i < _snapshot.ToneCount; i++)
                    atlas.RegisterCharacters(_snapshot.Tones[i].ToneName);

                // Chord names inside the hand-shape bars.
                for (var i = 0; i < _snapshot.TemplateCount; i++)
                    atlas.RegisterCharacters(_snapshot.Templates[i].Name);

                atlas.RegisterCharacters(LinkLayer.Glyphs);

                _lastRegisterSig = sig;
            }
        }

        ts = StatsAccum(PhaseAtlas, ts);

        // BeginFrame rebuilds the atlas (registrations above), sizes the pool
        // and acquires an exportable image — false is composed back-pressure
        // (the compositor owns every pool image): skip this frame and retry
        // on the next tick.
        if (!backend.BeginFrame(boundsW, boundsH, renderScaling))
        {
            if (StatsLogEnabled)
            {
                StatsAccum(PhaseBegin, ts);
                _statsBeginSkips++;
            }

            RequestNextFrameRendering();
            return;
        }

        ts = StatsAccum(PhaseBegin, ts);

        _batcher.Reset();

        // Everything from here to EndFrame runs inside a BEGUN frame, and
        // EndFrame MUST run even if a layer throws. BeginFrame reset the frame
        // fence and acquired a pool image, but only EndFrame advances
        // `_frameSlot` and marks that image submitted — so an escaping layer
        // exception leaks the slot and leaves every later BeginFrame declining,
        // which the animation tick reads as benign back-pressure. The result is
        // a silent, permanent wedge: the timeline stops updating for the rest of
        // the session with no error anywhere. Submitting a partially recorded
        // frame is legal (it just draws less) and is far better than losing the
        // slot. Same reasoning — and the same shape — as
        // Fretboard3DEngine.RenderFrame, which has had this guard all along.
        try
        {
            if (StatsLogEnabled)
            {
                if (_layerSumMs.Length != _layers.Count) _layerSumMs = new double[_layers.Count];
                for (var i = 0; i < _layers.Count; i++)
                {
                    var layerTs = Stopwatch.GetTimestamp();
                    _layers[i].Render(_batcher, rc);
                    _layerSumMs[i] += Stopwatch.GetElapsedTime(layerTs).TotalMilliseconds;
                }

                ts = StatsAccum(PhaseLayers, ts);
            }
            else
            {
                foreach (var layer in _layers)
                    layer.Render(_batcher, rc);
            }

            if (Editor.LinkHighlight.FlashAlpha > 0f) RequestNextFrameRendering();

            // FPS is a diagnostic, not chart content: draw it only while the
            // Session Health dashboard is open (the 1 Hz summary is still
            // published above for that dashboard regardless). Dim, low-key readout
            // — diagnostics shouldn't compete with chart content.
            if (DrawLabels && Diagnostics.RenderDiagnostics.DashboardVisible)
            {
                _batcher.AddText(RenderStringCache.Fps(_currentFps),
                    (float)boundsW - ChartEditorConstants.RenderMetrics.FpsLabelXOffset,
                    ChartEditorConstants.RenderMetrics.FpsLabelYOffset,
                    Avalonia.Media.Color.FromArgb(110, 148, 163, 184));
            }
        }
        finally
        {
            backend.EndFrame(_batcher);
        }

        if (StatsLogEnabled)
        {
            StatsAccum(PhaseEnd, ts);
            _statsFrames++;
        }

        // Polling audio time only needs a continuous render loop during playback.
        // While paused we stay in continuous mode for the duration of an
        // interactive scrub (BeginContinuousRender) so motion looks smooth —
        // and for the brief pause-lead decay so the ease-out actually animates.
        if (_audioService?.IsPlaying == true || Transport.IsPlaying || _continuousRenderRequests > 0 ||
            _leadEase.IsEasing)
        {
            RequestNextFrameRendering();
        }
    }

    /// <summary>The render clock's lead and its pause ease-out (shared with the 3D
    /// highway).</summary>
    private readonly RenderLeadEase _leadEase = new();

    private long _lastRegisterSig;

    // Whole-chart glyph pre-registration (ChartTextPreRegistration): the 3D
    // driver has had this since the lyric-scroll hitch; the 2D timeline only
    // registered the WINDOWED strings per frame, so the first "don't" or
    // "Gtr_Lead!" to scroll in during playback rebuilt the 16 MB atlas inside
    // the frame — two GPU idles on the UI thread. Weak, like the 3D latch.
    private readonly ChartTextRegistrationLatch _textRegistration = new();

    /// <summary>Accumulates the elapsed time since <paramref name="startTs" />
    /// into phase <paramref name="phase" /> and returns a fresh timestamp for
    /// the next phase. No-op (returns 0) while stats logging is disabled.</summary>
    private long StatsAccum(int phase, long startTs)
    {
        if (!StatsLogEnabled) return 0L;
        var ms = Stopwatch.GetElapsedTime(startTs).TotalMilliseconds;
        _phaseSumMs[phase] += ms;
        if (ms > _phaseMaxMs[phase]) _phaseMaxMs[phase] = ms;
        return Stopwatch.GetTimestamp();
    }

    /// <summary>Writes the 5 s [2DStats] summary (per-phase avg/max UI-thread
    /// cost + per-layer averages + skip counters) and resets the accumulators.
    /// Only called while <see cref="StatsLogEnabled" />.</summary>
    private void EmitStatsLog(VulkanTimelineBackend backend)
    {
        var frames = Math.Max(1, _statsFrames);
        var summary = FrameStats.Summarize();
        var sb = new System.Text.StringBuilder(256);
        sb.Append("[2DStats] ").Append(_currentFps).Append(" fps · interval p50 ")
            .Append(summary.P50Ms.ToString("F2")).Append(" / p99 ")
            .Append(summary.P99Ms.ToString("F2")).Append(" / max ")
            .Append(summary.MaxMs.ToString("F1")).Append(" ms · phases avg(max) ms:");
        for (var p = 0; p < PhaseCount; p++)
        {
            sb.Append(' ').Append(PhaseNames[p]).Append(' ')
                .Append((_phaseSumMs[p] / frames).ToString("F2")).Append('(')
                .Append(_phaseMaxMs[p].ToString("F1")).Append(')');
            _phaseSumMs[p] = 0;
            _phaseMaxMs[p] = 0;
        }

        sb.Append(" · layers avg ms:");
        for (var i = 0; i < _layerSumMs.Length && i < _layers.Count; i++)
        {
            sb.Append(' ').Append(_layers[i].GetType().Name.Replace("Layer", string.Empty)).Append(' ')
                .Append((_layerSumMs[i] / frames).ToString("F2"));
            _layerSumMs[i] = 0;
        }

        var (fence, pool) = backend.SkipCounters;
        sb.Append(" · skips begin ").Append(_statsBeginSkips)
            .Append(" (fence ").Append(fence - _statsLastFenceSkips)
            .Append(", pool ").Append(pool - _statsLastPoolSkips).Append(')')
            .Append(" · frames ").Append(_statsFrames);
        _statsLastFenceSkips = fence;
        _statsLastPoolSkips = pool;
        _statsBeginSkips = 0;
        _statsFrames = 0;

        App.Services?.GetService<IAppLogger>()?.Info(AppLogCategory.Rendering, sb.ToString());
    }

    private long ComputeRegisterSignature()
    {
        // Reference-hash every string the layers can draw. Strings are
        // immutable, so an in-place text edit (lyric syllable, section name,
        // tone rename) replaces the string INSTANCE in the snapshot — an O(1)
        // RuntimeHelpers.GetHashCode per string therefore catches edits the
        // old (count, first.Time, last.Time) proxy missed entirely, while
        // still being far cheaper than per-char atlas probes (no dictionary
        // work, no char iteration; a stale-instance false positive merely
        // re-runs the idempotent registration).
        unchecked
        {
            long h = 17;
            h = h * 31 + _snapshot.LyricWordCount;
            for (var i = 0; i < _snapshot.LyricWordCount; i++)
                h = h * 31 + RuntimeHelpers.GetHashCode(_snapshot.LyricWords[i].Text);

            h = h * 31 + _snapshot.EventPointCount;
            for (var i = 0; i < _snapshot.EventPointCount; i++)
            {
                h = h * 31 + RuntimeHelpers.GetHashCode(_snapshot.EventPoints[i].Phrase);
                h = h * 31 + RuntimeHelpers.GetHashCode(_snapshot.EventPoints[i].Section);
            }

            h = h * 31 + _snapshot.PreviewEventPointCount;
            for (var i = 0; i < _snapshot.PreviewEventPointCount; i++)
            {
                h = h * 31 + RuntimeHelpers.GetHashCode(_snapshot.PreviewEventPoints[i].Phrase);
                h = h * 31 + RuntimeHelpers.GetHashCode(_snapshot.PreviewEventPoints[i].Section);
            }

            h = h * 31 + _snapshot.ToneCount;
            for (var i = 0; i < _snapshot.ToneCount; i++)
                h = h * 31 + RuntimeHelpers.GetHashCode(_snapshot.Tones[i].ToneName);

            h = h * 31 + _snapshot.TemplateCount;
            for (var i = 0; i < _snapshot.TemplateCount; i++)
                h = h * 31 + RuntimeHelpers.GetHashCode(_snapshot.Templates[i].Name);

            h = h * 31 + RuntimeHelpers.GetHashCode(_snapshot.DebugText);
            return h;
        }
    }

    /// <summary>
    ///     The level a row draws, or null for "the editor's active level"
    ///     (<c>PlacedNotes</c>/<c>Anchors</c>/…, what every edit, hit test and
    ///     selection addresses). The ACTIVE arrangement must not draw
    ///     <c>Levels[0]</c> here, or a levelled chart would show its easiest level
    ///     while edits land in the hardest — and every psarc import is
    ///     levelled. Another arrangement's row draws its hardest level, the
    ///     whole song. Allocation-free: this runs every frame.
    /// </summary>
    internal static ChartLevel? SnapshotSourceLevel(Arrangement? source, bool isOverride) =>
        isOverride ? ChartLevels.Hardest(source) : null;

    private void UpdateSnapshotContext(ChartEditorViewModel editor)
    {
        var ovr = OverrideArrangement;
        var active = editor.ActiveArrangement;
        var isOverride = ovr != null && ovr != active;
        var sourceArr = ovr ?? active;
        var sourceLevel = SnapshotSourceLevel(sourceArr, isOverride);

        _snapshotCtx.Capo = sourceArr?.Capo ?? 0;
        _snapshotCtx.Chart = editor.CurrentChart;
        _snapshotCtx.DebugText = string.Empty;
        _snapshotCtx.EventPoints = sourceArr?.EventPoints is { } eps
            ? eps
            : Array.Empty<EventPoint>();
        _snapshotCtx.Tones = sourceArr?.ToneChanges is { } tones
            ? tones
            : Array.Empty<ToneChange>();
        _snapshotCtx.LyricPhrases = isOverride
            ? EmptyLyricPhrases
            : editor.LyricPhrases;
        _snapshotCtx.ActiveNotes = sourceLevel?.Notes ?? editor.PlacedNotes;
        _snapshotCtx.ActiveChords = sourceLevel?.Chords ?? editor.PlacedChords;
        _snapshotCtx.ActiveTemplates = sourceArr?.ChordTemplates is { } templates
            ? templates
            : Array.Empty<ChordTemplate>();
        _snapshotCtx.ActiveBeats = editor.Beats;
        _snapshotCtx.ActiveAnchors = sourceLevel?.Anchors ?? editor.Anchors;
        _snapshotCtx.ActiveHandShapes = sourceLevel?.HandShapes ?? editor.HandShapes;
        // The frame-only repeat box is the EXPORT's decision, handed over as data.
        _snapshotCtx.RepeatChords = editor.SharedRepeatChords.Get(_snapshotCtx.ActiveTemplates, _snapshotCtx.ActiveNotes,
            _snapshotCtx.ActiveChords, _snapshotCtx.ActiveHandShapes, _snapshotCtx.ActiveBeats,
            editor.HistoryRevision, Environment.TickCount64);
        // The bars the inspector has an E05/D09 finding against — its own list, as data.
        _snapshotCtx.HandShapeFindings = editor.HandShapeFindings;
        // The dashed suggested bars, recomputed only on an edit.
        _snapshotCtx.SuggestedHandShapes = editor.SharedHandShapeSuggestions.Get(sourceArr,
            sourceLevel ?? editor.ActiveLevel, editor.ShowSuggestedHandShapes,
            editor.HistoryRevision, Environment.TickCount64).Bars;
        // What a pending bulk chord edit would change, as the chord panel resolved it.
        _snapshotCtx.EditTargetChords = editor.UseHighlight.Chords;
        _snapshotCtx.EditTargetHandShapes = editor.UseHighlight.HandShapes;
        // …and the hand positions the AI chart pass review would replace and add,
        // on the level this frame draws (a dictionary lookup, nothing allocated).
        _snapshotCtx.EditTargetAnchors = editor.UseHighlight.Anchors;
        _snapshotCtx.ProposedAnchors = editor.UseHighlight.On(sourceLevel?.Chords ?? editor.PlacedChords).ProposedAnchors;
        // …and the section / phrase markers the AI chat's structure review replaces and adds.
        (_snapshotCtx.EditTargetSections, _snapshotCtx.EditTargetPhrases, _snapshotCtx.ProposedEventPoints) =
            editor.UseHighlight.EventsOn(sourceArr);

        // Selection / preview / hover are global singletons (Phase 1-4); per-track
        // selection lands in Phase 5. Selection markers are overlay flags
        // matched by reference against this arrangement's own notes/chords —
        // safe to pass through globally because non-matching items just
        // won't be flagged.
        _snapshotCtx.SelectedNotes = editor.SelectedNotes;
        _snapshotCtx.SelectedChords = editor.SelectedChords;
        _snapshotCtx.SelectedHandShapes = editor.SelectedHandShapes;
        _snapshotCtx.SelectedAnchors = editor.SelectedAnchors;
        _snapshotCtx.SelectedEventPoints = editor.Selection.SelectedEventPoints;
        _snapshotCtx.SelectedTones = editor.Selection.SelectedTones;
        // v0.30 leg b: version the selection so FrameSnapshot only re-hashes
        // its lookup sets on a real selection edit, not on every frame.
        _snapshotCtx.SelectionVersion = editor.Selection.SelectionVersion;

        // Preview notes/chords/etc. are drawn directly (not as overlays), so
        // we must only render them in the panel that owns them. The owning
        // arrangement is tagged by the ToolCoordinator on every pointer
        // event. Null = belongs to active.
        var previewArr = editor.Selection.PreviewArrangement ?? active;
        var showPreviews = sourceArr == previewArr;
        _snapshotCtx.PreviewNotes = showPreviews ? editor.PreviewNotes : EmptyNotes;
        _snapshotCtx.PreviewChords = showPreviews ? editor.PreviewChords : EmptyChords;
        _snapshotCtx.PreviewHandShapes = showPreviews ? editor.PreviewHandShapes : EmptyHandShapes;
        _snapshotCtx.PreviewAnchors = showPreviews ? editor.PreviewAnchors : EmptyAnchors;
        _snapshotCtx.PreviewEventPoints = showPreviews
            ? editor.PreviewEventPoints
            : EmptyEventPoints;

        // Hover markers are overlays matched by reference against this
        // arrangement's own items, so cross-panel ghosting can't happen —
        // pass them through globally.
        _snapshotCtx.HoveredNote = editor.Selection.HoveredNote;
        _snapshotCtx.HoveredChord = editor.Selection.HoveredChord;
        _snapshotCtx.HoveredHandShape = editor.Selection.HoveredHandShape;
        _snapshotCtx.HoveredEventPoint = editor.Selection.HoveredEventPoint;
        _snapshotCtx.HoveredTone = editor.Selection.HoveredTone;
        _snapshotCtx.HoveredAnchor = editor.Selection.HoveredAnchor;
        _snapshotCtx.HoveredLyric = editor.Selection.HoveredLyric;
        _snapshotCtx.SelectedLyric = editor.Selection.SelectedLyric;

        // Ghost overlays only make sense on the active arrangement view —
        // when rendering an override, ghosts of *other* arrangements would
        // be redundant noise on top of that arrangement's own notes.
        if (isOverride)
        {
            _ghostNotesBuffer.Clear();
            _ghostChordsBuffer.Clear();
        }
        else
        {
            FillGhostBuffers(editor);
        }
        _snapshotCtx.GhostNotes = _ghostNotesBuffer;
        _snapshotCtx.GhostChords = _ghostChordsBuffer;
    }

    private static readonly ObservableCollection<GuitarNote> EmptyNotes = [];
    private static readonly ObservableCollection<Chord> EmptyChords = [];
    private static readonly ObservableCollection<HandShape> EmptyHandShapes = [];
    private static readonly ObservableCollection<FretAnchor> EmptyAnchors = [];
    private static readonly ObservableCollection<EventPoint> EmptyEventPoints = [];
    private static readonly ObservableCollection<LyricPhrase> EmptyLyricPhrases = [];

    // Ghost-buffer rebuild cache: the copy + sort below is O(total ghost
    // chart) and must not run EVERY frame (60–144 Hz), because ghosts change
    // rarely. A cheap structural signature (arrangement identity + counts)
    // skips identical rebuilds; the time throttle still refreshes at ~4 Hz
    // so position-only edits made to a ghosted arrangement from another
    // panel can't stay stale.
    private int _ghostSignature;
    private long _ghostBuiltTick;
    private const long GhostRefreshIntervalMs = 250;

    private void FillGhostBuffers(ChartEditorViewModel editor)
    {
        if (_ghostArrangements == null || _ghostArrangements.VisibleGhosts.Count == 0)
        {
            _ghostNotesBuffer.Clear();
            _ghostChordsBuffer.Clear();
            _ghostSignature = 0;
            return;
        }

        var active = editor.ActiveArrangement;
        var signature = 17;
        // Indexed: this signature runs every frame, BEFORE the 250 ms
        // throttle below, and foreach over an ObservableCollection boxes its
        // enumerator per frame (the FrameSnapshot rule; LayerCollectionEnumerationLintTests).
        var ghosts = _ghostArrangements.VisibleGhosts;
        for (var g = 0; g < ghosts.Count; g++)
        {
            var arr = ghosts[g];
            if (arr == active) continue;
            var level = ChartLevels.Hardest(arr);
            if (level == null) continue;
            signature = signature * 31 + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(arr);
            signature = signature * 31 + level.Notes.Count;
            signature = signature * 31 + level.Chords.Count;
        }

        var now = Environment.TickCount64;
        if (signature == _ghostSignature && now - _ghostBuiltTick < GhostRefreshIntervalMs)
            return;
        _ghostSignature = signature;
        _ghostBuiltTick = now;

        _ghostNotesBuffer.Clear();
        _ghostChordsBuffer.Clear();
        for (var g = 0; g < ghosts.Count; g++)
        {
            var arr = ghosts[g];
            if (arr == active) continue;
            var level = ChartLevels.Hardest(arr);
            if (level == null) continue;
            var notes = level.Notes;
            for (var i = 0; i < notes.Count; i++) _ghostNotesBuffer.Add(notes[i]);
            var chords = level.Chords;
            for (var i = 0; i < chords.Count; i++) _ghostChordsBuffer.Add(chords[i]);
        }

        // Sorted lists are required by the visible-range binary search the
        // FrameSnapshot performs on the active note collection.
        _ghostNotesBuffer.Sort(static (a, b) => a.Position.CompareTo(b.Position));
        _ghostChordsBuffer.Sort(static (a, b) => a.Position.CompareTo(b.Position));
    }
}
