using System.ComponentModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using DotCharter.Tone.UI.Controls.Dsp;
using DotCharter.Tone.UI.ViewModels.PlayMode;

namespace DotCharter.Tone.UI.Controls.Fire;

/// <summary>
///     A unit drawn as its generated FACEPLATE with the live controls on it: every
///     knob, selector, toggle and model window stands on the nut the art printed
///     for it (<see cref="RigFaceplates" />), so the knob in the picture is the knob
///     the player turns.
///
///     <para>
///         <b>Interactive</b> (the module editor): each control is a real
///         <see cref="FireKnob" /> in cap-sprite mode — drag, Shift for fine, wheel,
///         keys, double- or right-click to reset — or a toggle / window of its own,
///         two-way bound to the node's <see cref="PedalParamViewModel" />.
///         <b>Not interactive</b> (a board tile, the chain preview): nothing is a
///         child; the face and every cap are drawn in one pass, each cap turned to
///         the live value, so a chain of ten pedals is ten draws rather than seventy
///         controls, and the tile's own click and drag still land on the tile.
///     </para>
///     <para>
///         A module with no faceplate draws nothing and measures to zero;
///         <see cref="HasFaceplate" /> says which, so the host can keep its drawn
///         enclosure for it.
///     </para>
/// </summary>
public sealed class RigFaceplateView : Panel
{
    public static readonly StyledProperty<PedalNodeViewModel?> NodeProperty =
        AvaloniaProperty.Register<RigFaceplateView, PedalNodeViewModel?>(nameof(Node));

    public static readonly StyledProperty<bool> IsInteractiveProperty =
        AvaloniaProperty.Register<RigFaceplateView, bool>(nameof(IsInteractive), true);

    /// <summary>A module to preview with no live unit (the gear library): its face,
    /// each knob at the catalogue default. Ignored while <see cref="Node" /> is set.</summary>
    public static readonly StyledProperty<string?> ModuleTypeIdProperty =
        AvaloniaProperty.Register<RigFaceplateView, string?>(nameof(ModuleTypeId));

    /// <summary>Decode the face at this width (0 = full). A board tile sets it.</summary>
    public static readonly StyledProperty<int> DecodeWidthProperty =
        AvaloniaProperty.Register<RigFaceplateView, int>(nameof(DecodeWidth));

    private RigFaceplate? _face;
    private readonly List<(RigFaceControl Control, PedalParamViewModel Param)> _bound = [];
    private readonly FaceSurface _surface;

    public RigFaceplateView() => _surface = new FaceSurface(this);

    static RigFaceplateView() => AffectsMeasure<RigFaceplateView>(NodeProperty, ModuleTypeIdProperty);

    public PedalNodeViewModel? Node { get => GetValue(NodeProperty); set => SetValue(NodeProperty, value); }
    public string? ModuleTypeId { get => GetValue(ModuleTypeIdProperty); set => SetValue(ModuleTypeIdProperty, value); }

    /// <summary>The module drawn: the node's, else <see cref="ModuleTypeId" />.</summary>
    private string? DrawnModule => Node?.ModuleTypeId ?? ModuleTypeId;
    public bool IsInteractive { get => GetValue(IsInteractiveProperty); set => SetValue(IsInteractiveProperty, value); }
    public int DecodeWidth { get => GetValue(DecodeWidthProperty); set => SetValue(DecodeWidthProperty, value); }

    /// <summary>True when the node's module has a faceplate.</summary>
    public bool HasFaceplate => _face is not null;

    /// <summary>The tallest a face asks to be at scale 1, in DIPs.</summary>
    public const double NaturalHeightCap = 700;

    /// <summary>The controls placed on the face, for tests: param id → the control.</summary>
    internal IReadOnlyList<(RigFaceControl Control, PedalParamViewModel Param)> Placed => _bound;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsInteractiveProperty) IsHitTestVisible = IsInteractive; // a preview never takes the tile's click
        // bypass dims the face (FireUnitState, inherited): repaint it when that flips,
        // or a bypassed face keeps its lit look until something else redraws it
        if (change.Property == FireUnitState.IsBypassedProperty) _surface.InvalidateVisual();
        if (change.Property == NodeProperty || change.Property == IsInteractiveProperty
            || change.Property == ModuleTypeIdProperty)
        {
            Rebuild();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Subscribe();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Unsubscribe();
    }

    // ---- building ------------------------------------------------------------------

    private void Rebuild()
    {
        Unsubscribe();
        Children.Clear();
        _bound.Clear();
        var node = Node;
        _face = RigFaceplates.For(DrawnModule);
        Children.Add(_surface); // the face, or the illustration of a module that has none
        if (_face is null || node is null) return;

        foreach (var control in _face.Controls)
        {
            var param = node.Params.FirstOrDefault(p => p.Id == control.Param);
            if (param is null) continue; // a transport or file control: the editor draws it elsewhere
            _bound.Add((control, param));
            if (IsInteractive) Children.Add(CreateControl(_face, control, param));
        }

        // A floor unit's printed footswitch IS its bypass switch.
        if (IsInteractive && _face.Footswitch is { } foot) Children.Add(CreateFootswitch(foot, node));

        if (VisualRoot is not null) Subscribe();
    }

    private static Control CreateControl(RigFaceplate face, RigFaceControl control, PedalParamViewModel param)
    {
        Control child = control.Kind switch
        {
            RigFaceControlKind.Toggle => new RigFaceToggle { Param = param },
            RigFaceControlKind.Window => CreateWindow(param),
            _ => CreateKnob(face, control, param)
        };
        child.Tag = control;
        AutomationProperties.SetName(child, param.DisplayName);
        InstantTip(child);
        child.Bind(IsEnabledProperty, new Binding(nameof(PedalParamViewModel.IsControlActive)) { Source = param });
        return child;
    }

    private static FireKnob CreateKnob(RigFaceplate face, RigFaceControl control, PedalParamViewModel param)
    {
        var knob = new FireKnob
        {
            Dense = true,
            CapFile = control.Kind == RigFaceControlKind.Selector ? RigFaceplates.SelectorCap : face.KnobCapUri,
            Minimum = param.Minimum,
            Maximum = param.Maximum,
            DefaultValue = param.Default,
            Step = control.Kind == RigFaceControlKind.Selector ? Math.Max(1, param.Step) : param.Step,
            Taper = param.Taper,
            StepAngles = control.Angles,
            Label = param.Caption
        };
        knob.Bind(FireKnob.ValueProperty, new Binding(nameof(PedalParamViewModel.Value)) { Source = param, Mode = BindingMode.TwoWay });
        knob.Bind(FireKnob.ValueTextProperty, new Binding(nameof(PedalParamViewModel.DisplayText)) { Source = param });
        // The face prints the label and the scale; the exact value is a hover away.
        knob.Bind(ToolTip.TipProperty, new Binding(nameof(PedalParamViewModel.Value))
        {
            Source = param,
            Converter = new FuncValueConverter<float, string>(_ => $"{param.Caption}: {ValueLabel(param)}")
        });
        return knob;
    }

    private static ToggleButton CreateFootswitch(RigFaceSpot foot, PedalNodeViewModel node)
    {
        var name = LocalizationService.Instance["Rig_BypassUnit"];
        // Stretch: the theme aligns a toggle left/centre, which shrank the hit area to its border
        var toggle = new ToggleButton
        {
            Tag = foot,
            Cursor = new Cursor(StandardCursorType.Hand),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        toggle[!StyledElement.ThemeProperty] = new DynamicResourceExtension("FireFaceFootswitchToggle");
        toggle.Classes.Add("FireFaceFootswitch");
        toggle.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(PedalNodeViewModel.Bypassed)) { Source = node, Mode = BindingMode.TwoWay });
        ToolTip.SetTip(toggle, name);
        AutomationProperties.SetName(toggle, name);
        InstantTip(toggle);
        return toggle;
    }

    private static Button CreateWindow(PedalParamViewModel param)
    {
        var text = new TextBlock
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontWeight = FontWeight.SemiBold
        };
        text.Bind(TextBlock.TextProperty, new Binding(nameof(PedalParamViewModel.Value))
        {
            Source = param,
            Converter = new FuncValueConverter<float, string>(_ => ValueLabel(param))
        });
        text[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("FireTextPrimary");
        text[!TextBlock.FontSizeProperty] = new DynamicResourceExtension("FontSizeSm");

        var menu = new MenuFlyout();
        var labels = param.ValueLabels ?? [];
        for (var i = 0; i < labels.Count; i++)
        {
            var value = param.Minimum + i;
            var item = new MenuItem { Header = labels[i] };
            item.Click += (_, _) => param.Value = value;
            menu.Items.Add(item);
        }

        var button = new Button
        {
            Content = text,
            Flyout = menu,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
        // A family class is styled only inside its ControlTheme: the class
        // without the theme is a silent no-op.
        button[!StyledElement.ThemeProperty] = new DynamicResourceExtension("FireQuietButton");
        button.Classes.Add("FireQuiet");
        button.Classes.Add("FireFaceWindow");
        button.Bind(ToolTip.TipProperty, new Binding(nameof(PedalParamViewModel.Value))
        {
            Source = param,
            Converter = new FuncValueConverter<float, string>(_ => $"{param.Caption}: {ValueLabel(param)}")
        });
        return button;
    }

    /// <summary>
    ///     A face control's tooltip ("Gain: 6.2") opens AT ONCE: the printed scales on
    ///     the art are small, and reading the exact value is why the player hovers.
    ///     It follows the value live while the control turns.
    /// </summary>
    internal static void InstantTip(Control control) => ToolTip.SetShowDelay(control, 0);

    /// <summary>"Tube 300" for a labelled param, the formatted number otherwise.</summary>
    internal static string ValueLabel(PedalParamViewModel param)
    {
        if (param.ValueLabels is { Count: > 0 } labels)
        {
            var index = (int)Math.Round(param.Value - param.Minimum);
            return labels[Math.Clamp(index, 0, labels.Count - 1)];
        }

        return param.DisplayText;
    }

    // ---- live values on a non-interactive face ---------------------------------------

    private bool _subscribed;

    private void Subscribe()
    {
        if (_subscribed || IsInteractive) return;
        foreach (var (_, param) in _bound) param.PropertyChanged += OnParamChanged;
        _subscribed = true;
    }

    private void Unsubscribe()
    {
        if (!_subscribed) return;
        foreach (var (_, param) in _bound) param.PropertyChanged -= OnParamChanged;
        _subscribed = false;
    }

    private void OnParamChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PedalParamViewModel.Value)) _surface.InvalidateVisual();
    }

    // ---- layout ----------------------------------------------------------------------

    /// <summary>Width over height of what is drawn: the face, or the fallback illustration.</summary>
    private double Aspect
    {
        get
        {
            if (_face is not null) return _face.AspectRatio;
            var art = DrawnModule is { } id ? RigArtworkStore.Get(RigArtwork.IllustrationFor(id)) : null;
            return art is { Size.Height: > 0 } ? art.Size.Width / art.Size.Height : 1;
        }
    }

    /// <summary>The face rectangle inside <paramref name="size" />: uniform fit, centred.</summary>
    internal Rect FaceRect(Size size)
    {
        if (DrawnModule is null || size.Width <= 0 || size.Height <= 0) return default;
        var aspect = Aspect;
        var width = Math.Min(size.Width, size.Height * aspect);
        var height = width / aspect;
        return new Rect((size.Width - width) / 2, (size.Height - height) / 2, width, height);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (DrawnModule is null) return default;
        var aspect = Aspect;
        // The NATURAL size, never more: a host that scales to fit (the editor's
        // RigScaleToFit) measures twice, the second time with its scaled-up width, and
        // a face that grew to fill that came out three times the dock. A tall face is
        // capped so a stompbox fits an ordinary dock at a legible scale.
        double natural = _face?.PixelWidth ?? 160;
        if (_face is not null && _face.PixelHeight > NaturalHeightCap) natural = NaturalHeightCap * aspect;
        var width = natural;
        if (!double.IsInfinity(availableSize.Width)) width = Math.Min(width, availableSize.Width);
        if (!double.IsInfinity(availableSize.Height)) width = Math.Min(width, availableSize.Height * aspect);
        var size = new Size(width, width / aspect);
        foreach (var child in Children)
            child.Measure(ReferenceEquals(child, _surface) ? size : ControlSize(child, size.Width));
        return size;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var face = FaceRect(finalSize);
        _surface.Arrange(face);
        foreach (var child in Children)
        {
            var (x, y) = child.Tag switch
            {
                RigFaceControl c => (c.X, c.Y),
                RigFaceSpot spot => (spot.X, spot.Y),
                _ => (double.NaN, double.NaN)
            };
            if (double.IsNaN(x)) continue;
            var box = ControlSize(child, face.Width);
            child.Measure(box);
            var at = new Point(face.X + x * face.Width, face.Y + y * face.Height);
            child.Arrange(new Rect(at.X - box.Width / 2, at.Y - box.Height / 2, box.Width, box.Height));
        }

        return finalSize;
    }

    private Size ControlSize(Control child, double faceWidth)
    {
        if (_face is null) return default;
        if (child.Tag is RigFaceSpot foot)
        {
            // generous: the collar and a margin around it, so a quick stomp lands
            var side = Math.Max(20, foot.Diameter * 1.25 * faceWidth);
            return new Size(side, side);
        }

        if (child.Tag is not RigFaceControl control) return default;
        switch (child)
        {
            case FireKnob knob:
                var dial = Math.Max(12, _face.KnobCapDiameter * faceWidth);
                knob.DialSize = dial;
                return new Size(dial + 2 * FireKnobGeometry.ArcOutset, dial + 2 * FireKnobGeometry.ArcOutset);
            case RigFaceToggle:
                var toggle = Math.Max(14, control.Diameter * 1.5 * faceWidth);
                return new Size(toggle, toggle);
            default: // a model window: as wide as the one printed, a line of text tall
                var w = Math.Max(40, control.Diameter * faceWidth);
                return new Size(w, Math.Max(18, w * 0.32));
        }
    }

    // ---- drawing ---------------------------------------------------------------------

    /// <summary>The face picture and, on a preview, every cap — drawn by the surface
    /// child (a <see cref="Panel" /> cannot render itself).</summary>
    internal void Draw(DrawingContext context, Size size) => DrawFace(context, size);

    private void DrawFace(DrawingContext context, Size size)
    {
        var rect = new Rect(size);
        if (rect.Width < 1) return;
        if (_face is null)
        {
            // No faceplate for this module (yet): its illustration, as before.
            if (DrawnModule is { } id && RigArtworkStore.GetForRender(RigArtwork.IllustrationFor(id), _surface) is { } art)
                context.DrawImage(art, new Rect(art.Size), rect);
            return;
        }

        var bitmap = RigArtworkStore.GetForRender(_face.Uri, DecodeWidth, _surface);
        if (bitmap is null) return;

        // Bypassed, the FACE dims; the caps drawn over it below stay solid and get a
        // dark shade instead, or a see-through cap shows the chrome nut under it and
        // reads as a missing knob.
        var inactive = FireUnitState.IsInactive(_surface);
        using (context.PushOpacity(inactive ? FireUnitState.BypassedOpacity : 1.0))
            context.DrawImage(bitmap, new Rect(bitmap.Size), rect);
        if (IsInteractive) return;
        var shade = new SolidColorBrush(FireUnitState.At(FireUnitState.ColorFrom(_surface, "FireHardwareShadow"), 1 - FireUnitState.BypassedOpacity));

        // The preview: every cap turned to its live value (or, with no unit, to the
        // catalogue default), drawn in this one pass.
        var capDiameter = _face.KnobCapDiameter * rect.Width;
        var capDecode = capDiameter <= 48 ? 96 : 192;
        var values = Node is null
            ? _face.Controls.Select(c => (Control: c, Value: c.Default, c.Min, c.Max, Taper: KnobMath.TaperFor(c.Param, (float)c.Min, (float)c.Max, 0)))
            : _bound.Select(b => (b.Control, Value: (double)b.Param.Value, Min: (double)b.Param.Minimum, Max: (double)b.Param.Maximum, b.Param.Taper));
        foreach (var (control, value, min, max, taper) in values)
        {
            var at = new Point(rect.X + control.X * rect.Width, rect.Y + control.Y * rect.Height);
            switch (control.Kind)
            {
                case RigFaceControlKind.Toggle:
                    var toggleSize = control.Diameter * 1.5 * rect.Width;
                    var toggle = RigArtworkStore.GetForRender(value > min ? RigFaceplates.ToggleUp : RigFaceplates.ToggleDown, 96, _surface);
                    if (toggle is not null)
                        context.DrawImage(toggle, new Rect(toggle.Size), new Rect(at.X - toggleSize / 2, at.Y - toggleSize / 2, toggleSize, toggleSize));
                    break;
                case RigFaceControlKind.Window:
                    break; // the window stays as printed: a preview has no room for its words
                default:
                    var capUri = control.Kind == RigFaceControlKind.Selector ? RigFaceplates.SelectorCap : _face.KnobCapUri;
                    var cap = RigArtworkStore.GetForRender(capUri, capDecode, _surface);
                    if (cap is null) break;
                    var angle = (control.Angles is { Count: > 0 } printed
                        ? KnobMath.AngleForStep(value, min, printed)
                        : KnobMath.AngleDegreesForValue(value, min, max, taper)) * Math.PI / 180.0;
                    var turn = Matrix.CreateTranslation(-at.X, -at.Y) * Matrix.CreateRotation(angle) * Matrix.CreateTranslation(at.X, at.Y);
                    using (context.PushTransform(turn))
                    {
                        context.DrawImage(cap, new Rect(cap.Size),
                            new Rect(at.X - capDiameter / 2, at.Y - capDiameter / 2, capDiameter, capDiameter));
                    }

                    if (inactive) context.DrawEllipse(shade, null, at, capDiameter * 0.49, capDiameter * 0.49);
                    break;
            }
        }
    }
}

/// <summary>The drawing surface under a faceplate's controls: the picture, and on a
/// preview every cap. Never a hit target — the controls (or the tile) take the input.</summary>
internal sealed class FaceSurface : Control
{
    private readonly RigFaceplateView _owner;

    public FaceSurface(RigFaceplateView owner)
    {
        _owner = owner;
        IsHitTestVisible = false;
        // The face is drawn scaled (down on the chain, up on the stage); the default
        // filter blurred its printed scales and labels.
        RenderOptions.SetBitmapInterpolationMode(this, Avalonia.Media.Imaging.BitmapInterpolationMode.HighQuality);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        _owner.Draw(context, Bounds.Size);
    }
}

/// <summary>
///     A bat toggle on a faceplate: up is the param's upper value, down its lower;
///     a click (or Space / Enter) flips it. Drawn from the shared toggle sprites.
/// </summary>
public sealed class RigFaceToggle : Control
{
    public static readonly StyledProperty<PedalParamViewModel?> ParamProperty =
        AvaloniaProperty.Register<RigFaceToggle, PedalParamViewModel?>(nameof(Param));

    private bool _hover;

    static RigFaceToggle()
    {
        FocusableProperty.OverrideDefaultValue<RigFaceToggle>(true);
        AffectsRender<RigFaceToggle>(ParamProperty);
    }

    public PedalParamViewModel? Param { get => GetValue(ParamProperty); set => SetValue(ParamProperty, value); }

    /// <summary>True when the toggle is up (the param at its upper value).</summary>
    public bool IsUp => Param is { } p && p.Value > p.Minimum;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != ParamProperty) return;
        if (change.OldValue is PedalParamViewModel old) old.PropertyChanged -= OnParamChanged;
        if (change.NewValue is PedalParamViewModel now && VisualRoot is not null) now.PropertyChanged += OnParamChanged;
        UpdateTip();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (Param is { } p) p.PropertyChanged += OnParamChanged;
        UpdateTip();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (Param is { } p) p.PropertyChanged -= OnParamChanged;
    }

    private void OnParamChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PedalParamViewModel.Value)) return;
        UpdateTip();
        InvalidateVisual();
    }

    private void UpdateTip()
    {
        if (Param is { } p) ToolTip.SetTip(this, $"{p.Caption}: {RigFaceplateView.ValueLabel(p)}");
    }

    /// <summary>Flips the toggle.</summary>
    public void Flip()
    {
        if (Param is not { } p || !IsEffectivelyEnabled) return;
        p.Value = IsUp ? p.Minimum : p.Maximum;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        Flip();
        Focus(NavigationMethod.Pointer);
        e.Handled = true; // a toggle click is not a board drag
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || e.Key is not (Key.Space or Key.Enter)) return;
        Flip();
        e.Handled = true;
    }

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        _hover = true;
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hover = false;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var sprite = RigArtworkStore.GetForRender(IsUp ? RigFaceplates.ToggleUp : RigFaceplates.ToggleDown, 128, this);
        var box = new Rect(Bounds.Size);
        if (sprite is not null) context.DrawImage(sprite, new Rect(sprite.Size), box);
        if ((_hover || IsFocused) && IsEffectivelyEnabled)
        {
            context.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(0x90, 0xff, 0xff, 0xff)), 1),
                box.Center, box.Width / 2, box.Height / 2);
        }
    }
}
