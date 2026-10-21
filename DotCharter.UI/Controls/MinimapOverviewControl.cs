using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using DotCharter.Services.Editing.HandShapes;
using DotCharter.Tone.UI.Controls;
using DotCharter.UI.Constants;
using DotCharter.UI.Controls.Layers.Helpers;
using DotCharter.UI.Controls.Themes;
using DotCharter.UI.Input;
using DotCharter.UI.ViewModels.Editor;
using Microsoft.Extensions.DependencyInjection;
using ProjectState = DotCharter.Services.State.ProjectState;

namespace DotCharter.UI.Controls;

public class MinimapOverviewControl : Control
{
    /// <summary>The playhead / view-window repaint gate — shared with
    /// <see cref="LivePlaySectionGraph" /> through <see cref="RepaintThrottle" />,
    /// so the two strips cannot drift to different rates.</summary>
    private RepaintThrottle _dynamicRepaint = new(RepaintThrottle.DefaultIntervalMs);

    /// <summary>The selection's repaint gate: same rate as the playhead's, with a
    /// trailing repaint so the last change of a marquee burst still lands.</summary>
    private TrailingRepaintGate _selectionRepaint = new(RepaintThrottle.DefaultIntervalMs);

    private readonly Action _selectionTrailingRepaint;

    public static readonly StyledProperty<ChartEditorViewModel?> EditorProperty =
        AvaloniaProperty.Register<MinimapOverviewControl, ChartEditorViewModel?>(nameof(Editor));

    public static readonly StyledProperty<double> PixelsPerMsProperty =
        AvaloniaProperty.Register<MinimapOverviewControl, double>(nameof(PixelsPerMs), 0.2);

    public static readonly StyledProperty<WaveformSamples?> WaveformDataProperty =
        AvaloniaProperty.Register<MinimapOverviewControl, WaveformSamples?>(nameof(WaveformData));

    public static readonly StyledProperty<bool> ShowWaveformProperty =
        AvaloniaProperty.Register<MinimapOverviewControl, bool>(nameof(ShowWaveform), true);

    public static readonly StyledProperty<double> TotalTimeMsProperty =
        AvaloniaProperty.Register<MinimapOverviewControl, double>(nameof(TotalTimeMs));

    /// <summary>
    ///     Width (px) of the TIMELINE this map summarises, for hosts that know
    ///     it and are not the main window (a docked or floating second map).
    ///     0 = "ask <c>EditorUiState.TimelineViewportWidthPx</c>", which is what
    ///     the main window relies on. See <see cref="MinimapViewportMath" /> for
    ///     why the map's own width is the wrong number.
    /// </summary>
    public static readonly StyledProperty<double> TimelineViewportWidthProperty =
        AvaloniaProperty.Register<MinimapOverviewControl, double>(nameof(TimelineViewportWidth));

    private ProjectState? _projectState;
    private ChartEditorViewModel? _cachedEditor;
    private int _cachedPixelHeight;
    private int _cachedPixelWidth;
    private bool _cachedShowWaveform;
    private double _cachedTotalTimeMs;
    private WaveformSamples? _cachedWaveformData;
    private ChartEditorViewModel? _subscribedEditor;
    private bool _attachedToTree;
    private bool _effectivelyVisible;
    private RenderTargetBitmap? _staticLayerBitmap;
    private bool _staticLayerDirty = true;

    // Cached brushes/pens — the minimap repaints on every transport tick during
    // playback (≥30 Hz). Allocating a fresh Pen per paint creates an
    // EffectiveValue<PenLineJoin> box (visible in alloc traces) plus extra
    // SolidColorBrush instances. Cache once at type init.
    private static readonly IBrush ViewportBrush = new SolidColorBrush(Color.FromArgb(48,
        ChartTheme.SkyBlueColor.R, ChartTheme.SkyBlueColor.G, ChartTheme.SkyBlueColor.B));

    private static readonly IPen ViewportPen = new Pen(ChartTheme.SkyBlueBrush);

    // Mutated in place by OnThemeChanged, NOT re-created: the pen holds the
    // brush instance, so repainting the brush repaints the pen.
    private static readonly SolidColorBrush WaveformBrush = new(Color.FromArgb(160,
        ChartTheme.MeasureColor.R, ChartTheme.MeasureColor.G, ChartTheme.MeasureColor.B));

    private static readonly IPen WaveformPen = new Pen(WaveformBrush);

    private static readonly IBrush SelectionHaloBrush = new SolidColorBrush(Colors.White);

    // The bulk-edit preview's marks: ChartTheme's own EditTarget brush
    // (repainted in place on a palette change) and an alpha wash of it, which —
    // like WaveformBrush — no shared brush carries, so OnThemeChanged repaints it.
    private static readonly IBrush EditTargetMarkBrush = ChartTheme.EditTargetBrush;

    private static readonly SolidColorBrush EditTargetWashBrush = new(Color.FromArgb(90,
        ChartTheme.EditTargetColor.R, ChartTheme.EditTargetColor.G, ChartTheme.EditTargetColor.B));

    // Reused every frame by DrawUseMarks: cleared and refilled, never re-allocated.
    private readonly List<SongMapMark> _selectionMarks = [];
    private readonly List<SongMapMark> _highlightMarks = [];

    // The suggested hand shapes: the lane's own latch type over the lane's own inputs
    // (the level on screen, the View toggle, the history revision), so the detector reruns only on
    // an edit and an unchanged frame reuses the same list. Drawn as a faint wash of the hand-shape
    // bar colour with a stronger bar along the bottom edge; both alpha copies of a ChartTheme
    // colour, so OnThemeChanged repaints them like EditTargetWashBrush.
    private readonly HandShapeSuggestionLatch _suggestedHandShapes = new();

    // The emphasised link group's placements (hover or selection only): refilled per frame, and
    // only while a group is emphasised.
    private readonly List<SongMapLinkMark> _linkMarks = [];
    private readonly SongMapLinkPainter _linkPainter = new();
    private string? _drawnLinkEmphasis;
    private readonly List<SongMapMark> _suggestionMarks = [];
    internal const byte SuggestionWashAlpha = 40;
    internal const byte SuggestionBarAlpha = 150;

    private static readonly SolidColorBrush SuggestionWashBrush = new(Color.FromArgb(SuggestionWashAlpha,
        ChartTheme.HandShapeBarColor.R, ChartTheme.HandShapeBarColor.G, ChartTheme.HandShapeBarColor.B));

    private static readonly SolidColorBrush SuggestionBarBrush = new(Color.FromArgb(SuggestionBarAlpha,
        ChartTheme.HandShapeBarColor.R, ChartTheme.HandShapeBarColor.G, ChartTheme.HandShapeBarColor.B));

    // ChartTheme's OWN brush instances, not copies of its colours. `Apply`
    // repaints these in place on every palette change (a theme preset, a
    // customized token, a colour-blind string preset), so sharing them is what
    // makes the overview follow the palette. Copying the COLOURS — which is
    // what this did — froze the strip on whatever the palette was the first
    // time a minimap was constructed: every other surface changed colour and
    // this one did not, with no exception and no log line.
    private static readonly IBrush[] StringBrushes = [.. ChartTheme.StringBrushes];

    private static readonly IBrush PhraseBrush = ChartTheme.GoldBrush;
    private static readonly IBrush SectionBrush = ChartTheme.SkyBlueBrush;
    private static readonly IPen PlayheadPen = new Pen(ChartTheme.WhiteSmokeBrush);

    // Section names in the band: 9 px semibold. At 8 px regular the
    // strokes are thinner than a pixel and read as blur however sharp the
    // layer. Capitals only, so 9 px fits the 11 px band.
    private static readonly Typeface SectionTypeface = new("Segoe UI", FontStyle.Normal, FontWeight.SemiBold);
    // Bookmark text is cached per index: FormattedText runs text shaping,
    // which is far too heavy to redo per bookmark on every ~30 Hz repaint.
    // The cache is rebuilt on theme change so the brush tracks the palette.
    private readonly Dictionary<int, FormattedText> _bookmarkTextCache = [];
    private IBrush? _bookmarkBrush;

    public ChartEditorViewModel? Editor
    {
        get => GetValue(EditorProperty);
        set => SetValue(EditorProperty, value);
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

    public double TotalTimeMs
    {
        get => GetValue(TotalTimeMsProperty);
        set => SetValue(TotalTimeMsProperty, value);
    }

    /// <inheritdoc cref="TimelineViewportWidthProperty" />
    public double TimelineViewportWidth
    {
        get => GetValue(TimelineViewportWidthProperty);
        set => SetValue(TimelineViewportWidthProperty, value);
    }

    public MinimapOverviewControl()
    {
        // The cached layer is drawn 1:1 (see Render); nearest-neighbour keeps it that
        // way if the control lands on a fractional device-pixel offset, where the
        // default bilinear filter would soften every pixel of the map.
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.None);
        ClipToBounds = true;

        // Repaints are gated on EFFECTIVE visibility. The editor subtree is
        // IsVisible-gated (MainWindow.axaml), i.e. HIDDEN, not detached, so the
        // ProjectState subscription stayed live and the minimap kept requesting
        // ~30 Hz repaints of a surface nobody can see for the whole of playback.
        // Same mechanism as Preview3DComposedCanvas /
        // MenuBackdropControl — never an IsEffectivelyVisible property branch,
        // which Avalonia 12 does not raise (see EffectiveVisibilityWatcher).
        _ = new EffectiveVisibilityWatcher(this, OnEffectiveVisibilityFlip);
        _selectionTrailingRepaint = OnSelectionTrailingRepaint;
    }

    private void OnEffectiveVisibilityFlip(bool visible)
    {
        _effectivelyVisible = visible;
        // Nothing repainted while hidden, so anything that changed meanwhile is
        // still unpainted — one repaint on the false->true flip, bypassing the
        // gate that is only just now open. The static layer rebuilds itself if
        // _staticLayerDirty was set while hidden.
        if (visible) RepaintCore();
    }

    /// <summary>The one repaint funnel: a no-op while the minimap is not
    /// EFFECTIVELY VISIBLE.</summary>
    private void RequestRepaint()
    {
        if (!_effectivelyVisible) return;
        RepaintCore();
    }

    private void RepaintCore()
    {
        RepaintRequests++;
        InvalidateVisual();
    }

    /// <summary>Test seam: repaints that passed the visibility gate. It must not
    /// move while the minimap is hidden.</summary>
    internal int RepaintRequests { get; private set; }

    /// <summary>True while this control's own IsVisible AND every ancestor's are
    /// set — maintained by <see cref="EffectiveVisibilityWatcher" />.</summary>
    internal bool IsEffectivelyVisibleForRender => _effectivelyVisible;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == EditorProperty)
        {
            SyncSelectionSubscription();
            _staticLayerDirty = true;
            RequestRepaint();
        }
        else if (change.Property == WaveformDataProperty ||
            change.Property == ShowWaveformProperty ||
            change.Property == TotalTimeMsProperty ||
            change.Property.Name == "Bounds")
        {
            _staticLayerDirty = true;
            RequestRepaint();
        }
        else if (change.Property == PixelsPerMsProperty)
        {
            RequestRepaint();
        }
    }

    // Playhead updates arrive straight from ProjectState (cached event args,
    // ≤60 Hz on the UI thread) instead of an Avalonia binding — every bound
    // update costs ~10 KB on the compositor thread (see
    // AudioPlaybackCoordinator). Repaints stay throttled to ~33 ms.
    private void OnProjectStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        // A string-order flip (lefty / inverted strings) re-orients the cached
        // note layer — rebuild it and repaint at once (a rare event, so it skips
        // the playhead throttle).
        if (e.PropertyName is (nameof(ProjectState.InvertStrings)) or
            (nameof(ProjectState.LeftyFlip)))
        {
            _staticLayerDirty = true;
            RequestRepaint();
            return;
        }

        // Viewport pans / Follow flips move the view-window box exactly like
        // playhead moves do (0.16 6.6) — same throttled repaint.
        if (e.PropertyName is not (nameof(ProjectState.CurrentTimeMs)) and
            not (nameof(ProjectState.TimelineViewAnchorMs)) and
            not (nameof(ProjectState.TimelineFollowsPlayhead)))
        {
            return;
        }

        if (_dynamicRepaint.TryPass(Environment.TickCount64)) RequestRepaint();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ChartTheme.ThemeChanged += OnThemeChanged;
        _projectState ??= App.Services?.GetService<ProjectState>();
        _projectState?.PropertyChanged += OnProjectStateChanged;
        _attachedToTree = true;
        SyncSelectionSubscription();
    }

    /// <summary>
    ///     Brings the selection subscription in line with (attached ? Editor :
    ///     null) — the single writer, so the handler can never stack.
    ///     <para>
    ///         <b>The bug this ends:</b> the Editor binding lands BEFORE the first
    ///         attach (App.axaml.cs sets the window DataContext before Show()), so
    ///         the property-changed path and the attach path both ran and
    ///         <c>SelectedNotes</c>/<c>SelectedChords</c> each carried TWO copies
    ///         of <see cref="OnSelectionChanged" />. Detach removed one, leaving a
    ///         detached minimap rooted by the long-lived editor view model's
    ///         collections — the exact shape <c>ViewSubscriptionLeakGuardTests</c>
    ///         exists for — and every marquee burst raised two
    ///         <c>InvalidateVisual</c> per event.
    ///     </para>
    ///     <para>
    ///         Same <c>ReferenceEquals</c> idiom as
    ///         <c>ChartRenderer.SyncRibbonSubscription</c>. Gating on
    ///         <see cref="_attachedToTree" /> is what makes the invariant
    ///         "handlers exist IFF attached" hold even when Editor is re-bound
    ///         while the control sits outside the tree.
    ///     </para>
    /// </summary>
    private void SyncSelectionSubscription()
    {
        var target = _attachedToTree ? Editor : null;
        if (ReferenceEquals(_subscribedEditor, target)) return;

        if (_subscribedEditor != null)
        {
            _subscribedEditor.SelectedNotes.CollectionChanged -= OnSelectionChanged;
            _subscribedEditor.SelectedChords.CollectionChanged -= OnSelectionChanged;
            _subscribedEditor.SelectedHandShapes.CollectionChanged -= OnSelectionChanged;
            _subscribedEditor.UseHighlightChanged -= OnUseHighlightChanged;
            _subscribedEditor.LinkHighlight.Changed -= OnLinkHighlightChanged;
        }

        _subscribedEditor = target;

        if (_subscribedEditor != null)
        {
            _subscribedEditor.SelectedNotes.CollectionChanged += OnSelectionChanged;
            _subscribedEditor.SelectedChords.CollectionChanged += OnSelectionChanged;
            // Selected HAND SHAPES too: the Chord shapes list's "select uses"
            // selects bars, and the map never showed them.
            _subscribedEditor.SelectedHandShapes.CollectionChanged += OnSelectionChanged;
            _subscribedEditor.UseHighlightChanged += OnUseHighlightChanged;
            _subscribedEditor.LinkHighlight.Changed += OnLinkHighlightChanged;
        }
    }

    /// <summary>The hovered or selected link group changed: repaint once. A pointer moving over the
    /// same chip also raises Changed, and repaints nothing here.</summary>
    private void OnLinkHighlightChanged()
    {
        LinkHighlightHandlerInvocations++;
        if (Editor?.LinkHighlight.EmphasisGroupId == _drawnLinkEmphasis) return;
        RequestRepaint();
    }

    /// <summary>Test seam: how many times the link-highlight handler has run.</summary>
    internal int LinkHighlightHandlerInvocations { get; private set; }

    /// <summary>Test seam: the link marks the last frame drew.</summary>
    internal IReadOnlyList<SongMapLinkMark> LinkMarks => _linkMarks;

    /// <summary>The bulk-edit preview changed: one repaint through the funnel, like
    /// a selection change — a discrete event, not the throttled playhead path.</summary>
    private void OnUseHighlightChanged(object? sender, EventArgs e)
    {
        UseHighlightHandlerInvocations++;
        RequestRepaint();
    }

    /// <summary>Test seam: how many times the preview handler has run.</summary>
    internal int UseHighlightHandlerInvocations { get; private set; }

    /// <summary>Test seam: the use marks the last frame drew for the bulk-edit
    /// preview, and for the selected chords and hand shapes.</summary>
    internal IReadOnlyList<SongMapMark> HighlightMarks => _highlightMarks;

    /// <inheritdoc cref="HighlightMarks" />
    internal IReadOnlyList<SongMapMark> SelectionMarks => _selectionMarks;

    /// <summary>Test seam: the suggested hand shapes' bands the last frame drew.</summary>
    internal IReadOnlyList<SongMapMark> SuggestionMarks => _suggestionMarks;

    /// <summary>Test seam: how many times the map's suggestion latch ran the detector.</summary>
    internal int SuggestionComputeCount => _suggestedHandShapes.ComputeCount;

    private void OnSelectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        SelectionHandlerInvocations++;
        // A marquee raises one change per item and per pointer move: the first repaints at
        // once, the rest of the burst coalesce into one trailing repaint per interval.
        switch (_selectionRepaint.Request(Environment.TickCount64))
        {
            case RepaintDecision.Now:
                RequestRepaint();
                break;
            case RepaintDecision.ScheduleTrailing:
                Avalonia.Threading.DispatcherTimer.RunOnce(_selectionTrailingRepaint,
                    TimeSpan.FromMilliseconds(_selectionRepaint.IntervalMs));
                break;
        }
    }

    private void OnSelectionTrailingRepaint()
    {
        _selectionRepaint.TrailingFired(Environment.TickCount64);
        RequestRepaint();
    }

    /// <summary>Test seam: how many times the selection handler has run. One
    /// collection change must move this by exactly ONE — two means the
    /// double-subscription is back.</summary>
    internal int SelectionHandlerInvocations { get; private set; }

    private void OnThemeChanged()
    {
        // The one palette value this control derives rather than shares: the
        // waveform stroke is an alpha-160 wash of ChartTheme.MeasureColor, so
        // no shared brush carries it and it has to be repainted by hand.
        WaveformBrush.Color = Color.FromArgb(160,
            ChartTheme.MeasureColor.R, ChartTheme.MeasureColor.G, ChartTheme.MeasureColor.B);
        EditTargetWashBrush.Color = Color.FromArgb(90,
            ChartTheme.EditTargetColor.R, ChartTheme.EditTargetColor.G, ChartTheme.EditTargetColor.B);
        var bar = ChartTheme.HandShapeBarColor;
        SuggestionWashBrush.Color = Color.FromArgb(SuggestionWashAlpha, bar.R, bar.G, bar.B);
        SuggestionBarBrush.Color = Color.FromArgb(SuggestionBarAlpha, bar.R, bar.G, bar.B);
        _bookmarkBrush = null;
        _bookmarkTextCache.Clear();
        // Its sibling. Both are FormattedText keyed by a string the CHART
        // supplies, both are built with a theme brush, and only one of them was
        // ever cleared — so section labels kept the old theme's brush and the
        // dictionary kept one entry per distinct section name of every song the
        // session ever opened.
        _sectionTextCache.Clear();
        InvalidateStaticLayer();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ChartTheme.ThemeChanged -= OnThemeChanged;
        // Nothing here survives a detach usefully: both label caches are rebuilt
        // from the chart on the next static-layer pass.
        _bookmarkTextCache.Clear();
        _sectionTextCache.Clear();
        _projectState?.PropertyChanged -= OnProjectStateChanged;
        _attachedToTree = false;
        SyncSelectionSubscription();
        _staticLayerBitmap?.Dispose();
        _staticLayerBitmap = null;
        base.OnDetachedFromVisualTree(e);
    }

    public void InvalidateStaticLayer()
    {
        _staticLayerDirty = true;
        RequestRepaint();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var width = Bounds.Width;
        var height = Bounds.Height;
        if (width <= 0 || height <= 0) return;

        var editor = Editor;
        if (editor == null) return;

        // THE song length this strip is scaled by, and it must be the one the
        // NAVIGATION layer uses: ProjectState.EffectiveTimelineLengthMs (audio
        // when attached, else the tempo grid's end).
        //
        // Deriving its OWN fallback for a chart-only project — a
        // cached max over the beat grid, every note tail, every chord tail and
        // every event — which is a strictly LARGER number, and nothing else in
        // the app agrees with it. `MinimapDragController` seeks against the
        // effective length and `ChartEditorViewModel.ExecuteSeek` CLAMPS to it,
        // so the map would be drawn on one scale and clicked on another: every click
        // would land proportionally early (a chart whose last note rings 5 % past
        // its final beat puts every click 5 % of the song before the note under
        // the pointer), the viewport rectangle would sit where the click was not, and
        // the right end of the strip would not be reachable at all.
        //
        // The bound TotalTimeMs stays as the fallback for a detached control.
        // (What is now clipped instead is a sound whose tail rings past the
        // last beat — a region the transport cannot be seeked into anyway.)
        var totalTimeMs = _projectState?.EffectiveTimelineLengthMs ?? TotalTimeMs;
        if (totalTimeMs <= 0) return;

        var scale = width / totalTimeMs;
        EnsureStaticLayer(width, height, editor, totalTimeMs);
        if (_staticLayerBitmap != null)
        {
            // At the bitmap's OWN size, never squeezed into `width`: the layer is
            // Ceiling(width) device pixels, and stretching it into a fractional width
            // would resample — and blur — the whole map. The
            // sub-pixel overhang is clipped by the control's bounds.
            // Source in the bitmap's pixels, destination in this control's units: the pixels divided
            // by the device scale the layer was built for. They coincide only at a device scale of 1,
            // i.e. before the layer counted the interface scale.
            context.DrawImage(
                _staticLayerBitmap,
                new Rect(0, 0, _staticLayerBitmap.PixelSize.Width, _staticLayerBitmap.PixelSize.Height),
                new Rect(0, 0, _staticLayerBitmap.PixelSize.Width / _layerScaling,
                    _staticLayerBitmap.PixelSize.Height / _layerScaling));
        }

        DrawViewport(context, scale, width, height);
        DrawSuggestions(context, editor, scale, height);
        DrawLinkMarks(context, editor, scale, height);
        DrawSelection(context, editor, scale, height, MinimapFlipStrings);
        DrawUseMarks(context, editor, scale, height);
        DrawBookmarks(context, editor, scale, height);
    }

    /// <summary>
    /// The open hand-shape suggestions: a band per suggested bar under the section band,
    ///     under every other mark so a selection or a preview over the same run still reads. The
    ///     same inputs as <c>ChartRenderer</c>'s dashed bars, so the map and the lane agree.
    /// </summary>
    private void DrawSuggestions(DrawingContext context, ChartEditorViewModel editor, double scale, double height)
    {
        var bars = _suggestedHandShapes.Get(editor.ActiveArrangement, editor.ActiveLevel,
            editor.ShowSuggestedHandShapes, editor.HistoryRevision, Environment.TickCount64);
        SongMapMarks.CollectSuggestions(bars, editor.Beats, scale, SectionBandHeight, height, _suggestionMarks);
        for (var i = 0; i < _suggestionMarks.Count; i++)
        {
            var band = _suggestionMarks[i].Bounds;
            context.DrawRectangle(SuggestionWashBrush, null, band);
            context.DrawRectangle(SuggestionBarBrush, null,
                new Rect(band.X, height - SongMapMarks.BarHeight, band.Width, SongMapMarks.BarHeight));
        }
    }

    /// <summary>The emphasised link group's placements in the arrangement on screen (<see cref="SongMapLinkMarks" />).</summary>
    private void DrawLinkMarks(DrawingContext context, ChartEditorViewModel editor, double scale, double height)
    {
        _drawnLinkEmphasis = editor.LinkHighlight.EmphasisGroupId;
        SongMapLinkMarks.Collect(editor.ActiveProject, editor.ActiveArrangement, _drawnLinkEmphasis, scale,
            SectionBandHeight, height, _linkMarks);
        _linkPainter.Draw(context, _linkMarks);
    }

    /// <summary>
    /// The use marks (<see cref="SongMapMarks" />) — ONE path for both
    ///     sets: the selected chords and hand shapes in white (what the Chord
    ///     shapes list's "select uses" lights), then the chord panel's bulk-edit
    ///     preview on the level on screen, in the EditTarget colour with a faint
    ///     full-height line at each, so "where in the song" reads at a glance.
    /// </summary>
    private void DrawUseMarks(DrawingContext context, ChartEditorViewModel editor, double scale, double height)
    {
        SongMapMarks.Collect(editor.SelectedChords, editor.SelectedHandShapes, editor.Beats, scale, height,
            _selectionMarks);
        for (var i = 0; i < _selectionMarks.Count; i++)
            context.DrawRectangle(SelectionHaloBrush, null, _selectionMarks[i].Bounds);

        var marks = editor.UseHighlight.On(editor.PlacedChords);
        SongMapMarks.Collect(marks.Chords, marks.HandShapes, editor.Beats, scale, height, _highlightMarks);
        // The AI chart pass review's hand positions: replaced and proposed alike.
        SongMapMarks.AppendAnchors(marks.Anchors, editor.Beats, scale, _highlightMarks);
        SongMapMarks.AppendAnchors(marks.ProposedAnchors, editor.Beats, scale, _highlightMarks);
        // …and its section and phrase markers, replaced and proposed alike.
        var (sections, phrases, proposed) = editor.UseHighlight.EventsOn(editor.ActiveArrangement);
        if (sections.Count + phrases.Count + proposed.Count > 0)
            SongMapMarks.AppendEvents(sections.Concat(phrases).Concat(proposed), editor.Beats, scale, _highlightMarks);
        for (var i = 0; i < _highlightMarks.Count; i++)
        {
            var bounds = _highlightMarks[i].Bounds;
            context.DrawRectangle(EditTargetWashBrush, null, new Rect(bounds.X, 0, 1, height));
            context.DrawRectangle(EditTargetMarkBrush, null, bounds);
        }
    }

    private void DrawBookmarks(DrawingContext context, ChartEditorViewModel editor, double scale, double height)
    {
        var bookmarks = editor.Bookmarks;
        if (bookmarks.Count == 0) return;

        var brush = _bookmarkBrush ??= new SolidColorBrush(ChartTheme.BookmarkColor);
        var width = Bounds.Width;
        foreach (var bm in bookmarks)
        {
            var x = bm.Time * scale;
            if (x < 0 || x > width) continue;
            context.DrawRectangle(brush, null, new Rect(x - 0.5, 0, 1.5, height));
            if (!_bookmarkTextCache.TryGetValue(bm.Index, out var text))
            {
                text = new FormattedText(
                    bm.Index.ToString(),
                    CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    SectionTypeface,
                    SectionFontSize,
                    Brushes.Black);
                _bookmarkTextCache[bm.Index] = text;
            }

            // A green tag with a dark number, like the section names beside it: a
            // green number printed straight onto a yellow SOLO bar was unreadable.
            var tag = new Rect(x, 0, text.Width + 5, SectionBandHeight);
            context.DrawRectangle(brush, null, tag, 2, 2);
            var scaling = DeviceScale();
            context.DrawText(text, new Point(
                MinimapViewportMath.SnapToPixel(x + 2.5, scaling),
                MinimapViewportMath.SnapToPixel((SectionBandHeight - text.Height) / 2, scaling)));
        }
    }

    // Static-layer rebuild throttle (v0.32.5 GC work): every committed edit
    // invalidates the layer, and the rebuild costs ~14 ms + a full
    // RenderTargetBitmap redraw of the whole song. During an editing burst
    // that ran PER KEYSTROKE and was a measured top-3 contributor to the
    // "GC kicks in way too often while editing" report. Rebuilds are now
    // rate-limited: a lone edit still rebuilds immediately, a burst redraws
    // at ≤5 Hz with a guaranteed trailing rebuild, and only chart-content
    // changes are throttled (size/waveform/editor changes rebuild at once —
    // a stale layer would be drawn scaled).
    private long _lastStaticRebuildMs;
    private bool _staticRebuildScheduled;
    private bool _cachedFlipStrings;
    private const long StaticRebuildMinIntervalMs = 200;

    private void EnsureStaticLayer(
        double width,
        double height,
        ChartEditorViewModel editor,
        double totalTimeMs)
    {
        // Device pixels, so the map is as sharp as the rest of the window on a
        // scaled display (see StaticLayerPixels). A scaling change (the window
        // moved to another monitor) changes the pixel size and so rebuilds.
        var renderScaling = DeviceScale();
        var (pixelWidth, pixelHeight) = MinimapViewportMath.StaticLayerPixels(width, height, renderScaling);
        var sameTarget = _staticLayerBitmap != null &&
                         _cachedEditor == editor &&
                         ReferenceEquals(_cachedWaveformData, WaveformData) &&
                         _cachedShowWaveform == ShowWaveform &&
                         Math.Abs(_cachedTotalTimeMs - totalTimeMs) < 0.001 &&
                         _cachedPixelWidth == pixelWidth &&
                         _cachedPixelHeight == pixelHeight &&
                         _cachedFlipStrings == MinimapFlipStrings;
        if (!_staticLayerDirty && sameTarget)
            return;

        var now = Environment.TickCount64;
        if (sameTarget && now - _lastStaticRebuildMs < StaticRebuildMinIntervalMs)
        {
            // Mid-burst: draw the (one-edit-stale) layer this frame and
            // schedule the trailing rebuild so the final state always lands.
            if (!_staticRebuildScheduled)
            {
                _staticRebuildScheduled = true;
                Avalonia.Threading.DispatcherTimer.RunOnce(() =>
                {
                    _staticRebuildScheduled = false;
                    InvalidateVisual();
                }, TimeSpan.FromMilliseconds(StaticRebuildMinIntervalMs));
            }

            return;
        }

        _lastStaticRebuildMs = now;

        // Reuse the bitmap when the pixel size is unchanged —
        // CreateDrawingContext clears to transparent, and recreating a
        // ~2560-wide surface per edit was measurable GC/native churn.
        if (_staticLayerBitmap == null ||
            _cachedPixelWidth != pixelWidth || _cachedPixelHeight != pixelHeight)
        {
            _staticLayerBitmap?.Dispose();
            // 96 DPI — one bitmap unit is one pixel — with the device scale applied as a transform
            // while drawing, below. A DPI-scaled bitmap (the previous build) mapped its content and
            // its draw-back rect through two different conventions and came out magnified.
            _staticLayerBitmap = new RenderTargetBitmap(new PixelSize(pixelWidth, pixelHeight), new Vector(96, 96));
        }

        using var bitmapContext = _staticLayerBitmap.CreateDrawingContext();
        _layerScaling = renderScaling > 0 ? renderScaling : 1.0;
        using var deviceScale = bitmapContext.PushTransform(Matrix.CreateScale(_layerScaling, _layerScaling));

        var scale = width / totalTimeMs;

        // The named section band owns the top `SectionBandHeight` of the map and
        // everything else is pushed below it. Without the translate the note
        // ticks started at y=0 and painted straight over the section names —
        // which is what the owner saw. A translate rather than an offset
        // threaded through four draw helpers: they all measure lanes from 0, and
        // only this one call site knows the band exists.
        DrawSectionBand(bitmapContext, editor, scale, totalTimeMs);

        var bodyHeight = Math.Max(1, height - SectionBandHeight);
        using (bitmapContext.PushTransform(Matrix.CreateTranslation(0, SectionBandHeight)))
        {
            DrawWaveform(bitmapContext, width, bodyHeight);
            DrawEvents(bitmapContext, editor, scale, bodyHeight, totalTimeMs);
            DrawNotes(bitmapContext, editor, scale, bodyHeight, MinimapFlipStrings);
        }

        _cachedEditor = editor;
        _cachedWaveformData = WaveformData;
        _cachedShowWaveform = ShowWaveform;
        _cachedTotalTimeMs = totalTimeMs;
        _cachedPixelWidth = pixelWidth;
        _cachedPixelHeight = pixelHeight;
        _cachedFlipStrings = MinimapFlipStrings;
        _staticLayerDirty = false;
    }

    private void DrawWaveform(DrawingContext context, double width, double height)
    {
        if (!ShowWaveform || WaveformData is not { HasData: true } samples) return;
        if (width <= 0) return;
        // Minimap covers the full song in `width` pixels; pick the LOD whose
        // block ≥ 1 minimap pixel.
        var totalMs = samples.BaseBlockMs * samples.Mipmaps[0].Length;
        if (totalMs <= 0) return;
        var idealBlockMs = totalMs / width;
        var levelIndex = samples.PickLevel(idealBlockMs);
        var waveform = samples.Mipmaps[levelIndex];
        if (waveform.Length < 2) return;

        var centerY = height / 2.0;
        var step = Math.Max(1, waveform.Length / (int)Math.Max(1, width));
        Point? previousTop = null;
        Point? previousBottom = null;

        for (var i = 0; i < waveform.Length; i += step)
        {
            var x = i / (double)(waveform.Length - 1) * width;
            var amplitude = Math.Clamp(waveform[i], -1f, 1f) * centerY;
            var top = new Point(x, centerY - amplitude);
            var bottom = new Point(x, centerY + amplitude);

            if (previousTop.HasValue) context.DrawLine(WaveformPen, previousTop.Value, top);
            if (previousBottom.HasValue) context.DrawLine(WaveformPen, previousBottom.Value, bottom);

            previousTop = top;
            previousBottom = bottom;
        }
    }

    /// <summary>Height of the v0.29 section-status strip along the bottom edge.</summary>
    private const double StatusStripHeight = 4;

    /// <summary>Status → existing ChartTheme brush (no new palette entries):
    /// muted todo, amber draft, blue review, green done.</summary>
    private static IBrush StatusBrush(SectionStatus status) => status switch
    {
        SectionStatus.Draft => ChartTheme.GoldBrush,
        SectionStatus.Review => ChartTheme.SkyBlueBrush,
        SectionStatus.Done => ChartTheme.SpringGreenBrush,
        _ => ChartTheme.GrayBrush // Todo (and any future value)
    };

    /// <summary>Reused across static-layer rebuilds — see <see cref="DrawEvents" />.
    /// The control is a single instance and every rebuild is on the UI thread,
    /// so one buffer is enough.</summary>
    private readonly List<SectionStatusSpans.SectionPoint> _sectionScratch = [];

    private void DrawEvents(DrawingContext context, ChartEditorViewModel editor, double scale, double height,
        double totalTimeMs)
    {
        // v0.29 section-status tracker: a thin tinted strip along the bottom,
        // one span per STATUSED section (pure math in SectionStatusSpans).
        // The list is a REUSED field: this runs on the ≤5 Hz static-layer
        // rebuild, which is exactly the path the v0.32.5 editing-GC work
        // targeted, and a fresh List per rebuild is pure garbage.
        var sections = _sectionScratch;
        sections.Clear();
        foreach (var eventPoint in editor.EventPoints)
        {
            if (!string.IsNullOrEmpty(eventPoint.Section))
            {
                sections.Add(new SectionStatusSpans.SectionPoint(
                    eventPoint.Position.GetPositionMs(editor.Beats), eventPoint.Status));
            }
        }

        foreach (var span in SectionStatusSpans.Build(sections, totalTimeMs))
        {
            context.DrawRectangle(StatusBrush(span.Status), null,
                new Rect(span.StartMs * scale, height - StatusStripHeight,
                    Math.Max(1, (span.EndMs - span.StartMs) * scale), StatusStripHeight));
        }

        // Boundary ticks down the body, under the named band. A section already
        // shows as a labelled bar above; this is the hairline that lets you line
        // a note up with where it starts.
        foreach (var eventPoint in editor.EventPoints)
        {
            var x = eventPoint.Position.GetPositionMs(editor.Beats) * scale;
            if (!string.IsNullOrEmpty(eventPoint.Section))
            {
                context.DrawRectangle(
                    eventPoint.Status == SectionStatus.None
                        ? SectionBrush
                        : StatusBrush(eventPoint.Status),
                    null, new Rect(x, 0, 1, height));
            }
            else if (!string.IsNullOrEmpty(eventPoint.Phrase))
            {
                context.DrawRectangle(PhraseBrush, null, new Rect(x, 0, 1, height));
            }
        }
    }

    /// <summary>
    ///     The SONG MAP's named section band (<c>Chart Editor.dc.html</c>): one
    ///     filled bar per section, spanning to the next one, with its NAME
    ///     inside.
    ///
    ///     <para>
    ///         A bare 2 px full-height tick per section would say where a boundary is and
    ///         nothing about what the song is made
    ///         of — you could not tell the chorus from the solo, which is the one
    ///         question a whole-song overview should answer at a glance.
    ///     </para>
    ///
    ///     <para>
    ///         Colour is keyed off the section NAME, so every "chorus" in a song
    ///         is the same colour and two different names never collide by
    ///         accident of ordering. The label is drawn only where the bar is
    ///         wide enough to hold it — a clipped word is worse than none.
    ///     </para>
    /// </summary>
    private void DrawSectionBand(DrawingContext context, ChartEditorViewModel editor,
        double scale, double totalTimeMs)
    {
        double? openMs = null;
        string? openName = null;

        foreach (var point in editor.EventPoints)
        {
            if (string.IsNullOrEmpty(point.Section)) continue;
            var atMs = point.Position.GetPositionMs(editor.Beats);
            if (openName != null) EmitSection(context, openName, openMs!.Value, atMs, scale);
            openMs = atMs;
            openName = point.Section;
        }

        if (openName != null) EmitSection(context, openName, openMs!.Value, totalTimeMs, scale);
    }

    private void EmitSection(DrawingContext context, string name, double startMs, double endMs,
        double scale)
    {
        var x = startMs * scale;
        var w = (endMs - startMs) * scale;
        if (w < 2) return;

        context.DrawRectangle(SectionBandBrush(name), null,
            new Rect(x, 0, w, SectionBandHeight), 2, 2);

        // Only when the bar can hold the word. Cached per name: FormattedText
        // runs text shaping, and this rebuild is on the ≤5 Hz static layer.
        if (w < 34) return;
        if (!_sectionTextCache.TryGetValue(name, out var text))
        {
            text = new FormattedText(name.ToUpperInvariant(), CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, SectionTypeface, SectionFontSize, Brushes.Black);
            _sectionTextCache[name] = text;
        }

        if (text.Width + 6 > w) return;
        using (context.PushClip(new Rect(x, 0, w, SectionBandHeight)))
        {
            context.DrawText(text, new Point(
                MinimapViewportMath.SnapToPixel(x + 3, _layerScaling),
                MinimapViewportMath.SnapToPixel((SectionBandHeight - text.Height) / 2, _layerScaling)));
        }
    }

    /// <summary>Stable per-NAME colour from the chart palette, so the same
    /// section name is always the same colour across the song — and the same
    /// colour as its chip on the timeline (<see cref="SectionColors" />).</summary>
    private static IBrush SectionBandBrush(string name) => SectionBandPalette[SectionColors.IndexFor(name)];

    // In SectionColors.Base's order.
    private static readonly IBrush[] SectionBandPalette =
    [
        ChartTheme.SkyBlueBrush, ChartTheme.MagentaBrush, ChartTheme.GoldBrush,
        ChartTheme.SpringGreenBrush, ChartTheme.CyanBrush
    ];

    private readonly Dictionary<string, FormattedText> _sectionTextCache = new(StringComparer.Ordinal);

    /// <summary>Device pixels per map unit, including the interface scale's layout transform —
    /// the factor the 2D timeline and the 3D view already render at (<see cref="VisualPixelScale" />).</summary>
    private double DeviceScale() => VisualPixelScale.For(this);

    /// <summary>Height of the named band along the map's top edge.</summary>
    private const double SectionBandHeight = 13;

    /// <summary>The band's label size. It was 9 px, which at 100 % scaling is a smudge whatever the
    /// renderer does; 10 px on a 13-px band reads.</summary>
    private const double SectionFontSize = 10;

    /// <summary>The static layer's device-pixel scale, so its labels land on whole pixels: a label at
    /// a fractional x/y spreads every stroke over two pixel columns, which is the blur the owner saw.</summary>
    private double _layerScaling = 1.0;

    // The minimap shares the 2D timeline's string→lane order (low E on top by
    // default, flipping with lefty/invert via ChartRenderContext.LaneTopFor), so
    // the overview never reads upside-down relative to the timeline above it.
    private bool MinimapFlipStrings =>
        (_projectState?.LeftyFlip ?? false) ^ (_projectState?.InvertStrings ?? false);

    private static int MinimapLane(int stringIndex, int stringCount, bool flipStrings)
        => flipStrings ? stringCount - 1 - stringIndex : stringIndex;

    private static void DrawNotes(DrawingContext context, ChartEditorViewModel editor, double scale, double height,
        bool flipStrings)
    {
        // The ACTIVE arrangement's lane count, never a project-level one: the
        // overview must draw the same lanes the timeline above it does, and a
        // 4-string bass in a project with a 6-string lead is the case that made
        // the project-level copy (now removed) wrong. See the note on ChartData.
        var stringCount = StringDigitTargeting.StringCountOf(editor.ActiveArrangement);
        var laneHeight = height / stringCount;
        var markHeight = Math.Max(2, laneHeight * 0.32);
        var stringBrushes = StringBrushes;

        foreach (var chord in editor.PlacedChords)
        {
            foreach (var chordNote in chord.ChordNotes)
            {
                var y = MinimapLane(chordNote.StringIndex, stringCount, flipStrings) * laneHeight + (laneHeight - markHeight) / 2;
                var x = chord.Position.GetPositionMs(editor.Beats) * scale;
                var sustainWidth = Math.Max(2,
                    ((chord.Position + chordNote.Sustain).GetPositionMs(editor.Beats) -
                     chord.Position.GetPositionMs(editor.Beats)) * scale);
                var brush = stringBrushes[ChartTheme.StringSlot(chordNote.StringIndex, stringCount)];
                context.DrawRectangle(brush, null, new Rect(x, y, sustainWidth, markHeight));
            }
        }

        foreach (var note in editor.PlacedNotes)
        {
            var y = MinimapLane(note.StringIndex, stringCount, flipStrings) * laneHeight + (laneHeight - markHeight) / 2;
            var x = note.Position.GetPositionMs(editor.Beats) * scale;
            var sustainWidth = Math.Max(2,
                (note.EndPosition.GetPositionMs(editor.Beats) - note.Position.GetPositionMs(editor.Beats)) * scale);
            var brush = stringBrushes[ChartTheme.StringSlot(note.StringIndex, stringCount)];
            context.DrawRectangle(brush, null, new Rect(x, y, sustainWidth, markHeight));
        }
    }

    private void DrawSelection(DrawingContext context, ChartEditorViewModel editor, double scale, double height,
        bool flipStrings)
    {
        if (editor.SelectedNotes.Count == 0 && editor.SelectedChords.Count == 0) return;

        // The ACTIVE arrangement's lane count, never a project-level one: the
        // overview must draw the same lanes the timeline above it does, and a
        // 4-string bass in a project with a 6-string lead is the case that made
        // the project-level copy (now removed) wrong. See the note on ChartData.
        var stringCount = StringDigitTargeting.StringCountOf(editor.ActiveArrangement);
        var laneHeight = height / stringCount;
        var markHeight = Math.Max(3, laneHeight * 0.55);
        var halo = SelectionHaloBrush;
        var stringBrushes = StringBrushes;

        foreach (var note in editor.SelectedNotes)
        {
            DrawSelectedMarker(context, halo, stringBrushes, note.StringIndex,
                note.Position.GetPositionMs(editor.Beats) * scale,
                Math.Max(3, (note.EndPosition.GetPositionMs(editor.Beats) -
                             note.Position.GetPositionMs(editor.Beats)) * scale),
                stringCount, laneHeight, markHeight, flipStrings);
        }

        foreach (var chord in editor.SelectedChords)
        {
            foreach (var cn in chord.ChordNotes)
            {
                DrawSelectedMarker(context, halo, stringBrushes, cn.StringIndex,
                chord.Position.GetPositionMs(editor.Beats) * scale,
                Math.Max(3, ((chord.Position + cn.Sustain).GetPositionMs(editor.Beats) -
                             chord.Position.GetPositionMs(editor.Beats)) * scale),
                stringCount, laneHeight, markHeight, flipStrings);
            }
        }
    }

    private static void DrawSelectedMarker(DrawingContext context, IBrush halo, IBrush[] stringBrushes,
        int stringIndex, double x, double w, int stringCount, double laneHeight, double markHeight, bool flipStrings)
    {
        var y = MinimapLane(stringIndex, stringCount, flipStrings) * laneHeight + (laneHeight - markHeight) / 2;
        // White halo + opaque coloured core makes the selection unmistakable
        // even at minimap scale.
        context.DrawRectangle(halo, null, new Rect(x - 1.5, y - 1.5, w + 3, markHeight + 3));
        var brush = stringBrushes[ChartTheme.StringSlot(stringIndex, stringCount)];
        context.DrawRectangle(brush, null, new Rect(x, y, w, markHeight));
    }

    private void DrawViewport(DrawingContext context, double scale, double width, double height)
    {
        var currentTimeMs = _projectState?.CurrentTimeMs ?? 0;
        // The view-window box tracks the EFFECTIVE anchor (pan-aware, 0.16
        // 6.6); the playhead line below stays on the transport position.
        var viewAnchorMs = _projectState?.TimelineViewAnchorEffectiveMs ?? 0;
        // NOT `width`: that is the MAP's width, and the map is not the timeline
        // (MinimapViewportMath explains the 118 px the caption cell costs).
        var timelineWidth = MinimapViewportMath.ResolveTimelineWidth(
            TimelineViewportWidth,
            _projectState?.EditorUi.TimelineViewportWidthPx ?? 0,
            width);
        var (boxX, boxWidth) = MinimapViewportMath.ViewportBox(
            viewAnchorMs, ChartEditorConstants.PlayheadX, PixelsPerMs, timelineWidth, scale);
        context.DrawRectangle(ViewportBrush, ViewportPen, new Rect(boxX, 0, boxWidth, height));

        var playheadX = currentTimeMs * scale;
        context.DrawLine(PlayheadPen, new Point(playheadX, 0), new Point(playheadX, height));
    }

}
