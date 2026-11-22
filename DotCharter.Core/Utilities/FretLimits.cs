namespace DotCharter.Core.Utilities;

/// <summary>
///     THE neck limits. Every "highest fret", "lowest legal anchor" and "highest legal
///     anchor" in the app comes from here (a source lint forbids re-declaring them).
///     Frets are ABSOLUTE on the un-capoed neck, so the lowest legal anchor is
///     <c>capo + 1</c> (inspector rule L02).
/// </summary>
public static class FretLimits
{
    /// <summary>The last fret of a Rocksmith / EOF neck.</summary>
    public const int MaxFret = 24;

    /// <summary>
    ///     Highest fret an anchor may be PLACED on (hand edits, any width). A wider anchor must
    ///     also fit the neck: use <see cref="MaxAnchorFretFor" /> when the width is known.
    /// </summary>
    public const int MaxAnchorFret = MaxFret;

    /// <summary>Lowest legal anchor fret behind <paramref name="capo" />; a negative capo counts as 0.</summary>
    public static int MinAnchorFret(int capo) => Math.Clamp(Math.Max(0, capo) + 1, 1, MaxAnchorFret);

    /// <summary>Highest anchor fret whose whole span (<c>fret .. fret + width - 1</c>) stays on the neck.</summary>
    public static int MaxAnchorFretFor(int width) => Math.Clamp(MaxFret - Math.Max(1, width) + 1, 1, MaxAnchorFret);
}
