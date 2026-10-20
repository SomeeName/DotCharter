using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace DotCharter.Tone.UI.Converters;

/// <summary>
///     Turns a control's <see cref="Visual.Bounds" /> into the CHAMFERED
///     silhouette of the Fire primary action: square top-left and bottom-right,
///     with a 45° cut taken off the TOP-RIGHT and BOTTOM-LEFT corners.
///     <para>
///         That notch is a brand signature, not a rounding — the mockups draw it
///         with <c>clip-path: polygon(...)</c> cutting 12–18 px off those two
///         corners. Avalonia has no chamfered <c>CornerRadius</c>, and the first
///         port approximated it by ROUNDING the same two corners hard
///         (<c>FireRadiusCut = 0,16,0,16</c>). That reads as a lozenge, not a
///         cut, so the silhouette is produced here instead and handed to
///         <see cref="Visual.Clip" />.
///     </para>
///     <para>
///         <b>Why a converter and not a static geometry.</b> A
///         <c>StreamGeometry</c> resource is fixed at parse time and a
///         <c>Path</c> with <c>Stretch="Fill"</c> would scale the cut with the
///         button — a 16 px notch would become 40 px on a wide CTA and the two
///         corners would stop being 45°. Binding the geometry to the live bounds
///         is the only way to keep the cut a CONSTANT number of pixels at every
///         button size.
///     </para>
///     <para>
///         <b>Coordinate space.</b> <see cref="Visual.Clip" /> is evaluated in the
///         clipped element's OWN space, while <c>Bounds</c> is relative to its
///         parent. Only <see cref="Rect.Width" /> / <see cref="Rect.Height" /> are
///         read, so a chamfered element that is not at its parent's origin still
///         clips correctly.
///     </para>
///     <para>
///         The clip cuts the element's own rendering, so a lit action must keep
///         its <c>BoxShadow</c> on an UNCLIPPED ancestor or the glow is clipped
///         away with the corners.
///     </para>
/// </summary>
public sealed class FireChamferClipConverter : IValueConverter, IMultiValueConverter
{
    /// <summary>The mockups' cut, in DIPs. The spec range is 12–18 px.</summary>
    public const double DefaultCut = 16;

    public static readonly FireChamferClipConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not Rect bounds) return null;
        return Chamfer(bounds.Width, bounds.Height, ParseCut(parameter));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Avalonia.Data.BindingOperations.DoNothing;

    /// <summary>
    ///     The same silhouette from a MULTI binding — bounds first, then the cut
    ///     in DIPs. A converter parameter is a plain CLR value, so
    ///     <c>{DynamicResource FireCutSize}</c> cannot reach it; the control
    ///     theme puts the token on the control
    ///     (<c>Controls/Fire/FireShape.CutSize</c>) and the clip binds both,
    ///     which is what lets a THEME change the cut. A missing or unset second
    ///     value falls back to <see cref="DefaultCut" />, so the shape is never
    ///     lost to an unresolved token.
    /// </summary>
    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count == 0 || values[0] is not Rect bounds) return null;
        return Chamfer(bounds.Width, bounds.Height, CutFrom(values));
    }

    /// <summary>
    ///     The cut a multi binding carries: its SECOND value, or
    ///     <see cref="DefaultCut" /> when that is missing or unset. Pure, so the
    ///     default gate can assert it — <see cref="Chamfer" /> builds a
    ///     <see cref="StreamGeometry" />, which needs a render platform.
    /// </summary>
    public static double CutFrom(IList<object?> values) =>
        values.Count > 1 ? ParseCut(values[1]) : DefaultCut;

    /// <summary>
    ///     The silhouette's CORNERS, clockwise from the top-left — pure maths,
    ///     with no Avalonia platform behind it, so the shape can be asserted in
    ///     the default gate. (Building a <see cref="StreamGeometry" /> resolves
    ///     <c>IPlatformRenderInterface</c> and throws without one, which is why
    ///     the geometry and the numbers are separate methods.)
    ///     <para>
    ///         A degenerate box — zero width or height, NaN, or one shorter than
    ///         twice the cut — yields a plain RECTANGLE rather than a crossed-over
    ///         polygon. An un-laid-out control must not render as a bow tie.
    ///     </para>
    /// </summary>
    public static IReadOnlyList<Point> ChamferPoints(double width, double height, double cut = DefaultCut)
    {
        if (double.IsNaN(width) || double.IsNaN(height) || width <= 0 || height <= 0)
            return Corners(Math.Max(NonNaN(width), 0), Math.Max(NonNaN(height), 0));

        cut = Math.Min(cut, Math.Min(width, height) / 2);
        if (cut <= 0) return Corners(width, height);

        return
        [
            new Point(0, 0),
            new Point(width - cut, 0),      // top edge, stopping short
            new Point(width, cut),          // the top-right cut
            new Point(width, height),       // right edge
            new Point(cut, height),         // bottom edge, stopping short
            new Point(0, height - cut)      // the bottom-left cut
        ];
    }

    /// <summary>The silhouette as a closed, filled geometry. Needs a platform.</summary>
    public static StreamGeometry Chamfer(double width, double height, double cut = DefaultCut)
    {
        var points = ChamferPoints(width, height, cut);

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(points[0], isFilled: true);
            for (var i = 1; i < points.Count; i++) ctx.LineTo(points[i]);
            ctx.EndFigure(isClosed: true);
        }

        return geometry;
    }

    private static Point[] Corners(double width, double height) =>
    [
        new(0, 0), new(width, 0), new(width, height), new(0, height)
    ];

    private static double NonNaN(double v) => double.IsNaN(v) ? 0 : v;

    // Zero is a real answer, not "unset": FireShape.CutSize already defaults to
    // DefaultCut, so a 0 arrives only from a theme that wants a square action
    // (Graphite does). Only a missing, negative or non-numeric cut falls back.
    private static double ParseCut(object? parameter) => parameter switch
    {
        double d when d >= 0 => d,
        string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var p) && p >= 0 => p,
        _ => DefaultCut
    };
}
