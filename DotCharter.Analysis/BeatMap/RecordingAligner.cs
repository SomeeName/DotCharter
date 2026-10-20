namespace DotCharter.Analysis.BeatMap;

/// <summary>One beat of the grid being aligned. A <paramref name="IsFixed" /> beat keeps its time:
///     a locked (hand-placed) beat, or one outside the range being aligned.</summary>
public readonly record struct AlignmentBeat(double TimeMs, bool IsFixed);

/// <summary>
///     One sound the chart expects: its grid position in beats (beat index + fraction, the same
///     <c>BeatId + Fraction</c> a <c>FractionalPosition</c> carries), how long it rings in beats, and
///     its sounding MIDI pitches (empty when unknown — the onset still counts).
/// </summary>
public sealed record AlignmentEvent(double BeatPosition, double SustainBeats, IReadOnlyList<int> Midi, float Weight = 1f);

public sealed class RecordingAlignmentOptions
{
    /// <summary>How far a beat may move from the coarse warp's estimate.</summary>
    public double SearchRadiusMs { get; init; } = 160;

    /// <summary>Price of a beat interval that departs from the local tempo, per squared log ratio.</summary>
    public double TempoSmoothness { get; init; } = 150;

    /// <summary>Price of the full search radius away from the coarse estimate.</summary>
    public double PriorWeight { get; init; } = 0.3;

    /// <summary>Frame length of the coarse warp.</summary>
    public double CoarseHopMs { get; init; } = 40;

    /// <summary>Weight of the pitch-class (chroma) distance in the coarse warp, against the onset distance's 1.</summary>
    public double ChromaWeight { get; init; } = 1.0;

    /// <summary>Largest constant offset between the chart and the recording the coarse warp looks for.</summary>
    public double MaxOffsetSeconds { get; init; } = 120;

    // The options below are the tuned defaults; the corpus probe and RecordingAlignerDenseMixTests set them back to
    // the plain-warp values (skew 0.4, no candidates, no ring; TempoSmoothness 30) to measure against it.

    /// <summary>The coarse warp's price of a (1,2)/(2,1) step over a diagonal one: high, so the path keeps to
    ///     its diagonal where the features are ambiguous (a dense mix, a repeated chord) instead of wandering.</summary>
    internal double CoarseSkewPenalty { get; init; } = 2;

    /// <summary>Besides the written tempo, also warp on a chart axis at the best overall tempo and on one that
    ///     follows a loose warp (<c>CoarseWarp.Candidates</c>).</summary>
    internal bool CoarsePrescale { get; init; } = true;

    /// <summary>Let every charted sound ring until the next in the expected pitch classes.</summary>
    internal bool LetRing { get; init; } = true;

    /// <summary>The longest a sound is let ring into a rest, in beats.</summary>
    internal double RingCapBeats { get; init; } = 1;

    /// <summary>How much cheaper per chart frame another coarse candidate must be than the written tempo's to win.</summary>
    internal double CandidateMargin { get; init; } = 0.025;
}

/// <summary>How well one section of the chart matched the recording after alignment.
///     <paramref name="EndBeat" /> is exclusive. <paramref name="Confidence" /> is 0..1, or NaN when
///     the section has no notes to judge it by.</summary>
public sealed record AlignmentSectionScore(int StartBeat, int EndBeat, double Confidence, int EventCount, double MeanShiftMs);

public sealed class RecordingAlignmentResult(IReadOnlyList<double> beatTimesMs, IReadOnlyList<AlignmentSectionScore> sections)
{
    /// <summary>The aligned time of every input beat, same order and count; fixed beats unchanged.</summary>
    public IReadOnlyList<double> BeatTimesMs { get; } = beatTimesMs;

    public IReadOnlyList<AlignmentSectionScore> Sections { get; } = sections;
}

/// <summary>
/// Warps an existing chart's beat grid onto a real recording of it. The chart is the
///     WRITTEN structure — its notes sit at grid positions — and only the beat TIMES move.
///     <para>Two passes. (1) A coarse dynamic time warp (40 ms frames, steps (1,1)/(1,2)/(2,1) so
///     the local tempo ratio stays within ½…2, each skew step priced high so the path keeps to its
///     diagonal) between what the chart should sound like — an onset pulse per note plus a pitch-class
///     profile from its MIDI pitches, each sound ringing until the next — and what the recording is —
///     its onset envelope and chroma — inside a band around the best constant offset. Because a stiff
///     path needs its diagonal to be the song's tempo, the warp is run on up to three chart axes (the
///     written tempo, the best straight line at another tempo, a loose warp's smoothed path) and the
///     written tempo is kept unless another is clearly cheaper (<see cref="CoarseWarp" />). Its path,
///     re-fitted per beat, maps every beat to an estimated recording time. (2) A Viterbi pass over
///     the beats: each free beat may sit anywhere within <see cref="RecordingAlignmentOptions.SearchRadiusMs" />
///     of that estimate (on the ~2.7 ms onset frames); an interval scores the onset strength under
///     every note it contains (each note at its own fraction of the interval) minus a squared
///     log-tempo-ratio penalty against the coarse local tempo. Fixed beats have exactly one
///     candidate, so the grid aligns AROUND them and never moves them.</para>
///     <para>Deterministic: no randomness, no parallelism, no state between calls. The whole clip is
///     held in memory (an explicit user action, like the audio lint), never a per-tick path.</para>
/// </summary>
public sealed class RecordingAligner
{
    /// <summary>Below this a section is offered as "skip" by default.</summary>
    public const double LowConfidence = 0.5;

    private const double HitRadiusMs = 30;
    private const float HitLevel = 0.3f;
    private const float PeakLevel = 0.5f;
    private const double PeakMatchMs = 40;
    private const double ChartChromaMinMs = 150;
    private const double ChartChromaMaxMs = 2000;

    public RecordingAlignmentResult Align(ReadOnlySpan<float> mono, int sampleRate, IReadOnlyList<AlignmentBeat> beats,
        IReadOnlyList<AlignmentEvent> events, IReadOnlyList<int> sectionStarts, RecordingAlignmentOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        options ??= new RecordingAlignmentOptions();
        var old = new double[beats.Count];
        for (var i = 0; i < beats.Count; i++)
        {
            old[i] = beats[i].TimeMs;
            if (!double.IsFinite(old[i]) || (i > 0 && old[i] <= old[i - 1]))
                throw new ArgumentException("Beat times must be finite and strictly ascending.", nameof(beats));
        }

        var usable = UsableEvents(events, beats.Count);
        if (beats.Count < 2 || mono.Length < sampleRate || !beats.Any(b => !b.IsFixed))
            return new RecordingAlignmentResult(old, Score(old, old, usable, sectionStarts, null));

        var features = RecordingAlignmentFeatures.Compute(mono, sampleRate, options.CoarseHopMs, cancellationToken);
        return Align(features, beats, old, usable, sectionStarts, options, cancellationToken);
    }

    /// <summary>The passes on features already computed (the corpus probe compares option sets on one song's features).</summary>
    internal static RecordingAlignmentResult Align(RecordingAlignmentFeatures features, IReadOnlyList<AlignmentBeat> beats,
        IReadOnlyList<AlignmentEvent> events, IReadOnlyList<int> sectionStarts, RecordingAlignmentOptions options,
        CancellationToken cancellationToken)
    {
        var old = beats.Select(b => b.TimeMs).ToArray();
        return Align(features, beats, old, UsableEvents(events, beats.Count), sectionStarts, options, cancellationToken);
    }

    private static RecordingAlignmentResult Align(RecordingAlignmentFeatures features, IReadOnlyList<AlignmentBeat> beats,
        double[] old, List<AlignmentEvent> usable, IReadOnlyList<int> sectionStarts, RecordingAlignmentOptions options,
        CancellationToken cancellationToken)
    {
        var ringing = options.LetRing ? LetRing(usable, options.RingCapBeats) : usable;
        var prior = CoarseWarp.Estimate(features, old, ringing, options, cancellationToken);
        var aligned = PlaceBeats(features, beats, old, prior, usable, options, cancellationToken);
        return new RecordingAlignmentResult(aligned, Score(old, aligned, ringing, sectionStarts, features));
    }

    /// <summary>
    ///     What the chart SOUNDS like for the pitch-class match: a plucked note rings on until the next one
    ///     is played, whatever sustain it is charted with (a tab marks a held note, not a quarter note's
    ///     natural decay), so each sound's ring reaches at least the next sound's position. The chroma
    ///     model still caps it at 2 s.
    /// </summary>
    internal static List<AlignmentEvent> LetRing(List<AlignmentEvent> events, double capBeats = double.PositiveInfinity)
    {
        var result = new List<AlignmentEvent>(events.Count);
        var next = 0;
        for (var i = 0; i < events.Count; i++)
        {
            if (next <= i) next = i + 1;
            while (next < events.Count && events[next].BeatPosition <= events[i].BeatPosition) next++;
            var gap = next < events.Count ? Math.Min(capBeats, events[next].BeatPosition - events[i].BeatPosition) : events[i].SustainBeats;
            result.Add(gap > events[i].SustainBeats ? events[i] with { SustainBeats = gap } : events[i]);
        }

        return result;
    }

    internal static List<AlignmentEvent> UsableEvents(IReadOnlyList<AlignmentEvent> events, int beatCount)
    {
        var list = new List<AlignmentEvent>(events.Count);
        foreach (var e in events)
        {
            if (!double.IsFinite(e.BeatPosition) || !double.IsFinite(e.SustainBeats) || !float.IsFinite(e.Weight)) continue;
            if (e.BeatPosition < 0 || e.BeatPosition >= beatCount || e.Weight <= 0) continue;
            list.Add(e);
        }

        list.Sort(static (a, b) => a.BeatPosition.CompareTo(b.BeatPosition));
        return list;
    }

    /// <summary>Chart time of a beat position on a grid, extrapolating off the last interval.</summary>
    internal static double TimeAt(double[] grid, double position)
    {
        var i = (int)Math.Floor(position);
        if (i < 0) i = 0;
        if (i >= grid.Length - 1) i = grid.Length - 2;
        return grid[i] + (position - i) * (grid[i + 1] - grid[i]);
    }

    // ------------------------------------------------------------ pass 2

    private static double[] PlaceBeats(RecordingAlignmentFeatures features, IReadOnlyList<AlignmentBeat> beats,
        double[] old, CoarseEstimate coarse, List<AlignmentEvent> events, RecordingAlignmentOptions options, CancellationToken ct)
    {
        var prior = coarse.Times;
        var n = beats.Count;
        var step = features.FineHopMs;
        var radius = options.SearchRadiusMs;

        // Events grouped by the beat interval they fall in, as fractions of it.
        var byBeat = new List<(double Fraction, float Weight)>[n];
        foreach (var e in events)
        {
            var i = Math.Min(n - 1, (int)Math.Floor(e.BeatPosition));
            (byBeat[i] ??= []).Add((e.BeatPosition - i, e.Weight));
        }

        // Candidate times per beat. A free beat's window stays strictly between its fixed neighbours.
        var candidates = new double[n][];
        var prevFixed = double.NegativeInfinity;
        var nextFixed = new double[n];
        var next = double.PositiveInfinity;
        for (var i = n - 1; i >= 0; i--)
        {
            nextFixed[i] = next;
            if (beats[i].IsFixed) next = old[i];
        }

        for (var i = 0; i < n; i++)
        {
            if (beats[i].IsFixed)
            {
                candidates[i] = [old[i]];
                prevFixed = old[i];
                continue;
            }

            var lo = Math.Max(prior[i] - radius, prevFixed + step);
            var hi = Math.Min(prior[i] + radius, nextFixed[i] - step);
            if (lo > hi)
            {
                var gapLo = prevFixed + step;
                var gapHi = nextFixed[i] - step;
                lo = hi = double.IsFinite(gapLo) && double.IsFinite(gapHi) ? (gapLo + gapHi) / 2 : Math.Clamp(prior[i], gapLo, gapHi);
            }

            var count = (int)Math.Floor((hi - lo) / step) + 1;
            var c = new double[count];
            for (var k = 0; k < count; k++) c[k] = lo + k * step;
            candidates[i] = c;
        }

        var score = new double[n][];
        var back = new int[n][];
        score[0] = new double[candidates[0].Length];
        back[0] = new int[candidates[0].Length];
        for (var k = 0; k < candidates[0].Length; k++)
            score[0][k] = Unary(candidates[0][k], prior[0], radius, options, beats[0].IsFixed);

        for (var i = 1; i < n; i++)
        {
            if ((i & 31) == 0) ct.ThrowIfCancellationRequested();
            var prevC = candidates[i - 1];
            var curC = candidates[i];
            var expected = Math.Max(step, 0.5 * (coarse.BeatMs[i - 1] + coarse.BeatMs[i]));
            var inInterval = byBeat[i - 1];
            score[i] = new double[curC.Length];
            back[i] = new int[curC.Length];
            for (var b = 0; b < curC.Length; b++)
            {
                var best = double.NegativeInfinity;
                var arg = 0;
                for (var a = 0; a < prevC.Length; a++)
                {
                    var from = score[i - 1][a];
                    if (double.IsNegativeInfinity(from)) continue;
                    var interval = curC[b] - prevC[a];
                    if (interval < 0.25 * expected) continue;
                    var s = from - options.TempoSmoothness * Sq(Math.Log(interval / expected));
                    if (inInterval != null)
                    {
                        foreach (var (fraction, weight) in inInterval)
                            s += weight * features.SmoothOnsetAt(prevC[a] + fraction * interval);
                    }

                    if (s > best)
                    {
                        best = s;
                        arg = a;
                    }
                }

                score[i][b] = best + Unary(curC[b], prior[i], radius, options, beats[i].IsFixed);
                back[i][b] = arg;
            }
        }

        // The last interval has no next beat: its notes are scored at the coarse tempo.
        var lastInterval = Math.Max(step, coarse.BeatMs[n - 1]);
        var bestEnd = 0;
        var bestEndScore = double.NegativeInfinity;
        for (var b = 0; b < candidates[n - 1].Length; b++)
        {
            var s = score[n - 1][b];
            if (byBeat[n - 1] is { } tail)
            {
                foreach (var (fraction, weight) in tail)
                    s += weight * features.SmoothOnsetAt(candidates[n - 1][b] + fraction * lastInterval);
            }

            if (s > bestEndScore)
            {
                bestEndScore = s;
                bestEnd = b;
            }
        }

        var result = new double[n];
        if (double.IsNegativeInfinity(bestEndScore)) return (double[])old.Clone();
        var idx = bestEnd;
        for (var i = n - 1; i >= 0; i--)
        {
            result[i] = beats[i].IsFixed ? old[i] : candidates[i][idx];
            idx = back[i][idx];
        }

        return result;
    }

    private static double Unary(double t, double prior, double radius, RecordingAlignmentOptions options, bool isFixed) =>
        isFixed ? 0 : -options.PriorWeight * Sq((t - prior) / radius);

    private static double Sq(double x) => x * x;

    // ------------------------------------------------------------ confidence

    private static List<AlignmentSectionScore> Score(double[] old, double[] aligned, List<AlignmentEvent> events,
        IReadOnlyList<int> sectionStarts, RecordingAlignmentFeatures? features)
    {
        var n = old.Length;
        var starts = sectionStarts.Where(s => s >= 0 && s < n).Distinct().Order().ToList();
        if (starts.Count == 0 || starts[0] != 0) starts.Insert(0, 0);
        var sections = new List<AlignmentSectionScore>(starts.Count);
        for (var s = 0; s < starts.Count; s++)
        {
            var start = starts[s];
            var end = s + 1 < starts.Count ? starts[s + 1] : n;
            var shift = 0.0;
            for (var i = start; i < end; i++) shift += Math.Abs(aligned[i] - old[i]);
            shift /= Math.Max(1, end - start);

            var inSection = events.Where(e => e.BeatPosition >= start && e.BeatPosition < end).ToList();
            var confidence = features == null || inSection.Count == 0 || n < 2
                ? double.NaN
                : Confidence(features, old, aligned, inSection, start, end);
            sections.Add(new AlignmentSectionScore(start, end, confidence, inSection.Count, shift));
        }

        return sections;
    }

    /// <summary>
    ///     Recall (how many charted notes have an onset under them), precision (how many strong
    ///     onsets in the section's span have a charted note near them) and, when the chart has
    ///     pitches, the chroma agreement between the chart and the recording along the alignment.
    /// </summary>
    private static double Confidence(RecordingAlignmentFeatures features, double[] old, double[] aligned,
        List<AlignmentEvent> inSection, int start, int end)
    {
        double hit = 0, total = 0;
        var times = new List<double>(inSection.Count);
        foreach (var e in inSection)
        {
            var t = TimeAt(aligned, e.BeatPosition);
            times.Add(t);
            total += e.Weight;
            if (features.PeakOnsetNear(t, HitRadiusMs) >= HitLevel) hit += e.Weight;
        }

        var recall = total > 0 ? hit / total : 0;

        var from = aligned[start];
        var to = end < aligned.Length ? aligned[end] : TimeAt(aligned, end);
        var onset = features.Onset;
        int peaks = 0, matched = 0;
        var k0 = Math.Max(1, (int)Math.Ceiling(features.FineFrameAt(from)));
        var k1 = Math.Min(onset.Length - 2, (int)Math.Floor(features.FineFrameAt(to)));
        var minGap = (int)Math.Ceiling(50 / features.FineHopMs);
        var lastPeak = int.MinValue;
        for (var k = k0; k <= k1; k++)
        {
            if (onset[k] < PeakLevel || onset[k] < onset[k - 1] || onset[k] < onset[k + 1] || k - lastPeak < minGap) continue;
            lastPeak = k;
            peaks++;
            var t = features.FineFrameTimeMs(k);
            if (NearAny(times, t, PeakMatchMs)) matched++;
        }

        var precision = peaks > 0 ? (double)matched / peaks : recall;
        var chroma = ChromaAgreement(features, old, aligned, inSection, start, end);
        return chroma is { } c
            ? Math.Clamp(0.5 * recall + 0.25 * precision + 0.25 * c, 0, 1)
            : Math.Clamp(0.65 * recall + 0.35 * precision, 0, 1);
    }

    private static bool NearAny(List<double> sorted, double t, double radius)
    {
        var lo = 0;
        var hi = sorted.Count - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (sorted[mid] < t - radius) lo = mid + 1;
            else if (sorted[mid] > t + radius) hi = mid - 1;
            else return true;
        }

        return false;
    }

    /// <summary>Mean cosine between the chart's expected chroma and the recording's along the aligned
    ///     grid, over coarse frames where both sound; null when the chart carries no pitches here.</summary>
    private static double? ChromaAgreement(RecordingAlignmentFeatures features, double[] old, double[] aligned,
        List<AlignmentEvent> inSection, int start, int end)
    {
        if (inSection.All(e => e.Midi.Count == 0)) return null;
        var hop = features.CoarseHopMs;
        var sum = 0.0;
        var count = 0;
        var bins = new double[12];
        var steps = Math.Max(1, (int)((TimeAt(old, end) - old[start]) / hop));
        for (var f = 0; f < steps; f++)
        {
            var chartMs = old[start] + (f + 0.5) * hop;
            var position = PositionAt(old, chartMs);
            Array.Clear(bins);
            foreach (var e in inSection)
                AddExpectedChroma(bins, e, old, chartMs);
            var expected = RecordingAlignmentFeatures.UnitChroma(bins, 1e-3);
            if (expected.All(v => v == 0)) continue;
            var frame = (int)(TimeAt(aligned, position) / hop);
            if (frame < 0 || frame >= features.CoarseChroma.Length) continue;
            var observed = features.CoarseChroma[frame];
            if (observed.All(v => v == 0)) continue;
            double dot = 0;
            for (var p = 0; p < 12; p++) dot += expected[p] * observed[p];
            sum += dot;
            count++;
        }

        return count == 0 ? null : sum / count;
    }

    /// <summary>The pitch-class energy one charted sound contributes at a chart time: its pitch and its
    ///     first overtones, decaying over its sustain (at least 150 ms, at most 2 s).</summary>
    internal static void AddExpectedChroma(double[] bins, AlignmentEvent e, double[] grid, double chartMs)
    {
        if (e.Midi.Count == 0) return;
        var onset = TimeAt(grid, e.BeatPosition);
        var ring = Math.Clamp(TimeAt(grid, e.BeatPosition + e.SustainBeats) - onset, ChartChromaMinMs, ChartChromaMaxMs);
        var dt = chartMs - onset;
        if (dt < 0 || dt > ring) return;
        var level = e.Weight * (1.0 - 0.5 * dt / ring);
        foreach (var midi in e.Midi)
        {
            bins[((midi % 12) + 12) % 12] += level;
            bins[((midi + 7) % 12 + 12) % 12] += 0.4 * level;
            bins[((midi + 4) % 12 + 12) % 12] += 0.2 * level;
        }
    }

    /// <summary>The inverse of <see cref="TimeAt" />: beat position of a chart time.</summary>
    internal static double PositionAt(double[] grid, double ms)
    {
        var n = grid.Length;
        if (ms <= grid[0]) return (ms - grid[0]) / (grid[1] - grid[0]);
        if (ms >= grid[n - 1]) return n - 1 + (ms - grid[n - 1]) / (grid[n - 1] - grid[n - 2]);
        var lo = 0;
        var hi = n - 1;
        while (hi - lo > 1)
        {
            var mid = (lo + hi) / 2;
            if (grid[mid] <= ms) lo = mid;
            else hi = mid;
        }

        return lo + (ms - grid[lo]) / (grid[lo + 1] - grid[lo]);
    }
}
