using Avalonia;

namespace DotCharter.UI.Controls;

/// <summary>
/// The read-only chord diagram's arithmetic: a chord CHART, not the editor's
///     fretboard — strings run down, lowest on the left, the nut on top, an <c>o</c> or <c>x</c>
///     above it per string, and only the first fret named ("8fr") when the shape sits up the neck.
///     A pure function of (size, string count, fret window, label width), so the rules the list
///     relies on are tested without a visual tree:
///     <list type="bullet">
///         <item>a finger dot is never wider than the gap to its neighbour, so two dots never touch;</item>
///         <item>the fret label sits in its own band left of the grid, so it never runs into a string;</item>
///         <item>the o / x markers sit in their own band above the nut.</item>
///     </list>
/// </summary>
public readonly record struct ChordDiagramLayout
{
    /// <summary>The fewest fret rows a diagram draws, so a two-fret shape still reads as a neck.</summary>
    public const int MinFretRows = 4;

    /// <summary>A dot's diameter as a share of the smaller cell side: the rest is air between dots.</summary>
    public const double DotShare = 0.84;

    /// <summary>The smallest dot radius a finger number is printed in; below it the dot alone reads better.</summary>
    public const double MinRadiusForFinger = 4.5;

    private ChordDiagramLayout(Size size, int stringCount, int startFret, int fretRows, double fontSize, double labelWidth)
    {
        Size = size;
        StringCount = stringCount;
        StartFret = startFret;
        FretRows = fretRows;
        FontSize = fontSize;

        var markerBand = fontSize + 3;
        var columns = Math.Max(1, stringCount - 1);
        var gridTop = markerBand + 2;
        var fretSpacing = Math.Max(0, size.Height - gridTop - 2) / fretRows;
        double stringSpacing, gridLeft;
        if (startFret > 1)
        {
            // The label band takes the place of the left half cell: the lowest string's dot sits
            // one pixel right of the label, so a shape up the neck keeps dots big enough for a
            // finger number. W = label + 1 + dot radius + columns * s + s / 2.
            var labelBand = labelWidth + 1;
            stringSpacing = Math.Max(0, size.Width - labelBand) / (columns + 0.5 + DotShare / 2);
            gridLeft = labelBand + Math.Min(stringSpacing, fretSpacing) * DotShare / 2;
        }
        else
        {
            // Half a cell of air each side keeps the outer strings' dots inside the box.
            stringSpacing = size.Width / (columns + 1);
            gridLeft = stringSpacing / 2;
        }

        StringSpacing = stringSpacing;
        FretSpacing = fretSpacing;
        GridLeft = gridLeft;
        GridTop = gridTop;
        GridRight = GridLeft + columns * stringSpacing;
        GridBottom = gridTop + fretRows * fretSpacing;
        MarkerY = markerBand / 2;
        DotRadius = Math.Min(stringSpacing, fretSpacing) * DotShare / 2;
        LabelRect = startFret > 1
            ? new Rect(0, gridTop + fretSpacing / 2 - fontSize * 0.65, labelWidth, fontSize * 1.3)
            : default;
    }

    public Size Size { get; }

    public int StringCount { get; }

    /// <summary>The fret the first row stands for; 1 means the nut is drawn.</summary>
    public int StartFret { get; }

    public int FretRows { get; }

    /// <summary>The size every label in the diagram is set in (fret label, o / x, finger numbers).</summary>
    public double FontSize { get; }

    public double StringSpacing { get; }

    public double FretSpacing { get; }

    public double GridLeft { get; }

    public double GridTop { get; }

    public double GridRight { get; }

    public double GridBottom { get; }

    /// <summary>Centre line of the o / x markers.</summary>
    public double MarkerY { get; }

    public double DotRadius { get; }

    /// <summary>Where "8fr" goes; empty when the shape starts at the nut.</summary>
    public Rect LabelRect { get; }

    public bool ShowsNut => StartFret <= 1;

    public bool ShowsFingerNumbers => DotRadius >= MinRadiusForFinger;

    public double StringX(int stringIndex) => GridLeft + stringIndex * StringSpacing;

    /// <summary>Centre of the cell a fretted note sits in.</summary>
    public double FretCenterY(int fret) => GridTop + (fret - StartFret + 0.5) * FretSpacing;

    public double FretLineY(int row) => GridTop + row * FretSpacing;

    /// <summary>True when <paramref name="fret" /> falls inside the drawn window.</summary>
    public bool Shows(int fret) => fret >= StartFret && fret < StartFret + FretRows;

    /// <summary>The label text for <see cref="StartFret" />, or empty at the nut.</summary>
    public string FretLabel => StartFret > 1 ? FretLabelFor(StartFret) : "";

    public static string FretLabelFor(int fret) => fret.ToString(System.Globalization.CultureInfo.InvariantCulture) + "fr";

    /// <summary>The type size for a diagram of this height: big enough to read, never bigger than the cells.</summary>
    public static double FontSizeFor(Size size) => Math.Clamp(size.Height * 0.12, 8, 12);

    /// <summary>The first-fret label is set a size smaller than the markers: it is a caption, not a mark.</summary>
    public static double LabelFontSizeFor(Size size) => Math.Max(7, Math.Round(FontSizeFor(size) * 0.85));

    /// <summary>
    ///     The fret window for a shape's fretted notes (open strings are not in it): from the nut
    ///     while everything fits in the first <see cref="MinFretRows" /> frets, else from the lowest
    ///     fretted note, at least <see cref="MinFretRows" /> rows tall.
    /// </summary>
    public static (int StartFret, int FretRows) WindowFor(IEnumerable<int> frets)
    {
        var fretted = frets.Where(f => f > 0).ToList();
        if (fretted.Count == 0) return (1, MinFretRows);

        var min = fretted.Min();
        var max = fretted.Max();
        var start = max <= MinFretRows ? 1 : min;
        return (start, Math.Max(MinFretRows, max - start + 1));
    }

    public static ChordDiagramLayout For(Size size, int stringCount, IEnumerable<int> frets, double labelWidth)
    {
        var (start, rows) = WindowFor(frets);
        return new ChordDiagramLayout(size, Math.Max(1, stringCount), start, rows, FontSizeFor(size), labelWidth);
    }
}
