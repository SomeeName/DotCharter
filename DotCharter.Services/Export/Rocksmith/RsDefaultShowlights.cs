namespace DotCharter.Services.Export.Rocksmith;

/// <summary>
///     The show-light set the package writes when a project has none. It is generated from the sections and
///     notes by <see cref="RsShowlightGenerator" />; a project it cannot read (no instrumental arrangement, no
///     beats) gets the minimal set instead (fog, beam and lasers at the first beat), so the game always has
///     something to render. The export dialog's "Generate show lights" fix stores this same set into the
///     project, so the next export finds it there and has nothing to report.
/// </summary>
public static class RsDefaultShowlights
{
    /// <summary>
    ///     THE one entry both the export fallback and the one-click fix call: the generated set for
    ///     <paramref name="project" />, or the minimal one when nothing can be generated.
    ///     <paramref name="songLengthMs" /> of zero or less is derived from the chart.
    /// </summary>
    public static IReadOnlyList<Showlight> For(ChartProject project, double songLengthMs)
    {
        ArgumentNullException.ThrowIfNull(project);
        var generated = RsShowlightGenerator.Generate(project, songLengthMs);
        return generated.Count > 0 ? generated : For(project.Beats);
    }

    /// <summary>The minimal set for <paramref name="beats" />, placed at the first beat (or time zero without one).</summary>
    public static IReadOnlyList<Showlight> For(IList<BeatMarker> beats)
    {
        ArgumentNullException.ThrowIfNull(beats);
        var startMs = beats.Count > 0 ? beats[0].Time : 0.0;
        var position = TimelineMath.PositionFromMs(startMs, beats);
        return
        [
            new Showlight { Position = position, Type = "FOG_BLUE", Note = 28 },
            new Showlight { Position = position, Type = "BEAMS_BLUE", Note = 52 },
            new Showlight { Position = position, Type = "LASERS_ON", Note = 66 }
        ];
    }
}
