using System.Globalization;
using Avalonia.Controls;
using Avalonia.Media;

namespace DotCharter.Tone.UI.Controls.Dsp;

/// <summary>
/// Shared caption/readout text helpers for the custom-drawn rig controls
/// (the Fire knob, and the retired rack's dial and treadle before it): theme-token
/// font-size resolution (with a hard fallback for headless/unstyled hosts) and
/// the adaptive numeric readout format.
/// </summary>
internal static class DspControlText
{
    internal static double ResolveFontSize(Control control, string key, double fallback)
        => control.TryFindResource(key, out var v) && v is double d ? d : fallback;

    internal static string FormatValue(double v)
    {
        var a = Math.Abs(v);
        return a >= 100 ? v.ToString("0", CultureInfo.InvariantCulture)
            : a >= 10 ? v.ToString("0.#", CultureInfo.InvariantCulture)
            : v.ToString("0.##", CultureInfo.InvariantCulture);
    }

    /// <summary>The units a native parameter name may carry in a trailing
    /// parenthesis. A whitelist, not "any short suffix": "Feedback (BBD)" is a
    /// name, and the tone-rig design keeps it whole.</summary>
    private static readonly HashSet<string> Units = new(StringComparer.Ordinal)
    {
        "Hz", "kHz", "ms", "s", "dB", "%", "st", "ct", "cents", "bpm", "x", "°", "deg", "sec", "semi", "oct"
    };

    /// <summary>
    ///     Splits "Rate (Hz)" into the caption "Rate" and the unit "Hz" — the
    ///     tone-rig design prints the unit beside the value plate and drops it
    ///     from the label, so a face never says "Hz" twice. A name whose
    ///     parenthesis is not a unit ("Feedback (BBD)") comes back whole with
    ///     no unit; so does a bare name.
    /// </summary>
    internal static (string Caption, string? Unit) SplitUnit(string name)
    {
        if (string.IsNullOrEmpty(name) || !name.EndsWith(')')) return (name, null);
        var open = name.LastIndexOf(" (", StringComparison.Ordinal);
        if (open <= 0) return (name, null);
        var unit = name[(open + 2)..^1];
        return Units.Contains(unit) ? (name[..open], unit) : (name, null);
    }

    /// <summary>
    /// A single trimmed line for a custom-drawn rig control.
    /// <para>
    /// <b>Alignment is deliberately Left.</b> <see cref="FormattedText.Width"/> is
    /// the measured GLYPH width, not <see cref="FormattedText.MaxTextWidth"/> — but
    /// <c>DrawText</c> lays the run out inside a <c>MaxTextWidth</c>-wide box
    /// anchored at the origin, so <see cref="TextAlignment.Center"/> centres the
    /// glyphs in THAT box. Every caller here already centres by hand
    /// (<c>DrawText(t, new Point(cx - t.Width / 2, y))</c>), so a centred box
    /// centred AGAIN shifts the run right by <c>(maxWidth − Width) / 2</c> — which
    /// is what right-aligned the value readouts and flung the skirted knob's
    /// number-ring digits out of the control. Keep it Left; the callers own the
    /// centring.
    /// </para>
    /// </summary>
    internal static FormattedText Make(string text, double size, Color color, double maxWidth)
        => new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            Typeface.Default, size, new SolidColorBrush(color))
        {
            TextAlignment = TextAlignment.Left,
            MaxTextWidth = maxWidth,
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis
        };
}
