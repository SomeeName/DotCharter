using System.Globalization;

namespace DotCharter.Tone.Ai;

/// <summary>
///     A knob value as the tone designer writes it to the model and quotes it
///     back in a validation message: the shortest round-trippable form, up to
///     four decimals, invariant culture. The prompt is charged by the token, and
///     "0.5" costs less than "0.500000".
/// </summary>
internal static class ToneDesignNumber
{
    public static string Format(float value) => value.ToString("0.####", CultureInfo.InvariantCulture);
}
