using DotCharter.Services.Editing.Tempo.Estimate;

namespace DotCharter.Services.Editing.Validation.Rules;

/// <summary>
///     Tempo and meter checks on the beat map, all Warnings:
///     <list type="bullet">
///         <item>
///             D60/D61 (<see cref="ArrangementIssueType.TempoLooksDoubled" />,
///             <see cref="ArrangementIssueType.TempoLooksHalved" />) and D65
///             (<see cref="ArrangementIssueType.TempoOffAudio" />): the charted part's average tempo against the
///             tempo read off the song audio. They run only when <see cref="IAudioTempoEstimateStore" /> already
///             holds a whole-song estimate for the project's audio (Tempo map ▸ Estimate BPM fills it); without
///             one they stay silent. Validation never decodes or analyses audio.
///         </item>
///         <item>
///             D62 (<see cref="ArrangementIssueType.TempoOutOfRange" />) a beat under <see cref="MinBpm" /> or
///             over <see cref="MaxBpm" />, and D63 (<see cref="ArrangementIssueType.TempoSpike" />) one beat at
///             least <see cref="SpikeRatio" /> times faster than BOTH neighbours: a beat line pushed early (a
///             slow beat is never a spike, because a held fermata beat is one). Both look only at the
///             beats from the first to the last note start: a ritardando into the last chord's ring-out is not
///             the chart's tempo. A spike, not a step, so a real tempo change (a step that stays) and swing or
///             triplet feel (which moves subdivisions, never the beat lines) are not flagged.
///         </item>
///         <item>
///             D64 (<see cref="ArrangementIssueType.TempoAnchorSpam" />): at least
///             <see cref="RedundantAnchorLimit" /> anchors that leave the tempo where it was. Anchors that
///             follow a live drummer change the tempo on every beat and are not counted.
///         </item>
///         <item>
///             D66/D67 (<see cref="ArrangementIssueType.MeterMissingOnFirstMeasure" />,
///             <see cref="ArrangementIssueType.MeterChangeUnmarked" />): the time signature on measure 1 and at
///             every change. A measure whose beat count differs from its own time signature means the meter
///             changed without one; reported once where such a run starts. The last measure may be cut short
///             and is never judged.
///         </item>
///     </list>
///     The thresholds come from the reference corpus (<c>TempoMapRuleCorpusTests</c>); nothing here edits the
///     beat map.
/// </summary>
public sealed class TempoMapRule(IAudioTempoEstimateStore audioTempo) : IArrangementRule
{
    public const double MinBpm = 40.0;

    public const double MaxBpm = 300.0;

    /// <summary>
    ///     A beat this many times faster than both neighbours is a spike. The 269 reference charts reach 1.13 inside the charted part (a pushed fill beat); a beat line pushed
    ///     half a beat early gives 2.
    /// </summary>
    public const double SpikeRatio = 1.4;

    /// <summary>
    ///     Redundant anchors before D64 speaks. The reference corpus holds at most 3 per chart; a chart that
    ///     anchors every beat of a steady tempo holds hundreds.
    /// </summary>
    public const int RedundantAnchorLimit = 16;

    /// <summary>An anchor whose tempo is within this of the beat before it changes nothing.</summary>
    public const double RedundantAnchorBpm = 0.005;

    /// <summary>How far from exactly double or half the average may be and still read as an octave slip.</summary>
    public const double OctaveTolerance = 0.03;

    /// <summary>How far the average may drift from the audio's tempo before D65 speaks.</summary>
    public const double AudioTolerance = 0.01;

    public void Validate(ArrangementValidationContext context, ICollection<ArrangementIssue> issues)
    {
        var beats = context.Beats;
        if (beats.Count < 3) return;

        if (ChartedSpan(context.Arrangement, beats.Count) is { } span)
        {
            CheckRange(beats, span, issues);
            CheckSpikes(beats, span, issues);
            CheckAudio(context, beats, span, issues);
        }

        CheckAnchors(beats, issues);
        CheckMeter(beats, issues);
    }

    /// <summary>The tempo from beat <paramref name="i" /> to the next one, or null where the gap is not positive.</summary>
    public static double? BeatTempo(IList<BeatMarker> beats, int i)
    {
        if (i < 0 || i + 1 >= beats.Count) return null;
        var gap = beats[i + 1].Time - beats[i].Time;
        return gap > 0 ? 60_000.0 / gap : null;
    }

    /// <summary>
    ///     How far beat <paramref name="i" /> is squeezed: the smaller of its tempo ratios to the two neighbours
    ///     when it is faster than both, else 1. Only a SHORT beat counts: a held beat (a fermata, a ritardando
    ///     into a held chord) is slower than both neighbours on purpose.
    /// </summary>
    public static double SpikeOf(IList<BeatMarker> beats, int i)
    {
        if (BeatTempo(beats, i - 1) is not { } before || BeatTempo(beats, i) is not { } tempo ||
            BeatTempo(beats, i + 1) is not { } after)
        {
            return 1.0;
        }

        return Math.Max(1.0, Math.Min(tempo / before, tempo / after));
    }

    /// <summary>Anchors that keep the tempo of the beat before them.</summary>
    public static int RedundantAnchors(IList<BeatMarker> beats)
    {
        var count = 0;
        for (var i = 1; i < beats.Count; i++)
        {
            if (beats[i].IsAnchor && Math.Abs(beats[i].Bpm - beats[i - 1].Bpm) < RedundantAnchorBpm) count++;
        }

        return count;
    }

    /// <summary>The beats from the one holding the first note start to the one holding the last, or null without notes.</summary>
    public static (int First, int Last)? ChartedSpan(Arrangement arrangement, int beatCount)
    {
        int? first = null, last = null;
        foreach (var level in arrangement.Levels)
        {
            foreach (var note in level.Notes) Widen(note.Position.BeatId, ref first, ref last);
            foreach (var chord in level.Chords) Widen(chord.Position.BeatId, ref first, ref last);
        }

        if (first is not { } from || last is not { } to) return null;
        from = Math.Clamp(from, 0, beatCount - 2);
        to = Math.Clamp(to, from, beatCount - 2);
        return (from, to);
    }

    private static void Widen(int beat, ref int? first, ref int? last)
    {
        if (first == null || beat < first) first = beat;
        if (last == null || beat > last) last = beat;
    }

    private static void CheckRange(IList<BeatMarker> beats, (int First, int Last) span, ICollection<ArrangementIssue> issues)
    {
        var inRun = false;
        for (var i = span.First; i <= span.Last; i++)
        {
            var outside = BeatTempo(beats, i) is { } tempo && tempo is < MinBpm or > MaxBpm;
            if (outside && !inRun)
            {
                issues.Add(ArrangementIssues.Make(ArrangementIssueType.TempoOutOfRange, "Validation_TempoOutOfRange",
                    beats[i].Time, args: [BeatTempo(beats, i)!.Value, MinBpm, MaxBpm]));
            }

            inRun = outside;
        }
    }

    private static void CheckSpikes(IList<BeatMarker> beats, (int First, int Last) span, ICollection<ArrangementIssue> issues)
    {
        for (var i = Math.Max(1, span.First); i <= span.Last; i++)
        {
            if (SpikeOf(beats, i) < SpikeRatio) continue;
            issues.Add(ArrangementIssues.Make(ArrangementIssueType.TempoSpike, "Validation_TempoSpike", beats[i].Time,
                args: [BeatTempo(beats, i)!.Value, BeatTempo(beats, i - 1)!.Value]));
        }
    }

    private void CheckAudio(ArrangementValidationContext context, IList<BeatMarker> beats, (int First, int Last) span,
        ICollection<ArrangementIssue> issues)
    {
        if (audioTempo.Find(context.Project?.AudioFileName) is not { Bpm: > 0 } estimate) return;
        var lengthMs = beats[span.Last + 1].Time - beats[span.First].Time;
        if (lengthMs <= 0) return;

        var average = 60_000.0 * (span.Last + 1 - span.First) / lengthMs;
        var ratio = average / estimate.Bpm;
        var (type, key) = Math.Abs(ratio / 2 - 1) <= OctaveTolerance
            ? (ArrangementIssueType.TempoLooksDoubled, "Validation_TempoLooksDoubled")
            : Math.Abs(ratio * 2 - 1) <= OctaveTolerance
                ? (ArrangementIssueType.TempoLooksHalved, "Validation_TempoLooksHalved")
                : Math.Abs(ratio - 1) > AudioTolerance
                    ? (ArrangementIssueType.TempoOffAudio, "Validation_TempoOffAudio")
                    : (default(ArrangementIssueType?), "");
        if (type is { } found)
            issues.Add(ArrangementIssues.Make(found, key, args: [average, estimate.Bpm]));
    }

    private static void CheckAnchors(IList<BeatMarker> beats, ICollection<ArrangementIssue> issues)
    {
        var redundant = RedundantAnchors(beats);
        if (redundant < RedundantAnchorLimit) return;
        issues.Add(ArrangementIssues.Make(ArrangementIssueType.TempoAnchorSpam, "Validation_TempoAnchorSpam",
            args: [redundant]));
    }

    private static void CheckMeter(IList<BeatMarker> beats, ICollection<ArrangementIssue> issues)
    {
        var starts = new List<int>();
        for (var i = 0; i < beats.Count; i++)
        {
            if (beats[i].IsMeasure) starts.Add(i);
        }

        if (starts.Count < 2) return;
        if (starts[0] != 0 || starts[1] - starts[0] != beats[0].BeatsInMeasure)
        {
            issues.Add(ArrangementIssues.Make(ArrangementIssueType.MeterMissingOnFirstMeasure,
                "Validation_MeterMissingOnFirstMeasure", beats[0].Time, args: [starts[1] - starts[0]]));
        }

        var previousWrong = -1;
        for (var k = 1; k + 1 < starts.Count; k++)
        {
            var held = starts[k + 1] - starts[k];
            var wrong = held != beats[starts[k]].BeatsInMeasure ? held : -1;
            if (wrong > 0 && wrong != previousWrong)
            {
                issues.Add(ArrangementIssues.Make(ArrangementIssueType.MeterChangeUnmarked,
                    "Validation_MeterChangeUnmarked", beats[starts[k]].Time,
                    args: [k + 1, held, beats[starts[k]].BeatsInMeasure, beats[starts[k]].NoteDenominator]));
            }

            previousWrong = wrong;
        }
    }
}
