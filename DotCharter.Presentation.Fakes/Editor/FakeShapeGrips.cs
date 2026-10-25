namespace DotCharter.Presentation.Fakes.Editor;

/// <summary>The grips the shape-workflow fakes draw, low string to high (<c>-1</c> unplayed or no finger).</summary>
internal static class FakeShapeGrips
{
    public static readonly FakeShape Am = new("Am", [-1, 0, 2, 2, 1, 0], [-1, -1, 2, 3, 1, -1]);
    public static readonly FakeShape Am7 = new("Am7", [-1, 0, 2, 0, 1, 0], [-1, -1, 2, -1, 1, -1]);
    public static readonly FakeShape AmOtherFingers = new("Am", [-1, 0, 2, 2, 1, 0], [-1, -1, 3, 2, 1, -1]);
    public static readonly FakeShape C = new("C", [-1, 3, 2, 0, 1, 0], [-1, 3, 2, -1, 1, -1]);
    public static readonly FakeShape Cadd9 = new("Cadd9", [-1, 3, 2, 0, 3, 0], [-1, 2, 1, -1, 3, -1]);
    public static readonly FakeShape G = new("G", [3, 2, 0, 0, 0, 3], [2, 1, -1, -1, -1, 3]);
    public static readonly FakeShape D = new("D", [-1, -1, 0, 2, 3, 2], [-1, -1, -1, 1, 3, 2]);
    public static readonly FakeShape Em = new("Em", [0, 2, 2, 0, 0, 0], [-1, 2, 3, -1, -1, -1]);
    public static readonly FakeShape F = new("F", [1, 3, 3, 2, 1, 1], [1, 3, 4, 2, 1, 1]);
    public static readonly FakeShape A5 = new("A5", [-1, 0, 2, 2, -1, -1], [-1, -1, 1, 1, -1, -1]);
    public static readonly FakeShape A5Copy = new("A5 (2)", [-1, 0, 2, 2, -1, -1], [-1, -1, 1, 1, -1, -1]);
    public static readonly FakeShape E5 = new("E5", [0, 2, 2, -1, -1, -1], [-1, 1, 1, -1, -1, -1]);
    public static readonly FakeShape PowerNoName = new("", [-1, 7, 9, 9, -1, -1], [-1, 1, 3, 4, -1, -1]);
    public static readonly FakeShape DyadNoName = new("", [-1, -1, 8, 6, -1, -1], [-1, -1, -1, -1, -1, -1]);

    /// <summary>An unnamed lead grip that an old chart carries as several copies, in two fingerings.</summary>
    public static readonly FakeShape LeadGrip = new("", [-1, -1, 9, 8, 11, -1], [-1, -1, 2, 1, 4, -1]);

    public static readonly FakeShape LeadGripOtherFingers = new("", [-1, -1, 9, 8, 11, -1], [-1, -1, 3, 1, 4, -1]);

    public static readonly FakeShape Bm7b5 = new("Bm7b5", [-1, 2, 3, 2, 3, -1], [-1, 1, 3, 2, 4, -1]);

    /// <summary>An eight-string grip, for the widest editor.</summary>
    public static readonly FakeShape EightString = new("F#5 (8)", [2, 4, 4, -1, -1, -1, -1, -1], [1, 3, 4, -1, -1, -1, -1, -1]);

    /// <summary>The eight-string grip with its third string added.</summary>
    public static readonly FakeShape EightStringEdited = new("F#5 (8)", [2, 4, 4, 3, -1, -1, -1, -1], [1, 3, 4, 2, -1, -1, -1, -1]);

    public static string FretsText(FakeShape shape) =>
        string.Join(' ', shape.Frets.Select(f => f < 0 ? "x" : f.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    /// <summary>The name, or the fret pattern when the shape has none.</summary>
    public static string Title(FakeShape shape) => shape.Name.Length == 0 ? FretsText(shape) : shape.Name;
}
