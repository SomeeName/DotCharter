using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using DotCharter.Tone.Audio.Equalizer;
using DotCharter.UI.Platform;

namespace DotCharter.UI.Controls;

/// <summary>
///     The EQ's plot: five zone tints and a log grid behind, the spectrum over
///     them, the summed response curve with its purple glow, the selected band's
///     own dashed contribution, and six numbered, coloured, draggable handles.
///     <para>
///         Custom-drawn rather than a <c>Polyline</c> over a bound point list,
///         because the curve is resampled on every width change and the handles
///         need per-band hit testing — but it owns NO maths. Every frequency,
///         gain and Q it draws or writes goes through
///         <see cref="EqCurve" /> and <see cref="EqSpectrum" />, both pure static
///         types with their own tests; this control converts that to pixels and
///         back and does nothing else. Burying the mapping here is exactly what
///         would have made the drag verb untestable.
///     </para>
///     <para>
///         It also holds no state of its own about the bands: it reads the one
///         <see cref="EqSection" /> the engine is pushed from, and reports edits
///         back through <see cref="BandChanged" /> so the view model can push and
///         redraw. A private copy here would be the display-model-drifts-from-DSP
///         fault this feature is built to avoid.
///     </para>
///     <para>
///         <b>The frequency labels live INSIDE the plot on pills</b>, the way the
///         mockup's <c>miniPlot</c> draws them (absolutely positioned at
///         <c>bottom: 2px</c> over a <c>rgba(10,8,18,.75)</c> chip), rather than
///         in a reserved strip beneath it. Two reasons, both load-bearing: the
///         curve gets the whole well — the mockup's 104 px is 104 px of CURVE,
///         and reserving a strip is what made ours 58 — and a bare label over the
///         analyser fill and the string ticks is unreadable, which is the pill's
///         whole job.
///     </para>
/// </summary>
public sealed class EqPlotControl : Control, IWheelAdjustable
{
    /// <summary>How many columns the response is sampled at. Fixed rather than
    /// per-pixel: the plot is between ~200 and ~900 px wide in practice, and a
    /// fixed count keeps the curve's shape identical as the pane is dragged
    /// instead of subtly re-sampling under the user.</summary>
    private const int CurveSamples = 160;

    /// <summary>
    ///     The height this control ASKS for, in px.
    ///     <para>
    ///         The mockup's compact module draws <c>miniPlot(104)</c>, and 104 is
    ///         the whole well including its 1 px frame — so the control inside
    ///         that frame wants 102. It has to be asked for: the plot sits in a
    ///         Grid <c>*</c> row inside a vertically scrolling inspector, which
    ///         measures its content with INFINITE height, and a star row measured
    ///         against infinity is worth exactly its child's desired size. With no
    ///         desired height of its own the row degenerated to the well's
    ///         <c>MinHeight</c> and the owner got a 58 px curve — "so squished
    ///         together in height that it isn't even readable".
    ///     </para>
    ///     <para>
    ///         It is a PREFERENCE, not a floor: the control still arranges to
    ///         whatever the row gives it, so a taller pane grows the curve and a
    ///         host that genuinely constrains the module shrinks it and then
    ///         scrolls.
    ///     </para>
    /// </summary>
    public const double DefaultPreferredHeight = 102.0;

    /// <summary>Handle radii. The selected one is larger; both are big enough to
    /// grab with a pointer at the module's smallest supported height.</summary>
    private const double HandleRadius = 6.5;
    private const double SelectedHandleRadius = 8.5;

    /// <summary>The mockup's own viewBox height. The two proportions taken off
    /// the drawing — the string ticks and the axis inset — are expressed against
    /// it so they scale with the plot instead of being px literals that only look
    /// right at one height.</summary>
    private const double MockupPlotUnits = 300.0;

    /// <summary>String fundamental ticks run <c>PH-14 → PH</c> in the mockup.</summary>
    private const double StringTickUnits = 14.0;

    /// <summary>The gridlines and their printed labels. Drawn from data so the
    /// axis under the plot and the lines through it can never disagree.</summary>
    private static readonly (double Hz, string Label)[] AxisTicks =
    [
        (20, "20"), (100, "100"), (1000, "1k"), (10000, "10k"), (20000, "20k")
    ];

    private static readonly double[] GridFrequencies =
        [30, 50, 100, 200, 500, 1000, 2000, 5000, 10000, 20000];

    /// <summary>The six string fundamentals of a guitar in standard tuning,
    /// ticked along the bottom of the plot. They are the whole reason a CHARTING
    /// EQ is not a mixing EQ: "cut below the low E" is a decision the user can
    /// only make if the low E is drawn.</summary>
    private static readonly (double Hz, string Name)[] StringFundamentals =
    [
        (82.41, "E"), (110.0, "A"), (146.83, "D"), (196.0, "G"), (246.94, "B"), (329.63, "e")
    ];

    /// <summary>
    ///     The mockup's five named regions of the spectrum, as faint tints.
    ///     <para>
    ///         They are what turns the plot from a graph into a MAP: "the guitar
    ///         is honking" and "there is mud at 300" are the sentences a charter
    ///         thinks in, and the tints are where those words live. The compact
    ///         module draws the rectangles only — the names are in the mockup's
    ///         big editor, where there is height for a 8.5 px caption.
    ///     </para>
    /// </summary>
    private static readonly (double FromHz, double ToHz)[] Zones =
    [
        (60, 160), (200, 450), (800, 1600), (2000, 5000), (6500, 20000)
    ];

    // ── properties ───────────────────────────────────────────────────────

    public static readonly StyledProperty<EqSection?> SectionProperty =
        AvaloniaProperty.Register<EqPlotControl, EqSection?>(nameof(Section));

    public static readonly StyledProperty<int> SelectedBandProperty =
        AvaloniaProperty.Register<EqPlotControl, int>(nameof(SelectedBand));

    /// <summary>The analyser's magnitudes in dBFS, log-spaced 20 Hz … 20 kHz.
    /// NULL, empty or SILENT draws <see cref="EqSpectrum.Preview" /> instead —
    /// dimmer and tagged, so it can never be read as measured audio.</summary>
    public static readonly StyledProperty<float[]?> SpectrumProperty =
        AvaloniaProperty.Register<EqPlotControl, float[]?>(nameof(Spectrum));

    /// <summary>False draws everything dimmed and un-draggable — what the module
    /// shows when the engine has no parametric EQ.</summary>
    public static readonly StyledProperty<bool> IsLiveProperty =
        AvaloniaProperty.Register<EqPlotControl, bool>(nameof(IsLive), true);

    /// <summary>The mark printed over the preview spectrum. Supplied by the view
    /// (a <c>{localization:Loc}</c>) rather than resolved here, so this control
    /// stays a drawing surface with no localization dependency — and so the tag
    /// is TESTABLE without a resource lookup.</summary>
    public static readonly StyledProperty<string?> PreviewLabelProperty =
        AvaloniaProperty.Register<EqPlotControl, string?>(nameof(PreviewLabel));

    /// <summary>See <see cref="DefaultPreferredHeight" />.</summary>
    public static readonly StyledProperty<double> PreferredHeightProperty =
        AvaloniaProperty.Register<EqPlotControl, double>(nameof(PreferredHeight), DefaultPreferredHeight);

    public EqSection? Section
    {
        get => GetValue(SectionProperty);
        set => SetValue(SectionProperty, value);
    }

    public int SelectedBand
    {
        get => GetValue(SelectedBandProperty);
        set => SetValue(SelectedBandProperty, value);
    }

    public float[]? Spectrum
    {
        get => GetValue(SpectrumProperty);
        set => SetValue(SpectrumProperty, value);
    }

    public bool IsLive
    {
        get => GetValue(IsLiveProperty);
        set => SetValue(IsLiveProperty, value);
    }

    public string? PreviewLabel
    {
        get => GetValue(PreviewLabelProperty);
        set => SetValue(PreviewLabelProperty, value);
    }

    public double PreferredHeight
    {
        get => GetValue(PreferredHeightProperty);
        set => SetValue(PreferredHeightProperty, value);
    }

    /// <summary>
    ///     Which spectrum the plot is showing. Read by the pixel harness, so "the
    ///     preview is up" is an assertion rather than an inference from colours.
    ///     <para>
    ///         DERIVED from <see cref="Spectrum" /> rather than recorded during
    ///         <see cref="Render" />, because a render only happens when something
    ///         actually presents a frame — a harness that laid the module out and
    ///         then asked would have been reading the field's initial value and
    ///         passing for the wrong reason.
    ///     </para>
    /// </summary>
    public EqSpectrumKind SpectrumKind =>
        EqSpectrum.IsSilent(Spectrum) ? EqSpectrumKind.Preview : EqSpectrumKind.Live;

    /// <summary>Raised when a drag, a wheel or a double-click changed a band, with
    /// its index. The view model pushes the section to the engine and re-reads
    /// the badge; this control never talks to audio.</summary>
    public event EventHandler<int>? BandChanged;

    /// <summary>Raised when a press selected a different band, with its index.</summary>
    public event EventHandler<int>? BandSelected;

    static EqPlotControl()
    {
        AffectsRender<EqPlotControl>(SectionProperty, SelectedBandProperty, SpectrumProperty,
            IsLiveProperty, PreviewLabelProperty);
        AffectsMeasure<EqPlotControl>(PreferredHeightProperty);
    }

    public EqPlotControl()
    {
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Cross);
    }

    /// <summary>The index being dragged, or null. Also what switches the cursor.</summary>
    private int? _dragging;

    /// <summary>The band as it was when the drag began, and where on the vertical
    /// axis it was grabbed. Both are required by <see cref="EqCurve.Drag" />: a
    /// gainless band's Q travel is RELATIVE, so feeding the live band back in
    /// would compound the multiplier once per pointer move.</summary>
    private EqBand _dragOrigin;
    private double _dragOriginY;

    // ── geometry ─────────────────────────────────────────────────────────

    /// <summary>The rectangle the curve lives in — the WHOLE control. The axis
    /// labels are drawn over it on pills, exactly as the mockup does.</summary>
    private Rect PlotRect => new(0, 0, Bounds.Width, Math.Max(1.0, Bounds.Height));

    private double XOf(double frequencyHz) => EqCurve.FrequencyToNormalized(frequencyHz) * PlotRect.Width;

    private double YOf(double gainDb) => EqCurve.GainDbToNormalized(gainDb) * PlotRect.Height;

    protected override Size MeasureOverride(Size availableSize)
    {
        _ = availableSize;
        // Zero WIDTH: the plot stretches to the module, and asking for a width
        // would make the plot decide how wide the inspector pane is.
        return new Size(0, Math.Max(0.0, PreferredHeight));
    }

    // ── input ────────────────────────────────────────────────────────────

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!IsLive || Section is not { } section || section.Bands.Count == 0) return;

        var point = e.GetPosition(this);
        var (nx, ny) = Normalize(point);
        var nearest = EqCurve.NearestHandle(section.Bands, nx, ny);
        if (nearest < 0) return;

        SelectedBand = nearest;
        BandSelected?.Invoke(this, nearest);

        // A double-click flattens the band rather than dragging it — the
        // mockup's "double-click to reset". Checked BEFORE the drag starts so a
        // reset is never also a one-pixel move.
        if (e.ClickCount >= 2)
        {
            section.ResetBand(nearest);
            BandChanged?.Invoke(this, nearest);
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        _dragging = nearest;
        _dragOrigin = section.Bands[nearest];
        _dragOriginY = ny;
        e.Pointer.Capture(this);
        Cursor = new Cursor(StandardCursorType.SizeAll);
        ApplyDrag(section, nearest, point);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragging is not { } index || Section is not { } section) return;
        ApplyDrag(section, index, e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragging is null) return;
        _dragging = null;
        e.Pointer.Capture(null);
        Cursor = new Cursor(StandardCursorType.Cross);
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _dragging = null;
        Cursor = new Cursor(StandardCursorType.Cross);
    }

    /// <summary>
    ///     Ctrl + wheel is the plot's Q gesture; a BARE notch scrolls the panel.
    ///     <para>
    ///         A plot that took any notch would be a trap: it sits 100 px tall in the
    ///         middle of a scrolling Mixer tab, so a few notches into a scroll the
    ///         plot would slide under the pointer, take every notch after that (the panel
    ///         stopping dead) and walk the selected band's Q — Band 1, the high
    ///         pass — with nobody aiming at it. <c>SliderWheelPolicy</c>
    ///         hands bare notches to the scroll host; requiring the modifier
    ///         HERE as well keeps the gesture identical in a host-less layout.
    ///     </para>
    /// </summary>
    public bool TakesWheel(KeyModifiers modifiers) => IsQWheel(modifiers);

    /// <summary>The pure half of <see cref="TakesWheel" />: Ctrl held.</summary>
    public static bool IsQWheel(KeyModifiers modifiers) => (modifiers & KeyModifiers.Control) != 0;

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (!TakesWheel(e.KeyModifiers)) return;
        if (!IsLive || Section is not { } section) return;

        var index = SelectedBand;
        if (index < 0 || index >= section.Bands.Count) return;

        // Scroll widens / narrows Q. Multiplicative because the Q axis is
        // logarithmic — a fixed step would crawl at 0.3 and leap at 18.
        var band = section.Bands[index];
        var factor = e.Delta.Y > 0 ? 1.14 : 0.88;
        section.SetBand(index, band with { Q = band.Q * factor });

        BandChanged?.Invoke(this, index);
        InvalidateVisual();
        e.Handled = true;
    }

    /// <summary>Pointer position to the 0..1 plot space
    /// <see cref="EqCurve" /> speaks. Not clamped here — the verb clamps, and
    /// clamping twice would hide a mapping bug.</summary>
    private (double X, double Y) Normalize(Point point)
    {
        var rect = PlotRect;
        return (point.X / Math.Max(1.0, rect.Width), point.Y / Math.Max(1.0, rect.Height));
    }

    private void ApplyDrag(EqSection section, int index, Point point)
    {
        if (index < 0 || index >= section.Bands.Count) return;

        var (nx, ny) = Normalize(point);
        section.SetBand(index, EqCurve.Drag(_dragOrigin, nx, ny, _dragOriginY));
        BandChanged?.Invoke(this, index);
        InvalidateVisual();
    }

    // ── paint caches ─────────────────────────────────────────────────────
    //
    // The plot repaints at ~15 Hz for as long as the EQ module is attached
    // (EqModuleView's spectrum poll writes Spectrum, which AffectsRender), so
    // everything a frame can reuse is kept across frames and dropped only when
    // it can have gone stale: the theme (brushes, pens, text), the font family
    // (typeface, text) and the band values / plot size (curve geometries).
    // Pinned by UI/EqPlotPaintCacheTests; the allocation per frame is measured in
    // EqPlotRenderAllocationUiPerfSmokeTests.

    private readonly Dictionary<string, IBrush> _brushes = new(StringComparer.Ordinal);
    private readonly Dictionary<PenKey, Pen> _pens = [];
    private readonly Dictionary<TextKey, FormattedText> _texts = [];
    private Typeface? _typeface;

    private readonly double[] _curveResponse = new double[CurveSamples];
    private readonly double[] _bandResponse = new double[CurveSamples];
    private readonly List<EqBand> _audible = new(6);
    private double[] _levels = [];

    private StreamGeometry? _curveGeometry;
    private int _curveSignature;
    private StreamGeometry? _bandGeometry;
    private int _bandSignature;

    /// <summary>The six band keys, once. <c>$"FireEqBand{n}"</c> per band per
    /// frame was 105 string allocations a second.</summary>
    private static readonly string[] BandKeys =
        ["FireEqBand1", "FireEqBand2", "FireEqBand3", "FireEqBand4", "FireEqBand5", "FireEqBand6"];

    private static readonly string[] HandleLabels = ["1", "2", "3", "4", "5", "6"];

    private static readonly double[] ReferenceDbLines = [12.0, 6.0, -6.0, -12.0];

    private static readonly (double Width, double Opacity)[] GlowStrokes = [(7.0, 0.14), (4.0, 0.24)];

    private static readonly double[] PreviewLevels = EqSpectrum.Preview(EqSpectrum.PreviewPoints);

    private static readonly DashStyle GridDash = new([3, 5], 0);
    private static readonly DashStyle ContributionDash = new([4, 4], 0);
    private static readonly DashStyle GuideDash = new([2, 6], 0);

    private enum Dash : byte { None, Grid, Contribution, Guide }

    private readonly record struct PenKey(string BrushKey, double Width, Dash Dash, bool Round);

    private readonly record struct TextKey(string Text, string BrushKey);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // Both palette sources, the way TimelineGutterControl does it: the
        // runtime palette switch and the Avalonia theme variant.
        Themes.ChartTheme.ThemeChanged += OnThemeOrPaletteChanged;
        ActualThemeVariantChanged += OnThemeOrPaletteChanged;
        // A brush resolved while detached would be the FALLBACK, cached — the
        // v0.43.2 unstyled-dropdown shape. Start every attach clean.
        ClearRenderCaches();
    }

    /// <summary>The static <c>ChartTheme.ThemeChanged</c> subscription would
    /// root a detached plot (pane views are re-created on float/redock).</summary>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Themes.ChartTheme.ThemeChanged -= OnThemeOrPaletteChanged;
        ActualThemeVariantChanged -= OnThemeOrPaletteChanged;
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextElement.FontFamilyProperty)
        {
            _typeface = null;
            _texts.Clear();
            InvalidateVisual();
        }
    }

    private void OnThemeOrPaletteChanged() => OnThemeOrPaletteChanged(this, EventArgs.Empty);

    /// <summary>A cached brush bakes its colour and a cached FormattedText
    /// bakes its brush, so both are only right for one palette.</summary>
    internal void OnThemeOrPaletteChanged(object? sender, EventArgs e)
    {
        ClearRenderCaches();
        InvalidateVisual();
    }

    internal void ClearRenderCaches()
    {
        _brushes.Clear();
        _pens.Clear();
        _texts.Clear();
        _typeface = null;
    }

    /// <summary>How many theme brushes are currently cached — read by the
    /// harness that proves a second frame resolves nothing.</summary>
    internal int CachedBrushCount => _brushes.Count;

    /// <summary>A brush from the app's resources, or a fallback that is visibly
    /// wrong rather than plausibly right. Resolved ONCE per theme — the tree
    /// walk climbs every style scope to <c>Application.Resources</c>, and this
    /// control asked for ~26 of them per frame at 15 Hz.</summary>
    private IBrush Brush(string key, Color fallback)
    {
        if (_brushes.TryGetValue(key, out var cached)) return cached;

        var brush = this.TryFindResource(key, out var found) && found is IBrush resolved
            ? resolved
            : new SolidColorBrush(fallback);
        _brushes[key] = brush;
        return brush;
    }

    private Pen Pen(string brushKey, Color fallback, double width, Dash dash = Dash.None, bool round = false)
    {
        var key = new PenKey(brushKey, width, dash, round);
        if (_pens.TryGetValue(key, out var pen)) return pen;

        pen = new Pen(Brush(brushKey, fallback), width)
        {
            DashStyle = dash switch
            {
                Dash.Grid => GridDash,
                Dash.Contribution => ContributionDash,
                Dash.Guide => GuideDash,
                _ => null
            },
            LineJoin = round ? PenLineJoin.Round : PenLineJoin.Miter,
            LineCap = round ? PenLineCap.Round : PenLineCap.Flat
        };
        _pens[key] = pen;
        return pen;
    }

    private Typeface Typeface => _typeface ??= new Typeface(TextElement.GetFontFamily(this));

    /// <summary>A shaped label, once per (text, brush, theme, font). The handle
    /// numbers and the axis ticks never change, and shaping ~15 of them per
    /// frame was 225 shapings a second on the UI thread.</summary>
    private FormattedText Text(string text, double size, string brushKey, Color fallback, CultureInfo culture)
    {
        var key = new TextKey(text, brushKey);
        if (_texts.TryGetValue(key, out var cached)) return cached;

        cached = new FormattedText(text, culture, FlowDirection.LeftToRight, Typeface, size,
            Brush(brushKey, fallback));
        _texts[key] = cached;
        return cached;
    }

    /// <summary>Band N's identity colour key. The same six keys the chips and
    /// the band editor's dot resolve, so all three agree.</summary>
    private static string BandKey(int index) => BandKeys[index % BandKeys.Length];

    private IBrush BandBrush(int index) => Brush(BandKey(index), Colors.Magenta);

    // ── rendering ────────────────────────────────────────────────────────

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var rect = PlotRect;
        if (rect.Width < 4 || rect.Height < 4) return;

        var section = Section;
        var dim = IsLive ? 1.0 : 0.35;

        DrawZones(context, rect, dim);
        DrawGrid(context, rect);
        DrawSpectrum(context, rect, dim);
        DrawStringFundamentals(context, rect, dim);

        if (section is not null)
        {
            DrawSelectedBandContribution(context, section, rect, dim);
            DrawCurve(context, section, rect, dim);
            DrawHandles(context, section, rect, dim);
        }

        DrawAxis(context, rect, dim);
    }

    /// <summary>The five named regions, as the mockup's
    /// <c>rgba(167,139,250,.03)</c> wash. Drawn FIRST so everything else sits on
    /// top of them.</summary>
    private void DrawZones(DrawingContext context, Rect rect, double dim)
    {
        var fill = Brush("FireEqZone", Color.FromArgb(8, 167, 139, 250));
        using (context.PushOpacity(dim))
        {
            foreach (var (fromHz, toHz) in Zones)
            {
                var left = XOf(fromHz);
                var right = XOf(toHz);
                context.FillRectangle(fill, new Rect(left, 0, Math.Max(0.0, right - left), rect.Height));
            }
        }
    }

    private void DrawGrid(DrawingContext context, Rect rect)
    {
        var gridColour = Color.FromArgb(14, 255, 255, 255);
        var grid = Pen("FireEqGrid", gridColour, 1);
        foreach (var hz in GridFrequencies)
        {
            var x = Math.Round(XOf(hz)) + 0.5;
            context.DrawLine(grid, new Point(x, 0), new Point(x, rect.Height));
        }

        // The dB reference lines. 0 dB is brighter than the rest: it is the
        // "did I actually change anything" line and every handle without gain
        // sits on it.
        var faint = Pen("FireEqGrid", gridColour, 1, Dash.Grid);
        foreach (var db in ReferenceDbLines)
        {
            var y = Math.Round(YOf(db)) + 0.5;
            context.DrawLine(faint, new Point(0, y), new Point(rect.Width, y));
        }

        var zero = Pen("FireEqZeroLine", Color.FromArgb(41, 255, 255, 255), 1);
        var zeroY = Math.Round(YOf(0)) + 0.5;
        context.DrawLine(zero, new Point(0, zeroY), new Point(rect.Width, zeroY));
    }

    /// <summary>
    ///     The analyser: the engine's own frame while something is playing, and
    ///     <see cref="EqSpectrum.Preview" /> — dimmer, and TAGGED by
    ///     <see cref="DrawAxis" /> — while nothing is.
    /// </summary>
    private void DrawSpectrum(DrawingContext context, Rect rect, double dim)
    {
        var silent = SpectrumKind == EqSpectrumKind.Preview;
        ReadOnlySpan<double> levels;
        if (silent)
        {
            levels = PreviewLevels;
        }
        else
        {
            var frame = Spectrum!;
            if (_levels.Length < frame.Length) _levels = new double[frame.Length];
            EqSpectrum.Levels(frame, _levels);
            levels = _levels.AsSpan(0, frame.Length);
        }

        if (levels.Length < 2) return;

        // The one geometry a frame still builds: the spectrum is new data every
        // tick, so there is nothing to reuse.
        var geometry = new StreamGeometry();
        using (var sink = geometry.Open())
        {
            sink.BeginFigure(new Point(0, rect.Height), true);
            for (var i = 0; i < levels.Length; i++)
            {
                var x = i / (double)(levels.Length - 1) * rect.Width;
                sink.LineTo(new Point(x, rect.Height - levels[i] * rect.Height * 0.8));
            }

            sink.LineTo(new Point(rect.Width, rect.Height));
            sink.EndFigure(true);
        }

        var fill = Brush("FireEqPlotFill", Color.FromArgb(36, 139, 92, 246));
        var stroke = Pen("FireEqPlotStroke", Color.FromArgb(61, 167, 139, 250), 1);
        // The preview is drawn at little over half strength. That difference is
        // the honesty: it must read as a backdrop, never as measured audio.
        using (context.PushOpacity(dim * (silent ? 0.55 : 1.0)))
        {
            context.DrawGeometry(fill, stroke, geometry);
        }
    }

    private void DrawStringFundamentals(DrawingContext context, Rect rect, double dim)
    {
        var pen = Pen("FireGearAmps", Color.FromRgb(240, 165, 74), 1.5);
        // The mockup's PH-14 → PH, as a proportion so it holds at every height
        // the pane can take. Floored so the tick never disappears in a squeezed
        // dock; capped so it never climbs into the curve in a tall one.
        var tick = Math.Clamp(rect.Height * StringTickUnits / MockupPlotUnits, 5.0, 12.0);
        using (context.PushOpacity(0.5 * dim))
        {
            foreach (var (hz, _) in StringFundamentals)
            {
                var x = Math.Round(XOf(hz)) + 0.5;
                context.DrawLine(pen, new Point(x, rect.Height - tick), new Point(x, rect.Height));
            }
        }
    }

    /// <summary>The selected band ALONE, dashed and in its own colour, so the
    /// user can see which part of the white curve they are holding. Drawn only
    /// when that band is actually audible — a dashed contribution from a band
    /// the engine is not running would be the same lie in miniature.</summary>
    private void DrawSelectedBandContribution(DrawingContext context, EqSection section, Rect rect, double dim)
    {
        var index = SelectedBand;
        if (index < 0 || index >= section.Bands.Count) return;
        if (!section.IsEnabled) return;

        var band = section.Bands[index];
        var soloed = section.SoloedBand;
        var audible = soloed >= 0 ? soloed == index : band.Enabled;
        if (!audible) return;

        var pen = Pen(BandKey(index), Colors.Magenta, 1.2, Dash.Contribution);

        // Only an EDIT moves this line; a spectrum tick does not.
        var signature = HashCode.Combine(band, rect.Width, rect.Height);
        if (_bandGeometry is null || signature != _bandSignature)
        {
            _audible.Clear();
            _audible.Add(band);
            _bandGeometry = CurveGeometry(_audible, _bandResponse, rect);
            _bandSignature = signature;
        }

        using (context.PushOpacity(0.75 * dim))
        {
            context.DrawGeometry(null, pen, _bandGeometry);
        }
    }

    /// <summary>
    ///     The summed response, under the mockup's
    ///     <c>drop-shadow(0 0 7px rgba(167,139,250,.85))</c>.
    ///     <para>
    ///         Avalonia has no per-geometry drop shadow, so the glow is two wider
    ///         low-opacity purple strokes UNDER the white one. It is what makes
    ///         the curve read as the one line on the plot instead of one more
    ///         hairline among the grid, the zones and the analyser outline — the
    ///         owner's "there isn't even the nice lines".
    ///     </para>
    ///     <para>
    ///         A BYPASSED section is grey (<c>#5D5675</c>) and gets NO glow, also
    ///         from the mockup: the curve that is not being applied must not be
    ///         the brightest thing in the well.
    ///     </para>
    /// </summary>
    private void DrawCurve(DrawingContext context, EqSection section, Rect rect, double dim)
    {
        section.CopyAudibleBandsTo(_audible);

        var signature = new HashCode();
        signature.Add(rect.Width);
        signature.Add(rect.Height);
        foreach (var band in _audible) signature.Add(band);
        var hash = signature.ToHashCode();
        if (_curveGeometry is null || hash != _curveSignature)
        {
            _curveGeometry = CurveGeometry(_audible, _curveResponse, rect);
            _curveSignature = hash;
        }

        var geometry = _curveGeometry;

        using (context.PushOpacity(dim))
        {
            if (section.IsEnabled)
            {
                foreach (var (width, opacity) in GlowStrokes)
                {
                    using (context.PushOpacity(opacity))
                    {
                        context.DrawGeometry(null,
                            Pen("FireEqCurveGlow", Color.FromRgb(167, 139, 250), width, round: true),
                            geometry);
                    }
                }
            }

            var pen = section.IsEnabled
                ? Pen("FireEqCurve", Colors.White, 2.0, round: true)
                : Pen("FireEqCurveBypassed", Color.FromRgb(93, 86, 117), 2.0, round: true);
            context.DrawGeometry(null, pen, geometry);
        }
    }

    /// <summary>The summed response as a polyline across the plot. Clamped
    /// generously OUTSIDE the rect rather than at its edge, so a band pushed past
    /// the top leaves the plot on a straight line instead of flattening against
    /// the ceiling and reading as a filter that stopped working.</summary>
    private StreamGeometry CurveGeometry(IReadOnlyList<EqBand> bands, double[] response, Rect rect)
    {
        EqCurve.Response(bands, response);
        var geometry = new StreamGeometry();
        using var sink = geometry.Open();

        for (var i = 0; i < response.Length; i++)
        {
            var x = i / (double)(response.Length - 1) * rect.Width;
            var y = Math.Clamp(YOf(response[i]), -rect.Height, rect.Height * 2);
            if (i == 0) sink.BeginFigure(new Point(x, y), false);
            else sink.LineTo(new Point(x, y));
        }

        sink.EndFigure(false);
        return geometry;
    }

    private void DrawHandles(DrawingContext context, EqSection section, Rect rect, double dim)
    {
        var offFill = Brush("FireEqHandleOff", Color.FromRgb(34, 28, 51));
        var mutedColour = Color.FromRgb(108, 100, 136);
        var inkColour = Color.FromRgb(13, 10, 22);

        for (var i = 0; i < section.Bands.Count; i++)
        {
            var band = section.Bands[i];
            var isSelected = i == SelectedBand;
            var soloed = section.SoloedBand;
            var on = section.IsEnabled && (soloed >= 0 ? soloed == i : band.Enabled);

            var centre = new Point(
                Math.Clamp(XOf(band.FrequencyHz), SelectedHandleRadius, rect.Width - SelectedHandleRadius),
                Math.Clamp(YOf(band.HasGain ? band.GainDb : 0.0), SelectedHandleRadius,
                    rect.Height - SelectedHandleRadius));

            var colour = BandBrush(i);
            var radius = isSelected ? SelectedHandleRadius : HandleRadius;

            using (context.PushOpacity(dim))
            {
                // The selected band gets a dashed vertical guide down the plot,
                // which is what makes "which one am I editing" answerable without
                // reading the header.
                if (isSelected)
                {
                    var guide = Pen(BandKey(i), Colors.Magenta, 1, Dash.Guide);
                    using (context.PushOpacity(0.5))
                    {
                        context.DrawLine(guide, new Point(centre.X, 0), new Point(centre.X, rect.Height));
                    }
                }

                // A soft halo, then the handle itself.
                using (context.PushOpacity(on ? (isSelected ? 0.18 : 0.10) : 0.05))
                {
                    context.DrawEllipse(colour, null, centre, radius + 8, radius + 8);
                }

                var outline = on
                    ? Pen("FireTextBright", Colors.White, isSelected ? 2 : 1.2)
                    : Pen("FireTextMuted", mutedColour, isSelected ? 2 : 1.2);
                context.DrawEllipse(on ? colour : offFill, outline, centre, radius, radius);

                var label = i < HandleLabels.Length ? HandleLabels[i] : (i + 1).ToString(CultureInfo.InvariantCulture);
                var text = on
                    ? Text(label, 9, "FireTextOnAccentInk", inkColour, CultureInfo.InvariantCulture)
                    : Text(label, 9, "FireTextMuted", mutedColour, CultureInfo.InvariantCulture);
                context.DrawText(text, new Point(centre.X - text.Width / 2, centre.Y - text.Height / 2));
            }
        }
    }

    /// <summary>
    ///     The frequency labels, on pills, along the bottom INSIDE the plot — and
    ///     the preview tag, on the same pill, top left.
    /// </summary>
    private void DrawAxis(DrawingContext context, Rect rect, double dim)
    {
        var mutedColour = Color.FromRgb(93, 86, 117);
        var pill = Brush("FireEqAxisPill", Color.FromArgb(191, 10, 8, 18));

        using (context.PushOpacity(dim))
        {
            foreach (var (hz, label) in AxisTicks)
            {
                var x = XOf(hz);
                var text = Text(label, 8.5, "FireTextDim", mutedColour, CultureInfo.InvariantCulture);

                // The first and last labels are pinned INSIDE the plot rather
                // than centred on their tick, or "20" and "20k" are half cut off
                // at every width.
                var left = Math.Clamp(x - text.Width / 2, 2, Math.Max(2.0, rect.Width - text.Width - 2));
                var top = rect.Height - text.Height - 2;
                DrawPill(context, pill, new Rect(left, top, text.Width, text.Height));
                context.DrawText(text, new Point(left, top));
            }

            if (SpectrumKind == EqSpectrumKind.Preview && PreviewLabel is { Length: > 0 } previewLabel)
            {
                var tag = Text(previewLabel, 8.5, "FireTextDim", mutedColour, CultureInfo.CurrentCulture);
                DrawPill(context, pill, new Rect(6, 5, tag.Width, tag.Height));
                context.DrawText(tag, new Point(6, 5));
            }
        }
    }

    /// <summary>The mockup's <c>padding: 0 2px; border-radius: 3px</c> chip. Its
    /// only job is to stop a 8.5 px label being unreadable over the analyser fill
    /// and the amber string ticks it sits on.</summary>
    private static void DrawPill(DrawingContext context, IBrush fill, Rect textBounds) =>
        context.DrawRectangle(fill, null, textBounds.Inflate(new Thickness(2.5, 1.0)), 3, 3);
}
