namespace DotCharter.UI.Controls;

/// <summary>
///     The pure half of <see cref="HeroStringsBackdrop" />: where the frets sit
///     and how far a plucked string is displaced at a point and an instant. No
///     Avalonia types, so the default gate can pin it.
/// </summary>
public static class HeroStringsMath
{
    public const int StringCount = 6;

    /// <summary>Seconds between strums.</summary>
    public const double StrumPeriod = 3.6;

    /// <summary>Seconds between one string being plucked and the next (low E first).</summary>
    public const double StrumStagger = 0.05;

    /// <summary>Seconds for a string's swing to fall to 1/e.</summary>
    public const double Decay = 0.55;

    /// <summary>Peak displacement, in pixels, of the lowest string.</summary>
    public const double PeakAmplitude = 4.5;

    /// <summary>Below this many pixels a string is drawn straight and the scene stops repainting.</summary>
    public const double SettledAmplitude = 0.15;

    /// <summary>
    ///     Where fret <paramref name="n" /> sits along a neck of <paramref name="scaleLength" />,
    ///     measured from the nut: the equal-tempered rule, each fret 2^(-1/12) of the one before.
    /// </summary>
    public static double FretPosition(int n, double scaleLength) =>
        scaleLength * (1 - Math.Pow(2, -n / 12.0));

    /// <summary>Seconds since string <paramref name="index" /> was last plucked, or null before its first pluck.</summary>
    public static double? SincePluck(int index, double seconds)
    {
        var sinceStrum = seconds % StrumPeriod;
        var since = sinceStrum - index * StrumStagger;
        if (since >= 0) return since;
        // Still waiting for this strum's pluck: the previous strum's is ringing out, if there was one.
        return seconds >= StrumPeriod ? since + StrumPeriod : null;
    }

    /// <summary>The swing's envelope for string <paramref name="index" />: its peak, decaying.</summary>
    public static double Envelope(int index, double seconds)
    {
        if (SincePluck(index, seconds) is not { } t) return 0;
        // Thicker strings swing wider.
        var peak = PeakAmplitude * (1 - index * 0.09);
        return peak * Math.Exp(-t / Decay);
    }

    /// <summary>
    ///     Displacement in pixels of string <paramref name="index" /> at <paramref name="x" />
    ///     (0 at one end, 1 at the other): the fundamental's standing wave, so the ends never move.
    /// </summary>
    public static double Displacement(int index, double x, double seconds)
    {
        var envelope = Envelope(index, seconds);
        if (envelope < SettledAmplitude) return 0;
        var t = SincePluck(index, seconds)!.Value;
        // A visible wobble, not the audible pitch: a few cycles a second, lower strings slower.
        var hz = 5.5 + index * 0.8;
        return envelope * Math.Sin(Math.PI * Math.Clamp(x, 0, 1)) * Math.Sin(2 * Math.PI * hz * t);
    }

    /// <summary>Whether every string has rung out, i.e. a repaint would draw the same frame.</summary>
    public static bool IsSettled(double seconds)
    {
        for (var i = 0; i < StringCount; i++)
            if (Envelope(i, seconds) >= SettledAmplitude) return false;
        return true;
    }
}
