using System.Globalization;

namespace DotCharter.UI.Controls;

/// <summary>
///     Grid-relative note lengths, as text (04-inspector-redesign.md §6.2,
///     owner-confirmed: grid-relative, never milliseconds).
///     <para>
///         A <c>FractionalPosition</c> counts BEATS, and one beat is a quarter
///         note, so a half-beat sustain is an EIGHTH note. Below one beat the
///         musician's spelling ("1/8", "3/16") is the useful one; from one beat
///         up, beats are ("2 beats"), because that is what the grid divisor and
///         the <c>]</c>/<c>[</c> keys actually step in.
///     </para>
///     <para>
///         Pure and culture-invariant on the parse side: the box accepts what it
///         prints, and a German decimal comma must not change which fret… which
///         LENGTH the user gets.
///     </para>
/// </summary>
public static class FractionalLengthFormat
{
    /// <summary>Beats per whole note — a beat is a quarter, so a length in beats
    /// is <c>beats / 4</c> whole notes.</summary>
    private const int BeatsPerWhole = 4;

    /// <summary>Formats a length in BEATS. Returns null for "no length", which
    /// the caller renders as its own localized "none".</summary>
    public static string? Format(double beats)
    {
        if (beats <= 0.0001) return null;

        // One beat or more: beats, because that is the unit the grid steps in.
        if (beats >= 1.0)
            return beats.ToString("0.##", CultureInfo.InvariantCulture);

        // Under a beat: spell it as a note value if it is a clean one.
        for (var denominator = 8; denominator <= 64; denominator *= 2)
        {
            var exact = beats * denominator / BeatsPerWhole;
            var rounded = Math.Round(exact);
            if (rounded >= 1 && Math.Abs(exact - rounded) < 0.001)
                return $"{(int)rounded}/{denominator}";
        }

        // Triplets and other off-ladder values: three-based denominators.
        foreach (var denominator in new[] { 12, 24, 48 })
        {
            var exact = beats * denominator / BeatsPerWhole;
            var rounded = Math.Round(exact);
            if (rounded >= 1 && Math.Abs(exact - rounded) < 0.001)
                return $"{(int)rounded}/{denominator}";
        }

        return beats.ToString("0.###", CultureInfo.InvariantCulture);
    }

    /// <summary>
    ///     Parses what <see cref="Format" /> prints, plus the obvious variants a
    ///     charter will type: <c>1/8</c> (a note value), <c>2</c> or <c>2 beats</c>
    ///     (beats), <c>0</c> / empty (no sustain).
    /// </summary>
    public static bool TryParse(string? text, out double beats)
    {
        var ok = TryParseBeats(text, out var numerator, out var denominator);
        beats = ok ? (double)numerator / denominator : 0;
        return ok;
    }

    /// <summary>
    ///     The EXACT form: the length in beats as numerator/denominator, so the
    ///     caller can build a <c>Fraction</c> without a double round trip.
    ///     Timing in this app is grid-relative and exact by design; parsing to a
    ///     double and back would reintroduce the drift <c>FractionalPosition</c>
    ///     exists to prevent.
    /// </summary>
    public static bool TryParseBeats(string? text, out int numerator, out int denominator)
    {
        numerator = 0;
        denominator = 1;
        if (string.IsNullOrWhiteSpace(text)) return true;

        var trimmed = text.Trim();
        // Tolerate the unit word in either language; the number is what counts.
        var space = trimmed.IndexOf(' ');
        if (space > 0) trimmed = trimmed[..space];

        var slash = trimmed.IndexOf('/');
        if (slash > 0)
        {
            if (!int.TryParse(trimmed[..slash], NumberStyles.Integer, CultureInfo.InvariantCulture, out var num) ||
                !int.TryParse(trimmed[(slash + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out var den) ||
                den <= 0 || num < 0)
            {
                return false;
            }

            // A note value: 1/8 of a WHOLE is 1/2 of a BEAT.
            numerator = num * BeatsPerWhole;
            denominator = den;
            return true;
        }

        // Accept both decimal separators — the box prints invariant, but a German
        // keyboard produces a comma and refusing it would be a silent no-op.
        trimmed = trimmed.Replace(',', '.');
        if (!double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ||
            value < 0 || double.IsNaN(value) || double.IsInfinity(value))
        {
            return false;
        }

        // Three decimals is well past any grid divisor the editor offers, and it
        // keeps the denominator small enough that Fraction stays exact.
        numerator = (int)Math.Round(value * 1000);
        denominator = 1000;
        return true;
    }
}
