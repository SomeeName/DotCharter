namespace DotCharter.Services.Editing.Tempo;

/// <summary>
///     The beat-grid arithmetic behind the tempo-map verbs of
///     <see cref="Maintenance.IChartMaintenanceService" /> (Insert / Remove beat at the playhead,
///     Add measures at start / end): pure edits on a project's <c>Beats</c>, run on the AFTER clone
///     of a whole-project snapshot step. Moving chart positions along with the grid is
///     <see cref="ChartProjectPositions" />'s; the verbs call both.
/// </summary>
public static class TempoMapBeats
{
    /// <summary>The index of the beat nearest <paramref name="timeMs" />; -1 for an empty grid.</summary>
    public static int NearestBeatIndex(IList<BeatMarker> beats, double timeMs)
    {
        if (beats.Count == 0) return -1;
        var best = 0;
        var bestDelta = Math.Abs(beats[0].Time - timeMs);
        for (var i = 1; i < beats.Count; i++)
        {
            var delta = Math.Abs(beats[i].Time - timeMs);
            if (delta < bestDelta)
            {
                best = i;
                bestDelta = delta;
            }
        }
        return best;
    }

    /// <summary>
    /// Removes any chart content whose start <see cref="FractionalPosition.BeatId"/>
    /// equals <paramref name="beatId"/>. Called before the beat itself is removed
    /// so we don't leave content anchored to a non-existent beat.
    /// <para>
    /// Same inventory obligation as <see cref="ChartProjectPositions"/> — a new
    /// position-bearing element class has to be dropped here too, or Remove Beat
    /// leaves it stranded on a beat that is gone while everything around
    /// it slides back one. Kept as its own traversal because a REMOVE is not a
    /// remap: vocal phrases are deliberately not deleted here (only their
    /// lyrics), which no shared visitor could express.
    /// </para>
    /// </summary>
    public static void RemoveContentAtBeat(ChartProject project, int beatId)
    {
        bool At(FractionalPosition p) => p.BeatId == beatId;

        for (var i = project.Showlights.Count - 1; i >= 0; i--)
            if (At(project.Showlights[i].Position)) project.Showlights.RemoveAt(i);

        foreach (var arrangement in project.Arrangements)
        {
            foreach (var level in arrangement.Levels)
            {
                for (var i = level.Notes.Count - 1; i >= 0; i--)
                    if (At(level.Notes[i].Position)) level.Notes.RemoveAt(i);
                for (var i = level.Chords.Count - 1; i >= 0; i--)
                    if (At(level.Chords[i].Position)) level.Chords.RemoveAt(i);
                for (var i = level.Anchors.Count - 1; i >= 0; i--)
                    if (At(level.Anchors[i].Position)) level.Anchors.RemoveAt(i);
                for (var i = level.HandShapes.Count - 1; i >= 0; i--)
                    if (At(level.HandShapes[i].Position)) level.HandShapes.RemoveAt(i);
                for (var i = level.FretHandMutes.Count - 1; i >= 0; i--)
                    if (At(level.FretHandMutes[i].Position)) level.FretHandMutes.RemoveAt(i);
            }
            foreach (var voice in arrangement.NotationVoices)
            {
                for (var i = voice.Notes.Count - 1; i >= 0; i--)
                    if (At(voice.Notes[i].Position)) voice.Notes.RemoveAt(i);
                for (var i = voice.Chords.Count - 1; i >= 0; i--)
                    if (At(voice.Chords[i].Position)) voice.Chords.RemoveAt(i);
            }
            for (var i = arrangement.EventPoints.Count - 1; i >= 0; i--)
                if (At(arrangement.EventPoints[i].Position)) arrangement.EventPoints.RemoveAt(i);
            for (var i = arrangement.ToneChanges.Count - 1; i >= 0; i--)
                if (At(arrangement.ToneChanges[i].Position)) arrangement.ToneChanges.RemoveAt(i);
            for (var i = arrangement.PhraseIterations.Count - 1; i >= 0; i--)
                if (At(arrangement.PhraseIterations[i].Position)) arrangement.PhraseIterations.RemoveAt(i);
        }

        foreach (var vocalTrack in project.VocalTracks)
        {
            foreach (var phrase in vocalTrack.Phrases)
            {
                for (var i = phrase.Lyrics.Count - 1; i >= 0; i--)
                    if (At(phrase.Lyrics[i].Position)) phrase.Lyrics.RemoveAt(i);
            }
        }
    }

    /// <summary>Inserts a beat at <paramref name="pivot" /> one beat interval before the beat that
    /// was there, pushing it and every later beat forward by that interval.</summary>
    public static void InsertBeatAt(ChartProject project, int pivot)
    {
        var beats = project.Beats;
        var originalTime = beats[pivot].Time;
        var template = beats[pivot];
        // Compute the beat interval. Prefer the gap to the next beat; fall
        // back to the gap from the previous beat; finally derive from BPM.
        double interval;
        if (pivot + 1 < beats.Count) interval = beats[pivot + 1].Time - beats[pivot].Time;
        else if (pivot - 1 >= 0) interval = beats[pivot].Time - beats[pivot - 1].Time;
        else interval = 60000.0 / Math.Max(1.0, template.Bpm > 0 ? template.Bpm : project.Bpm);

        if (interval <= 0) interval = 60000.0 / Math.Max(1.0, template.Bpm > 0 ? template.Bpm : project.Bpm);

        // Push every beat at index >= pivot forward by `interval` ms so the
        // new beat at `pivot` slots into the original time of beats[pivot].
        for (var i = pivot; i < beats.Count; i++)
            beats[i].Time += interval;

        // The inserted beat normally sits INSIDE the bar the pivot belongs to,
        // so it is not a bar line. Inserting before index 0 is the exception:
        // the new beat becomes the start of the chart, and a grid whose first
        // beat carries no IsMeasure has no bar 1 — ChartPositionLabel and the
        // status bar both count bars by walking the flags from index 0, so the
        // dropped line renumbered every bar in the song. Hand the line over
        // rather than inventing a second one.
        var takesOverBarLine = pivot == 0 && template.IsMeasure;
        if (takesOverBarLine) template.IsMeasure = false;

        beats.Insert(pivot, new BeatMarker
        {
            Time = originalTime,
            Bpm = template.Bpm,
            IsAnchor = false,
            IsMeasure = takesOverBarLine,
            BeatsInMeasure = template.BeatsInMeasure,
            NoteDenominator = template.NoteDenominator
        });
    }

    /// <summary>Removes the beat at <paramref name="pivot" /> and pulls every later beat back by
    /// the gap it occupied.</summary>
    public static void RemoveBeatAt(ChartProject project, int pivot)
    {
        var beats = project.Beats;
        if (pivot < 0 || pivot >= beats.Count) return;

        // Gap that the removed beat occupied — use the distance to the next
        // beat so the rest of the timeline stays at its absolute time after
        // we shift it back.
        double interval;
        if (pivot + 1 < beats.Count) interval = beats[pivot + 1].Time - beats[pivot].Time;
        else if (pivot - 1 >= 0) interval = beats[pivot].Time - beats[pivot - 1].Time;
        else interval = 0;

        // Removing index 0 promotes whatever followed it to the start of the
        // chart, and that beat is usually mid-bar. The mirror of the hand-over
        // in InsertBeatAt: the grid must still begin on a bar line, or bar 1
        // silently absorbs the promoted beat and every later bar number shifts.
        var handOverBarLine = pivot == 0 && beats[0].IsMeasure && beats.Count > 1;

        beats.RemoveAt(pivot);
        for (var i = pivot; i < beats.Count; i++)
            beats[i].Time -= interval;

        if (handOverBarLine) beats[0].IsMeasure = true;
    }

    /// <summary>The beat interval <see cref="PrependBeats" /> extends the grid backwards with.</summary>
    private static double PrependIntervalMs(ChartProject project)
    {
        var first = project.Beats[0];
        var denominator = first.NoteDenominator > 0 ? first.NoteDenominator : 4;
        var bpm = first.Bpm > 0 ? first.Bpm : project.Bpm > 0 ? project.Bpm : 120.0;
        return 60000.0 / bpm * 4.0 / denominator;
    }

    /// <summary>
    ///     How many whole measures <see cref="PrependBeats" /> can add before the
    ///     first beat without a beat landing before 0:00.
    /// </summary>
    public static int MeasuresThatFitBeforeFirstBeat(ChartProject project)
    {
        if (project.Beats.Count == 0) return 0;
        var measureMs = PrependIntervalMs(project) * Math.Max(1, project.Beats[0].BeatsInMeasure);
        // A hair of tolerance so a grid that starts exactly one measure in still fits.
        return measureMs > 0 ? (int)Math.Floor((project.Beats[0].Time + 0.001) / measureMs) : 0;
    }

    /// <summary>Prepends <paramref name="beatCount" /> beats before the first, at the first beat's
    /// tempo, bar lines counted back from it.</summary>
    public static void PrependBeats(ChartProject project, int beatCount)
    {
        var first = project.Beats[0];
        var denominator = first.NoteDenominator > 0 ? first.NoteDenominator : 4;
        var bpm = first.Bpm > 0 ? first.Bpm : project.Bpm > 0 ? project.Bpm : 120.0;
        var intervalMs = PrependIntervalMs(project);
        var newBeats = new List<BeatMarker>(beatCount);
        for (var i = 0; i < beatCount; i++)
        {
            var beatsUntilExistingStart = beatCount - i;
            newBeats.Add(new BeatMarker
            {
                Time = first.Time - intervalMs * beatsUntilExistingStart,
                Bpm = bpm,
                IsAnchor = false,
                IsMeasure = beatsUntilExistingStart % Math.Max(1, first.BeatsInMeasure) == 0,
                BeatsInMeasure = first.BeatsInMeasure,
                NoteDenominator = denominator
            });
        }

        for (var i = newBeats.Count - 1; i >= 0; i--)
            project.Beats.Insert(0, newBeats[i]);
    }

    /// <summary>
    ///     Appends <paramref name="measureCount" /> whole measures after the last
    ///     beat — <paramref name="measureCount" /> NEW BAR LINES on the ruler —
    ///     and returns how many beats that came to.
    ///     <para>
    ///         The interval comes from the last REAL gap in the grid, not from
    ///         BPM: the tempo map may have been dragged by hand, and a chart
    ///         whose printed BPM disagrees with its actual spacing would grow a
    ///         tail at the wrong tempo. BPM is the fallback for a one-beat grid.
    ///     </para>
    /// </summary>
    public static int AppendMeasures(ChartProject project, int measureCount)
    {
        var beats = project.Beats;
        var last = beats[^1];
        var beatsInMeasure = Math.Max(1, last.BeatsInMeasure);
        var denominator = last.NoteDenominator > 0 ? last.NoteDenominator : 4;
        var bpm = last.Bpm > 0 ? last.Bpm : project.Bpm > 0 ? project.Bpm : 120.0;

        var interval = beats.Count >= 2 ? last.Time - beats[^2].Time : 0;
        if (interval <= 0) interval = 60000.0 / bpm * 4.0 / denominator;

        var sinceDownbeat = 0;
        for (var i = beats.Count - 1; i >= 0; i--)
        {
            if (beats[i].IsMeasure) break;
            sinceDownbeat++;
        }

        // ONE measure asked for is ONE new bar line on the ruler — the count the
        // charter can see. Two things follow, and the first is the reason this
        // is not just `measureCount * beatsInMeasure`:
        //
        //   * The beats that FINISH an open bar come first and are free. A grid
        //     ending three beats into M251 needs one beat to reach M252;
        //     otherwise "add one measure" leaves the END phrase sharing a bar
        //     with the last note, which is the whole thing being fixed.
        //   * The run stops on the last beat OF the new measure, not on the bar
        //     line after it. A measure is delimited by two bar lines, so
        //     appending that trailing downbeat drew M253 as well and one measure
        //     read as two.
        //
        // toNextDownbeat is never zero: a grid whose last beat IS a bar line has
        // an EMPTY bar open, and the next bar line is a whole measure away.
        var toNextDownbeat = beatsInMeasure - sinceDownbeat % beatsInMeasure;
        var total = toNextDownbeat + measureCount * beatsInMeasure - 1;

        for (var i = 1; i <= total; i++)
        {
            beats.Add(new BeatMarker
            {
                Time = last.Time + interval * i,
                Bpm = bpm,
                IsAnchor = false,
                IsMeasure = (sinceDownbeat + i) % beatsInMeasure == 0,
                BeatsInMeasure = last.BeatsInMeasure,
                NoteDenominator = denominator
            });
        }

        return total;
    }
}
