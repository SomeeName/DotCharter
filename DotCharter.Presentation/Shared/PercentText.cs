using System.Globalization;

namespace DotCharter.Presentation.Shared;

/// <summary>A 0..1 share in the Windows region's format, never the UI language's: "99.9%" en-US, "99,9 %" de-AT.</summary>
public static class PercentText
{
    public static string Format(double fraction, int decimals) =>
        Format(fraction, decimals, CultureInfo.CurrentCulture);

    public static string Format(double fraction, int decimals, IFormatProvider culture) =>
        fraction.ToString("P" + Math.Clamp(decimals, 0, 3).ToString(CultureInfo.InvariantCulture), culture);
}
