namespace DotCharter.Services.Parsing.GuitarPro;

/// <summary>
///     Turns a <see cref="GpImportMetadata"/> (bar meters + tempo automations
///     from Guitar Pro / Songsterr) into a project beat map. Shared by the GP and
///     Songsterr import dialogs so the two Editor-on-Fire toggles apply the same
///     way to both:
///     <list type="bullet">
///         <item><c>importTimeSignatures</c> (EOF <c>import_ts</c>, default on) —
///         when off, every bar is treated as 4/4.</item>
///         <item><c>dropMidBeatTempoChanges</c> (EOF, default off) — when on,
///         tempo changes that do not land on a beat are discarded.</item>
///     </list>
///     Extracted verbatim from the dialogs' former private BuildBeatsFromMetadata
///     so the toggle logic is unit-testable and identical across both paths.
/// </summary>
public static class GpBeatMapBuilder
{
    /// <summary>Tolerance for "this tempo point sits ON that beat". Shared by the
    /// applier's window and <see cref="IsOnBeat"/>, so the toggle that KEEPS an
    /// on-beat change and the pass that APPLIES one cannot disagree about which
    /// points are on a beat.</summary>
    private const double OnBeatEpsilon = 1e-6;

    /// <summary>
    ///     The song's OPENING tempo: the BPM of the tempo point that sorts
    ///     earliest by <c>(BarIndex, BarPosition)</c>, or
    ///     <paramref name="fallback"/> when the file states none.
    ///     <para>
    ///     No caller may spell this <c>TempoPoints.FirstOrDefault()?.Bpm</c>
    ///     — DOCUMENT order, not bar order. <see cref="Build"/> sorts internally,
    ///     so the beat map itself was right, but the value it is HANDED for every
    ///     bar before the first tempo point was whichever point the parser
    ///     happened to append first. `GpMetadataParser` walks
    ///     <c>&lt;Automations&gt;</c> in document order and fans a repeated
    ///     section's automation out over every unfolded pass, and
    ///     `SongsterrConverter` appends its `automations` block AFTER the
    ///     beat-level tempos it read bar by bar — so a score whose bar-0 tempo
    ///     lives in `automations` and whose first beat-level tempo sits at bar 12
    ///     opened at BAR 12's tempo. That value also became `project.Bpm` and the
    ///     transport's BPM, so the whole head of the song played at the wrong
    ///     speed.
    ///     </para>
    ///     The filter is <see cref="Build"/>'s own, so the two cannot disagree
    ///     about which points are usable.
    /// </summary>
    public static double InitialBpm(GpImportMetadata? meta, double fallback)
    {
        var first = meta?.TempoPoints
            .Where(p => double.IsFinite(p.Bpm) && p.Bpm > 0)
            .OrderBy(p => p.BarIndex)
            .ThenBy(p => p.BarPosition)
            .FirstOrDefault();
        return first?.Bpm ?? fallback;
    }

    public static List<BeatMarker> Build(GpImportMetadata meta, double initialBpm, double startOffsetMs,
        bool importTimeSignatures = true, bool dropMidBeatTempoChanges = false)
    {
        var beats = new List<BeatMarker>();
        var bpm = initialBpm > 0 ? initialBpm : 120.0;
        var t = startOffsetMs;

        // Effective bar meters: 4/4 for every bar when TS import is off.
        var bars = meta.Bars
            .Select(b => importTimeSignatures
                ? b
                : new GpBarInfo { BeatsInMeasure = 4, NoteDenominator = 4, TripletFeel = b.TripletFeel })
            .ToList();

        // Tempo points, optionally filtered to on-beat changes only, then sorted
        // by (bar, position) for the same lookup the dialogs did.
        // A non-positive or non-finite tempo point turns `60000.0 / bpm` into
        // Infinity/NaN, so every later BeatMarker.Time is unusable and the grid
        // stops ascending — which BinarySearchBeats assumes. Only the INITIAL
        // bpm was guarded; drop the bad points here too so no caller can feed
        // one in (the metadata parsers each guard at their own read as well).
        var tempoByBar = meta.TempoPoints
            .Where(p => double.IsFinite(p.Bpm) && p.Bpm > 0)
            .Where(p => !dropMidBeatTempoChanges || IsOnBeat(p, bars))
            .OrderBy(p => p.BarIndex)
            .ThenBy(p => p.BarPosition)
            .ToList();

        for (var barIndex = 0; barIndex < bars.Count; barIndex++)
        {
            var bar = bars[barIndex];
            var beatsInMeasure = bar.BeatsInMeasure > 0 ? bar.BeatsInMeasure : 4;
            var noteDenominator = bar.NoteDenominator > 0 ? bar.NoteDenominator : 4;

            // Apply tempo points whose position is 0 at the start of this bar.
            foreach (var tp in tempoByBar.Where(p => p.BarIndex == barIndex && p.BarPosition <= 0.0001))
                bpm = tp.Bpm;

            for (var beatInBar = 0; beatInBar < beatsInMeasure; beatInBar++)
            {
                // Tempo points whose position falls in THIS beat's half-open
                // window [posFrac, nextFrac).
                //
                // The lower bound is INCLUSIVE; an exclusive one would drop the most
                // common spelling of all: a change authored exactly ON a beat
                // inside the bar. Such a point equals this window's lower bound
                // and the PREVIOUS window's upper bound, so neither admitted it,
                // and the bar-start pass above only takes `<= 0.0001` — the
                // change vanished and every later beat kept the old tempo, so
                // the rest of the import drifted in wall-clock time. Worse, it
                // made `dropMidBeatTempoChanges` self-contradictory: IsOnBeat
                // KEEPS exactly these points, and then nothing applied them.
                //
                // The epsilon is what makes it hold for a meter whose beats are
                // not binary-exact (1/3 and 2/3 of a 3/4 bar): a file's stored
                // 0.333333 must still land on beat 1, not one beat early.
                var posFrac = (double)beatInBar / beatsInMeasure;
                var nextFrac = (double)(beatInBar + 1) / beatsInMeasure;
                foreach (var tp in tempoByBar
                             .Where(p => p.BarIndex == barIndex && p.BarPosition > 0.0001 &&
                                         p.BarPosition >= posFrac - OnBeatEpsilon &&
                                         p.BarPosition < nextFrac))
                {
                    bpm = tp.Bpm;
                }

                beats.Add(new BeatMarker
                {
                    Time = t,
                    Bpm = bpm,
                    BeatsInMeasure = beatsInMeasure,
                    NoteDenominator = noteDenominator,
                    IsMeasure = beatInBar == 0
                });

                var msPerBeat = 60000.0 / bpm * 4.0 / noteDenominator;
                t += msPerBeat;
            }
        }

        return beats;
    }

    /// <summary>A tempo change lands on a beat when its fractional bar position is
    /// an integer multiple of 1/beatsInMeasure (bar start included).</summary>
    private static bool IsOnBeat(GpTempoPoint tp, IReadOnlyList<GpBarInfo> bars)
    {
        var beatsInMeasure = tp.BarIndex >= 0 && tp.BarIndex < bars.Count
            ? bars[tp.BarIndex].BeatsInMeasure
            : 4;
        if (beatsInMeasure <= 0) beatsInMeasure = 4;
        var beatPos = tp.BarPosition * beatsInMeasure;
        return Math.Abs(beatPos - Math.Round(beatPos)) < OnBeatEpsilon;
    }
}
