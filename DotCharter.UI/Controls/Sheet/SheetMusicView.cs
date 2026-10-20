using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Threading;
using DotCharter.Services.Export.Sheet;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.UI.Controls.Sheet;

/// <summary>
/// Toggleable strip rendered above the timeline that shows the active
/// arrangement as standard notation + tablature using AlphaTab. Renders
/// directly onto Avalonia's GPU SKCanvas via <see cref="ICustomDrawOperation"/>
/// — no PNG round-trip, no <c>RenderTargetBitmap</c>, no <c>Image</c> control —
/// so scrolling and the playback cursor stay at native frame-rate even on a
/// long arrangement.
/// </summary>
public sealed class SheetMusicView : UserControl
{
    public static readonly StyledProperty<ProjectState?> ProjectStateProperty =
        AvaloniaProperty.Register<SheetMusicView, ProjectState?>(nameof(ProjectState));

    /// <summary>
    /// User zoom for the sheet. 1.0 = fit the strip height exactly (the
    /// historical behavior); larger values enlarge notation/tab text and the
    /// strip scrolls vertically. Ctrl+wheel adjusts it in 0.1 steps.
    /// </summary>
    public static readonly StyledProperty<double> UserScaleProperty =
        AvaloniaProperty.Register<SheetMusicView, double>(nameof(UserScale), 1.0, defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    /// <summary>0 = notation + tab, 1 = notation only, 2 = tab only. Fewer
    /// staves render LARGER at the same strip height.</summary>
    public static readonly StyledProperty<int> StavesModeProperty =
        AvaloniaProperty.Register<SheetMusicView, int>(nameof(StavesMode));

    /// <summary>
    /// false = single horizontal STRIP (the default synced-follow editing flow),
    /// true = GP8-style PAGE layout: bars wrap into stacked systems, the content
    /// scrolls vertically, and playhead-follow scrolls to the active system.
    /// Click-mapping, selection and hover work in BOTH layouts (both go through
    /// the measure/beat bounds, never screen heuristics).
    /// </summary>
    public static readonly StyledProperty<bool> PageModeProperty =
        AvaloniaProperty.Register<SheetMusicView, bool>(nameof(PageMode));

    /// <summary>
    /// The app-wide shared selection model. The strip highlights the selected
    /// notes/chords (so a selection made in the 2D timeline or 3D highway shows
    /// here too) and pushes its own click-to-select through <see cref="SelectRequested"/>.
    /// The strip subscribes to <see cref="ISelectionService.SelectionChanged"/>
    /// and must unsubscribe on detach (pane views are re-created on float/redock).
    /// </summary>
    public static readonly StyledProperty<ISelectionService?> SelectionProperty =
        AvaloniaProperty.Register<SheetMusicView, ISelectionService?>(nameof(Selection));

    /// <summary>
    /// The app-wide undo history. The strip subscribes to
    /// <see cref="IHistoryService.HistoryChanged"/> (attach/detach balanced, like
    /// <see cref="Selection"/>) so every committed edit — including edits made ON
    /// the sheet — re-renders the notation. ProjectState property changes alone
    /// don't cover in-place note mutations.
    /// </summary>
    public static readonly StyledProperty<IHistoryService?> HistoryProperty =
        AvaloniaProperty.Register<SheetMusicView, IHistoryService?>(nameof(History));

    public const double MinUserScale = 0.6;
    public const double MaxUserScale = 2.5;

    private readonly SheetMusicRenderer _renderer = new();
    private readonly SheetCanvas _canvas;
    private readonly CursorOverlay _cursorOverlay;
    private readonly SelectionOverlay _selectionOverlay;
    private readonly HoverOverlay _hoverOverlay;
    private readonly SheetMusicViewInput _input;
    private readonly ScrollViewer _scroll;
    private readonly TextBlock _statusText;
    private readonly DispatcherTimer _debounce;
    private CancellationTokenSource? _renderCts;
    private double _lastWidth;
    private double _lastHeight;
    private double _sourceHeight;
    private double _songStartMs;
    private double _songEndMs;
    private int _cursorPostPending;
    private int _selectionPostPending;
    private readonly Action _cursorPostAction;
    private readonly Action _selectionPostAction;
    private bool _rebuildPendingWhileHidden;
    private bool _attached;

    /// <summary>
    ///     What is currently ON the canvas, so a rebuild that would reproduce it
    ///     can skip the AlphaTab layout + AlphaSkia raster. Null whenever the
    ///     canvas holds nothing usable — no arrangement, an empty render, a
    ///     thrown export. See <see cref="SheetRenderKey" />.
    /// </summary>
    private SheetRenderKey? _lastRenderedKey;
    private IReadOnlyList<AlphaTexExporter.MeasureSpan> _measureSpans = Array.Empty<AlphaTexExporter.MeasureSpan>();
    private IReadOnlyList<SheetMusicRenderer.MeasureBound> _measureBounds = Array.Empty<SheetMusicRenderer.MeasureBound>();

    /// <summary>Monotonic cursor into <see cref="_measureSpans" /> — see
    /// <see cref="SpanIndexAt(double)" />. Never needs resetting: it is clamped
    /// and then walked in both directions.</summary>
    private int _spanCursor;

    // Page-layout extras: per-system tab lattices (string Y varies per stacked
    // system) and a measure-index → bound map for the per-item Y (cursor segment,
    // selection band, hover column all need the containing system's Y in page
    // mode). Both empty / unused in the strip layout.
    private IReadOnlyList<SheetMusicRenderer.SystemStringLattice> _systemLattices =
        Array.Empty<SheetMusicRenderer.SystemStringLattice>();
    private Dictionary<int, SheetMusicRenderer.MeasureBound> _measureBoundByIndex = [];
    private bool _pageMode;

    // Editing surface state: the exporter's per-beat map, AlphaTab's per-beat
    // bounds and the tab staff's string-line lattice — together they turn a
    // click on a glyph into the exact domain note (and a double-click into an
    // insertion point). Swapped atomically with the render result.
    private IReadOnlyList<AlphaTexExporter.SheetBeat> _beatMap = Array.Empty<AlphaTexExporter.SheetBeat>();
    private Dictionary<int, List<(SheetMusicRenderer.BeatBound Bound, AlphaTexExporter.SheetBeat Beat)>> _beatsByBar = [];
    private IReadOnlyList<SheetMusicRenderer.StringLine> _stringLattice = Array.Empty<SheetMusicRenderer.StringLine>();

    /// <summary>
    /// Raised when the user clicks or drags on the sheet to move the
    /// playhead. Payload is the requested transport time in ms. Wired by
    /// MainWindow to <c>ChartEditor.ExecuteSeek</c>.
    /// </summary>
    public event Action<double>? SeekRequested;

    /// <summary>
    /// Raised on a click (not on drag) with the clicked transport time so the
    /// host can select the nearest note/chord — the FALLBACK when the click
    /// cannot be resolved to an exact glyph (bounds unavailable).
    /// </summary>
    public event Action<double>? SelectRequested;

    /// <summary>
    /// Raised when a click resolves to an exact rendered beat: the payload is
    /// the precise domain notes/chords that beat was emitted from, plus whether
    /// the click was ADDITIVE (Ctrl held → toggle into the existing selection
    /// rather than replace it). Empty lists on a non-additive click = the user
    /// clicked a REST (host should clear the selection); an additive rest click
    /// is a no-op.
    /// </summary>
    public event Action<IReadOnlyList<GuitarNote>, IReadOnlyList<Chord>, bool>? SelectSoundsRequested;

    /// <summary>
    /// Raised on a double-click over the sheet: insert a note at the clicked
    /// time (host quantizes to the editor grid) on the given DOMAIN string.
    /// </summary>
    public event Action<double, int>? AddNoteRequested;

    /// <summary>GP-style fret typing: raised per keystroke with the resolved
    /// fret value (multi-digit within a timeout).</summary>
    public event Action<int>? SetFretRequested;

    /// <summary>GP-style duration step: +1 = longer (double the note value,
    /// OemPlus/Num+), −1 = shorter (halve it, OemMinus/Num−). Raised only while
    /// the sheet is focused AND a sound is selected — so with nothing selected
    /// the keys fall through to the global playback-speed binding.</summary>
    public event Action<int>? DurationStepRequested;

    public SheetMusicView()
    {
        // The strip is an editing surface: it takes focus on click so digit
        // keys type frets here, while unhandled keys (Entf, arrows) keep
        // bubbling to the window's global keybind routes.
        Focusable = true;

        // Cached once: a fresh closure per coalesced render-priority post
        // (cursor at 60 Hz during playback, selection during rubber-band
        // drags) would defeat the point of the coalescing.
        _cursorPostAction = () =>
        {
            Interlocked.Exchange(ref _cursorPostPending, 0);
            UpdateCursor();
        };
        _selectionPostAction = () =>
        {
            Interlocked.Exchange(ref _selectionPostPending, 0);
            UpdateSelectionHighlights();
        };

        Background = new SolidColorBrush(Color.FromRgb(28, 28, 32));
        ClipToBounds = true;

        _canvas = new SheetCanvas();
        _selectionOverlay = new SelectionOverlay { IsHitTestVisible = false };
        _hoverOverlay = new HoverOverlay { IsHitTestVisible = false };
        _cursorOverlay = new CursorOverlay { IsHitTestVisible = false };

        // Stack the selection-highlight + hover + cursor overlays on top of the
        // chunked canvas inside the scroll viewer so all share the scroll offset.
        // Only the overlay that changed invalidates, so chunks never re-blit
        // just to advance the playhead or move the selection highlight — that's
        // the key to smooth, lag-free motion at native frame rate. Order (bottom
        // → top): selection, hover, cursor — the playhead stays visible over a
        // highlight, and hover reads over a selection without hiding the cursor.
        // The Grid NEEDS a background to be hit-testable: without one the click
        // target is the ScrollViewer behind it (the chunks' custom draw op opts
        // out of hit-testing) and none of these pointer handlers ever fire —
        // click-to-seek/select and every editing gesture silently die.
        var scrollContent = new Grid
        {
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            Background = Brushes.Transparent
        };
        scrollContent.Children.Add(_canvas);
        scrollContent.Children.Add(_selectionOverlay);
        scrollContent.Children.Add(_hoverOverlay);
        scrollContent.Children.Add(_cursorOverlay);

        // Press seeks + selects, drag scrubs, double-click inserts, move hovers:
        // SheetMusicViewInput hangs every pointer handler on the scroll content.
        _input = new SheetMusicViewInput(this, scrollContent, _canvas, _hoverOverlay);

        _statusText = new TextBlock
        {
            Text = LocalizationService.Instance["SheetMusic_Status_Idle"],
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromRgb(170, 170, 170))
        };

        _scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = scrollContent,
            Background = Brushes.Transparent
        };

        var root = new Grid();
        root.Children.Add(_scroll);
        root.Children.Add(_statusText);
        Content = root;

        _debounce = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); _ = RebuildAsync(); };

        // Ctrl+wheel zooms the sheet; a plain wheel keeps scrolling.
        AddHandler(PointerWheelChangedEvent, (_, e) =>
        {
            if (!e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Control)) return;
            var step = e.Delta.Y > 0 ? 0.1 : -0.1;
            UserScale = Math.Clamp(Math.Round(UserScale + step, 2), MinUserScale, MaxUserScale);
            e.Handled = true;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    public ProjectState? ProjectState
    {
        get => GetValue(ProjectStateProperty);
        set => SetValue(ProjectStateProperty, value);
    }

    public double UserScale
    {
        get => GetValue(UserScaleProperty);
        set => SetValue(UserScaleProperty, value);
    }

    public int StavesMode
    {
        get => GetValue(StavesModeProperty);
        set => SetValue(StavesModeProperty, value);
    }

    public bool PageMode
    {
        get => GetValue(PageModeProperty);
        set => SetValue(PageModeProperty, value);
    }

    public ISelectionService? Selection
    {
        get => GetValue(SelectionProperty);
        set => SetValue(SelectionProperty, value);
    }

    public IHistoryService? History
    {
        get => GetValue(HistoryProperty);
        set => SetValue(HistoryProperty, value);
    }

    // The strip is dock-hosted now: floating/redocking detaches one view
    // instance and materializes another. Without detach-time unsubscription
    // every detached instance stays rooted by the singleton ProjectState
    // (and keeps posting cursor updates) for the process lifetime.
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        if (ProjectState is { } state)
            state.PropertyChanged -= OnStateChanged;
        if (Selection is { } selection)
            selection.SelectionChanged -= OnSelectionChanged;
        if (History is { } history)
            history.HistoryChanged -= OnHistoryChanged;
        _debounce.Stop();
        // Stopping the debounce only prevents the NEXT rebuild. A render already in
        // flight would keep an AlphaTab layout + raster running for a view nobody
        // will ever show again (every float/redock during a rebuild burned one).
        _renderCts?.Cancel();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        if (Selection is { } selection)
        {
            // Idempotent thanks to -= first — the singleton selection service
            // would otherwise keep every detached (floated/redocked) strip
            // rooted for the process lifetime.
            selection.SelectionChanged -= OnSelectionChanged;
            selection.SelectionChanged += OnSelectionChanged;
        }
        if (History is { } history)
        {
            history.HistoryChanged -= OnHistoryChanged;
            history.HistoryChanged += OnHistoryChanged;
        }
        if (ProjectState is { } state)
        {
            // Balance the detach unsubscription (idempotent thanks to -= first).
            state.PropertyChanged -= OnStateChanged;
            state.PropertyChanged += OnStateChanged;
            ScheduleRebuild();
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ProjectStateProperty)
        {
            if (change.OldValue is ProjectState oldState)
                oldState.PropertyChanged -= OnStateChanged;
            if (change.NewValue is ProjectState newState)
                newState.PropertyChanged += OnStateChanged;
            ScheduleRebuild();
        }
        else if (change.Property == SelectionProperty)
        {
            if (change.OldValue is ISelectionService oldSel)
                oldSel.SelectionChanged -= OnSelectionChanged;
            if (change.NewValue is ISelectionService newSel && _attached)
            {
                newSel.SelectionChanged -= OnSelectionChanged;
                newSel.SelectionChanged += OnSelectionChanged;
            }
            UpdateSelectionHighlights();
        }
        else if (change.Property == HistoryProperty)
        {
            if (change.OldValue is IHistoryService oldHist)
                oldHist.HistoryChanged -= OnHistoryChanged;
            if (change.NewValue is IHistoryService newHist && _attached)
            {
                newHist.HistoryChanged -= OnHistoryChanged;
                newHist.HistoryChanged += OnHistoryChanged;
            }
        }
        else if (change.Property == BoundsProperty)
        {
            // Height matters too now: the render scale is derived from the
            // strip height, so a resized strip needs a re-render to stay at
            // 1:1 device pixels instead of stretching the old bitmaps.
            if (Math.Abs(Bounds.Width - _lastWidth) > 16 || Math.Abs(Bounds.Height - _lastHeight) > 24)
                ScheduleRebuild();
        }
        else if (change.Property == IsVisibleProperty && change.GetNewValue<bool>())
        {
            // The strip stays in the tree when toggled off; catch up on any
            // chart edits that arrived while it was hidden.
            if (_rebuildPendingWhileHidden)
                ScheduleRebuild();
        }
        else if (change.Property == StavesModeProperty)
        {
            ScheduleRebuild();
        }
        else if (change.Property == PageModeProperty)
        {
            ApplyScrollBarPolicy();
            ScheduleRebuild();
        }
        else if (change.Property == UserScaleProperty)
        {
            ApplyScrollBarPolicy();
            ScheduleRebuild();
        }
    }

    /// <summary>
    /// Strip: horizontal scroll always, vertical only while zoomed in (at
    /// fit-height the whole score fits and a scrollbar would just steal strip
    /// pixels). Page: vertical scroll always (systems stack downward), horizontal
    /// off (systems are fit to the page width).
    /// </summary>
    private void ApplyScrollBarPolicy()
    {
        if (PageMode)
        {
            _scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            _scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        }
        else
        {
            _scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
            _scroll.VerticalScrollBarVisibility = Math.Clamp(UserScale, MinUserScale, MaxUserScale) > 1.001
                ? ScrollBarVisibility.Auto
                : ScrollBarVisibility.Disabled;
        }
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ProjectState.CurrentTimeMs):
                if (!IsEffectivelyVisible) break;
                // Coalesce high-frequency updates (minimap drag fires hundreds
                // of times per second). Only enqueue one render-priority job
                // at a time; it will read the latest CurrentTimeMs when it
                // runs, so we never queue work we can't catch up to.
                if (Interlocked.Exchange(ref _cursorPostPending, 1) == 0)
                    Dispatcher.UIThread.Post(_cursorPostAction, DispatcherPriority.Render);
                break;
            case nameof(ProjectState.ActiveArrangement):
            case nameof(ProjectState.ActiveLevel):
            case nameof(ProjectState.PlacedNotes):
            case nameof(ProjectState.PlacedChords):
            case nameof(ProjectState.Beats):
                ScheduleRebuild();
                break;
        }
    }

    private void OnHistoryChanged()
    {
        // Every committed chart action (add/delete/fret change — from ANY view,
        // including this one) re-renders the notation. Edits land on the UI
        // thread today; post defensively (ScheduleRebuild touches a DispatcherTimer).
        if (Dispatcher.UIThread.CheckAccess()) ScheduleRebuild();
        else Dispatcher.UIThread.Post(ScheduleRebuild);
    }

    private void OnSelectionChanged(object? sender, EventArgs e)
    {
        if (!IsEffectivelyVisible) return;
        // Coalesce like the cursor path: selection can change in bursts (e.g. a
        // rubber-band select in the 2D timeline). Only one render-priority job
        // is queued; it reads the latest selection when it runs. Recomputing the
        // highlight overlay never re-blits the cached chunk bitmaps.
        if (Interlocked.Exchange(ref _selectionPostPending, 1) == 0)
            Dispatcher.UIThread.Post(_selectionPostAction, DispatcherPriority.Render);
    }

    private void ScheduleRebuild()
    {
        // Rendering a full sheet is expensive (AlphaTab layout + AlphaSkia
        // raster); don't pay for it while the strip is toggled off.
        if (!IsEffectivelyVisible)
        {
            _rebuildPendingWhileHidden = true;
            return;
        }
        _rebuildPendingWhileHidden = false;
        _debounce.Stop();
        _debounce.Start();
    }

    private async Task RebuildAsync()
    {
        var state = ProjectState;
        if (state?.ActiveArrangement == null || state.ActiveLevel == null || state.Beats.Count == 0)
        {
            _canvas.SetResult(null);
            // Nothing is on the canvas any more, so the next rebuild may not be
            // skipped however familiar its key looks.
            _lastRenderedKey = null;
            _statusText.IsVisible = true;
            _statusText.Text = LocalizationService.Instance["SheetMusic_Status_NoArrangement"];
            return;
        }

        var width = Math.Max(640, Bounds.Width);
        _lastWidth = width;
        _lastHeight = Bounds.Height;

        CancellationTokenSource cts;

        try
        {
            // Inside the try: a chart the exporter chokes on must not throw out of
            // this fire-and-forget task (RebuildAsync is `_ = `-discarded), leaving
            // the pane stuck on "Rendering sheet music…" forever with nothing logged.
            //
            // The export stays HERE, on the UI thread, before any await: it reads
            // the live chart graph and an edit landing under a worker would tear
            // it. It is also the only way to find out whether anything the
            // notation shows actually changed.
            var export = AlphaTexExporter.Export(
                state.ActiveProject, state.ActiveArrangement, state.ActiveLevel, state.Beats);

            // Device pixels per unit drive the render resolution in both layouts — the window's
            // scaling AND the interface scale's layout transform (VisualPixelScale). With the
            // window's scaling alone the raster came out 1/uiScale too small and was stretched
            // back up on screen, which softened every glyph at auto interface scale.
            var dpi = VisualPixelScale.For(this);
            var stripHeight = Bounds.Height;
            var zoom = Math.Clamp(UserScale, MinUserScale, MaxUserScale);
            var pageMode = PageMode;
            var staves = Math.Clamp(StavesMode, 0, 2);

            // Before the key check: the spans map song time to bars, and the AlphaTex does not
            // carry beat TIMES, so a beat nudged a few ms (a sub-BPM tempo move) skips
            // the render below with identical pixels but must still move the cursor.
            _measureSpans = export.MeasureSpans;
            if (_measureSpans.Count > 0)
            {
                _songStartMs = _measureSpans[0].StartMs;
                _songEndMs = _measureSpans[^1].EndMs;
            }
            else
            {
                _songStartMs = 0;
                _songEndMs = 0;
            }

            var key = new SheetRenderKey(export.AlphaTex, width, stripHeight, dpi, zoom, pageMode, staves);
            if (!SheetRenderKey.ShouldRerender(_lastRenderedKey, key))
            {
                // Byte-identical AlphaTex at the same geometry: the AlphaTab
                // layout and the AlphaSkia raster would land on the pixels
                // already on the canvas. A technique toggle with no glyph, a
                // selection-adjacent edit and a non-structural undo all arrive
                // here. The cursor and the highlights are NOT part of the key —
                // they are overlays over the same chunks — so they still follow.
                _statusText.IsVisible = false;
                UpdateCursor();
                UpdateSelectionHighlights();
                return;
            }

            // After the key check, not before: this is a synchronous stretch with
            // no dispatcher turn in it, so the pill could never have been drawn
            // during the export anyway — and setting it on a render we then skip
            // would flash "Rendering…" over a score that is not being rendered.
            _statusText.IsVisible = true;
            _statusText.Text = LocalizationService.Instance["SheetMusic_Status_Rendering"];

            // Only now: cancelling here rather than before the key check means a
            // skipped rebuild cannot kill a render that is already in flight for
            // the very key it matched.
            _renderCts?.Cancel();
            _renderCts?.Dispose();
            cts = _renderCts = new CancellationTokenSource();

            _pageMode = pageMode;
            ApplyScrollBarPolicy();

            SheetMusicRenderer.RenderResult? result;

            if (pageMode)
            {
                // PAGE layout: bars wrap to a fixed page width (the viewport),
                // zoom drives glyph size (zooming in re-wraps to more, taller
                // systems). Displayed at 1 device px → 1 DIP so the page fills the
                // viewport width and scrolls vertically; no fit-to-height pass.
                var pageDisplayScale = dpi > 0 ? 1.0 / dpi : 1.0;
                var glyphScale = 1.3 * dpi * zoom;
                _canvas.SetPageMode(true, pageDisplayScale);
                _cursorOverlay.SetPageMode(true, pageDisplayScale);
                _selectionOverlay.SetPageMode(true, pageDisplayScale);
                _hoverOverlay.SetPageMode(true, pageDisplayScale);

                result = await _renderer.RenderPagedAsync(
                    export.AlphaTex, width * dpi, glyphScale, staves, cts.Token);
                if (cts.IsCancellationRequested)
                {
                    result?.Dispose();
                    return;
                }
            }
            else
            {
                // STRIP layout: render at device resolution and fit the score to
                // the strip height, so a render whose natural height is ≈
                // stripHeight × DPI blits 1:1 device pixels — the crispest the
                // strip can show. A fixed scale wasted the strip or blurred HiDPI.
                var baseScale = 1.3 * dpi * zoom;
                _canvas.SetPageMode(false, 0);
                _cursorOverlay.SetPageMode(false, 0);
                _selectionOverlay.SetPageMode(false, 0);
                _hoverOverlay.SetPageMode(false, 0);

                _canvas.ZoomFactor = zoom;
                _canvas.ViewportHeightHint = stripHeight;
                _cursorOverlay.ZoomFactor = zoom;
                _cursorOverlay.ViewportHeightHint = stripHeight;
                _selectionOverlay.ZoomFactor = zoom;
                _selectionOverlay.ViewportHeightHint = stripHeight;
                _hoverOverlay.ZoomFactor = zoom;
                _hoverOverlay.ViewportHeightHint = stripHeight;

                result = await _renderer.RenderAsync(export.AlphaTex, width * dpi, baseScale, staves, cts.Token);
                if (cts.IsCancellationRequested)
                {
                    result?.Dispose();
                    return;
                }

                if (result is { Height: > 0 } && stripHeight > 60)
                {
                    var targetHeight = stripHeight * dpi * zoom;
                    var fit = targetHeight / result.Height;
                    // Correct only when meaningfully off — every extra pass is a
                    // full AlphaTab layout + raster. The band is deliberately
                    // tight: leaving 10%+ headroom directly shrinks the staves,
                    // which users read as "the sheet is tiny".
                    if (fit is < 0.95 or > 1.08)
                    {
                        var corrected = Math.Clamp(baseScale * fit, 0.8, 4.0);
                        var refined = await _renderer.RenderAsync(export.AlphaTex, width * dpi, corrected, staves, cts.Token);
                        if (cts.IsCancellationRequested)
                        {
                            refined?.Dispose();
                            result.Dispose();
                            return;
                        }

                        if (refined is { Height: > 0 })
                        {
                            result.Dispose();
                            result = refined;
                        }
                        else
                        {
                            refined?.Dispose();
                        }
                    }
                }
            }

            if (result == null || result.Chunks.Count == 0)
            {
                _canvas.SetResult(null);
                // A failed render leaves nothing to reuse.
                _lastRenderedKey = null;
                _measureBounds = Array.Empty<SheetMusicRenderer.MeasureBound>();
                _beatMap = Array.Empty<AlphaTexExporter.SheetBeat>();
                _beatsByBar = [];
                _stringLattice = Array.Empty<SheetMusicRenderer.StringLine>();
                _systemLattices = Array.Empty<SheetMusicRenderer.SystemStringLattice>();
                _measureBoundByIndex = [];
                _sourceHeight = 0;
                _hoverOverlay.SetHover(null);
                _statusText.Text = LocalizationService.Instance["SheetMusic_Status_Unavailable"];
                return;
            }

            _measureBounds = result.MeasureBounds;
            _beatMap = export.BeatMap;
            _stringLattice = result.StringLattice;
            _systemLattices = result.SystemLattices;
            _measureBoundByIndex = SheetHitIndex.BuildMeasureBoundIndex(result.MeasureBounds);
            _sourceHeight = result.Height;
            _beatsByBar = SheetHitIndex.BuildBeatLookup(export.BeatMap, result.BeatBounds);
            _canvas.SetResult(result);
            _cursorOverlay.SetMetrics(result.Width, result.Height);
            _selectionOverlay.SetMetrics(result.Width, result.Height);
            _hoverOverlay.SetMetrics(result.Width, result.Height);
            _hoverOverlay.SetHover(null); // the score changed; drop any stale hover cell
            _statusText.IsVisible = false;
            // Recorded only on the path that actually put chunks on the canvas.
            _lastRenderedKey = key;
            UpdateCursor();
            UpdateSelectionHighlights();
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer rebuild, or the strip was detached.
        }
        catch (Exception ex)
        {
            _lastRenderedKey = null;
            _statusText.IsVisible = true;
            _statusText.Text = LocalizationService.Instance.Format("SheetMusic_Status_ErrorFmt", ex.Message);
            App.Services?.GetService<IAppLogger>()?.Error(AppLogCategory.Rendering,
                "Sheet music rebuild failed (export or render); the strip shows an error instead of the score.", ex);
        }
    }

    private void UpdateCursor()
    {
        var state = ProjectState;
        if (state == null) return;

        var contentWidth = _canvas.ContentWidth;
        if (contentWidth <= 0) return;

        // Source pixels (un-scaled render output); the overlay multiplies by its
        // own RenderScale so the cursor stays aligned with the chunks. In page
        // mode the cursor is a SEGMENT spanning the active system's Y band.
        var (srcX, srcTop, srcHeight) = MapTimeToCursor(state.CurrentTimeMs);
        _cursorOverlay.SetCursor(srcX, srcTop, srcHeight);

        var scale = _canvas.RenderScale;
        if (_pageMode)
        {
            // Follow VERTICALLY: keep the active system inside the comfort zone.
            var viewportH = _scroll.Viewport.Height;
            if (viewportH <= 0) return;
            var displayTop = srcTop * scale;
            var displayBottom = (srcTop + srcHeight) * scale;
            var topEdge = _scroll.Offset.Y + viewportH * 0.15;
            var bottomEdge = _scroll.Offset.Y + viewportH * 0.85;
            if (displayTop < topEdge || displayBottom > bottomEdge)
            {
                var target = displayTop - viewportH * 0.3;
                if (target < 0) target = 0;
                var contentHeight = _sourceHeight * scale;
                var maxOffset = Math.Max(0, contentHeight - viewportH);
                if (target > maxOffset) target = maxOffset;
                _scroll.Offset = new Vector(_scroll.Offset.X, target);
            }

            return;
        }

        // Strip: follow HORIZONTALLY. Only re-target scroll when the cursor
        // leaves the central comfort zone, otherwise scroll-easing dominates the
        // visible motion and the playhead looks laggy.
        var displayX = srcX * scale;
        var viewport = _scroll.Viewport.Width;
        if (viewport > 0)
        {
            var leftEdge = _scroll.Offset.X + viewport * 0.15;
            var rightEdge = _scroll.Offset.X + viewport * 0.85;
            if (displayX < leftEdge || displayX > rightEdge)
            {
                var target = displayX - viewport * 0.4;
                if (target < 0) target = 0;
                var maxOffset = Math.Max(0, contentWidth - viewport);
                if (target > maxOffset) target = maxOffset;
                _scroll.Offset = new Vector(target, _scroll.Offset.Y);
            }
        }
    }

    /// <summary>
    ///     The measure the playhead is in: the FIRST span ending after
    ///     <paramref name="timeMs" />, or the last span when the time is past
    ///     the end.
    ///     <para>
    ///         A MONOTONIC CURSOR, not a scan. A <c>for (idx = 0; idx &lt; spans.Count - 1; idx++)</c>
    ///         per time→X mapping would restart at bar 1 on every call, and the
    ///         cursor is updated at up to 60 Hz for the whole of playback while
    ///         the strip is visible, over every measure of the exported level.
    ///         The playhead is monotone during playback, so the walk below is
    ///         one step per bar crossed; it walks BOTH directions and clamps, so
    ///         it re-finds its place after a seek from any starting point on its
    ///         own — the same shape as
    ///         <c>AudioPlaybackCoordinator._sectionScanIndex</c>,
    ///         <c>StatusBarViewModel._measureScanIndex</c> and
    ///         <c>RsToneScanner</c>.
    ///     </para>
    ///     <para>
    ///         The hint is deliberately shared by every caller and never needs
    ///         resetting when the score re-renders: a stale index is clamped and
    ///         then walked, which is what "self-correcting" means here.
    ///     </para>
    /// </summary>
    private int SpanIndexAt(double timeMs)
    {
        _spanCursor = SpanIndexAt(_measureSpans, timeMs, _spanCursor);
        return _spanCursor;
    }

    /// <summary>The pure half of <see cref="SpanIndexAt(double)" />, so the
    /// equivalence with the scan it replaced can be swept in the default gate.
    /// Spans are emitted in bar order, so <c>timeMs &lt; EndMs[i]</c> is monotone
    /// in <c>i</c> and the first true index is what both forms find.</summary>
    internal static int SpanIndexAt(IReadOnlyList<AlphaTexExporter.MeasureSpan> spans, double timeMs, int hint)
    {
        if (spans.Count == 0) return 0;
        var idx = Math.Clamp(hint, 0, spans.Count - 1);
        while (idx < spans.Count - 1 && timeMs >= spans[idx].EndMs) idx++;
        while (idx > 0 && timeMs < spans[idx - 1].EndMs) idx--;
        return idx;
    }

    /// <summary>Time → cursor segment (X, top, height) in source pixels. Strip:
    /// top 0, full height. Page: the active bar's X-progress and its system's Y
    /// band, so the playhead is a segment on the right system.</summary>
    private (double X, double Top, double Height) MapTimeToCursor(double timeMs)
    {
        var spans = _measureSpans;
        var bounds = _measureBounds;
        if (spans.Count > 0 && bounds.Count == spans.Count)
        {
            var idx = SpanIndexAt(timeMs);
            var span = spans[idx];
            var bound = bounds[idx];
            var spanLength = Math.Max(1.0, span.EndMs - span.StartMs);
            var progress = Math.Clamp((timeMs - span.StartMs) / spanLength, 0, 1);
            var x = bound.X + bound.Width * progress;
            var top = _pageMode ? bound.Y : 0;
            var height = _pageMode ? bound.Height : _sourceHeight;
            return (x, top, height);
        }

        // Fallback (incomplete bounds): proportional X, full height.
        return (MapTimeToSourceX(timeMs), 0, _sourceHeight);
    }

    /// <summary>
    /// Projects the shared selection onto the sheet as source-X band centers and
    /// hands them to the highlight overlay. Called after every re-render (the
    /// time→X mapping changes) and on every <see cref="ISelectionService.SelectionChanged"/>.
    /// Only the overlay repaints — the chunk bitmaps are untouched.
    /// </summary>
    private void UpdateSelectionHighlights()
    {
        var selection = Selection;
        var state = ProjectState;
        if (selection == null || state == null || _cursorOverlay.SourceWidth <= 0)
        {
            _selectionOverlay.SetHighlights(Array.Empty<HighlightBand>(), 0);
            return;
        }

        var beats = state.Beats;
        if (beats.Count == 0)
        {
            _selectionOverlay.SetHighlights(Array.Empty<HighlightBand>(), 0);
            return;
        }

        // Exact path: highlight the rendered beat COLUMN a selected note/chord
        // was emitted into (reference identity against the exporter's beat map).
        // In page mode the band is confined to the containing SYSTEM's Y span;
        // in the strip it spans the full staff height (visually as before).
        // Falls back to the time-projected band per item when the lookup misses.
        var selectedSet = new HashSet<object>();
        foreach (var n in selection.SelectedNotes) selectedSet.Add(n);
        foreach (var c in selection.SelectedChords) selectedSet.Add(c);

        var bands = new List<HighlightBand>(selectedSet.Count);
        if (selectedSet.Count > 0 && _beatsByBar.Count > 0)
        {
            foreach (var barBeats in _beatsByBar.Values)
            {
                foreach (var (bound, beat) in barBeats)
                {
                    if (beat.Kind != AlphaTexExporter.SheetBeatKind.Head) continue;
                    var hit = false;
                    foreach (var n in beat.Notes)
                        if (selectedSet.Remove(n)) hit = true;
                    foreach (var c in beat.Chords)
                        if (selectedSet.Remove(c)) hit = true;
                    if (hit)
                    {
                        double top = 0, height = _sourceHeight;
                        if (_pageMode && _measureBoundByIndex.TryGetValue(bound.BarIndex, out var mb))
                        {
                            top = mb.Y;
                            height = mb.Height;
                        }

                        bands.Add(new HighlightBand(bound.X + bound.Width / 2, top, height));
                    }

                    if (selectedSet.Count == 0) break;
                }

                if (selectedSet.Count == 0) break;
            }
        }

        foreach (var remaining in selectedSet)
        {
            var pos = remaining switch
            {
                GuitarNote n => n.Position,
                Chord c => c.Position,
                _ => default
            };
            var ms = pos.GetPositionMs(beats);
            if (ms >= _songStartMs - 1 && ms <= _songEndMs + 1)
            {
                var (fx, ftop, fheight) = MapTimeToCursor(ms);
                bands.Add(new HighlightBand(fx, ftop, fheight));
            }
        }

        // Band width ~ a fifth of a measure so a highlight reads as "this beat"
        // without swamping neighbours; clamped so it stays visible at any zoom.
        var perMeasure = _cursorOverlay.SourceWidth / Math.Max(1, _measureSpans.Count);
        var band = Math.Clamp(perMeasure * 0.2, 8.0, 60.0);
        _selectionOverlay.SetHighlights(bands, band);
    }

    internal void SeekToPointer(double canvasX, double canvasY)
    {
        var scale = _canvas.RenderScale;
        if (scale <= 0 || _canvas.ContentWidth <= 0) return;
        var timeMs = MapSourceXToTime(canvasX / scale, canvasY / scale);
        if (timeMs >= 0) SeekRequested?.Invoke(timeMs);
    }

    /// <summary>
    /// Index into <see cref="_measureBounds"/> of the bar the source point falls
    /// in. Strip: purely by X (one row). Page: the bar whose rect best contains
    /// (srcX, srcY) — Y dominates so the correct SYSTEM is chosen first, then the
    /// nearest bar in X (a click in a system's margin still resolves to a bar).
    /// Both go through the real bounds, never a screen-position heuristic.
    /// </summary>
    private int FindMeasureIndex(double srcX, double srcY)
    {
        var bounds = _measureBounds;
        if (bounds.Count == 0) return -1;

        if (!_pageMode)
        {
            var idx = 0;
            for (; idx < bounds.Count - 1; idx++)
                if (srcX < bounds[idx].X + bounds[idx].Width) break;
            return idx;
        }

        var best = -1;
        var bestScore = double.MaxValue;
        for (var i = 0; i < bounds.Count; i++)
        {
            var b = bounds[i];
            var dy = srcY < b.Y ? b.Y - srcY : srcY > b.Y + b.Height ? srcY - (b.Y + b.Height) : 0;
            var dx = srcX < b.X ? b.X - srcX : srcX > b.X + b.Width ? srcX - (b.X + b.Width) : 0;
            var score = dy * 100000 + dx; // Y first (right system), then X within it
            if (score < bestScore) { bestScore = score; best = i; }
        }

        return best;
    }

    private bool TryHitBeat(double srcX, double srcY,
        out SheetMusicRenderer.BeatBound bound, out AlphaTexExporter.SheetBeat beat)
    {
        bound = default;
        beat = null!;
        var bounds = _measureBounds;
        if (bounds.Count == 0 || _beatsByBar.Count == 0) return false;

        // Bar via measure bounds (X in the strip, rect in page mode), then the
        // beat column via X ranges. A beat's span runs to the start of the next
        // beat so clicks between glyphs resolve to the beat they belong to.
        var barIdx = FindMeasureIndex(srcX, srcY);
        if (barIdx < 0) return false;
        if (!_beatsByBar.TryGetValue(bounds[barIdx].Index, out var barBeats) || barBeats.Count == 0)
            return false;

        var best = 0;
        for (var i = 0; i < barBeats.Count; i++)
        {
            if (srcX >= barBeats[i].Bound.X) best = i;
            else break;
        }
        (bound, beat) = barBeats[best];
        return true;
    }

    internal void SelectAtPointer(double canvasX, double canvasY, bool additive)
    {
        var scale = _canvas.RenderScale;
        if (scale <= 0 || _canvas.ContentWidth <= 0) return;
        var srcX = canvasX / scale;
        var srcY = canvasY / scale;

        if (TryHitBeat(srcX, srcY, out _, out var beat))
        {
            if (beat.Notes.Count == 0 && beat.Chords.Count == 0)
            {
                // A rest: a plain click deselects (GP behavior); a Ctrl+click on
                // empty score keeps the current multi-selection intact.
                if (!additive)
                    SelectSoundsRequested?.Invoke(Array.Empty<GuitarNote>(), Array.Empty<Chord>(), false);
                return;
            }

            // When the beat merges several independent notes (an interval),
            // pick the one on the string line nearest to the click.
            var notes = beat.Notes;
            if (notes.Count > 1 && beat.Chords.Count == 0)
            {
                var stringIdx = MapSourceYToString(srcY);
                if (stringIdx >= 0)
                {
                    GuitarNote? bestNote = null;
                    var bestDist = int.MaxValue;
                    foreach (var n in notes)
                    {
                        var d = Math.Abs(n.StringIndex - stringIdx);
                        if (d < bestDist) { bestDist = d; bestNote = n; }
                    }
                    if (bestNote != null) notes = new[] { bestNote };
                }
            }

            SelectSoundsRequested?.Invoke(notes, beat.Chords, additive);
            return;
        }

        // No glyph under the cursor: a plain click selects the nearest sound in
        // time; a Ctrl+click on blank space leaves the selection untouched.
        if (additive) return;
        var timeMs = MapSourceXToTime(srcX, srcY);
        if (timeMs >= 0) SelectRequested?.Invoke(timeMs);
    }

    /// <summary>
    /// Hover affordance: highlights the exact note head / tab digit under the
    /// cursor (the specific string cell when the tab lattice is present, else the
    /// whole beat column). Only the thin hover overlay repaints, and only when
    /// the hovered cell actually moved. Clears over rests and blank space.
    /// </summary>
    internal void UpdateHover(double canvasX, double canvasY)
    {
        var scale = _canvas.RenderScale;
        if (scale <= 0 || _canvas.ContentWidth <= 0 || _sourceHeight <= 0)
        {
            _hoverOverlay.SetHover(null);
            return;
        }

        var srcX = canvasX / scale;
        var srcY = canvasY / scale;
        if (!TryHitBeat(srcX, srcY, out var bound, out var beat) ||
            (beat.Notes.Count == 0 && beat.Chords.Count == 0))
        {
            _hoverOverlay.SetHover(null);
            return;
        }

        var centerX = bound.X + bound.Width / 2;

        double top = 0;
        var height = _sourceHeight;
        if (_pageMode)
        {
            // Page: highlight the beat column within the containing SYSTEM (its
            // measure band). The per-digit cell reads clearly in the wide strip
            // but is unnecessary in the denser page view.
            if (_measureBoundByIndex.TryGetValue(bound.BarIndex, out var mb))
            {
                top = mb.Y;
                height = mb.Height;
            }
        }
        else if (_stringLattice.Count > 0)
        {
            // Strip: prefer the exact tab-digit cell; fall back to a full-height
            // beat column in notation-only mode or off the staff.
            var stringIdx = MapSourceYToString(srcY);
            if (stringIdx >= 0 && TryStringCell(stringIdx, out var cellTop, out var cellHeight))
            {
                top = cellTop;
                height = cellHeight;
            }
        }

        var perMeasure = _cursorOverlay.SourceWidth / Math.Max(1, _measureSpans.Count);
        var band = Math.Clamp(perMeasure * 0.2, 8.0, 60.0);
        _hoverOverlay.SetHover(new HoverBand(centerX, top, height, band));
    }

    /// <summary>Source-space Y cell of one tab string line (line ± half the
    /// average line spacing), so a hover box lands on the exact digit.</summary>
    private bool TryStringCell(int stringIndex, out double top, out double height)
    {
        top = 0;
        height = 0;
        var lattice = _stringLattice;
        if (lattice.Count == 0) return false;

        var y = double.NaN;
        double minY = double.MaxValue, maxY = double.MinValue;
        foreach (var l in lattice)
        {
            if (l.StringIndex == stringIndex) y = l.Y;
            if (l.Y < minY) minY = l.Y;
            if (l.Y > maxY) maxY = l.Y;
        }
        if (double.IsNaN(y)) return false;

        var spacing = lattice.Count > 1 ? (maxY - minY) / (lattice.Count - 1) : 14.0;
        if (spacing <= 0) spacing = 14.0;
        top = y - spacing / 2;
        height = spacing;
        return true;
    }

    /// <summary>
    /// Double-click insert: resolves the click to (quantizable time, domain
    /// string) and asks the host to add a note there. Returns false when the
    /// click cannot be resolved (no bounds / no tab lattice) so the caller can
    /// fall back to plain seek behavior.
    /// </summary>
    internal bool TryAddAtPointer(double canvasX, double canvasY)
    {
        var scale = _canvas.RenderScale;
        if (scale <= 0 || _canvas.ContentWidth <= 0) return false;
        var srcX = canvasX / scale;
        var srcY = canvasY / scale;
        var stringIdx = MapSourceYToString(srcY);
        if (stringIdx < 0) return false;

        var timeMs = MapSourceXToTime(srcX, srcY);
        if (timeMs < 0) return false;

        AddNoteRequested?.Invoke(timeMs, stringIdx);
        return true;
    }

    /// <summary>
    /// Maps a source-space Y to the DOMAIN string whose tab line is nearest.
    /// Strip: the single lattice. Page: the lattice of the SYSTEM whose Y band
    /// contains srcY (string Y is not constant across stacked systems). Returns
    /// −1 when there is no lattice (notation-only mode) or the click is far
    /// outside every tab staff.
    /// </summary>
    private int MapSourceYToString(double srcY)
    {
        if (_pageMode)
        {
            var lines = ResolveSystemLines(srcY);
            return lines == null ? -1 : SheetHitIndex.NearestStringOnLines(lines, srcY);
        }

        return SheetHitIndex.NearestStringOnLines(_stringLattice, srcY);
    }

    /// <summary>Lines of the system whose Y band contains (or is nearest to)
    /// srcY, or null when there are no per-system lattices.</summary>
    private IReadOnlyList<SheetMusicRenderer.StringLine>? ResolveSystemLines(double srcY)
    {
        var systems = _systemLattices;
        if (systems.Count == 0) return null;

        SheetMusicRenderer.SystemStringLattice? best = null;
        var bestDist = double.MaxValue;
        foreach (var s in systems)
        {
            if (s.Lines.Count == 0) continue;
            var dist = srcY < s.Top ? s.Top - srcY : srcY > s.Bottom ? srcY - s.Bottom : 0;
            if (dist < bestDist) { bestDist = dist; best = s; }
        }

        return best?.Lines;
    }

    /// <summary>GP-style keys (digits type a fret, +/- step the note value) —
    /// <see cref="SheetMusicViewInput.TryHandleKey" />.</summary>
    protected override void OnKeyDown(Avalonia.Input.KeyEventArgs e)
    {
        if (!_input.TryHandleKey(e)) base.OnKeyDown(e);
    }

    internal void RaiseDurationStep(int step) => DurationStepRequested?.Invoke(step);

    internal void RaiseSetFret(int fret) => SetFretRequested?.Invoke(fret);

    /// <summary>Inverse of <see cref="MapTimeToSourceX"/> for click-to-seek.
    /// srcY resolves the SYSTEM in page mode (ignored in the strip).</summary>
    private double MapSourceXToTime(double srcX, double srcY)
    {
        var spans = _measureSpans;
        var bounds = _measureBounds;
        if (spans.Count == 0) return -1;

        if (bounds.Count == spans.Count)
        {
            var idx = FindMeasureIndex(srcX, srcY);
            if (idx < 0) idx = 0;
            var bound = bounds[idx];
            var span = spans[idx];
            var progress = bound.Width > 0 ? (srcX - bound.X) / bound.Width : 0;
            if (progress < 0) progress = 0;
            if (progress > 1) progress = 1;
            return span.StartMs + (span.EndMs - span.StartMs) * progress;
        }

        var sourceWidth = _cursorOverlay.SourceWidth;
        if (sourceWidth <= 0) return -1;
        var perMeasure = sourceWidth / spans.Count;
        if (perMeasure <= 0) return -1;
        var fIdx = srcX / perMeasure;
        var i = (int)Math.Clamp(Math.Floor(fIdx), 0, spans.Count - 1);
        var frac = Math.Clamp(fIdx - i, 0, 1);
        return spans[i].StartMs + (spans[i].EndMs - spans[i].StartMs) * frac;
    }

    private double MapTimeToSourceX(double timeMs)
    {
        var spans = _measureSpans;
        var bounds = _measureBounds;
        var sourceWidth = _cursorOverlay.SourceWidth;
        if (sourceWidth <= 0) return 0;

        // Preferred path: per-bar bounds reported by AlphaTab. We use this
        // ONLY when AlphaTab gave us a bound for every measure span we
        // exported. If the lookup is partial (e.g. AlphaTab's horizontal
        // layout produced fewer bars than we emitted) the per-bar path
        // would clamp the cursor to the last-known bar's right edge for
        // the entire remainder of the song — which is exactly what the
        // user described as "stuck on the same measure for several
        // measures". The proportional fallback below is monotonic over
        // the whole rendered width and never sticks.
        if (spans.Count > 0 && bounds.Count == spans.Count)
        {
            var idx = SpanIndexAt(timeMs);
            var span = spans[idx];
            var bound = bounds[idx];
            var spanLength = Math.Max(1.0, span.EndMs - span.StartMs);
            var progress = (timeMs - span.StartMs) / spanLength;
            if (progress < 0) progress = 0;
            if (progress > 1) progress = 1;
            return bound.X + bound.Width * progress;
        }

        // Fallback: proportional time → width over the whole rendered
        // sheet. Chooses a measure index by time, then linearly maps that
        // index into the available width so the cursor advances smoothly
        // even when BoundsLookup is incomplete.
        if (spans.Count > 0)
        {
            var idx = SpanIndexAt(timeMs);
            var span = spans[idx];
            var spanLength = Math.Max(1.0, span.EndMs - span.StartMs);
            var progress = (timeMs - span.StartMs) / spanLength;
            if (progress < 0) progress = 0;
            if (progress > 1) progress = 1;
            var perMeasure = sourceWidth / spans.Count;
            return (idx + progress) * perMeasure;
        }

        var songLength = _songEndMs - _songStartMs;
        if (songLength <= 0) return 0;
        var t = (timeMs - _songStartMs) / songLength;
        if (t < 0) t = 0;
        if (t > 1) t = 1;
        return t * sourceWidth;
    }
}
