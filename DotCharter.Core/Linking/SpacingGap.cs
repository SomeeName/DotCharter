using System.Globalization;
using DotCharter.Core.Domain.Events;
using DotCharter.Core.Domain.Primitives;
using DotCharter.Core.Utilities;

namespace DotCharter.Core.Linking;

/// <summary>How the minimum gap before the next sound is measured (the app's "minimum note distance").</summary>
public enum SpacingGapMode
{
    /// <summary>A fixed number of milliseconds — a different number of beats at every tempo.</summary>
    Milliseconds,

    /// <summary>The measure's beat count over the divisor (1/64 of a 4/4 measure = 1/16 beat).</summary>
    MeasureFraction,

    /// <summary>One beat over the divisor.</summary>
    BeatFraction
}

/// <summary>
///     The minimum gap <c>ChartSpacingEnforcer</c> leaves between a sustain's end and the next
///     sound, and between a hand shape's end and what follows it. This is THE implementation:
///     the enforcer's <c>GetMinDistance</c> calls <see cref="At" />, so the comparison rule and
///     the clean-up pass can never disagree about the bound.
/// </summary>
public readonly record struct SpacingGap(SpacingGapMode Mode, int Divisor, double Milliseconds)
{
    /// <summary>The app's default setting (1/64 of a measure).</summary>
    public static readonly SpacingGap AppDefault = new(SpacingGapMode.MeasureFraction, 64, 10.0);

    /// <summary>The gap that applies to a sound starting at <paramref name="position" />, in beats.</summary>
    public FractionalPosition At(FractionalPosition position, IList<BeatMarker> beats)
    {
        var denominator = Divisor > 0 ? Divisor : 64;
        return Mode switch
        {
            SpacingGapMode.Milliseconds => MillisecondsAt(position, beats),
            SpacingGapMode.BeatFraction => new FractionalPosition(new Fraction(1, denominator)),
            _ => new FractionalPosition(new Fraction(BeatsInMeasure(position, beats), denominator))
        };
    }

    /// <summary>
    ///     The community charting floor: a 32nd note at <paramref name="position" />, in beats (1/8 beat
    ///     under a quarter-note beat, 1/4 beat under an eighth-note beat). The validator's sustain and
    ///     hand-shape gap checks never ask for less, whatever the minimum note distance setting says;
    ///     the spacing clean-up itself keeps the setting, since Rocksmith needs no more than that.
    /// </summary>
    public static FractionalPosition ThirtySecondNote(FractionalPosition position, IList<BeatMarker> beats)
    {
        var denominator = beats.Count == 0 ? 4 : beats[Math.Clamp(position.BeatId, 0, beats.Count - 1)].NoteDenominator;
        return new FractionalPosition(new Fraction(Math.Max(1, denominator), 32));
    }

    /// <summary>The larger of <see cref="At" /> and <see cref="ThirtySecondNote" />: the gap the
    /// validator asks a sustain or hand shape to leave before the next attack.</summary>
    public FractionalPosition AtLeastThirtySecondNote(FractionalPosition position, IList<BeatMarker> beats) =>
        FractionalPosition.Max(At(position, beats), ThirtySecondNote(position, beats));

    /// <summary>A short, stable spelling for key headers: <c>ms10</c>, <c>mf64</c>, <c>bf64</c>.</summary>
    public string Describe() => Mode switch
    {
        SpacingGapMode.Milliseconds => string.Create(CultureInfo.InvariantCulture, $"ms{Milliseconds:0.###}"),
        SpacingGapMode.BeatFraction => string.Create(CultureInfo.InvariantCulture, $"bf{(Divisor > 0 ? Divisor : 64)}"),
        _ => string.Create(CultureInfo.InvariantCulture, $"mf{(Divisor > 0 ? Divisor : 64)}")
    };

    private FractionalPosition MillisecondsAt(FractionalPosition position, IList<BeatMarker> beats)
    {
        if (Milliseconds <= 0 || beats.Count == 0) return FractionalPosition.Zero;

        var startMs = position.GetPositionMs(beats);
        var endPosition = TimelineMath.PositionFromMs(startMs + Milliseconds, beats);
        return FractionalPosition.Max(FractionalPosition.Zero, endPosition - position);
    }

    private static int BeatsInMeasure(FractionalPosition position, IList<BeatMarker> beats)
    {
        if (beats.Count == 0) return 4;

        var beatIndex = Math.Clamp(position.BeatId, 0, beats.Count - 1);
        for (var i = beatIndex; i >= 0; i--)
        {
            if (beats[i].IsMeasure)
                return Math.Max(1, beats[i].BeatsInMeasure);
        }

        return Math.Max(1, beats[beatIndex].BeatsInMeasure);
    }
}
