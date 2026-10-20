using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using DotCharter.UI.Controls.Themes;

namespace DotCharter.UI.Controls;

/// <summary>
/// A read-only chord diagram the way a chord chart prints one: strings down, lowest on
///     the left, the nut on top, <c>o</c> / <c>x</c> above it, one dot per fretted string with its
///     finger number inside, and only the first fret named ("8fr") when the shape sits up the neck.
///     <para>
///         The Chord shapes list and its recent strip draw every shape with it. It replaced
///         <see cref="ChordBoxControl" /> there: that control is the inspector's EDITING fretboard
///         — fixed 40 px rail, a fret number under every fret, 8.5 px dots — and at a list's
///         thumbnail size its fret numbers ran together ("56789"), its dots overlapped and its
///         mask pips read as a stack of grey dots. Every size here comes from
///         <see cref="ChordDiagramLayout" />, so a dot never touches its neighbour at any size.
///     </para>
///     <para>
///         Like <see cref="ChordBoxControl" />, reassign <see cref="Chord" /> and <see cref="Template" />
///         as FRESH references to repaint; collection edits are not observed.
///     </para>
/// </summary>
public sealed class ChordDiagramControl : Control
{
    public static readonly StyledProperty<Chord?> ChordProperty =
        AvaloniaProperty.Register<ChordDiagramControl, Chord?>(nameof(Chord));

    public static readonly StyledProperty<ChordTemplate?> TemplateProperty =
        AvaloniaProperty.Register<ChordDiagramControl, ChordTemplate?>(nameof(Template));

    public static readonly StyledProperty<int> StringCountProperty =
        AvaloniaProperty.Register<ChordDiagramControl, int>(nameof(StringCount), 6);

    private static readonly Pen NutPen = new(ChartTheme.LightGrayBrush, 3);
    private static readonly Pen FretPen = new(ChartTheme.GrayBrush, 1);
    private static readonly Pen[] StringPens = ChartTheme.StringBrushes.Select(b => new Pen(b, 1.5)).ToArray();
    private static readonly Pen[] OpenPens = ChartTheme.StringBrushes.Select(b => new Pen(b, 1.5)).ToArray();
    private static readonly Pen MutedPen = new(ChartTheme.GrayBrush, 1.5);
    private static readonly ISolidColorBrush FingerOnDark = new ImmutableSolidColorBrush(Colors.White);
    private static readonly ISolidColorBrush FingerOnLight = new ImmutableSolidColorBrush(Color.FromRgb(20, 20, 24));

    static ChordDiagramControl() => AffectsRender<ChordDiagramControl>(ChordProperty, TemplateProperty, StringCountProperty);

    public Chord? Chord
    {
        get => GetValue(ChordProperty);
        set => SetValue(ChordProperty, value);
    }

    public ChordTemplate? Template
    {
        get => GetValue(TemplateProperty);
        set => SetValue(TemplateProperty, value);
    }

    public int StringCount
    {
        get => GetValue(StringCountProperty);
        set => SetValue(StringCountProperty, value);
    }

    /// <summary>The layout at the control's current size — what <see cref="Render" /> draws, and what a test measures.</summary>
    internal ChordDiagramLayout CurrentLayout()
    {
        var size = Bounds.Size;
        var frets = Chord?.ChordNotes.Select(n => n.Fret) ?? [];
        var (start, _) = ChordDiagramLayout.WindowFor(frets);
        var labelWidth = start > 1 ? LabelText(start, ChordDiagramLayout.LabelFontSizeFor(size)).Width : 0;
        return ChordDiagramLayout.For(size, StringCount, frets, labelWidth);
    }

    public override void Render(DrawingContext context)
    {
        if (Bounds.Width <= 0 || Bounds.Height <= 0) return;

        var layout = CurrentLayout();
        var strings = layout.StringCount;

        for (var row = 0; row <= layout.FretRows; row++)
        {
            var y = layout.FretLineY(row);
            context.DrawLine(row == 0 && layout.ShowsNut ? NutPen : FretPen,
                new Point(layout.GridLeft, y), new Point(layout.GridRight, y));
        }

        for (var s = 0; s < strings; s++)
        {
            var x = layout.StringX(s);
            context.DrawLine(StringPens[ChartTheme.StringSlot(s, strings)], new Point(x, layout.GridTop), new Point(x, layout.GridBottom));
        }

        if (layout.StartFret > 1)
        {
            var label = LabelText(layout.StartFret, ChordDiagramLayout.LabelFontSizeFor(layout.Size));
            context.DrawText(label, new Point(layout.LabelRect.X, layout.LabelRect.Center.Y - label.Height / 2));
        }

        var played = new bool[strings];
        var markerRadius = Math.Max(2, layout.FontSize * 0.3);
        if (Chord != null)
        {
            var brushes = ChartTheme.StringBrushes;
            foreach (var note in Chord.ChordNotes)
            {
                if (note.StringIndex < 0 || note.StringIndex >= strings) continue;
                played[note.StringIndex] = true;
                var x = layout.StringX(note.StringIndex);

                if (note.Fret <= 0)
                {
                    context.DrawEllipse(null, OpenPens[ChartTheme.StringSlot(note.StringIndex, strings)],
                        new Point(x, layout.MarkerY), markerRadius, markerRadius);
                    continue;
                }

                if (!layout.Shows(note.Fret)) continue;
                var y = layout.FretCenterY(note.Fret);
                var dot = brushes[ChartTheme.StringSlot(note.StringIndex, strings)];
                context.DrawEllipse(dot, null, new Point(x, y), layout.DotRadius, layout.DotRadius);

                if (layout.ShowsFingerNumbers && Template?.Fingers.TryGetValue(note.StringIndex, out var finger) == true
                                              && finger >= 0)
                {
                    var digit = ChartTheme.GetCachedText(finger == 0 ? "T" : finger.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        Math.Round(Math.Min(layout.FontSize, layout.DotRadius * 1.5)), FingerBrushOn(dot.Color), true);
                    context.DrawText(digit, new Point(x - digit.Width / 2, y - digit.Height / 2));
                }
            }
        }

        // An unplayed string is an x above the nut, like a printed chord chart.
        for (var s = 0; s < strings; s++)
        {
            if (played[s]) continue;
            var x = layout.StringX(s);
            var r = markerRadius;
            context.DrawLine(MutedPen, new Point(x - r, layout.MarkerY - r), new Point(x + r, layout.MarkerY + r));
            context.DrawLine(MutedPen, new Point(x + r, layout.MarkerY - r), new Point(x - r, layout.MarkerY + r));
        }
    }

    /// <summary>
    ///     The finger number's colour on a dot: white on the dark strings, near-black on the light
    ///     ones (yellow, green), where white disappeared.
    /// </summary>
    internal static ISolidColorBrush FingerBrushOn(Color dot) =>
        (0.2126 * dot.R + 0.7152 * dot.G + 0.0722 * dot.B) / 255 > 0.55 ? FingerOnLight : FingerOnDark;

    private static FormattedText LabelText(int fret, double fontSize) =>
        ChartTheme.GetCachedText(ChordDiagramLayout.FretLabelFor(fret), fontSize, ChartTheme.LightGrayBrush, true);
}
