namespace DotCharter.Analysis.BeatMap;

/// <summary>Per beat: the estimated recording time and the local length of one beat there, in ms.</summary>
internal sealed record CoarseEstimate(double[] Times, double[] BeatMs);

/// <summary>One coarse warp the aligner may build on, and what its path cost per chart frame.</summary>
internal sealed record CoarseCandidate(string Name, CoarseEstimate Estimate, double CostPerFrame);

/// <summary>
///     <see cref="RecordingAligner" />'s first pass: a banded dynamic time warp between the chart's
///     expected features and the recording's on coarse frames, turned into an estimated recording
///     time for every beat. Internal; deterministic.
///     <para>Measured on the corpus probe: the skew step is priced high, because a loose warp
///     wandered off the diagonal by part of a beat wherever a full mix's features were ambiguous, on
///     tabs written at exactly the recording's tempo. A stiff warp in turn cuts corners when the
///     diagonal is NOT the song's tempo, so it is run on more than one chart axis and the written
///     tempo's warp is kept unless another explains the recording clearly more cheaply per chart frame
///     (<see cref="RecordingAlignmentOptions.CandidateMargin" />: on the corpus a wrong candidate was at most
///     1.6 % cheaper; on the synthetic tabs 3–10 % off tempo the right one was 3.5–15 % cheaper).</para>
/// </summary>
internal static class CoarseWarp
{
    private const double MinBandSeconds = 10;
    private const double BandFraction = 0.15;
    private const double FitBeats = 2.0;
    private const float ExtraOnsetCost = 0.25f;
    private const int LagBlock = 4;
    private const double MinRatio = 0.8;
    private const double RatioStep = 0.005;
    private const double RatioReachSeconds = 8;
    private const float LooseSkewPenalty = 0.4f;
    private const double CurveBeats = 8;
    private const float SilentChromaDistance = 0.3f;

    public static CoarseEstimate Estimate(RecordingAlignmentFeatures features, double[] grid, List<AlignmentEvent> events,
        RecordingAlignmentOptions options, CancellationToken ct) =>
        Choose(Candidates(features, grid, events, options, ct), options);

    /// <summary>The written tempo unless another candidate explains the recording clearly more cheaply.</summary>
    internal static CoarseEstimate Choose(IReadOnlyList<CoarseCandidate> candidates, RecordingAlignmentOptions options)
    {
        var best = candidates[0];
        foreach (var c in candidates.Skip(1))
        {
            if (c.CostPerFrame < (1 - options.CandidateMargin) * candidates[0].CostPerFrame && c.CostPerFrame < best.CostPerFrame) best = c;
        }

        return best.Estimate;
    }

    /// <summary>
    ///     The stiff warp prices every step off its diagonal, so the diagonal has to be the song's own tempo.
    ///     Candidates: the written tempo (always first), a straight line at the overall tempo that fits best,
    ///     and the smoothed path of a loose warp (a recording that speeds up and slows down); each lays the
    ///     chart on its own time axis and warps it stiffly.
    /// </summary>
    internal static List<CoarseCandidate> Candidates(RecordingAlignmentFeatures features, double[] grid, List<AlignmentEvent> events,
        RecordingAlignmentOptions options, CancellationToken ct)
    {
        var hop = features.CoarseHopMs;
        var origin = grid[0] - 1000;
        var end = grid[^1] + 1000;
        var skew = (float)options.CoarseSkewPenalty;
        var written = Pass(features, grid, events, options, UniformAxis(origin, end, hop), skew, null, ct);
        var list = new List<CoarseCandidate> { new("written", written.Estimate, written.CostPerFrame) };
        if (!options.CoarsePrescale) return list;

        var (ratio, lag) = BestRatio(features, grid, events, options, written.Lag, ct);
        if (ratio != 1)
        {
            var stretched = Pass(features, grid, events, options, UniformAxis(origin, end, hop / ratio), skew, lag, ct);
            list.Add(new CoarseCandidate("ratio", stretched.Estimate, stretched.CostPerFrame));
        }

        var loose = Pass(features, grid, events, options, UniformAxis(origin, end, hop), LooseSkewPenalty, written.Lag, ct);
        if (CurveAxis(grid, loose.Estimate.Times, origin, end, hop) is { } curve)
        {
            var followed = Pass(features, grid, events, options, curve.Bounds, skew, curve.Lag, ct);
            list.Add(new CoarseCandidate("curve", followed.Estimate, followed.CostPerFrame));
        }

        return list;
    }

    /// <summary>Chart-frame boundaries in chart ms, <paramref name="chartHop" /> apart.</summary>
    private static double[] UniformAxis(double origin, double end, double chartHop)
    {
        var frames = Math.Max(1, (int)Math.Ceiling((end - origin) / chartHop));
        var bounds = new double[frames + 1];
        for (var n = 0; n <= frames; n++) bounds[n] = origin + n * chartHop;
        return bounds;
    }

    /// <summary>
    ///     A chart axis that follows a loose warp: its beat times smoothed over ±<see cref="CurveBeats" /> beats
    ///     and kept between half and twice the chart's tempo make a monotone map from chart time to recording
    ///     time; the chart frames are the chart times that map onto evenly spaced recording frames. The lag is
    ///     the recording frame the first chart frame maps to.
    /// </summary>
    private static (double[] Bounds, int Lag)? CurveAxis(double[] grid, double[] times, double origin, double end, double hop)
    {
        var n = grid.Length;
        if (n < 2) return null;
        var index = new double[n];
        for (var i = 0; i < n; i++) index[i] = i;
        var smooth = new double[n];
        for (var i = 0; i < n; i++)
        {
            var (at, _) = Fit(index, times, Math.Max(0, i - (int)CurveBeats), Math.Min(n - 1, i + (int)CurveBeats), i, CurveBeats);
            smooth[i] = double.IsFinite(at) ? at : times[i];
        }

        for (var i = 1; i < n; i++)
        {
            var chart = grid[i] - grid[i - 1];
            smooth[i] = Math.Clamp(smooth[i], smooth[i - 1] + 0.5 * chart, smooth[i - 1] + 2 * chart);
        }

        var m0 = Map(grid, smooth, origin);
        var frames = Math.Max(1, (int)Math.Ceiling((Map(grid, smooth, end) - m0) / hop));
        var bounds = new double[frames + 1];
        for (var k = 0; k <= frames; k++) bounds[k] = Map(smooth, grid, m0 + k * hop);
        return (bounds, (int)Math.Round(m0 / hop));
    }

    /// <summary>Piecewise-linear map through (<paramref name="from" />[i], <paramref name="to" />[i]), both
    ///     ascending, extrapolated off the end segments.</summary>
    private static double Map(double[] from, double[] to, double x)
    {
        var i = Segment(from, x);
        return to[i] + (x - from[i]) * (to[i + 1] - to[i]) / (from[i + 1] - from[i]);
    }

    /// <summary>The segment [i, i+1] of an ascending array that holds <paramref name="x" />, the end segments outside it.</summary>
    private static int Segment(double[] values, double x)
    {
        var n = values.Length;
        if (x <= values[0]) return 0;
        if (x >= values[n - 1]) return n - 2;
        var lo = 0;
        var hi = n - 1;
        while (hi - lo > 1)
        {
            var mid = (lo + hi) / 2;
            if (values[mid] <= x) lo = mid;
            else hi = mid;
        }

        return lo;
    }

    /// <summary>
    ///     The recording's overall tempo against the chart's, and where the chart starts in it: every ratio
    ///     from <see cref="MinRatio" /> to 1/<see cref="MinRatio" /> (steps of <see cref="RatioStep" /> in log
    ///     tempo) is tried as a straight line through the recording — the chart's onsets and pitch classes laid
    ///     on a time axis stretched by it, slid around the written tempo's best lag — and the line whose
    ///     features match best per charted frame wins (ties go to the ratio nearer 1). A stiff warp needs this:
    ///     its diagonal must already be the song's tempo, or a tab written a few BPM off the recording costs a
    ///     skew step every few frames and the path cuts corners.
    /// </summary>
    private static (double Ratio, int Lag) BestRatio(RecordingAlignmentFeatures features, double[] grid, List<AlignmentEvent> events,
        RecordingAlignmentOptions options, int lag1, CancellationToken ct)
    {
        var hop = features.CoarseHopMs;
        var origin = grid[0] - 1000;
        var end = grid[^1] + 1000;
        var weight = (float)options.ChromaWeight;
        var audioOnset = Smooth(features.CoarseOnset);
        var ao = Pool(audioOnset, features.CoarseChroma, LagBlock, out var ac);

        // A ratio pivots the line about the chart's middle, where lag1 is best supported.
        var middle = (0.5 * (grid[0] + grid[^1]) - origin) / hop;
        var reach = (int)Math.Ceiling(RatioReachSeconds * 1000 / hop / LagBlock);
        var steps = (int)Math.Round(-Math.Log(MinRatio) / RatioStep);
        var bestRatio = 1.0;
        var bestLag = lag1;
        var bestScore = double.NegativeInfinity;
        var bestK = 0;
        for (var k = -steps; k <= steps; k++)
        {
            ct.ThrowIfCancellationRequested();
            var ratio = Math.Exp(k * RatioStep);
            var (onset, chroma) = ChartFeatures(grid, events, UniformAxis(origin, end, hop / ratio));
            var co = Pool(onset, chroma, LagBlock, out var cc);
            var centre = (int)Math.Round((middle + lag1 - ratio * middle) / LagBlock);
            var (lag, score) = Search(co, cc, ao, ac, centre, reach, weight, centre);
            if (score > bestScore + 1e-9 || (Math.Abs(score - bestScore) <= 1e-9 && Math.Abs(k) < Math.Abs(bestK)))
            {
                bestScore = score;
                bestRatio = ratio;
                bestK = k;
                bestLag = lag * LagBlock;
            }
        }

        if (bestK == 0) return (1, lag1);
        var (fo, fc) = ChartFeatures(grid, events, UniformAxis(origin, end, hop / bestRatio));
        return (bestRatio, Search(fo, fc, audioOnset, features.CoarseChroma, bestLag, LagBlock * 2, weight, bestLag).Lag);
    }

    /// <summary>One banded warp of the chart laid on <paramref name="bounds" /> (chart-frame boundaries in chart
    ///     ms) against the recording's coarse frames; its per-beat estimate, its cost per chart frame and the
    ///     lag its band was centred on.</summary>
    private static (CoarseEstimate Estimate, double CostPerFrame, int Lag) Pass(RecordingAlignmentFeatures features, double[] grid,
        List<AlignmentEvent> events, RecordingAlignmentOptions options, double[] bounds, float skew, int? fixedLag, CancellationToken ct)
    {
        var hop = features.CoarseHopMs;
        var chartFrames = bounds.Length - 1;
        var (chartOnset, chartChroma) = ChartFeatures(grid, events, bounds);
        var audioOnset = Smooth(features.CoarseOnset);
        var audioChroma = features.CoarseChroma;
        var audioFrames = audioOnset.Length;

        // The best constant offset (in frames, recording = chart + lag) centres the band.
        var maxLag = (int)(options.MaxOffsetSeconds * 1000 / hop);
        var originFrames = (int)Math.Round(bounds[0] / hop);
        var lag = fixedLag ?? BestLag(chartOnset, chartChroma, audioOnset, audioChroma, originFrames, maxLag, (float)options.ChromaWeight);
        var half = Math.Min(audioFrames, (int)(Math.Max(MinBandSeconds, BandFraction * chartFrames * hop / 1000) * 1000 / hop));
        var width = 2 * half + 1;

        var wChroma = (float)options.ChromaWeight;
        var moves = new byte[(long)chartFrames * width];
        var rowArg = new int[chartFrames];
        var rowCost = new float[chartFrames];
        var d2 = Filled(width);
        var d1 = Filled(width);
        var d0 = Filled(width);
        for (var n = 0; n < chartFrames; n++)
        {
            if ((n & 255) == 0) ct.ThrowIfCancellationRequested();
            var baseM = n + lag - half;
            for (var j = 0; j < width; j++)
            {
                var m = baseM + j;
                if (m < 0)
                {
                    d0[j] = float.PositiveInfinity;
                    continue;
                }

                // Past the end of the recording: a chart that runs on longer than the recording (an outro it
                // fades out of, a cut clip) keeps its own tempo there instead of being squeezed into it. Each
                // such frame costs as much as the worst real match, so the path never prefers it to audio.
                var cost = m < audioFrames
                    ? OnsetCost(chartOnset[n], audioOnset[m]) + wChroma * ChromaDistance(chartChroma[n], audioChroma[m])
                    : chartOnset[n] + ExtraOnsetCost + wChroma;
                if (n == 0)
                {
                    d0[j] = cost;
                    continue;
                }

                // (n-1, m-1) sits at the same band index one row up; (n-1, m-2) one to the left;
                // (n-2, m-1) one to the right two rows up — the band slides one frame per row.
                var best = d1[j] + cost;
                byte move = 1;
                if (j > 0 && d1[j - 1] + 2 * cost + skew < best)
                {
                    best = d1[j - 1] + 2 * cost + skew;
                    move = 2;
                }

                if (n > 1 && j + 1 < width && d2[j + 1] + 2 * cost + skew < best)
                {
                    best = d2[j + 1] + 2 * cost + skew;
                    move = 3;
                }

                d0[j] = best;
                moves[(long)n * width + j] = move;
            }

            var arg = 0;
            for (var j = 1; j < width; j++)
            {
                if (d0[j] < d0[arg]) arg = j;
            }

            rowArg[n] = arg;
            rowCost[n] = d0[arg];
            (d2, d1, d0) = (d1, d0, d2);
        }

        // Free end: the cheapest cell of the last row (an earlier row only if a band ran off the recording's start).
        var endRow = chartFrames - 1;
        while (endRow > 0 && float.IsPositiveInfinity(rowCost[endRow])) endRow--;
        var path = new double[chartFrames];
        var skews = 0;
        if (float.IsPositiveInfinity(rowCost[endRow]))
        {
            for (var n = 0; n < chartFrames; n++) path[n] = n + lag;
        }
        else
        {
            for (var n = endRow + 1; n < chartFrames; n++) path[n] = endRow + lag - half + rowArg[endRow] + (n - endRow);
            var row = endRow;
            var col = rowArg[endRow];
            while (row >= 0)
            {
                var m = row + lag - half + col;
                path[row] = m;
                if (row == 0) break;
                switch (moves[(long)row * width + col])
                {
                    case 2:
                        if (m < audioFrames) skews++;
                        row -= 1;
                        col -= 1;
                        break;
                    case 3:
                        if (m < audioFrames) skews++;
                        path[row - 1] = m - 0.5;
                        row -= 2;
                        col += 1;
                        break;
                    default:
                        row -= 1;
                        break;
                }
            }
        }

        // What the path costs per chart frame INSIDE the recording (frames past its end are priced as the worst
        // match so the path avoids them, which must not make a warp that keeps a chart's outro out of the
        // recording look worse than one that squeezes it in): the match along the path plus its skew steps.
        double sum = 0;
        var inside = 0;
        for (var n = 0; n < chartFrames; n++)
        {
            var m = (int)Math.Floor(path[n]);
            if (m < 0 || m >= audioFrames) continue;
            sum += OnsetCost(chartOnset[n], audioOnset[m]) + wChroma * ChromaDistance(chartChroma[n], audioChroma[m]);
            inside++;
        }

        var costPerFrame = inside == 0 ? double.PositiveInfinity : (sum + skew * skews) / inside;
        return (InBeats(path, grid, bounds, hop), costPerFrame, lag);
    }

    /// <summary>
    ///     The warp path re-expressed per BEAT: a local linear fit of recording time against beat
    ///     position (±<see cref="FitBeats" /> beats, triangular weights) gives each beat's estimated
    ///     time and the local length of a beat in the recording. Fitting in beats rather than in
    ///     chart milliseconds keeps a chart whose own grid is uneven (a hand-placed beat far off its
    ///     neighbours) from bending the tempo it is measured against.
    /// </summary>
    private static CoarseEstimate InBeats(double[] path, double[] grid, double[] bounds, double hop)
    {
        var frames = path.Length;
        var pos = new double[frames];
        var rec = new double[frames];
        for (var n = 0; n < frames; n++)
        {
            pos[n] = RecordingAligner.PositionAt(grid, 0.5 * (bounds[n] + bounds[n + 1]));
            rec[n] = (path[n] + 0.5) * hop;
        }

        var (_, globalSlope) = Fit(pos, rec, 0, frames - 1, double.NaN, double.PositiveInfinity);
        if (!(globalSlope > 0)) globalSlope = grid.Length > 1 ? (grid[^1] - grid[0]) / (grid.Length - 1) : 500;

        var times = new double[grid.Length];
        var beatMs = new double[grid.Length];
        var lo = 0;
        var hi = 0;
        for (var i = 0; i < grid.Length; i++)
        {
            // Only frames inside the grid: before the first beat and after the last the chart is silent and
            // the path is free, so a window reaching there would bend the first and last beats' tempo.
            while (lo < frames - 1 && pos[lo] < Math.Max(0, i - FitBeats)) lo++;
            while (hi < frames - 1 && pos[hi + 1] <= Math.Min(grid.Length - 1, i + FitBeats)) hi++;
            var (at, slope) = Fit(pos, rec, lo, hi, i, FitBeats);
            if (!(slope > 0.25 * globalSlope && slope < 4 * globalSlope) || !double.IsFinite(at))
            {
                slope = globalSlope;
                at = double.IsFinite(at) ? at : rec[Math.Clamp(lo, 0, frames - 1)];
            }

            times[i] = at;
            beatMs[i] = slope;
        }

        return new CoarseEstimate(times, beatMs);
    }

    /// <summary>Weighted least squares of <paramref name="y" /> on <paramref name="x" /> over [lo, hi];
    ///     returns the fitted value at <paramref name="centre" /> (NaN centre: the plain fit's intercept) and the slope.</summary>
    private static (double At, double Slope) Fit(double[] x, double[] y, int lo, int hi, double centre, double width)
    {
        double sw = 0, sx = 0, sy = 0, sxx = 0, sxy = 0;
        for (var k = lo; k <= hi; k++)
        {
            var w = double.IsNaN(centre) ? 1 : Math.Max(0, 1 - Math.Abs(x[k] - centre) / (width + 1e-9));
            if (w <= 0) continue;
            sw += w;
            sx += w * x[k];
            sy += w * y[k];
            sxx += w * x[k] * x[k];
            sxy += w * x[k] * y[k];
        }

        if (sw <= 0) return (double.NaN, double.NaN);
        var mx = sx / sw;
        var my = sy / sw;
        var vxx = sxx / sw - mx * mx;
        if (vxx <= 1e-12) return (my, double.NaN);
        var slope = (sxy / sw - mx * my) / vxx;
        return (my + slope * ((double.IsNaN(centre) ? mx : centre) - mx), slope);
    }

    private static float[] Filled(int width)
    {
        var a = new float[width];
        Array.Fill(a, float.PositiveInfinity);
        return a;
    }

    private static float ChromaDistance(float[] a, float[] b)
    {
        float dot = 0, na = 0, nb = 0;
        for (var i = 0; i < 12; i++)
        {
            dot += a[i] * b[i];
            na += a[i];
            nb += b[i];
        }

        return na == 0 || nb == 0 ? SilentChromaDistance : 1 - dot;
    }

    private static (float[] Onset, float[][] Chroma) ChartFeatures(double[] grid, List<AlignmentEvent> events, double[] bounds)
    {
        var frames = bounds.Length - 1;
        var onset = new float[frames];
        foreach (var e in events)
        {
            var f = FrameOf(bounds, RecordingAligner.TimeAt(grid, e.BeatPosition));
            if (f >= 0) onset[f] = Math.Min(1f, onset[f] + e.Weight);
        }

        var chroma = new float[frames][];
        var bins = new double[12];
        var first = 0;
        for (var f = 0; f < frames; f++)
        {
            var chartMs = 0.5 * (bounds[f] + bounds[f + 1]);
            Array.Clear(bins);
            // Sounds ring at most 2 s: skip those that ended long before this frame.
            while (first < events.Count && RecordingAligner.TimeAt(grid, events[first].BeatPosition) < chartMs - 2100) first++;
            for (var i = first; i < events.Count; i++)
            {
                if (RecordingAligner.TimeAt(grid, events[i].BeatPosition) > chartMs) break;
                RecordingAligner.AddExpectedChroma(bins, events[i], grid, chartMs);
            }

            chroma[f] = RecordingAlignmentFeatures.UnitChroma(bins, 1e-3);
        }

        return (Smooth(onset), chroma);
    }

    /// <summary>The frame whose [start, end) holds <paramref name="ms" />, or −1 outside the axis.</summary>
    private static int FrameOf(double[] bounds, double ms)
    {
        if (!(ms >= bounds[0]) || ms >= bounds[^1]) return -1;
        var lo = 0;
        var hi = bounds.Length - 1;
        while (hi - lo > 1)
        {
            var mid = (lo + hi) / 2;
            if (bounds[mid] <= ms) lo = mid;
            else hi = mid;
        }

        return lo;
    }

    private static float[] Smooth(float[] x)
    {
        var y = new float[x.Length];
        for (var i = 0; i < x.Length; i++)
        {
            var l = i > 0 ? x[i - 1] : 0f;
            var r = i + 1 < x.Length ? x[i + 1] : 0f;
            y[i] = Math.Min(1f, 0.5f * x[i] + 0.25f * (l + r));
        }

        return y;
    }

    /// <summary>A charted onset with no onset under it costs fully; an onset the chart does not expect
    ///     (drums, a voice, another instrument in a full mix) costs a quarter as much.</summary>
    private static float OnsetCost(float chart, float audio) => chart * (1 - audio) + ExtraOnsetCost * audio * (1 - chart);

    /// <summary>
    ///     The constant lag (recording frame − chart frame) under which the chart's onsets AND pitch
    ///     classes best match the recording's: searched on <see cref="LagBlock" />-frame blocks over the
    ///     whole reach, then refined frame by frame around the winner. Onsets alone are periodic in a full
    ///     mix (every beat has a drum hit), so the chroma is what pins the bar.
    /// </summary>
    private static int BestLag(float[] chartOnset, float[][] chartChroma, float[] audioOnset, float[][] audioChroma,
        int identity, int reach, float chromaWeight)
    {
        var co = Pool(chartOnset, chartChroma, LagBlock, out var cc);
        var ao = Pool(audioOnset, audioChroma, LagBlock, out var ac);
        var coarse = Search(co, cc, ao, ac, identity / LagBlock, reach / LagBlock + 1, chromaWeight, identity / LagBlock).Lag;
        return Search(chartOnset, chartChroma, audioOnset, audioChroma, coarse * LagBlock, LagBlock * 2, chromaWeight, identity).Lag;
    }

    /// <summary>The best lag in [centre − reach, centre + reach] (by the summed score, as ever) and that lag's
    ///     score per chart frame it laid inside the recording.</summary>
    private static (int Lag, double Score) Search(float[] co, float[][] cc, float[] ao, float[][] ac, int centre, int reach, float chromaWeight, int prefer)
    {
        var active = new List<int>();
        for (var n = 0; n < co.Length; n++)
        {
            if (co[n] > 0.2f || cc[n][0] + cc[n][1] + cc[n][2] + cc[n][3] + cc[n][4] + cc[n][5] + cc[n][6] + cc[n][7] + cc[n][8] + cc[n][9] + cc[n][10] + cc[n][11] > 0)
                active.Add(n);
        }

        var bestLag = centre;
        var best = double.NegativeInfinity;
        var bestCounted = 0;
        for (var lag = centre - reach; lag <= centre + reach; lag++)
        {
            double s = 0;
            var counted = 0;
            foreach (var n in active)
            {
                var m = n + lag;
                if (m < 0 || m >= ao.Length) continue;
                counted++;
                s += co[n] * ao[m];
                double dot = 0;
                for (var p = 0; p < 12; p++) dot += cc[n][p] * ac[m][p];
                s += chromaWeight * dot;
            }

            // Lags that leave most of the chart outside the recording are not evidence.
            if (counted < active.Count / 2) continue;
            if (s > best + 1e-9 || (Math.Abs(s - best) <= 1e-9 && Math.Abs(lag - prefer) < Math.Abs(bestLag - prefer)))
            {
                best = s;
                bestLag = lag;
                bestCounted = counted;
            }
        }

        return (bestLag, bestCounted == 0 ? double.NegativeInfinity : best / bestCounted);
    }

    /// <summary>Max-pooled onsets and re-normalised mean chroma over blocks of <paramref name="block" /> frames.</summary>
    private static float[] Pool(float[] onset, float[][] chroma, int block, out float[][] pooledChroma)
    {
        var count = (onset.Length + block - 1) / block;
        var pooled = new float[count];
        pooledChroma = new float[count][];
        var bins = new double[12];
        for (var b = 0; b < count; b++)
        {
            Array.Clear(bins);
            for (var k = b * block; k < Math.Min(onset.Length, (b + 1) * block); k++)
            {
                pooled[b] = Math.Max(pooled[b], onset[k]);
                for (var p = 0; p < 12; p++) bins[p] += chroma[k][p];
            }

            var norm = Math.Sqrt(bins.Sum(v => v * v));
            pooledChroma[b] = new float[12];
            if (norm <= 0) continue;
            for (var p = 0; p < 12; p++) pooledChroma[b][p] = (float)(bins[p] / norm);
        }

        return pooled;
    }
}
