namespace DotCharter.Services.Gameplay;

/// <summary>
/// Pure plan for the pre-gameplay metronome count-in: N clicks played before
/// the transport starts, spaced at the song's start tempo (scaled by the
/// practice-speed the run will play at), with measure downbeats accented.
///
/// The plan carries no clock or audio dependency so the schedule is fully
/// unit-testable; a UI driver awaits each click's <see cref="Click.DelayBeforeMs"/>
/// and plays it through the existing native metronome click. Mirrors the pure,
/// injected-time style of <c>LivePlaySessionService.CountBeatsCrossed</c>.
/// </summary>
public static class CountInPlan
{
    /// <summary>One count-in click. <paramref name="DelayBeforeMs"/> is how long
    /// to wait BEFORE playing it (0 for the first), so a driver can await between
    /// clicks. <paramref name="Accent"/> marks a measure downbeat.</summary>
    public readonly record struct Click(int Index, double DelayBeforeMs, bool Accent);

    /// <summary>Normalises a persisted setting to the supported set:
    /// 0 = off, otherwise 4 or 8 beats.</summary>
    public static int Sanitize(int beats) => beats switch
    {
        >= 8 => 8,
        >= 4 => 4,
        _ => 0
    };

    /// <summary>
    /// Floor under any grid tempo the count-in will trust. No playable song sits
    /// below 30 bpm — but a CDLC import's beat 0 routinely does: many charts park
    /// an isolated first beat at t=0 whose "measure" spans the leading SILENCE,
    /// so its per-beat Bpm (derived from the gap to beat 1) encodes that silence
    /// — e.g. 11.45 "bpm" from a 5240 ms gap (Ice Nine Kills – Funeral
    /// Derangements) or 3.68 from 16310 ms (Animals as Leaders – Monomyth).
    /// Spacing a 4-beat count-in by such a value put ~21 s (worst case 65 s)
    /// between session start and transport start.
    /// </summary>
    public const double MinSaneGridBpm = 30.0;

    /// <summary>
    /// The tempo (and meter) the run-start count-in should click at, read from
    /// the beat grid the run is about to play: starting at the beat whose span
    /// covers <paramref name="startMs"/> (the transport's run-start position),
    /// the FIRST beat carrying a sane per-beat Bpm wins. Beats with a
    /// silence-encoding Bpm (see <see cref="MinSaneGridBpm"/>) are skipped, so a
    /// CDLC's parked-at-zero first beat does not stretch the count-in across
    /// its intro gap; on an ordinary steady grid the very first candidate is
    /// already sane and the behaviour is unchanged. Returns
    /// (<paramref name="fallbackBpm"/>, 4) when the grid offers nothing sane —
    /// <see cref="MsPerClick"/> still sanitises that fallback.
    /// </summary>
    public static (double Bpm, int BeatsPerMeasure) ResolveGridTempo(
        IList<BeatMarker>? beats, double startMs, double fallbackBpm)
    {
        if (beats is { Count: > 0 })
        {
            // The beat whose span covers startMs (the last one at/before it),
            // clamped to the first beat when the run starts ahead of the grid.
            var first = 0;
            for (var i = beats.Count - 1; i >= 0; i--)
            {
                if (beats[i].Time <= startMs)
                {
                    first = i;
                    break;
                }
            }

            for (var i = first; i < beats.Count; i++)
            {
                if (beats[i].Bpm is >= MinSaneGridBpm and < 1000)
                    return (beats[i].Bpm, Math.Max(1, beats[i].BeatsInMeasure));
            }
        }

        return (fallbackBpm, 4);
    }

    /// <summary>Real milliseconds between count-in clicks at <paramref name="bpm"/>
    /// when the run plays back at <paramref name="playbackSpeed"/> (0.25..1). A
    /// 60% practice run leads in at 60% tempo so the pickup lands with the song.
    /// Defends against nonsense tempo/speed values.</summary>
    public static double MsPerClick(double bpm, double playbackSpeed)
    {
        var safeBpm = bpm is > 1 and < 1000 ? bpm : 120.0;
        var safeSpeed = playbackSpeed is > 0.01 and <= 4 ? playbackSpeed : 1.0;
        return 60_000.0 / safeBpm / safeSpeed;
    }

    /// <summary>A click is accented when it lands on a measure downbeat (the
    /// first beat of each group of <paramref name="beatsPerMeasure"/>).</summary>
    public static bool IsAccent(int clickIndex, int beatsPerMeasure)
        => beatsPerMeasure > 0 && clickIndex % beatsPerMeasure == 0;

    /// <summary>Builds the full click sequence for a count-in of
    /// <paramref name="beats"/> beats. Returns an empty array when the count-in
    /// is off.</summary>
    public static Click[] Build(int beats, int beatsPerMeasure, double bpm, double playbackSpeed)
    {
        var n = Sanitize(beats);
        if (n == 0) return [];

        var gap = MsPerClick(bpm, playbackSpeed);
        var clicks = new Click[n];
        for (var i = 0; i < n; i++)
            clicks[i] = new Click(i, i == 0 ? 0.0 : gap, IsAccent(i, beatsPerMeasure));
        return clicks;
    }
}
