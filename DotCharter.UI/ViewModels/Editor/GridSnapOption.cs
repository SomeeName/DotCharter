namespace DotCharter.UI.ViewModels.Editor;

/// <summary>
///     One row of the toolbar's grid-snap picker.
///     <para>
///         The label is the DIVISOR, spelled the way EOF spells it
///         (<c>EOF_SRC/menu/edit.c</c>, <c>eof_edit_snap_menu</c>): 1/4, 1/8,
///         1/16, 1/32, 1/64 and then the triplet group 1/12, 1/24, 1/48, 1/96.
///         <c>TempoService.SnapToGrid</c> divides the interval between two BEAT
///         MARKERS by it, so <c>1/24</c> is a twenty-fourth OF A BEAT.
///     </para>
///     <para>
///         The label is the divisor (24), not <c>1/16T</c>: the latter is internally
///         consistent (a triplet sixteenth IS a twenty-fourth of a beat) but
///         wrong for the person using it. A charter asked "what is the T, and why
///         is there no 1/24 1/48 1/96?" — those are useful for triplet feel and triplets.
///         Both halves
///         of that are the same complaint: the divisor is the number a charter
///         thinks in, and re-deriving it into a note name hid the ladder.
///         <see cref="IsTriplet" /> keeps the hint without replacing the name.
///     </para>
/// </summary>
public sealed record GridSnapOption(int Divisor, string Label, bool IsTriplet)
{
    /// <summary>The label for a divisor — pure, so it is testable against the
    /// snap math without a view model.</summary>
    public static string LabelFor(int divisor) => $"1/{divisor}";

    /// <summary>
    ///     A divisor divisible by three splits the beat into triplets — three
    ///     notes in the space of two of the next step down.
    /// </summary>
    public static bool IsTripletDivisor(int divisor) => divisor > 3 && divisor % 3 == 0;

    public static GridSnapOption For(int divisor) =>
        new(divisor, LabelFor(divisor), IsTripletDivisor(divisor));

    /// <summary>The note value this divisor lands on, for the picker's hint —
    /// "1/24" is a triplet SIXTEENTH, which is the name the ear knows it by.</summary>
    public string NoteName => IsTriplet ? $"1/{Divisor * 2 / 3}T" : $"1/{Divisor}";

    public override string ToString() => Label;
}
