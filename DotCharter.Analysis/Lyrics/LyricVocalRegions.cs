namespace DotCharter.Analysis.Lyrics;

/// <summary>
///     The voice between a line's neighbours: every voiced stretch, split wherever the stem
///     rests for at least 150 ms. A line can span several (a breath, an instrumental break).
/// </summary>
internal sealed class LyricVocalRegions
{
    /// <summary>An onset may lead the first voiced frame by this much (a consonant) and still count as voiced.</summary>
    internal const double OnsetLeadSeconds = 0.080;

    /// <summary>The shortest silence that is a rest: it splits two regions and separates a region from the next line.</summary>
    internal const double MinRestSeconds = 0.150;

    /// <summary>One frame under the threshold is a consonant inside a word, not a pause before a line.</summary>
    private const double MinPauseSeconds = 0.020;

    private readonly VocalEnvelope _envelope;
    private readonly double _before;
    private readonly double _threshold;

    private LyricVocalRegions(VocalEnvelope envelope, double before, double threshold, (double Start, double End)[] regions)
    {
        _envelope = envelope;
        _before = before;
        _threshold = threshold;
        Regions = regions;
    }

    /// <summary>Voiced stretches in time order.</summary>
    internal IReadOnlyList<(double Start, double End)> Regions { get; }

    internal static LyricVocalRegions? Find(VocalEnvelope envelope, double before, double after, CancellationToken ct)
    {
        if (envelope.FrameCount == 0 || after <= before) return null;
        var threshold = Math.Max(0.002, envelope.ReferenceLevel(before, after, 0.9) * 0.15);
        var minRest = Math.Max(1, (int)Math.Ceiling(MinRestSeconds / envelope.HopSeconds));
        var regions = new List<(double Start, double End)>();
        var start = -1;
        var quiet = 0;
        var last = Math.Min(envelope.FrameCount, (int)Math.Ceiling(after / envelope.HopSeconds));
        for (var f = envelope.FrameAt(before); f <= last; f++)
        {
            ct.ThrowIfCancellationRequested();
            if (f < last && envelope.Level(f) >= threshold)
            {
                if (start < 0) start = f;
                quiet = 0;
            }
            else if (start >= 0 && (++quiet >= minRest || f == last))
            {
                regions.Add((Math.Max(before, start * envelope.HopSeconds), Math.Min(after, (f - quiet + 1) * envelope.HopSeconds)));
                start = -1;
            }
        }

        return regions.Count == 0 ? null : new LyricVocalRegions(envelope, before, threshold, [.. regions]);
    }

    /// <summary>The region an onset sits on, or -1 for an onset on unvoiced frames.</summary>
    internal int RegionOf(double seconds)
    {
        for (var r = 0; r < Regions.Count; r++)
            if (seconds >= Regions[r].Start - OnsetLeadSeconds && seconds < Regions[r].End) return r;

        return -1;
    }

    /// <summary>The region nearest in time to <paramref name="seconds" />; the earlier one when two are equally far.</summary>
    internal int Nearest(double seconds)
    {
        var nearest = 0;
        for (var r = 1; r < Regions.Count; r++)
            if (Distance(Regions[r], seconds) < Distance(Regions[nearest], seconds) - 1e-9) nearest = r;

        return nearest;
    }

    /// <summary>Whether the stem sounds at <paramref name="seconds" />, measured against this line's threshold.</summary>
    internal bool IsVoiced(double seconds) => _envelope.Level(_envelope.FrameAt(seconds)) >= _threshold;

    /// <summary>
    ///     Where region <paramref name="region" />'s voice began. A region cut off by the start of
    ///     this line's window may have begun earlier: then the voice is followed back past the
    ///     window to the last rest (as long as <see cref="Find" /> splits on).
    /// </summary>
    internal double VoiceBegan(int region)
    {
        if (Regions[region].Start > _before + 1e-9) return Regions[region].Start;

        var minRest = Math.Max(1, (int)Math.Ceiling(MinRestSeconds / _envelope.HopSeconds));
        var quiet = 0;
        for (var f = _envelope.FrameAt(_before); f >= 0; f--)
        {
            quiet = _envelope.Level(f) < _threshold ? quiet + 1 : 0;
            if (quiet >= minRest) return (f + quiet) * _envelope.HopSeconds;
        }

        return 0;
    }

    private static double Distance((double Start, double End) region, double seconds) =>
        Math.Max(0, Math.Max(region.Start - seconds, seconds - region.End));

    /// <summary>
    ///     Where the voice carrying <paramref name="onset" /> began: the frame after the last
    ///     pause since the previous line. Null when the voice never paused since then, so the
    ///     previous line's held note cannot be told from this line's opening.
    /// </summary>
    internal double? OpeningVoiceStart(double onset)
    {
        var first = _envelope.FrameAt(_before);
        var pause = Math.Max(1, (int)Math.Ceiling(MinPauseSeconds / _envelope.HopSeconds - 1e-9));
        var quiet = 0;
        for (var f = _envelope.FrameAt(onset); f >= first; f--)
        {
            quiet = _envelope.Level(f) < _threshold ? quiet + 1 : 0;
            if (quiet >= pause) return (f + quiet) * _envelope.HopSeconds;
        }

        return null;
    }
}
