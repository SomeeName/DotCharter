namespace DotCharter.Services.Editing.Tempo.Estimate;

/// <summary>
///     The tempo read off the song audio. <see cref="Bpm" /> is the estimate; <see cref="Half" /> and
///     <see cref="Double" /> are the two octave alternatives (null when they leave the sane range), because
///     a tempo estimate is only ever right up to a power of two. <see cref="FromMs" /> is where the
///     estimated part starts: 0 for the whole song, the start marker otherwise.
/// </summary>
public sealed record TempoEstimate(double Bpm, double? Half, double? Double, double FromMs, int BeatCount)
{
    public const double LowestBpm = 30;

    public const double HighestBpm = 400;

    public static TempoEstimate From(double bpm, double fromMs, int beatCount)
    {
        var rounded = Math.Round(bpm, 2);
        var half = Math.Round(rounded / 2, 2);
        var twice = Math.Round(rounded * 2, 2);
        return new TempoEstimate(rounded, half >= LowestBpm ? half : null, twice <= HighestBpm ? twice : null, fromMs, beatCount);
    }
}

public enum TempoEstimateFailure
{
    None,
    NoAudio,
    DecodeFailed,
    NoTempoFound
}

public sealed record TempoEstimateOutcome(TempoEstimate? Estimate, TempoEstimateFailure Failure);

public enum TempoEstimateApplyStatus
{
    Applied,
    NoChange,
    Locked,
    NoBeats,
    Invalid
}

/// <summary>
///     The pure half of Tempo map ▸ Estimate BPM ▸ Apply (EOF's <c>eof_menu_beat_estimate_bpm</c>): the
///     tempo goes on one beat and runs to the next anchor. <see cref="ITempoService.UpdateBpm" /> already
///     carries a tempo over the unanchored beats that follow and stops at an anchor, so this only picks
///     the beat and works on a CLONE: the caller wraps the result in one undo step.
/// </summary>
public static class TempoEstimateApplier
{
    /// <summary>The beat the part starting at <paramref name="fromMs" /> begins on: the one nearest it; 0 for the whole song.</summary>
    public static int StartBeatIndex(IReadOnlyList<BeatMarker> beats, double fromMs)
    {
        if (beats.Count == 0 || fromMs <= 0) return 0;
        var best = 0;
        for (var i = 1; i < beats.Count; i++)
        {
            if (Math.Abs(beats[i].Time - fromMs) < Math.Abs(beats[best].Time - fromMs)) best = i;
        }

        return best;
    }

    /// <summary>The beats as they would stand once <paramref name="bpm" /> is set from the start beat to the next anchor; the input is untouched.</summary>
    public static List<BeatMarker>? Plan(ITempoService tempo, IReadOnlyList<BeatMarker> beats, double bpm, double fromMs)
    {
        if (beats.Count == 0 || !double.IsFinite(bpm) || bpm <= 0) return null;
        var plan = beats.Select(b => b.Clone()).ToList();
        tempo.UpdateBpm(plan, StartBeatIndex(plan, fromMs), bpm);
        for (var i = 0; i < plan.Count; i++)
        {
            if (!double.IsFinite(plan[i].Time) || (i > 0 && plan[i].Time <= plan[i - 1].Time)) return null;
        }

        return plan;
    }
}
