using DotCharter.Services.State;

namespace DotCharter.Services.Editing.Tempo;

// ReSharper disable CompareOfFloatsByEqualityOperator
// — factor is a caller-supplied discrete value (exactly 2.0 or 0.5) and
//   beat Time/Bpm equality compares stored doubles for "no change at all".
public class BeatScaleService : IBeatScaleService
{
    private readonly IAppLogger _logger;

    public BeatScaleService(IAppLogger logger) => _logger = logger;

    public IChartAction? BuildScaleAction(ChartProject project, double startMs, double endMs, double factor)
    {
        if (!CanScale(project, startMs, endMs, factor)) return null;

        var oldBeats = project.Beats.Select(beat => beat.Clone()).ToList();
        var newBeats = factor == 2.0
            ? BuildDoubleTimeBeats(oldBeats, startMs, endMs)
            : BuildHalfTimeBeats(oldBeats, startMs, endMs);
        RetimeSignatures(newBeats, startMs, endMs, factor);

        if (newBeats.Count == oldBeats.Count && BeatsIdentical(oldBeats, newBeats))
        {
            _logger.Info(AppLogCategory.Editing,
                $"Tempo scale produced no change (factor={factor}, range=[{startMs:0.##},{endMs:0.##}]).");
            return null;
        }

        var walk = new ScaleWalk(oldBeats, newBeats);
        walk.WalkProject(project);

        var label = factor == 2.0 ? "double-time" : "half-time";
        _logger.Info(AppLogCategory.Editing,
            $"Built {label} action: range=[{startMs:0.##},{endMs:0.##}], beats {oldBeats.Count}→{newBeats.Count}, " +
            $"notes={walk.Notes.Count}, chords={walk.Chords.Count}, handshapes={walk.Hands.Count}, anchors={walk.Anchors.Count}, events={walk.Events.Count}, tones={walk.Tones.Count}, otherPositions={walk.Positions.Count}.");

        return new BeatScaleAction(project.Beats, oldBeats, newBeats,
            walk.Notes, walk.Chords, walk.Hands, walk.Anchors, walk.Events, walk.Tones, walk.Positions);
    }

    private bool CanScale(ChartProject project, double startMs, double endMs, double factor)
    {
        if (project.IsBeatMapLocked)
        {
            _logger.Warning(AppLogCategory.Editing, "Tempo scale rejected: beat map is locked.");
            return false;
        }

        if (factor is not 2.0 and not 0.5)
        {
            _logger.Warning(AppLogCategory.Editing, $"Tempo scale rejected: unsupported factor {factor}.");
            return false;
        }

        if (project.Beats.Count < 2 || endMs <= startMs)
        {
            _logger.Warning(AppLogCategory.Editing,
                $"Tempo scale rejected: invalid range start={startMs:0.##} end={endMs:0.##} beats={project.Beats.Count}.");
            return false;
        }

        return true;
    }

    /// <summary>
    ///     The hand-written traversal of every position-bearing collection a scale
    ///     moves, recording the per-element undo snapshots <see cref="BeatScaleAction" />
    ///     needs. It stays in this file: <c>ChartProjectPositionsTests</c> reads this
    ///     source to check that every position-bearing collection is named here.
    /// </summary>
    private sealed class ScaleWalk(List<BeatMarker> oldBeats, List<BeatMarker> newBeats)
    {
        // Capture per-element snapshots so undo can restore exact positions.
        public List<(GuitarNote n, FractionalPosition oPos, FractionalPosition oSus, FractionalPosition nPos, FractionalPosition nSus)> Notes { get; } = [];
        public List<(Chord c, FractionalPosition oPos, List<FractionalPosition> oSus, FractionalPosition nPos, List<FractionalPosition> nSus)> Chords { get; } = [];
        public List<(HandShape h, FractionalPosition oPos, FractionalPosition oEnd, FractionalPosition nPos, FractionalPosition nEnd)> Hands { get; } = [];
        public List<(FretAnchor a, FractionalPosition oPos, FractionalPosition nPos)> Anchors { get; } = [];
        public List<(EventPoint e, FractionalPosition oPos, FractionalPosition nPos)> Events { get; } = [];
        public List<(ToneChange t, FractionalPosition oPos, FractionalPosition nPos)> Tones { get; } = [];

        // Everything else that carries a FractionalPosition. Bend points are the
        // destructive one: BendValue.Position is ABSOLUTE, so a scale would otherwise
        // leave every bend point at its pre-scale position — outside its own
        // note — and the EnforceProject the calling dialogs run then COLLAPSED
        // them all onto the note start or end. Undo restored positions and
        // sustains but not the destroyed curve, so the action was not an
        // inverse. Fret-hand mutes, showlights and vocal phrase/lyric positions
        // simply desynced from the rescaled grid.
        //
        // Bookmarks and ChartAnnotations are deliberately NOT here: they store
        // absolute MILLISECONDS, and a tempo scale never moves a beat's Time
        // (double-time inserts midpoints, half-time drops alternates), so an
        // ms-keyed marker is already correct.
        public List<ScaledPosition> Positions { get; } = [];

        /// <summary>Where <paramref name="old" /> lands on the new grid: the same instant.</summary>
        private FractionalPosition Scaled(FractionalPosition old)
            => TimelineMath.PositionFromMs(old.GetPositionMs(oldBeats), newBeats);

        private void Set(FractionalPosition current, FractionalPosition scaled, Action<FractionalPosition> apply)
        {
            if (scaled == current) return;
            Positions.Add(new ScaledPosition(apply, current, scaled));
        }

        private void Remap(FractionalPosition current, Action<FractionalPosition> apply)
            => Set(current, Scaled(current), apply);

        private void RemapBends(IEnumerable<BendValue> bends)
        {
            foreach (var bend in bends)
            {
                var target = bend;
                Remap(target.Position, p => target.Position = p);
            }
        }

        public void WalkProject(ChartProject project)
        {
            foreach (var arr in project.Arrangements) WalkArrangement(arr);

            // Project-level position-bearing elements (not per-arrangement).
            foreach (var sl in project.Showlights)
            {
                var target = sl;
                Remap(target.Position, p => target.Position = p);
            }

            foreach (var track in project.VocalTracks)
            {
                foreach (var phrase in track.Phrases)
                {
                    var targetPhrase = phrase;
                    Remap(targetPhrase.Position, p => targetPhrase.Position = p);
                    Remap(targetPhrase.EndPosition, p => targetPhrase.EndPosition = p);
                    foreach (var lyric in phrase.Lyrics)
                    {
                        var targetLyric = lyric;
                        Remap(targetLyric.Position, p => targetLyric.Position = p);
                        Remap(targetLyric.EndPosition, p => targetLyric.EndPosition = p);
                    }
                }
            }

            // Linked runs scale with their barlines; the history re-validates them afterwards
            // and unlinks any placement that does not span the same measures as its group.
            foreach (var group in project.LinkGroups)
            {
                foreach (var placement in group.Placements)
                {
                    var target = placement;
                    Remap(target.Position, p => target.Position = p);
                    Remap(target.EndPosition, p => target.EndPosition = p);
                }
            }
        }

        private void WalkArrangement(Arrangement arr)
        {
            foreach (var lvl in arr.Levels) WalkLevel(lvl);

            // Notation-only sidecar voices (GP voices 2-4). They are never played,
            // but the sheet strip draws them on the SAME beat grid as the level
            // beside them — so a scale that skips them leaves every voice-2/3/4
            // note pointing at a beat index that now means a different instant,
            // and the sheet tears for the rest of the song. Their onsets are
            // absolute; their sustains are spans in BEATS, and a scale changes
            // what a beat is worth, so the end is re-derived by ms too.
            foreach (var voice in arr.NotationVoices) WalkVoice(voice);

            foreach (var ep in arr.EventPoints) Events.Add((ep, ep.Position, Scaled(ep.Position)));

            foreach (var tc in arr.ToneChanges) Tones.Add((tc, tc.Position, Scaled(tc.Position)));

            // Phrase iterations are the OTHER half of the phrase story, and on an
            // imported chart they are the ONLY half: measured over the 89 RS2
            // exports in TestData/EOF, EventPoint.Phrase yields 0 markers and
            // PhraseIterations yields 2,596. Skipping them moved every phrase
            // marker on every psarc/RS-XML chart a scale touched.
            foreach (var pi in arr.PhraseIterations)
            {
                var target = pi;
                Remap(target.Position, p => target.Position = p);
            }
        }

        private void WalkLevel(ChartLevel lvl)
        {
            foreach (var n in lvl.Notes)
            {
                var nPos = Scaled(n.Position);
                var nEnd = Scaled(n.EndPosition);
                Notes.Add((n, n.Position, n.Sustain, nPos, nEnd - nPos));
                RemapBends(n.BendValues);
            }

            foreach (var m in lvl.FretHandMutes)
            {
                var target = m;
                Remap(target.Position, p => target.Position = p);
            }

            foreach (var c in lvl.Chords)
            {
                var nPos = Scaled(c.Position);
                var oSus = c.ChordNotes.Select(cn => cn.Sustain).ToList();
                var nSus = c.ChordNotes.Select(cn => Scaled(c.Position + cn.Sustain) - nPos).ToList();
                Chords.Add((c, c.Position, oSus, nPos, nSus));
                foreach (var cn in c.ChordNotes) RemapBends(cn.BendValues);
            }

            foreach (var h in lvl.HandShapes)
                Hands.Add((h, h.Position, h.EndPosition, Scaled(h.Position), Scaled(h.EndPosition)));

            foreach (var a in lvl.Anchors) Anchors.Add((a, a.Position, Scaled(a.Position)));
        }

        private void WalkVoice(NotationVoice voice)
        {
            foreach (var n in voice.Notes)
            {
                var target = n;
                var newPos = Scaled(target.Position);
                var newEnd = Scaled(target.EndPosition);
                Set(target.Position, newPos, p => target.Position = p);
                Set(target.Sustain, newEnd - newPos, s => target.Sustain = s);
                RemapBends(target.BendValues);
            }

            foreach (var c in voice.Chords)
            {
                var chord = c;
                var newPos = Scaled(chord.Position);
                // Read BEFORE the applies run — nothing in `Positions` has
                // been committed yet, so chord.Position is still the old one.
                var oldChordPos = chord.Position;
                Set(oldChordPos, newPos, p => chord.Position = p);
                foreach (var cn in chord.ChordNotes)
                {
                    var note = cn;
                    Set(note.Sustain, Scaled(oldChordPos + note.Sustain) - newPos, s => note.Sustain = s);
                    RemapBends(note.BendValues);
                }
            }
        }
    }

    private static List<BeatMarker> BuildDoubleTimeBeats(List<BeatMarker> oldBeats, double startMs, double endMs)
    {
        var result = new List<BeatMarker>(oldBeats.Count * 2);
        for (var i = 0; i < oldBeats.Count; i++)
        {
            var b = oldBeats[i].Clone();
            var inRange = b.Time >= startMs && b.Time < endMs;
            if (inRange) b.Bpm *= 2.0;
            result.Add(b);

            if (inRange && i + 1 < oldBeats.Count)
            {
                var next = oldBeats[i + 1];
                var mid = new BeatMarker
                {
                    Time = (b.Time + next.Time) / 2.0,
                    Bpm = b.Bpm,
                    BeatsInMeasure = b.BeatsInMeasure,
                    NoteDenominator = b.NoteDenominator,
                    IsAnchor = false,
                    IsMeasure = false
                };
                result.Add(mid);
            }
        }
        return result;
    }

    private static List<BeatMarker> BuildHalfTimeBeats(List<BeatMarker> oldBeats, double startMs, double endMs)
    {
        var result = new List<BeatMarker>(oldBeats.Count);
        var keepNext = true;
        var insideRange = false;

        // Which beats the alternation keeps depends on where the RANGE starts,
        // not on where the bar lines are — so a range beginning on an off-beat
        // lands on the odd beats and deletes every downbeat inside it. A bar
        // line is structural: ChartPositionLabel and the status bar count bars
        // by walking IsMeasure, so one dropped renumbers every bar for the rest
        // of the song, well past the scaled range. An anchor is the charter's
        // explicit pin and is just as silent to lose.
        //
        // Both therefore travel to the next beat that IS kept. Half-time never
        // moves a beat's Time (it only deletes), so the marker the flag lands on
        // is already at its final instant — the transfer changes which beat
        // carries it, not what it means.
        var pendingMeasure = false;
        var pendingAnchor = false;

        void Add(BeatMarker beat)
        {
            beat.IsMeasure |= pendingMeasure;
            beat.IsAnchor |= pendingAnchor;
            pendingMeasure = false;
            pendingAnchor = false;
            result.Add(beat);
        }

        for (var i = 0; i < oldBeats.Count; i++)
        {
            var b = oldBeats[i];
            var inRange = b.Time >= startMs && b.Time < endMs;

            if (inRange)
            {
                if (!insideRange) { insideRange = true; keepNext = true; }
                if (keepNext)
                {
                    var c = b.Clone();
                    c.Bpm *= 0.5;
                    Add(c);
                }
                else
                {
                    pendingMeasure |= b.IsMeasure;
                    pendingAnchor |= b.IsAnchor;
                }

                keepNext = !keepNext;
            }
            else
            {
                insideRange = false;
                Add(b.Clone());
            }
        }

        return result;
    }

    /// <summary>
    /// Keeps the time signature true to the scaled grid. Double-time put
    ///     two beats in every beat's place and half-time removed every other one,
    ///     but both left <c>BeatsInMeasure</c> / <c>NoteDenominator</c> as they
    ///     were, so a doubled 4/4 bar held 8 beats while saying 4/4. The Guitar Pro
    ///     export groups a bar as <c>BeatsInMeasure</c> consecutive beats, and the
    ///     sheet music, the SNG time signature, the count-in and the spacing
    ///     enforcer all read it too.
    ///     <para>
    ///         The invariant: <c>BeatsInMeasure</c> is the number of beats the bar
    ///         actually has. Every bar touching the range is recounted from the new
    ///         grid; the beat unit doubles for double-time (4/4 → 8/8, 3/4 → 6/8,
    ///         always whole) and halves for half-time (4/4 → 2/2). An odd meter under
    ///         half-time cannot halve evenly: its bars come out as the grid really is
    ///         (3/4 → alternating 2/2 and 1/2), never a fractional count.
    ///     </para>
    /// </summary>
    internal static void RetimeSignatures(List<BeatMarker> beats, double startMs, double endMs, double factor)
    {
        foreach (var b in beats)
        {
            if (b.Time < startMs || b.Time >= endMs) continue;
            var denominator = b.NoteDenominator > 0 ? b.NoteDenominator : 4;
            b.NoteDenominator = factor == 2.0 ? denominator * 2 : Math.Max(1, denominator / 2);
        }

        var barStart = -1;
        for (var i = 0; i <= beats.Count; i++)
        {
            if (i < beats.Count && !beats[i].IsMeasure) continue;
            if (barStart >= 0)
            {
                var touchesRange = false;
                for (var k = barStart; k < i && !touchesRange; k++)
                    touchesRange = beats[k].Time >= startMs && beats[k].Time < endMs;
                // The last bar runs to the end of the map and may be cut short;
                // only a bar closed by the next marker has a known size.
                if (touchesRange && i < beats.Count)
                {
                    for (var k = barStart; k < i; k++) beats[k].BeatsInMeasure = i - barStart;
                }
            }

            barStart = i;
        }
    }

    private static bool BeatsIdentical(List<BeatMarker> a, List<BeatMarker> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (a[i].Time != b[i].Time || a[i].Bpm != b[i].Bpm) return false;
        }
        return true;
    }
}
