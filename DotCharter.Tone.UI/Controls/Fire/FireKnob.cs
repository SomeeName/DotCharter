using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using DotCharter.Tone.UI.Controls.Dsp;

namespace DotCharter.Tone.UI.Controls.Fire;

/// <summary>
/// The Fire design system's knob: a 46 px dial (38 compact) with a 270° value
/// arc in its UNIT'S fixed colour, an indicator line, a mono caption and a mono
/// readout. Built from <c>Amp and FX Rig.dc.html</c>'s <c>knob()</c>; the
/// geometry is <see cref="FireKnobGeometry"/> and the colour identity is
/// <see cref="FireUnitState"/>.
///
/// <para><b>The interaction is a VERTICAL POINTER DRAG with pointer capture,
/// and 170 px of travel is the whole range.</b> Not a rotary gesture, and not
/// scaled to the control's size — see
/// <see cref="FireKnobGeometry.PixelsForFullRange"/>. Shift gives a 10× finer
/// pass.</para>
///
/// <para><b>Every gesture has a keyboard equal, and they all commit through the
/// same clamp+snap the drag does.</b> The mapping is the pure
/// <see cref="FireKnobGeometry.ValueForKey"/>: Up/Right and Down/Left move one
/// <see cref="Step"/> (1 % of the range when the parameter is continuous),
/// PageUp/PageDown move ten of those, Home/End go to the ends, and
/// Delete/Backspace revert to <see cref="DefaultValue"/> — the keyboard's half
/// of the double-click reset, which is otherwise the ONE way back to a
/// parameter's default.</para>
///
/// <para><b>The knob names ITSELF.</b> Its <c>AutomationProperties.Name</c>
/// follows <see cref="Label"/> and its peer is a real
/// <c>AutomationControlType.Slider</c> over <c>IRangeValueProvider</c>
/// (<see cref="FireKnobAutomationPeer"/>), so a screen reader reads the caption
/// and the value and can set it. Naming knobs at the CALL SITE would be a
/// second place to forget: there are two markup sites and both bind a
/// catalogue-supplied caption, so deriving it here names every knob the rig can
/// ever build, exactly once.</para>
///
/// <para><b>Avalonia has no conic gradient.</b> The arc is a real
/// <c>ArcTo</c> path stroked with a round-capped pen — the detail most likely
/// to be got wrong when porting the mockup's
/// <c>conic-gradient(from 225deg, …)</c>. (225° clockwise from 12 o'clock is
/// the same bearing as −135°, which is where the sweep starts.)</para>
///
/// <para>The two text lines are child <see cref="TextBlock"/>s rather than
/// <c>FormattedText</c> runs, because <c>FormattedText</c> cannot letter-space
/// and "monospace means machine data" includes the tracking. They carry the
/// mono font and their colours as <c>DynamicResource</c> bindings, so nothing
/// in this file is a literal colour.</para>
///
/// <para>Its identity and its bypass are the INHERITED attached properties on
/// <see cref="FireUnitState"/>, set once on the unit card: there is no per-knob
/// colour and no per-knob bypass to forget.</para>
/// </summary>
public sealed class FireKnob : FireDrawnControl
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<FireKnob, double>(
            nameof(Value), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<double> MinimumProperty =
        AvaloniaProperty.Register<FireKnob, double>(nameof(Minimum));

    public static readonly StyledProperty<double> MaximumProperty =
        AvaloniaProperty.Register<FireKnob, double>(nameof(Maximum), 1.0);

    public static readonly StyledProperty<double> DefaultValueProperty =
        AvaloniaProperty.Register<FireKnob, double>(nameof(DefaultValue));

    /// <summary>Quantization step; 0 is continuous. Every write goes through it,
    /// so a stepped parameter can never land between two steps whatever the
    /// gesture was.</summary>
    public static readonly StyledProperty<double> StepProperty =
        AvaloniaProperty.Register<FireKnob, double>(nameof(Step));

    /// <summary>The caption under the dial. Uppercased for display; two lines
    /// are allowed.</summary>
    public static readonly StyledProperty<string?> LabelProperty =
        AvaloniaProperty.Register<FireKnob, string?>(nameof(Label));

    /// <summary>The readout. Null formats <see cref="Value"/> adaptively — bind
    /// it when the parameter has a unit ("−6.2 dB", "480 Hz").</summary>
    public static readonly StyledProperty<string?> ValueTextProperty =
        AvaloniaProperty.Register<FireKnob, string?>(nameof(ValueText));

    /// <summary>The 38 px dial a collapsed card uses.</summary>
    public static readonly StyledProperty<bool> IsCompactProperty =
        AvaloniaProperty.Register<FireKnob, bool>(nameof(IsCompact));

    /// <summary>Opt-in tactile hardware face for the rig. Geometry and gestures stay shared.</summary>
    public static readonly StyledProperty<bool> HardwareStyleProperty =
        AvaloniaProperty.Register<FireKnob, bool>(nameof(HardwareStyle));

    public bool HardwareStyle
    {
        get => GetValue(HardwareStyleProperty);
        set => SetValue(HardwareStyleProperty, value);
    }

    /// <summary>True when the readout shows a unit-range parameter as 0–10
    /// (<see cref="KnobMath.DisplayValue"/>). A typed value is read in that
    /// same display space, so "7" on a ten-scale knob means 0.7.</summary>
    public static readonly StyledProperty<bool> TenScaleProperty =
        AvaloniaProperty.Register<FireKnob, bool>(nameof(TenScale));

    /// <summary>A bare 26 px dial with no caption and no readout — the design's
    /// mixer pan knob. The caption still names it for the accessibility tree and
    /// the value text is the caller's to print beside it.</summary>
    public static readonly StyledProperty<bool> DenseProperty =
        AvaloniaProperty.Register<FireKnob, bool>(nameof(Dense));

    public bool Dense { get => GetValue(DenseProperty); set => SetValue(DenseProperty, value); }

    /// <summary>The unit printed beside the value plate ("Hz", "ms", "dB") —
    /// the tone-rig design's <c>.parameter-unit</c>. The caption then drops
    /// the parenthesised suffix it would otherwise carry (see
    /// <c>PedalParamViewModel.Caption</c>). Null prints nothing. Hardware
    /// faces only; the compact dial has no plate to sit beside.</summary>
    public static readonly StyledProperty<string?> UnitProperty =
        AvaloniaProperty.Register<FireKnob, string?>(nameof(Unit));

    public string? Unit { get => GetValue(UnitProperty); set => SetValue(UnitProperty, value); }

    /// <summary>
    ///     How this dial's TRAVEL maps onto [<see cref="Minimum"/>,
    ///     <see cref="Maximum"/>]. Defaults to <see cref="KnobTaper.Linear"/>,
    ///     which is what every knob in the app did before the 0.9 gear wave, so
    ///     a knob that does not set it is bit-identical to the one that shipped.
    ///     <para>
    ///         The tone rig binds it from
    ///         <c>PedalParamViewModel.Taper</c>, which decides it from the
    ///         native catalogue metadata — a frequency id or a multi-decade
    ///         range. It changes NOTHING about the value: <see cref="Value"/>,
    ///         the readout, the typed entry, the step grid and what is pushed to
    ///         the DSP are all in the param's own plain units either way.
    ///     </para>
    /// </summary>
    public static readonly StyledProperty<KnobTaper> TaperProperty =
        AvaloniaProperty.Register<FireKnob, KnobTaper>(nameof(Taper));

    /// <inheritdoc cref="TaperProperty"/>
    public KnobTaper Taper { get => GetValue(TaperProperty); set => SetValue(TaperProperty, value); }

    /// <summary>An explicit dial diameter for a HARDWARE face (0 = the design's
    /// 64 px, or 26 px when <see cref="Dense"/>). The amp's secondary cards
    /// draw a bare 62 px dial beside their own caption; the side rack fits four
    /// 52 px dials across its 300 px. The cell grows and shrinks with it.</summary>
    public static readonly StyledProperty<double> DialSizeProperty =
        AvaloniaProperty.Register<FireKnob, double>(nameof(DialSize));

    public double DialSize { get => GetValue(DialSizeProperty); set => SetValue(DialSizeProperty, value); }

    /// <summary>
    ///     A knob-cap SPRITE (a full <c>avares://</c> URI) to draw instead of the dial:
    ///     the faceplate knob. The sprite's pointer is at 12 o'clock and the whole cap
    ///     turns with the value — the faceplate under it prints the label and the
    ///     scale, so nothing else is drawn. Every gesture (drag, Shift for fine,
    ///     wheel, keys, double/right-click to reset) is the same knob. Use with
    ///     <see cref="Dense" /> and a <see cref="DialSize" />.
    /// </summary>
    public static readonly StyledProperty<string?> CapFileProperty =
        AvaloniaProperty.Register<FireKnob, string?>(nameof(CapFile));

    /// <inheritdoc cref="CapFileProperty"/>
    public string? CapFile { get => GetValue(CapFileProperty); set => SetValue(CapFileProperty, value); }

    /// <summary>Where a stepped control's positions are printed, one angle per step
    /// (degrees clockwise from 12 o'clock) — a faceplate selector whose labels sit
    /// on four sides or a whole circle. Null: the even −135°..+135° sweep.</summary>
    public static readonly StyledProperty<IReadOnlyList<double>?> StepAnglesProperty =
        AvaloniaProperty.Register<FireKnob, IReadOnlyList<double>?>(nameof(StepAngles));

    /// <inheritdoc cref="StepAnglesProperty"/>
    public IReadOnlyList<double>? StepAngles { get => GetValue(StepAnglesProperty); set => SetValue(StepAnglesProperty, value); }

    private const double DenseDial = 26.0;
    private const double HardwareDial = 64.0;

    /// <summary>The hardware face's fixed rows: a 32 px caption block above the
    /// dial and an 18 px readout line below it.</summary>
    private const double HardwareLabelHeight = 32.0;
    private const double HardwareReadoutHeight = 18.0;

    private double DialOf()
    {
        if (Dense) return DialSize > 0 ? DialSize : DenseDial;
        if (HardwareStyle && !IsCompact) return DialSize > 0 ? DialSize : HardwareDial;
        return FireKnobGeometry.DialDiameter(IsCompact);
    }

    /// <summary>The hardware cell: wide enough for the 66 px value plate (or the
    /// dial plus its arc, whichever is wider) and exactly as tall as its three
    /// rows — 84×132 at the design's 64 px dial.</summary>
    private Size HardwareCellSize(double dial)
    {
        var width = Math.Max(dial + 20, 64);
        var height = HardwareLabelHeight + FireKnobGeometry.RowGap
                     + FireKnobGeometry.DialBlockHeight(dial) + FireKnobGeometry.RowGap
                     + HardwareReadoutHeight;
        return new Size(width, height);
    }

    public bool TenScale { get => GetValue(TenScaleProperty); set => SetValue(TenScaleProperty, value); }

    /// <summary>.04em at 10 px — the readout is machine data, so it is tracked
    /// too, just less than the caption's .08em.</summary>
    private const double ValueLetterSpacing = 0.4;

    private readonly TextBlock _label;
    private readonly TextBlock _readout;
    private readonly TextBlock _unit;

    /// <summary>The inline value editor (design: "Type a value · Enter to apply
    /// · Escape to cancel"). Built on first use — most knobs are never typed
    /// into, and a TextBox per knob on a 57-module board is not free.</summary>
    private TextBox? _editor;
    private bool _editing;

    private bool _dragging;
    private bool _dragFine;
    private double _dragAnchorY;
    private double _dragAnchorValue;
    private bool _hover;

    static FireKnob() => FocusableProperty.OverrideDefaultValue<FireKnob>(true);

    public FireKnob()
        : base(followsThemeVariant: true)
    {
        // The cap sprite is scaled and turned every frame: the default (low-quality)
        // filter smeared its pointer and printed edge.
        RenderOptions.SetBitmapInterpolationMode(this, Avalonia.Media.Imaging.BitmapInterpolationMode.HighQuality);
        _label = MonoLine(FireKnobGeometry.LabelFontSize, FireKnobGeometry.LabelLetterSpacing,
            "FireTextMuted", wrap: true);
        _label.MaxLines = FireKnobGeometry.MaxLabelLines;
        _label.LineHeight = FireKnobGeometry.LabelLineHeight;

        _readout = MonoLine(FireKnobGeometry.ValueFontSize, ValueLetterSpacing,
            "FireTextBright", wrap: false);
        _readout.LineHeight = (HardwareStyle ? 18 : FireKnobGeometry.ValueLineHeight);

        // The unit beside the plate: the design's 11 px UI-font suffix in the
        // muted ink, left-aligned so "Hz" and "ms" start at the same x.
        _unit = new TextBlock
        {
            FontSize = 11,
            TextAlignment = TextAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.NoWrap
        };
        _unit.Bind(TextBlock.FontFamilyProperty, new DynamicResourceExtension("FireFontUi"));
        _unit.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension("FireTextMuted"));

        VisualChildren.Add(_label);
        VisualChildren.Add(_readout);
        VisualChildren.Add(_unit);
        LogicalChildren.Add(_label);
        LogicalChildren.Add(_readout);
        LogicalChildren.Add(_unit);

        SyncText();
    }

    public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Minimum { get => GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }

    public double DefaultValue
    {
        get => GetValue(DefaultValueProperty);
        set => SetValue(DefaultValueProperty, value);
    }

    public double Step { get => GetValue(StepProperty); set => SetValue(StepProperty, value); }
    public string? Label { get => GetValue(LabelProperty); set => SetValue(LabelProperty, value); }

    public string? ValueText
    {
        get => GetValue(ValueTextProperty);
        set => SetValue(ValueTextProperty, value);
    }

    public bool IsCompact { get => GetValue(IsCompactProperty); set => SetValue(IsCompactProperty, value); }

    /// <summary>The 0..1 sweep fraction the arc and the indicator are drawn
    /// at.</summary>
    public double Fraction => FireKnobGeometry.FractionFromValue(Value, Minimum, Maximum, Taper);

    /// <summary>The indicator's angle, degrees clockwise from 12 o'clock:
    /// −135° at the minimum, +135° at the maximum — or, with
    /// <see cref="StepAngles" />, the printed position of the current step.</summary>
    public double IndicatorAngleDegrees => StepAngles is { Count: > 0 } printed
        ? KnobMath.AngleForStep(Value, Minimum, printed)
        : FireKnobGeometry.AngleDegreesFor(Fraction);

    /// <summary>The readout as rendered.</summary>
    internal string ReadoutText => _readout.Text ?? string.Empty;

    /// <summary>The caption as rendered (uppercased).</summary>
    internal string CaptionText => _label.Text ?? string.Empty;

    // ── Layout ───────────────────────────────────────────────────────────────

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Dense)
        {
            var bare = DialOf() + 2 * FireKnobGeometry.ArcOutset;
            _label.Measure(new Size(0, 0));
            _readout.Measure(new Size(0, 0));
            _unit.Measure(new Size(0, 0));
            return new Size(bare, bare);
        }
        var size = HardwareStyle && !IsCompact ? HardwareCellSize(DialOf()) : FireKnobGeometry.DesiredSize(IsCompact);
        _label.Measure(new Size(size.Width,
            FireKnobGeometry.MaxLabelLines * (HardwareStyle ? 16 : FireKnobGeometry.LabelLineHeight)));
        _readout.Measure(new Size(size.Width, (HardwareStyle ? 18 : FireKnobGeometry.ValueLineHeight)));
        _unit.Measure(new Size(UnitWidth, HardwareReadoutHeight));
        _editor?.Measure(new Size(size.Width, EditorHeight));
        return size;
    }

    /// <summary>The unit's slot beside the plate. It is allowed to hang past
    /// the cell (the design's <c>width:0; overflow:visible</c>) so a cell is
    /// no wider for carrying "kHz".</summary>
    private const double UnitWidth = 40.0;

    /// <summary>Where the unit sits: just right of the value plate, on the
    /// readout's line. Empty when there is no plate to sit beside.</summary>
    private Rect UnitRect(Size size, Rect readout)
    {
        if (!HardwareStyle || Dense || string.IsNullOrEmpty(Unit)) return default;
        var box = EditorRect(size, readout);
        return new Rect(box.Right + 4, readout.Y, UnitWidth, readout.Height);
    }

    private const double EditorHeight = 26.0;

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Dense)
        {
            _label.Arrange(new Rect(0, 0, 0, 0));
            _readout.Arrange(new Rect(0, 0, 0, 0));
            _unit.Arrange(new Rect(0, 0, 0, 0));
            _editor?.Arrange(new Rect(0, 0, 0, 0));
            return finalSize;
        }
        var dial = DialOf();
        var top = FireKnobGeometry.DialBlockHeight(dial) + FireKnobGeometry.RowGap;
        var labelHeight = FireKnobGeometry.MaxLabelLines * (HardwareStyle ? 16 : FireKnobGeometry.LabelLineHeight);

        _label.Arrange(new Rect(0, HardwareStyle ? 0 : top, finalSize.Width, labelHeight));
        var readout = new Rect(0, top + labelHeight + FireKnobGeometry.RowGap,
            finalSize.Width, (HardwareStyle ? 18 : FireKnobGeometry.ValueLineHeight));
        _readout.Arrange(readout);
        _unit.Arrange(UnitRect(finalSize, readout));
        _editor?.Arrange(EditorRect(finalSize, readout));
        return finalSize;
    }

    /// <summary>The readout's line, where a press starts typing rather than
    /// dragging. Recomputed from the same numbers Arrange uses.</summary>
    private Rect ReadoutRect()
    {
        if (Dense) return default;
        var dial = DialOf();
        var top = FireKnobGeometry.DialBlockHeight(dial) + FireKnobGeometry.RowGap;
        var labelHeight = FireKnobGeometry.MaxLabelLines * (HardwareStyle ? 16 : FireKnobGeometry.LabelLineHeight);
        return new Rect(0, top + labelHeight + FireKnobGeometry.RowGap, Bounds.Width,
            HardwareStyle ? 18 : FireKnobGeometry.ValueLineHeight);
    }

    private static Rect EditorRect(Size size, Rect readout)
    {
        var width = Math.Min(size.Width, 66);
        return new Rect((size.Width - width) / 2, readout.Y - (EditorHeight - readout.Height) / 2, width, EditorHeight);
    }

    // ── Rendering ────────────────────────────────────────────────────────────

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var dial = DialOf();
        var center = Dense
            ? new Point(Bounds.Width / 2.0, Bounds.Height / 2.0)
            : new Point(Bounds.Width / 2.0, FireKnobGeometry.ArcOutset + dial / 2.0 + (HardwareStyle ? 32 + FireKnobGeometry.RowGap : 0));
        var accent = FireUnitState.AccentOf(this);
        var inactive = FireUnitState.IsInactive(this);

        if (IsFocused && _focusFromKeyboard && !inactive) DrawFocusRing(context);

        if (CapFile is { Length: > 0 } capFile)
        {
            DrawCapSprite(context, capFile, center, dial, accent, inactive);
            return;
        }

        if (HardwareStyle)
        {
            DrawHardwareDial(context, center, dial, accent, inactive);
            return;
        }

        // The dial. Fill and rim are dictionary brushes — the mockup's
        // radial-gradient recipe lives in FireControls.axaml, not here.
        var fill = Paint.DictionaryBrush(this, "FireDialFill");
        var rim = Paint.Color(this, _hover && !inactive ? "FireDialRimHover" : "FireDialRim");
        context.DrawEllipse(fill, Paint.Pen(rim, 1), center, dial / 2.0, dial / 2.0);

        var radius = FireKnobGeometry.ArcRadius(dial);

        // The UNLIT remainder of the sweep — the one arc whose shape never
        // changes for a given size, so its geometry is kept — then the lit part
        // over it.
        context.DrawGeometry(null,
            Paint.Pen(Paint.Color(this, "FireSurfaceTrack"), FireKnobGeometry.ArcStrokeWidth, PenLineCap.Round),
            FullArc(center, radius));

        var fraction = Fraction;
        var sweep = fraction * FireKnobGeometry.SweepDegrees;
        if (sweep > 0)
        {
            // The glow: the same arc under a wider, much fainter pen. A
            // DrawingContext has no blur, so anything lit is layered.
            if (!inactive)
            {
                DrawArc(context, center, radius, 0, sweep,
                    FireUnitState.At(accent, 0.30), FireKnobGeometry.ArcStrokeWidth + 6);
            }

            DrawArc(context, center, radius, 0, sweep, accent, FireKnobGeometry.ArcStrokeWidth);
        }

        // The indicator: centre → outward, at −135° + pct × 270°.
        var tip = FireKnobGeometry.PointOnDial(
            center, dial * FireKnobGeometry.IndicatorLengthRatio,
            FireKnobGeometry.AngleDegreesFor(fraction));

        if (!inactive)
        {
            context.DrawLine(
                Paint.Pen(FireUnitState.At(accent, 0.35), FireKnobGeometry.IndicatorWidth + 5, PenLineCap.Round),
                center, tip);
        }

        context.DrawLine(
            Paint.Pen(accent, FireKnobGeometry.IndicatorWidth, PenLineCap.Round),
            center, tip);
    }

    /// <summary>
    ///     The design's knob (tone-rig <c>redesign.css .rig-knob</c>): the
    ///     texture pack's STATIONARY metal cap (its lighting never spins), eleven
    ///     tick dots outside it, the value arc in the unit's accent on a faint
    ///     track just outside the cap, and the one thing that rotates — a pale
    ///     pointer line — drawn by code. Proportions are the CSS's: the cap is
    ///     82 % of the knob box, the arc radius 40 %, the ticks at 45–48 %, the
    ///     pointer from 31 % to 7 % out of centre. Without the cap bitmap (a
    ///     stripped build) the vector face below stands in.
    /// </summary>
    private void DrawHardwareDial(DrawingContext context, Point center, double dial, Color accent, bool inactive)
    {
        var edge = Paint.Color(this, "FireHardwareEdge");
        var highlight = Paint.Color(this, "FireHardwareHighlight");
        var shadow = Paint.Color(this, "FireHardwareShadow");

        // Tick DOTS, not lines — that is what the device artwork shows.
        for (var tick = 0; tick <= 10; tick++)
        {
            var angle = FireKnobGeometry.AngleDegreesFor(tick / 10.0);
            var at = FireKnobGeometry.PointOnDial(center, dial * 0.465, angle);
            context.DrawEllipse(Paint.Brush(tick % 5 == 0 ? highlight : edge), null, at, 1.3, 1.3);
        }

        var cap = RigArtworkStore.GetForRender(RigArtwork.KnobCap, this);
        var capRadius = dial * 0.41;
        context.DrawEllipse(Paint.Brush(shadow), null, new Point(center.X + 1, center.Y + 3), capRadius, capRadius);
        if (cap is not null)
        {
            // The cap PNG is 256 px with a 16 px transparent margin, so the
            // metal disc is 224/256 of the bitmap: scale it so the disc fills
            // the cap circle exactly.
            var bitmapRadius = capRadius * 256.0 / 224.0;
            context.DrawImage(cap, new Rect(cap.Size),
                new Rect(center.X - bitmapRadius, center.Y - bitmapRadius, bitmapRadius * 2, bitmapRadius * 2));
        }
        else
        {
            context.DrawEllipse(Paint.DictionaryBrush(this, "FireHardwareBezel"), Paint.Pen(edge, 1), center, capRadius, capRadius);
            context.DrawEllipse(Paint.DictionaryBrush(this, "FireHardwareKnobFace"), Paint.Pen(highlight, 0.7), center, capRadius - 4, capRadius - 4);
        }

        // The value ring just outside the cap: a faint track in the accent's
        // tint (a fixed grey read as maroon against teal enclosures) and the
        // lit sweep over it.
        var arcRadius = dial * 0.40;
        context.DrawGeometry(null, Paint.Pen(FireUnitState.At(accent, 0.20), 2.6, PenLineCap.Round), FullArc(center, arcRadius));
        if (Fraction > 0)
            DrawArc(context, center, arcRadius, 0, Fraction * FireKnobGeometry.SweepDegrees, accent, 2.6);

        // The pointer — the only part that turns.
        var angleNow = IndicatorAngleDegrees;
        var pointer = inactive ? accent : Paint.Color(this, "FireHardwarePointer");
        context.DrawLine(Paint.Pen(shadow, 5, PenLineCap.Round),
            FireKnobGeometry.PointOnDial(center, dial * 0.07, angleNow),
            FireKnobGeometry.PointOnDial(center, dial * 0.31, angleNow));
        context.DrawLine(Paint.Pen(pointer, 3, PenLineCap.Round),
            FireKnobGeometry.PointOnDial(center, dial * 0.07, angleNow),
            FireKnobGeometry.PointOnDial(center, dial * 0.31, angleNow));

        if (_hover && !inactive)
            context.DrawEllipse(null, Paint.Pen(accent, 1), center, capRadius + 1, capRadius + 1);

        // The readout sits in a quiet box: it is a FIELD you can type into.
        var readout = ReadoutRect();
        if (readout.Width > 0)
        {
            var box = EditorRect(Bounds.Size, readout);
            context.DrawRectangle(Paint.Brush(FireUnitState.At(shadow, 0.45)),
                _hover && !inactive ? Paint.Pen(edge, 1) : null, box, 4, 4);
        }
    }

    /// <summary>The faceplate knob: the style's cap sprite, turned to the value.
    /// The cap's own lighting turns with it, as a real knob's does.</summary>
    private void DrawCapSprite(DrawingContext context, string capFile, Point center, double dial, Color accent, bool inactive)
    {
        var cap = RigArtworkStore.GetForRender(capFile, CapDecodeWidth(dial), this);
        if (cap is null)
        {
            // A stripped build still shows a turnable knob, never an empty nut.
            context.DrawEllipse(Paint.DictionaryBrush(this, "FireHardwareKnobFace"),
                Paint.Pen(Paint.Color(this, "FireHardwareEdge"), 1), center, dial / 2, dial / 2);
            context.DrawLine(Paint.Pen(Paint.Color(this, "FireHardwarePointer"), 2, PenLineCap.Round), center,
                FireKnobGeometry.PointOnDial(center, dial * 0.42, IndicatorAngleDegrees));
            return;
        }

        var radians = IndicatorAngleDegrees * Math.PI / 180.0;
        var rotate = Matrix.CreateTranslation(-center.X, -center.Y)
                     * Matrix.CreateRotation(radians)
                     * Matrix.CreateTranslation(center.X, center.Y);
        using (context.PushTransform(rotate))
        {
            context.DrawImage(cap, new Rect(cap.Size),
                new Rect(center.X - dial / 2, center.Y - dial / 2, dial, dial));
        }

        // A bypassed unit's knob DIMS — it never turns see-through: at partial
        // opacity the cap showed the bright chrome nut under it and read as a knob
        // that had gone missing. A dark shade over the solid cap.
        if (inactive)
        {
            context.DrawEllipse(Paint.Brush(FireUnitState.At(Paint.Color(this, "FireHardwareShadow"), 1 - FireUnitState.BypassedOpacity)),
                null, center, dial * 0.49, dial * 0.49);
        }

        if (_hover && !inactive)
            context.DrawEllipse(null, Paint.Pen(FireUnitState.At(accent, 0.8), 1.5), center, dial / 2 + 1, dial / 2 + 1);
    }

    /// <summary>Decode the cap near the size it is drawn at, in steps, so a board of
    /// small knobs shares one small decode. Generous: a face on the stage is scaled
    /// up by its <c>RigScaleToFit</c> and again by the screen's DPI, and a cap decoded
    /// smaller than it is drawn is a blurry pointer. The sprites are 256 px.</summary>
    private static int CapDecodeWidth(double dial) => dial <= 20 ? 128 : 0;

    // Pens, brushes and the unlit arc, remembered across paints: a drag frame
    // would build ~14 objects and walk the resource tree ~4 times.
    // See FirePaintCache.
    private StreamGeometry? _fullArc;
    private (Point Center, double Radius) _fullArcKey;

    private StreamGeometry FullArc(Point center, double radius)
    {
        if (_fullArc != null && _fullArcKey == (center, radius)) return _fullArc;
        _fullArc = ArcGeometry(center, radius, 0, FireKnobGeometry.SweepDegrees)!;
        _fullArcKey = (center, radius);
        return _fullArc;
    }

    private void DrawFocusRing(DrawingContext context)
    {
        // The spec's focus is "2px solid #cfe2ff, offset 3–4px". A CIRCLE at
        // that offset would not fit the 58 px cell (the arc already reaches
        // 27 px of the 29 px half-width), so the ring goes round the cell —
        // which is what the offset amounts to here, and it can never clip.
        var pale = Paint.Color(this, "FireAccentPale");
        context.DrawRectangle(
            null,
            Paint.Pen(pale, FireKnobGeometry.FocusRingWidth),
            new Rect(Bounds.Size).Deflate(FireKnobGeometry.FocusRingWidth / 2.0),
            FireKnobGeometry.FocusRingOffset, FireKnobGeometry.FocusRingOffset);
    }

    /// <summary>
    /// One stroked segment of the value ring, from <paramref name="fromDegrees"/>
    /// to <paramref name="toDegrees"/> measured ALONG the sweep (0 = the 225°
    /// start, i.e. −135° clockwise from 12 o'clock).
    /// </summary>
    private void DrawArc(
        DrawingContext context, Point center, double radius,
        double fromDegrees, double toDegrees, Color color, double thickness)
    {
        var geometry = ArcGeometry(center, radius, fromDegrees, toDegrees);
        if (geometry == null) return;
        context.DrawGeometry(null, Paint.Pen(color, thickness, PenLineCap.Round), geometry);
    }

    private static StreamGeometry? ArcGeometry(Point center, double radius, double fromDegrees, double toDegrees)
    {
        var sweep = toDegrees - fromDegrees;
        if (sweep <= 0 || radius <= 0) return null;

        var start = FireKnobGeometry.PointOnDial(
            center, radius, FireKnobGeometry.StartAngleDegrees + fromDegrees);
        var end = FireKnobGeometry.PointOnDial(
            center, radius, FireKnobGeometry.StartAngleDegrees + toDegrees);

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(start, false);
            ctx.ArcTo(end, new Size(radius, radius), 0,
                isLargeArc: sweep > 180.0, SweepDirection.Clockwise);
            ctx.EndFigure(false);
        }

        return geometry;
    }

    // ── Interaction ──────────────────────────────────────────────────────────

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!IsEffectivelyEnabled) return;

        var point = e.GetCurrentPoint(this);

        // RIGHT-click resets to the default anywhere on the knob (owner:
        // "there should be a way to reset … like right click or
        // double click") — the plate included, where a double-click opens
        // the editor instead.
        if (point.Properties.IsRightButtonPressed)
        {
            Commit(DefaultValue);
            e.Handled = true;
            return;
        }
        if (!point.Properties.IsLeftButtonPressed) return;

        if (ReadoutRect().Contains(point.Position))
        {
            BeginEdit();
            e.Handled = true;
            return;
        }

        if (e.ClickCount == 2)
        {
            Commit(DefaultValue);
            e.Handled = true;
            return;
        }

        _dragging = true;
        _dragFine = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        _dragAnchorY = point.Position.Y;
        _dragAnchorValue = Value;
        e.Pointer.Capture(this);
        // Pointer focus: the wheel and the keys work, but the focus ring is
        // for keyboard navigation and does not draw for a click.
        Focus(NavigationMethod.Pointer);
        e.Handled = true; // a knob drag must never start a board reorder
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_dragging) return;

        var y = e.GetPosition(this).Y;

        // Re-anchor when Shift is toggled mid-drag, so the value never jumps.
        var fine = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (fine != _dragFine)
        {
            _dragFine = fine;
            _dragAnchorY = y;
            _dragAnchorValue = Value;
        }

        Commit(FireKnobGeometry.ValueForDrag(
            _dragAnchorValue, y - _dragAnchorY, Minimum, Maximum, Taper, fine));
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_dragging) return;
        _dragging = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    /// <summary>True while the current focus arrived by keyboard (Tab, the
    /// arrows) or programmatically — the cases the ring is for. A click focuses
    /// the knob too (so the wheel and the keys work) but draws no ring: the
    /// pale rectangle round a knob someone just turned read as "a weird white
    /// border".</summary>
    private bool _focusFromKeyboard;

    /// <summary>The focus ring as drawn — the test seam.</summary>
    internal bool ShowsFocusRing => IsFocused && _focusFromKeyboard;

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        _focusFromKeyboard = e.NavigationMethod != NavigationMethod.Pointer;
        Repaint();
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        _focusFromKeyboard = false;
        Repaint();
    }

    /// <summary>The wheel turns a FOCUSED knob (Shift = one step, else five),
    /// and nothing else: an unfocused knob inside the editor's scroller must let
    /// the editor scroll, or every wheel over a pedal face turns a random knob.</summary>
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (!IsEffectivelyEnabled || !IsFocused) return;

        var span = Maximum - Minimum;
        if (span <= 0) return;

        var step = Step > 0 ? Step : span / 100.0;
        var multiplier = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 1 : 5;
        Commit(Value + Math.Sign(e.Delta.Y) * step * multiplier);
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || !IsEffectivelyEnabled) return;

        // A modified press is somebody else's: Ctrl+Home is the board's, and a
        // knob that swallowed it would strand the keyboard user on one card.
        if (e.KeyModifiers is not KeyModifiers.None) return;

        if (e.Key is Key.Enter or Key.F2)
        {
            BeginEdit();
            e.Handled = true;
            return;
        }

        if (FireKnobGeometry.ValueForKey(e.Key, Value, Minimum, Maximum, Step, DefaultValue)
            is not { } target)
        {
            return;
        }

        Commit(target);
        e.Handled = true;
    }

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        SetHover(true);
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        SetHover(false);
    }

    /// <summary>The ns-resize cursor is built HERE, not in the constructor:
    /// <c>new Cursor(StandardCursorType…)</c> resolves <c>ICursorFactory</c> off
    /// the locator, which would make the control unconstructable without an
    /// Avalonia platform — the trap that took out nine settings tests in
    /// 0.35.</summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Cursor ??= new Cursor(StandardCursorType.SizeNorthSouth);
        ApplyUnitState();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        // A detached knob has no business holding a platform cursor.
        Cursor = null;
    }

    // ── Typed entry ──────────────────────────────────────────────────────────

    /// <summary>True while the inline value editor is open.</summary>
    public bool IsEditing => _editing;

    /// <summary>Opens the inline editor over the readout with the current
    /// readout text selected. Enter commits, Escape cancels, focus leaving
    /// commits (the design's number field).</summary>
    public void BeginEdit()
    {
        if (!IsEffectivelyEnabled || _editing) return;
        _editor ??= BuildEditor();
        _editing = true;
        _editor.Text = KnobMath.DisplayValue(Value, Minimum, Maximum, TenScale)
            .ToString("0.###", System.Globalization.CultureInfo.CurrentCulture);
        _editor.IsVisible = true;
        _readout.IsVisible = false;
        InvalidateMeasure();
        _editor.Focus();
        _editor.SelectAll();
    }

    /// <summary>Reads the typed text in display space and commits it through the
    /// same clamp+snap every other gesture uses. Unparseable text changes nothing.
    /// Returns whether a value was applied.</summary>
    public bool CommitEdit()
    {
        if (!_editing) return false;
        var text = _editor?.Text;
        EndEdit();
        if (!KnobMath.TryParseEntry(text, Minimum, Maximum, TenScale, out var value)) return false;
        Commit(value);
        return true;
    }

    public void CancelEdit()
    {
        if (!_editing) return;
        EndEdit();
    }

    /// <summary>Test seam: the text in the open editor.</summary>
    internal string? EditorText
    {
        get => _editor?.Text; set => _editor?.Text = value;
    }

    private void EndEdit()
    {
        _editing = false;
        _editor?.IsVisible = false;
        _readout.IsVisible = true;
        Focus();
    }

    private TextBox BuildEditor()
    {
        var box = new TextBox
        {
            IsVisible = false,
            MinHeight = EditorHeight,
            Height = EditorHeight,
            Padding = new Thickness(4, 2),
            TextAlignment = TextAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        box.Bind(TextBox.FontFamilyProperty, new DynamicResourceExtension("FireFontMono"));
        box.Bind(TextBox.FontSizeProperty, new DynamicResourceExtension("FireSizeSm"));
        AutomationProperties.SetName(box, Label);
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { CommitEdit(); e.Handled = true; }
            else if (e.Key == Key.Escape) { CancelEdit(); e.Handled = true; }
        };
        box.LostFocus += (_, _) => { if (_editing) CommitEdit(); };
        VisualChildren.Add(box);
        LogicalChildren.Add(box);
        return box;
    }

    // ── State ────────────────────────────────────────────────────────────────

    /// <summary>Every write to <see cref="Value"/> goes through here: clamped,
    /// then snapped onto the parameter's step grid, so no gesture can leave the
    /// knob showing a value the DSP will not run. <c>internal</c> so the
    /// automation peer's <c>SetValue</c> lands on the SAME seam as the drag and
    /// the keyboard rather than writing the property raw.</summary>
    internal void Commit(double value)
        => SetCurrentValue(ValueProperty,
            KnobMath.Snap(Math.Clamp(value, Minimum, Maximum), Minimum, Maximum, Step));

    private void SetHover(bool hover)
    {
        if (_hover == hover) return;
        _hover = hover;
        Repaint();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        var p = change.Property;

        if (p == LabelProperty || p == ValueTextProperty || p == ValueProperty ||
            p == MinimumProperty || p == MaximumProperty)
        {
            SyncText();
        }

        if (p == LabelProperty) SyncAutomationName();

        if (p == ValueProperty && change.OldValue is double old && change.NewValue is double now)
        {
            // Only when something is actually listening: FromElement returns the
            // peer if one was created, and creating one here would build an
            // automation tree for every knob on a board nobody is inspecting.
            (ControlAutomationPeer.FromElement(this) as FireKnobAutomationPeer)
                ?.RaiseValueChanged(old, now);
        }

        if (p == FireUnitState.IsBypassedProperty || p == IsEffectivelyEnabledProperty)
            ApplyUnitState();

        // IsFocused is here rather than in an OnGotFocus/OnLostFocus override
        // because the focus-args type is not stable across Avalonia majors and
        // the property is: the focus ring must be VISIBLE, and this is the one
        // signal that says whether to draw it.
        if (p == ValueProperty || p == MinimumProperty || p == MaximumProperty || p == TaperProperty ||
            p == IsCompactProperty || p == HardwareStyleProperty || p == FireUnitState.CategoryProperty ||
            p == FireUnitState.AccentProperty ||
            p == FireUnitState.IsBypassedProperty || p == IsEffectivelyEnabledProperty ||
            p == IsFocusedProperty)
        {
            Repaint();
        }

        if (p == HardwareStyleProperty)
        {
            // The design's face caption is near-white (#eee6f0), not the muted
            // ink: on a coloured enclosure the muted violet sank into the paint.
            _label.Bind(TextBlock.ForegroundProperty,
                new DynamicResourceExtension(HardwareStyle ? "FireTextPrimary" : "FireTextMuted"));
        }
        if (p == HardwareStyleProperty)
        {
            _label.FontSize = HardwareStyle ? 12 : FireKnobGeometry.LabelFontSize;
            _label.Bind(TextBlock.FontFamilyProperty, new DynamicResourceExtension(HardwareStyle ? "FireFontUi" : "FireFontMono"));
            SyncText();
            _label.LineHeight = HardwareStyle ? 16 : FireKnobGeometry.LabelLineHeight;
            _readout.FontSize = HardwareStyle ? 14 : FireKnobGeometry.ValueFontSize;
        }
        if (p == UnitProperty || p == DenseProperty) SyncText();
        if (p == DialSizeProperty || p == CapFileProperty || p == StepAnglesProperty) Repaint();
        if (p == CapFileProperty) ApplyUnitState();
        if (p == IsCompactProperty || p == HardwareStyleProperty || p == DenseProperty || p == DialSizeProperty || p == UnitProperty)
            InvalidateMeasure();
    }

    /// <summary>Bypass and disabled are applied in ONE place, to EVERYTHING the
    /// control shows — the drawn dial through <see cref="FireUnitState.AccentOf"/>
    /// in <see cref="Render"/>, the two text lines through this opacity. A caller
    /// sets one inherited property; there is no second switch to forget.</summary>
    /// <para>A faceplate knob (<see cref="CapFile" />) is the exception: it sits on a
    /// printed chrome nut, and at partial opacity the nut shone through the cap and
    /// the knob read as GONE. It stays opaque and its cap is
    /// shaded in <c>DrawCapSprite</c> instead.</para>
    private void ApplyUnitState() => Opacity = CapFile is { Length: > 0 } ? 1.0 : FireUnitState.OpacityFor(this);

    private void SyncText()
    {
        _label.Text = HardwareStyle ? Label : Label?.ToUpperInvariant();
        _readout.Text = ValueText ?? DspControlText.FormatValue(Value);
        _unit.Text = HardwareStyle && !Dense ? Unit : null;
    }

    /// <summary>The unit as rendered beside the plate — the test seam.</summary>
    internal string UnitText => _unit.Text ?? string.Empty;

    /// <summary>The accessible name follows the caption, in the caption's OWN
    /// case — the dial uppercases for the look, and "LOW CUT" read aloud is not
    /// the improvement it looks like.</summary>
    private void SyncAutomationName()
        => AutomationProperties.SetName(this, string.IsNullOrWhiteSpace(Label) ? null : Label);

    protected override AutomationPeer OnCreateAutomationPeer() => new FireKnobAutomationPeer(this);

    private static TextBlock MonoLine(double size, double tracking, string colorKey, bool wrap)
    {
        var block = new TextBlock
        {
            FontSize = size,
            LetterSpacing = tracking,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        // DynamicResource, never a literal: the font and both text colours are
        // Fire tokens and must follow the dictionary.
        block.Bind(TextBlock.FontFamilyProperty, new DynamicResourceExtension("FireFontMono"));
        block.Bind(TextBlock.ForegroundProperty, new DynamicResourceExtension(colorKey));
        return block;
    }
}
