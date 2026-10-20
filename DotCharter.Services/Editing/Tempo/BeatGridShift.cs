namespace DotCharter.Services.Editing.Tempo;

/// <summary>What <see cref="BeatGridShift.Apply" /> did to the grid.</summary>
/// <param name="Applied">False when nothing was changed (no grid, a zero or non-finite offset).</param>
/// <param name="BeatsAddedAtStart">Beats prepended so the earliest element still has a beat before it.</param>
/// <param name="BeatsAddedAtEnd">Beats appended so the latest element still has a beat after it.</param>
/// <param name="BeatsDropped">Beats the shift pushed before 0:00, which a Rocksmith beat list cannot hold.</param>
public sealed record BeatGridShiftResult(bool Applied, int BeatsAddedAtStart, int BeatsAddedAtEnd, int BeatsDropped)
{
    public static BeatGridShiftResult None { get; } = new(false, 0, 0, 0);
}

/// <summary>
/// Tempo map ▸ Align beat grid to notes: moves EVERY beat by one offset
///     while every timed element keeps its absolute time.
///     <para>
///         Positions are stored relative to the beats (<see cref="FractionalPosition" />),
///         so moving the grid alone would carry every note with it — which is
///         exactly what a grid that sits early against its own notes must not do.
///         Each position is read in ms on the OLD grid and re-placed at that ms on
///         the NEW one, through <see cref="ChartProjectPositions.Remap" /> (the one
///         exhaustive walk; its inventory test covers a new collection). The two
///         RELATIVE spans that walk leaves alone on purpose — <c>GuitarNote.Sustain</c>
///         and <c>ChordNote.Sustain</c>, durations in beats — are re-derived from
///         their absolute END here, because what a beat is worth can change across
///         a shifted tempo change. <c>Bookmark</c>/<c>ChartAnnotation</c> are ms-keyed
///         and the audio does not move, so they stay.
///     </para>
///     <para>
///         The grid is extended where the shift uncovers an element the old grid
///         covered: beats are prepended (never before 0:00; the last one is
///         clamped to 0:00 instead) and appended at the edge interval. A beat the
///         shift pushes before 0:00 is dropped. Either way the first beat stays a
///         bar line, and a leading bar that is now short says so in its
///         <c>BeatsInMeasure</c> (the Tempo invariants in editing-invariants).
///     </para>
///     Pure: mutates only the project it is given (the maintenance verb hands it
///     the AFTER clone of a <c>ProjectSnapshotAction</c>), no clock, no I/O.
/// </summary>
public static class BeatGridShift
{
    /// <summary>An element this close past a grid edge still counts as covered.</summary>
    private const double EdgeToleranceMs = 0.5;

    /// <summary>A prepended beat clamped to 0:00 closer than this to the first beat would be a duplicate.</summary>
    private const double MinBeatGapMs = 1.0;

    /// <summary>Bounds the edge extension so a nonsense offset cannot loop for ever.</summary>
    private const int MaxEdgeBeats = 10_000;

    private const double DefaultBeatLengthMs = 500.0;

    public static BeatGridShiftResult Apply(ChartProject project, double offsetMs)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (!double.IsFinite(offsetMs) || Math.Abs(offsetMs) < 1e-9 || project.Beats.Count < 2)
            return BeatGridShiftResult.None;

        var oldBeats = project.Beats.Select(beat => beat.Clone()).ToList();

        // Every instant the chart stores, read once on the OLD grid. The extension
        // only has to keep covered what the old grid covered: an element that was
        // already extrapolated past an edge is not a reason to grow the grid.
        var earliest = double.PositiveInfinity;
        var latest = double.NegativeInfinity;
        void See(double ms)
        {
            if (ms < earliest) earliest = ms;
            if (ms > latest) latest = ms;
        }

        ChartProjectPositions.Remap(project, position =>
        {
            See(position.GetPositionMs(oldBeats));
            return position;
        });
        var sustains = CaptureSustainEnds(project, oldBeats);
        foreach (var sustain in sustains) See(sustain.EndMs);

        var newBeats = oldBeats.Select(beat =>
        {
            var moved = beat.Clone();
            moved.Time += offsetMs;
            return moved;
        }).ToList();

        var dropped = newBeats.RemoveAll(beat => beat.Time < 0);
        if (newBeats.Count < 2) return BeatGridShiftResult.None;

        var added = 0;
        var appended = 0;
        if (double.IsFinite(earliest))
        {
            added = Prepend(newBeats, Math.Max(0, Math.Max(earliest, oldBeats[0].Time)));
            appended = Append(newBeats, Math.Min(latest, oldBeats[^1].Time));
        }

        MakeFirstBeatABarLine(newBeats, added);

        ChartProjectPositions.Remap(project,
            position => TimelineMath.PositionFromImportedMs(position.GetPositionMs(oldBeats), newBeats));
        foreach (var sustain in sustains)
        {
            var start = sustain.Start();
            var span = TimelineMath.PositionFromImportedMs(sustain.EndMs, newBeats) - start;
            if (span > FractionalPosition.Zero) sustain.Set(span);
        }

        project.Beats.Clear();
        foreach (var beat in newBeats) project.Beats.Add(beat);
        return new BeatGridShiftResult(true, added, appended, dropped);
    }

    /// <summary>A relative span: where its sound starts NOW, how to write it, and the absolute END it must keep.</summary>
    private sealed record SustainEnd(Func<FractionalPosition> Start, Action<FractionalPosition> Set, double EndMs);

    private static List<SustainEnd> CaptureSustainEnds(ChartProject project, List<BeatMarker> oldBeats)
    {
        var spans = new List<SustainEnd>();

        void Sounds(IEnumerable<GuitarNote> notes, IEnumerable<Chord> chords)
        {
            foreach (var note in notes)
            {
                if (note.Sustain <= FractionalPosition.Zero) continue;
                var target = note;
                spans.Add(new SustainEnd(() => target.Position, s => target.Sustain = s,
                    target.EndPosition.GetPositionMs(oldBeats)));
            }

            foreach (var chord in chords)
            {
                foreach (var chordNote in chord.ChordNotes)
                {
                    if (chordNote.Sustain <= FractionalPosition.Zero) continue;
                    var owner = chord;
                    var target = chordNote;
                    spans.Add(new SustainEnd(() => owner.Position, s => target.Sustain = s,
                        (owner.Position + target.Sustain).GetPositionMs(oldBeats)));
                }
            }
        }

        foreach (var arrangement in project.Arrangements)
        {
            foreach (var level in arrangement.Levels) Sounds(level.Notes, level.Chords);
            foreach (var voice in arrangement.NotationVoices) Sounds(voice.Notes, voice.Chords);
        }

        return spans;
    }

    /// <summary>Prepends beats at the first interval until one sits at or before <paramref name="targetMs" />.</summary>
    private static int Prepend(List<BeatMarker> beats, double targetMs)
    {
        var count = 0;
        while (count < MaxEdgeBeats && targetMs < beats[0].Time - EdgeToleranceMs)
        {
            var first = beats[0];
            var interval = beats[1].Time - first.Time;
            if (!(interval > 0)) interval = BeatLengthMs(first);
            var time = Math.Max(0, first.Time - interval);
            var gap = first.Time - time;
            if (gap < MinBeatGapMs) break;

            beats.Insert(0, new BeatMarker
            {
                Time = time,
                // A beat clamped to 0:00 is shorter than its neighbours; its tempo says so.
                Bpm = gap < interval - 1e-6 ? BpmFor(gap, first.NoteDenominator) : first.Bpm,
                BeatsInMeasure = first.BeatsInMeasure,
                NoteDenominator = first.NoteDenominator
            });
            count++;
        }

        return count;
    }

    /// <summary>Appends beats at the last interval until one sits at or after <paramref name="targetMs" />, continuing the bar count.</summary>
    private static int Append(List<BeatMarker> beats, double targetMs)
    {
        var sinceDownbeat = 0;
        for (var i = beats.Count - 1; i >= 0 && !beats[i].IsMeasure; i--) sinceDownbeat++;

        var count = 0;
        while (count < MaxEdgeBeats && targetMs > beats[^1].Time + EdgeToleranceMs)
        {
            var last = beats[^1];
            var interval = last.Time - beats[^2].Time;
            if (!(interval > 0)) interval = BeatLengthMs(last);
            sinceDownbeat++;
            beats.Add(new BeatMarker
            {
                Time = last.Time + interval,
                Bpm = last.Bpm,
                IsMeasure = sinceDownbeat % Math.Max(1, last.BeatsInMeasure) == 0,
                BeatsInMeasure = last.BeatsInMeasure,
                NoteDenominator = last.NoteDenominator
            });
            count++;
        }

        return count;
    }

    /// <summary>
    ///     The beats before the first bar line — prepended ones, or the tail of a
    ///     bar whose head the shift pushed before 0:00 — are re-barred counting
    ///     BACK from that line, and whatever does not fill a whole bar becomes a
    ///     short leading bar that starts on index 0.
    /// </summary>
    private static void MakeFirstBeatABarLine(List<BeatMarker> beats, int prepended)
    {
        var downbeat = prepended;
        while (downbeat < beats.Count && !beats[downbeat].IsMeasure) downbeat++;
        if (downbeat == beats.Count) downbeat = prepended;
        if (downbeat == 0)
        {
            beats[0].IsMeasure = true;
            return;
        }

        var barLength = Math.Max(1, beats[downbeat].BeatsInMeasure);
        var shortBar = downbeat % barLength;
        for (var i = 0; i < downbeat; i++)
        {
            beats[i].IsMeasure = (downbeat - i) % barLength == 0;
            beats[i].BeatsInMeasure = i < shortBar ? shortBar : barLength;
        }

        beats[0].IsMeasure = true;
    }

    private static double BeatLengthMs(BeatMarker beat) =>
        beat.Bpm > 0 ? 60000.0 / beat.Bpm * 4.0 / (beat.NoteDenominator > 0 ? beat.NoteDenominator : 4) : DefaultBeatLengthMs;

    private static double BpmFor(double intervalMs, int noteDenominator) =>
        60000.0 / intervalMs * 4.0 / (noteDenominator > 0 ? noteDenominator : 4);
}
