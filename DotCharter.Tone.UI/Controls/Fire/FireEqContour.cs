using System.Collections;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using DotCharter.Tone.UI.ViewModels.PlayMode;

namespace DotCharter.Tone.UI.Controls.Fire;

/// <summary>
///     The graphic equaliser's gain contour: a polyline through the LIVE band
///     params, over three hairline grid rules.
///
///     <para>
///         It draws the same <see cref="PedalParamViewModel" /> instances the
///         faders write, and subscribes to each one, so dragging a fader moves
///         the curve. A converter over the list could not: the list never
///         changes, only the values inside it do, and a contour frozen at its
///         opening shape is a picture that contradicts the controls under it.
///     </para>
///     <para>
///         Decorative and never a hit target — the faders are the controls.
///     </para>
/// </summary>
public sealed class FireEqContour : Control
{
    public static readonly StyledProperty<IEnumerable?> BandsProperty =
        AvaloniaProperty.Register<FireEqContour, IEnumerable?>(nameof(Bands));

    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<FireEqContour, IBrush?>(nameof(Stroke));

    public static readonly StyledProperty<IBrush?> GridBrushProperty =
        AvaloniaProperty.Register<FireEqContour, IBrush?>(nameof(GridBrush));

    private readonly List<PedalParamViewModel> _watched = [];
    private IPen? _pen;
    private IBrush? _penFor;
    private IPen? _rule;
    private IBrush? _ruleFor;
    private bool _attached;

    static FireEqContour()
    {
        IsHitTestVisibleProperty.OverrideDefaultValue<FireEqContour>(false);
        AffectsRender<FireEqContour>(BandsProperty, StrokeProperty, GridBrushProperty);
    }

    /// <summary>The band params, in display order.</summary>
    public IEnumerable? Bands { get => GetValue(BandsProperty); set => SetValue(BandsProperty, value); }

    public IBrush? Stroke { get => GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public IBrush? GridBrush { get => GetValue(GridBrushProperty); set => SetValue(GridBrushProperty, value); }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        Watch();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        Unwatch();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BandsProperty && _attached) Watch();
    }

    private void Watch()
    {
        Unwatch();
        if (Bands is null) return;
        foreach (var band in Bands.OfType<PedalParamViewModel>())
        {
            band.PropertyChanged += OnBandChanged;
            _watched.Add(band);
        }
    }

    private void Unwatch()
    {
        foreach (var band in _watched) band.PropertyChanged -= OnBandChanged;
        _watched.Clear();
    }

    private void OnBandChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PedalParamViewModel.Value)) InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (Bounds.Width < 8 || Bounds.Height < 8) return;

        var bands = Bands?.OfType<PedalParamViewModel>().ToArray() ?? [];
        if (GridBrush is { } grid)
        {
            if (!ReferenceEquals(_ruleFor, grid) || _rule is null)
            {
                _ruleFor = grid;
                _rule = new Pen(grid, 1);
            }

            var rule = _rule;
            for (var i = 0; i <= 2; i++)
            {
                var y = Math.Round(Bounds.Height * (0.15 + 0.35 * i)) + 0.5;
                context.DrawLine(rule, new Point(4, y), new Point(Bounds.Width - 4, y));
            }
        }

        if (bands.Length < 2 || Stroke is null) return;

        var points = new Point[bands.Length];
        var step = (Bounds.Width - 24) / (bands.Length - 1);
        for (var i = 0; i < bands.Length; i++)
        {
            var band = bands[i];
            var span = band.Maximum - band.Minimum;
            var fraction = span > 0 ? (band.Value - band.Minimum) / span : 0.5;
            points[i] = new Point(12 + step * i, Bounds.Height * (0.85 - 0.7 * fraction));
        }

        var pen = ContourPen();
        for (var i = 1; i < points.Length; i++) context.DrawLine(pen, points[i - 1], points[i]);
        foreach (var point in points)
            context.DrawEllipse(Stroke, null, point, 3, 3);
    }

    private IPen ContourPen()
    {
        if (!ReferenceEquals(_penFor, Stroke) || _pen is null)
        {
            _penFor = Stroke;
            _pen = new Pen(Stroke!, 2, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        }

        return _pen;
    }
}
