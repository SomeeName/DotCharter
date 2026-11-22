namespace DotCharter.Services.Export.Rocksmith;

/// <summary>The XML and SNG end at min(chart extent, positive audio length) minus one millisecond.</summary>
internal static class RsSongLength
{
    public static int ResolveMs(Arrangement arrangement, IList<BeatMarker> beats, double? audioLengthMs)
    {
        var chartLengthMs = beats.Count > 0 ? (double)TimelineMath.ToWholeMs(beats[^1].Time) : 0;
        foreach (var level in arrangement.Levels)
        {
            foreach (var note in level.Notes)
                chartLengthMs = Math.Max(chartLengthMs, note.EndPosition.GetPositionMs(beats));
            foreach (var chord in level.Chords)
                chartLengthMs = Math.Max(chartLengthMs, chord.EndPosition.GetPositionMs(beats));
        }

        var endMs = audioLengthMs is { } audio && audio > 0
            ? Math.Min(chartLengthMs, audio)
            : chartLengthMs;

        return Math.Max(0, TimelineMath.ToWholeMs(endMs) - 1);
    }
}
