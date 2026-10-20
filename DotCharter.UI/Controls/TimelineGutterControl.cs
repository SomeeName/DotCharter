using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using DotCharter.Tone.Audio.Tuner;
using DotCharter.UI.Controls.Themes;
using DotCharter.UI.Editing.Tracks;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.UI.Controls;

/// <summary>
///     The chart timeline's fixed LEFT GUTTER: a mono caption for every lane in
///     the timeline beside it — <c>SECTIONS</c>, <c>PHRASES</c>, <c>LYRICS</c>,
///     <c>MEASURES</c>, then one row per string, then <c>TIME</c>.
///     <para>
///         The owner's report was that you cannot tell what a lane is without
///         already knowing. The string rows carry the most information: a tick
///         in that string's own colour, its NOTE NAME (which is not constant —
///         a drop tuning renames every string, so it comes from the
///         arrangement), a dim string number, and <c>LOW</c>/<c>HIGH</c> on the
///         outer two so the vertical order is unambiguous under either flip.
///     </para>
///     <para>
///         <b>It is a sibling of the renderer, not part of it.</b> Drawing the
///         gutter inside <c>ChartRenderer</c> would put a fixed offset on the
///         renderer's x axis, and every hit-tester, the marquee, the minimap
///         drag and the one time↔screen anchor all map through that axis. As a
///         sibling in column 0 it changes no coordinate at all.
///     </para>
///     <para>
///         <b>The colours are the CHART palette, not the Fire identities.</b>
///         The six Fire string colours are fixed art direction; the timeline's
///         are <c>ChartTheme.BaseStringColors</c>, which a theme pack and the
///         colour-blind presets really do change. A gutter on the fixed set
///         would disagree with the notes 20 px to its right the moment a player
///         picked a colour-blind preset — and the whole point of the gutter is
///         to say which lane is which string.
///     </para>
///     <para>
///         Repaints only when the renderer's published layout SHAPE changes or
///         the arrangement's tuning does — never per frame.
///     </para>
/// </summary>
public sealed class TimelineGutterControl : Control
{
    public static readonly StyledProperty<ChartRenderer?> RendererProperty =
        AvaloniaProperty.Register<TimelineGutterControl, ChartRenderer?>(nameof(Renderer));

    public static readonly StyledProperty<ChartEditorViewModel?> EditorProperty =
        AvaloniaProperty.Register<TimelineGutterControl, ChartEditorViewModel?>(nameof(Editor));

    /// <summary>"Play in my tuning": the semitones the song is shifted to the player's
    /// tuning. The string names print the tuning the GUITAR must be in: the chart's plus this.</summary>
    public static readonly StyledProperty<int> TransposeSemitonesProperty =
        AvaloniaProperty.Register<TimelineGutterControl, int>(nameof(TransposeSemitones));

    /// <summary>Caption band, mono caps. Matches the Fire eyebrow step.</summary>
    private const double CaptionSize = 9.5;
    private const double StringNameSize = 13.0;
    private const double MetaSize = 9.0;

    private static readonly Typeface Mono =
        new(new FontFamily("avares://DotCharter.Tone.UI/Assets/Fonts#JetBrains Mono"));

    private static readonly Typeface MonoBold =
        new(new FontFamily("avares://DotCharter.Tone.UI/Assets/Fonts#JetBrains Mono"),
            FontStyle.Normal, FontWeight.Bold);

    private ChartRenderer? _subscribed;
    private TimelineGutterLayout _lastShape;
    private string _lastTuningStamp = string.Empty;

    /// <summary>Struct-keyed shaping cache (P2-13: the old key was a
    /// <c>string.Create</c> per LOOKUP — an allocation per label per paint).
    /// Bounded by clearing on theme/palette change and on a tuning change
    /// (the only sources of new keys).</summary>
    private readonly Dictionary<CaptionKey, FormattedText> _captionCache = [];

    private readonly record struct CaptionKey(FontWeight Weight, double Size, Color Color, string Text);

    // The three theme brushes were resolved (TryFindResource) and ALLOCATED on
    // every Render; they only change with the theme, so they are cached and
    // dropped by OnThemeOrPaletteChanged.
    private IBrush? _dim;
    private IBrush? _faint;
    private IBrush? _seam;

    public TimelineGutterControl()
    {
        // Every hit belongs to the timeline; the gutter is a legend.
        IsHitTestVisible = false;
        ClipToBounds = true;
    }

    public ChartRenderer? Renderer
    {
        get => GetValue(RendererProperty);
        set => SetValue(RendererProperty, value);
    }

    public ChartEditorViewModel? Editor
    {
        get => GetValue(EditorProperty);
        set => SetValue(EditorProperty, value);
    }

    public int TransposeSemitones
    {
        get => GetValue(TransposeSemitonesProperty);
        set => SetValue(TransposeSemitonesProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == RendererProperty && VisualRoot != null)
        {
            Resubscribe(Renderer);
        }
        else if (change.Property == EditorProperty)
        {
            InvalidateVisual();
        }
        else if (change.Property == TransposeSemitonesProperty)
        {
            // New names mint new cache keys and orphan the old ones.
            ClearRenderCaches();
            InvalidateVisual();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Resubscribe(Renderer);
        // Both palette sources: ChartTheme's runtime palette switch and the
        // Avalonia theme variant (the Solid() fallback keys live in resources).
        Themes.ChartTheme.ThemeChanged += OnThemeOrPaletteChanged;
        ActualThemeVariantChanged += OnThemeOrPaletteChanged;
    }

    /// <summary>Pane views are re-created on float/redock and the renderer
    /// outlives them, so a kept subscription roots a dead gutter
    /// (<c>ViewSubscriptionLeakGuardTests</c>) — same for the static
    /// <c>ChartTheme.ThemeChanged</c>.</summary>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Resubscribe(null);
        Themes.ChartTheme.ThemeChanged -= OnThemeOrPaletteChanged;
        ActualThemeVariantChanged -= OnThemeOrPaletteChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnThemeOrPaletteChanged() => OnThemeOrPaletteChanged(this, EventArgs.Empty);

    /// <summary>A cached FormattedText bakes its brush and a cached theme
    /// brush bakes its colour, so BOTH caches are only correct for one
    /// palette — drop them and repaint.</summary>
    internal void OnThemeOrPaletteChanged(object? sender, EventArgs e)
    {
        ClearRenderCaches();
        InvalidateVisual();
    }

    internal void ClearRenderCaches()
    {
        _captionCache.Clear();
        _dim = null;
        _faint = null;
        _seam = null;
    }

    internal int CaptionCacheCount => _captionCache.Count;

    private void Resubscribe(ChartRenderer? target)
    {
        if (ReferenceEquals(_subscribed, target)) return;
        _subscribed?.GutterLayoutChanged -= OnLayoutChanged;
        _subscribed = target;
        _subscribed?.GutterLayoutChanged += OnLayoutChanged;
        InvalidateVisual();
    }

    /// <summary>
    ///     The renderer publishes once per frame, gated on a real change to the
    ///     layout shape OR the tuning (<see cref="GutterPublishGate" />). That
    ///     second half is not optional: the TUNING
    ///     changes without the shape moving every time one six-string chart
    ///     replaces another, and while the renderer gated on the shape alone this
    ///     handler was simply never called — so the gutter kept the previous
    ///     song's string names and a Drop C# CDLC opened under "E A D G B E".
    ///     The stamp comparison here is the second gate, not the first.
    /// </summary>
    private void OnLayoutChanged(object? sender, EventArgs e)
    {
        var layout = _subscribed?.GutterLayout ?? default;
        var stamp = TuningStamp();
        if (layout.SameShapeAs(_lastShape) && string.Equals(stamp, _lastTuningStamp, StringComparison.Ordinal))
            return;
        // A tuning change mints new string-name cache keys and orphans the old
        // song's — drop them so the cache stays bounded by one song's labels.
        if (!string.Equals(stamp, _lastTuningStamp, StringComparison.Ordinal)) ClearRenderCaches();
        _lastShape = layout;
        _lastTuningStamp = stamp;
        InvalidateVisual();
    }

    private string TuningStamp() => TuningStampFor(Editor?.ActiveArrangement, TransposeSemitones);

    /// <summary>
    ///     Everything <see cref="StringNamesFor" /> can print, as one comparable
    ///     string. <c>TuningPitches</c> is in it because that is the vector
    ///     <c>TuningCheckTargets</c> reads FIRST — a stamp built from the note
    ///     letters alone missed an offsets-only change and left the lane names
    ///     stale. Built only when the renderer publishes, never per frame.
    /// </summary>
    internal static string TuningStampFor(Arrangement? arrangement, int transposeSemitones = 0)
    {
        if (arrangement == null) return string.Empty;
        return string.Create(CultureInfo.InvariantCulture,
            $"{arrangement.ArrangementType}/{arrangement.StringCount}/{arrangement.TuningType}/{arrangement.Capo}/" +
            $"{string.Join(',', arrangement.Tuning)}/" +
            $"{string.Join(',', arrangement.TuningPitches.Select(p => p.Step))}/{transposeSemitones}");
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var renderer = _subscribed;
        if (renderer == null || Bounds.Height <= 1) return;

        // The renderer's own answer when it has drawn a frame; the same pure
        // formula on the same inputs before the first one, so the gutter is
        // never blank while the chart beside it is not.
        var layout = renderer.GutterLayout.StringCount > 0
            ? renderer.GutterLayout
            : TimelineGutterLayout.For(
                Bounds.Height, renderer.TopRibbon,
                Editor?.ActiveArrangement?.Tuning.Length ?? 6,
                renderer.LeftyFlip ^ renderer.InvertStrings,
                renderer.BottomMargin);

        var w = Bounds.Width;
        // Cached per control (dropped by OnThemeOrPaletteChanged): resolving +
        // allocating these on every Render was P2-13.
        var dim = _dim ??= Solid("FireTextDim", Color.FromRgb(0x6C, 0x64, 0x88));
        var faint = _faint ??= Solid("FireTextFaint", Color.FromRgb(0x4F, 0x49, 0x66));
        var seam = _seam ??= Solid("FireBorderRow", Color.FromArgb(0x0D, 0xFF, 0xFF, 0xFF));

        DrawRibbonCaptions(context, layout, w, dim, seam);
        DrawStringRows(context, layout, w, faint);
        DrawBottomBandCaptions(context, layout, w, dim, seam);
    }

    // ── the ribbon's four lanes ─────────────────────────────────────────────

    private void DrawRibbonCaptions(
        DrawingContext context, TimelineGutterLayout layout, double w, IBrush dim, IBrush seam)
    {
        var r = layout.Ribbon;
        if (r.ShowTones) Caption(context, "Ribbon_Tones", r.ToneY, RibbonLayout.RowHeight, w, dim, seam);
        if (r.ShowSections) Caption(context, "Ribbon_Sections", r.SectionY, RibbonLayout.RowHeight, w, dim, seam);
        if (r.ShowPhrases) Caption(context, "Ribbon_Phrases", r.PhraseY, RibbonLayout.RowHeight, w, dim, seam);
        if (r.ShowLyrics) Caption(context, "Ribbon_Lyrics", r.LyricY, RibbonLayout.RowHeight, w, dim, seam);
        if (r.ShowMeasureMetadata)
            Caption(context, "Timeline_Gutter_Measures", r.MetadataY, RibbonLayout.MetadataBandHeight, w, dim, seam);
    }

    private void Caption(
        DrawingContext context, string key, double y, double rowHeight, double w, IBrush brush, IBrush seam)
    {
        var text = Cached(Loc(key).ToUpperInvariant(), CaptionSize, brush, Mono);
        context.DrawText(text, new Point(12, y + (rowHeight - text.Height) * 0.5));
        // A hairline under each caption band, so a lane's label and its lane
        // read as one row rather than as floating text.
        context.DrawRectangle(seam, null, new Rect(0, y + rowHeight - 1, w, 1));
    }

    // ── one row per string ──────────────────────────────────────────────────

    private void DrawStringRows(DrawingContext context, TimelineGutterLayout layout, double w, IBrush faint)
    {
        if (layout.LaneHeight <= 2) return;

        var names = StringNames(layout.StringCount);

        // ChartTheme.StringBrushFor hands out the theme layer's own palette-updated
        // brushes — allocating a SolidColorBrush per string per paint was
        // P2-13 (the brushes are mutated in place on a palette switch, and the
        // shaping cache keys on the COLOUR, so a stale glyph cannot survive).
        // It colours by the string's identity: a 7-string's low E is red.
        for (var i = 0; i < layout.StringCount; i++)
        {
            var centre = layout.LaneCentreFor(i);
            var brush = ChartTheme.StringBrushFor(i, layout.StringCount);

            // A 3 px tick in the string's own colour — the same mark the Fire
            // layer uses for a selected row, and the one thing here that reads
            // at a glance.
            context.DrawRectangle(brush, null, new Rect(0, centre - 9, 3, 18));

            var name = Cached(names[i], StringNameSize, brush, MonoBold);
            context.DrawText(name, new Point(16, centre - name.Height * 0.5));

            // The string NUMBER counts the other way round from the lane index
            // (string 6 is the low one), and it is right-aligned so the column
            // of numbers is a column.
            var number = Cached((layout.StringCount - i).ToString(CultureInfo.InvariantCulture),
                MetaSize, faint, Mono);
            var edgeLabel = i == 0 ? Loc("Timeline_Gutter_Low")
                : i == layout.StringCount - 1 ? Loc("Timeline_Gutter_High")
                : null;

            if (edgeLabel != null)
            {
                var edge = Cached(edgeLabel.ToUpperInvariant(), MetaSize, faint, Mono);
                context.DrawText(edge, new Point(w - 10 - edge.Width, centre - edge.Height * 0.5));
                context.DrawText(number, new Point(w - 16 - edge.Width - number.Width, centre - number.Height * 0.5));
            }
            else
            {
                context.DrawText(number, new Point(w - 10 - number.Width, centre - number.Height * 0.5));
            }
        }
    }

    /// <summary>
    ///     The note each string is tuned to, from the ARRANGEMENT — a drop
    ///     tuning renames every one of them, which is exactly the case the
    ///     reference screenshot shows (Eb Bb F# C# G# Eb). Reuses
    ///     <see cref="TuningCheckTargets" />, the same table the tuner and Get
    ///     Ready judge against, so the gutter cannot claim a note the tuner
    ///     disagrees with. The octave is dropped: this column is ~150 px and
    ///     "Eb2" says nothing "Eb" does not.
    /// </summary>
    internal static string[] StringNamesFor(Arrangement? arrangement, int count, int transposeSemitones = 0)
    {
        var names = new string[count];
        if (arrangement != null)
        {
            try
            {
                // TryBuild, not Build: `Build` assumes standard when the chart
                // states no offsets AND names a tuning nothing can decode, which
                // is the one case this column must not answer.
                if (TuningCheckTargets.TryBuild(
                        arrangement, TuningCheckCapoMode.TuneOpenThenPlaceCapo, out var targets, transposeSemitones))
                {
                    for (var i = 0; i < count; i++)
                        names[i] = i < targets.Length ? StripOctave(targets[i].NoteName) : Unknown;
                    return names;
                }
            }
            catch (Exception)
            {
                // A malformed tuning must not take the whole timeline with it.
            }
        }

        for (var i = 0; i < count; i++) names[i] = Unknown;
        return names;
    }

    /// <summary>What a lane is called when the chart has not said — never an
    /// invented "E", which would be a confident lie about the tuning.</summary>
    internal const string Unknown = "?";

    private string[] StringNames(int count) => StringNamesFor(Editor?.ActiveArrangement, count, TransposeSemitones);

    private static string StripOctave(string noteName)
    {
        var end = noteName.Length;
        while (end > 0 && char.IsDigit(noteName[end - 1])) end--;
        return end > 0 ? noteName[..end] : noteName;
    }

    // ── the bottom margin's three bands ─────────────────────────────────────

    /// <summary>
    ///     The bottom margin stacks THREE bands — the hand-shape bar, the FHP
    ///     lane and the time ruler — and each carries its own caption: a single <c>TIME</c> caption
    ///     spanning all of them would leave the two EDITING bands the ToolCoordinator
    ///     routes clicks to unlabeled, and a click in the anonymous FHP band reads as "clicking the
    ///     ruler created a yellow region". The caption slots come off the SAME numbers the routing uses
    ///     (<see cref="Input.TimelineZones" /> / RenderMetrics), via the pure
    ///     <see cref="BottomBandCaptionSlots" /> so the agreement is testable.
    /// </summary>
    private void DrawBottomBandCaptions(
        DrawingContext context, TimelineGutterLayout layout, double w, IBrush dim, IBrush seam)
    {
        if (layout.BottomMargin <= 4) return;
        var top = layout.TopMargin + layout.LaneAreaHeight;
        context.DrawRectangle(seam, null, new Rect(0, top, w, 1));

        var slots = BottomBandCaptionSlots(top, layout.BottomMargin);
        if (slots.ShowBands)
        {
            BandCaption(context, "Timeline_Gutter_Shapes", slots.ShapesY, slots.ShapesH, dim);
            BandCaption(context, "Timeline_Gutter_Fhp", slots.FhpY, slots.FhpH, dim);
        }

        BandCaption(context, "Timeline_Gutter_Time", slots.TimeY, slots.TimeH, dim);
    }

    private void BandCaption(DrawingContext context, string key, double y, double h, IBrush brush)
    {
        var text = Cached(Loc(key).ToUpperInvariant(), CaptionSize, brush, Mono);
        context.DrawText(text, new Point(12, y + (h - text.Height) * 0.5));
    }

    /// <summary>Slot record for <see cref="BottomBandCaptionSlots" />.</summary>
    internal readonly record struct BottomBandSlots(
        double ShapesY, double ShapesH, double FhpY, double FhpH, double TimeY, double TimeH, bool ShowBands);

    /// <summary>
    ///     Pure: where each bottom-band caption sits, given the lane bottom and
    ///     the bottom margin. The Y/height numbers restate what
    ///     <see cref="Input.TimelineZones" /> routes by — pinned against those
    ///     predicates by <c>UI/TimelineGutterBandCaptionTests</c>. The editing
    ///     bands hide (<c>ShowBands</c> false) when a squeezed margin would run
    ///     them into the ruler — a caption on the wrong band is worse than none.
    /// </summary>
    internal static BottomBandSlots BottomBandCaptionSlots(double laneBottom, double bottomMargin)
    {
        var shapesY = laneBottom + Constants.ChartEditorConstants.RenderMetrics.HandShapeBarYOffset;
        var shapesH = Constants.ChartEditorConstants.RenderMetrics.HandShapeActiveBarHeight + 4.0;
        var fhpY = laneBottom + Constants.ChartEditorConstants.RenderMetrics.FhpYOffset;
        var fhpH = (double)Constants.ChartEditorConstants.RenderMetrics.AnchorTailHeight;
        var timeH = Input.TimelineZones.TimeRulerHeightPx;
        var timeY = laneBottom + bottomMargin - timeH;
        var showBands = fhpY + fhpH <= timeY;
        return new BottomBandSlots(shapesY, shapesH, fhpY, fhpH, timeY, timeH, showBands);
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    ///     <c>FormattedText</c> runs text shaping on construction, so the ~14
    ///     labels this control draws are cached by (text, size, colour). A
    ///     gutter repaint is rare, but a repaint that reshapes every caption is
    ///     a repaint nobody would notice getting slower.
    /// </summary>
    private FormattedText Cached(string text, double size, IBrush brush, Typeface face)
    {
        // Struct key — the old string.Create key was an allocation per label
        // per PAINT (P2-13). Keys on the COLOUR (the string brushes are
        // mutated in place on a palette switch), and the cache is cleared on
        // theme/palette and tuning changes, its only sources of new keys.
        var key = new CaptionKey(face.Weight, size,
            brush is ISolidColorBrush s ? s.Color : default, text);
        if (_captionCache.TryGetValue(key, out var cached)) return cached;
        var built = new FormattedText(text, CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, face, size, brush);
        _captionCache[key] = built;
        return built;
    }

    /// <summary>A named theme brush with a baked fallback — a custom-drawn
    /// control cannot use <c>DynamicResource</c>, and a missing key must not
    /// blank the gutter. Every key here is pinned by
    /// <c>UI/CustomDrawnPaletteTests</c>.</summary>
    private IBrush Solid(string key, Color fallback)
        => new SolidColorBrush(
            this.TryFindResource(key, out var v) && v is ISolidColorBrush b ? b.Color : fallback);

    private static string Loc(string key) => DotCharter.Localization.LocalizationService.Instance[key];
}
