namespace DotCharter.Tone.Audio.Tuning;

/// <summary>Whether a path can be played in the player's own tuning by shifting the SONG.</summary>
public enum TuningMatchVerdict
{
    /// <summary>Every string differs by the same step, within range: shift the audio by that step.</summary>
    Shift,

    /// <summary>The path is already in the player's tuning; there is nothing to shift.</summary>
    AlreadyMatches,

    /// <summary>The strings differ by different amounts (Drop D against E standard): the FRETS would
    /// have to move, and a pitch shift cannot do that.</summary>
    NotUniform,

    /// <summary>Uniform, but further away than <see cref="TuningMatchTranspose.MaxShiftSemitones" />.</summary>
    TooFar,

    /// <summary>Nothing in the chart says what its strings are tuned to.</summary>
    UnknownTuning,

    /// <summary>A string count whose offsets have no agreed base (a 5-string bass, 7+ strings).</summary>
    UnsupportedStringCount
}

/// <summary>The verdict and, for <see cref="TuningMatchVerdict.Shift" /> and
/// <see cref="TuningMatchVerdict.TooFar" />, the signed step: what the SONG must be shifted by so the
/// chart's frets sound right on the player's guitar (E♭ standard played in E standard = +1).</summary>
public readonly record struct TuningMatch(TuningMatchVerdict Verdict, int Semitones)
{
    public bool CanShift => Verdict == TuningMatchVerdict.Shift;
}

/// <summary>
/// Tuning-match transpose: a song charted in E♭ or D standard, played by someone in
///     E standard. When EVERY string of the path differs from the player's tuning by the same
///     number of semitones, the frets stay exactly as charted and only the pitch moves, so the song
///     can be shifted to the player's tuning instead of the player retuning. Anything else — a drop
///     tuning against a standard one, an open tuning — moves frets, and is refused with its reason.
///     <para>
///         Pure. The chart's offsets come from <see cref="TuningOffsetResolution" /> (what live
///         judging reads), falling back to a decodable tuning NAME through
///         <see cref="ArrangementTunerLogic.TryResolveTuningSteps" /> (what the tuners read). Only
///         the strings the path HAS are compared: a bass path keeps six slots, of which four are
///         strings.
///     </para>
/// </summary>
public static class TuningMatchTranspose
{
    /// <summary>The furthest the song is shifted. A mix shifted further than this stops sounding
    /// like the record (B standard → E standard is a fourth).</summary>
    public const int MaxShiftSemitones = 5;

    /// <summary>The verdict for <paramref name="arrangement" /> against the player's per-string
    /// <paramref name="playerSteps" /> (offsets from E standard, low string first, six of them).</summary>
    public static TuningMatch Resolve(Arrangement arrangement, IReadOnlyList<int> playerSteps)
    {
        ArgumentNullException.ThrowIfNull(arrangement);
        var stringCount = ArrangementTunerLogic.ResolvedStringCount(arrangement);
        var chartSteps = TuningOffsetResolution.Resolve(arrangement);
        if (chartSteps.Count == 0 && ArrangementTunerLogic.TryResolveTuningSteps(arrangement, out var named))
            chartSteps = named;
        return Resolve(chartSteps, stringCount, playerSteps);
    }

    /// <summary>The verdict from raw parts (the testable seam).</summary>
    public static TuningMatch Resolve(IReadOnlyList<int> chartSteps, int stringCount, IReadOnlyList<int> playerSteps)
    {
        ArgumentNullException.ThrowIfNull(chartSteps);
        ArgumentNullException.ThrowIfNull(playerSteps);
        // 4 and 6 are the counts whose offsets share a base with a guitar's low strings; a
        // 5-string bass is measured against B-E-A-D-G by one format and E-A-D-G-B by another.
        if (stringCount is not (4 or 6) || playerSteps.Count < stringCount)
            return new TuningMatch(TuningMatchVerdict.UnsupportedStringCount, 0);
        if (chartSteps.Count < stringCount) return new TuningMatch(TuningMatchVerdict.UnknownTuning, 0);

        var step = playerSteps[0] - chartSteps[0];
        for (var i = 1; i < stringCount; i++)
        {
            if (playerSteps[i] - chartSteps[i] != step) return new TuningMatch(TuningMatchVerdict.NotUniform, 0);
        }

        if (step == 0) return new TuningMatch(TuningMatchVerdict.AlreadyMatches, 0);
        return Math.Abs(step) > MaxShiftSemitones
            ? new TuningMatch(TuningMatchVerdict.TooFar, step)
            : new TuningMatch(TuningMatchVerdict.Shift, step);
    }
}
