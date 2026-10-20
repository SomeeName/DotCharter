namespace DotCharter.Services.Editing.Improvement;

public sealed class ChartImprovementService : IChartImprovementService
{
    private readonly IAppLogger? _logger;

    public ChartImprovementService()
    {
    }

    public ChartImprovementService(IAppLogger logger) => _logger = logger;

    public ChartImprovementResult ImproveProject(ChartProject? project, AppConfig config, double totalAudioLengthMs)
    {
        var result = new ChartImprovementResult();
        if (project == null)
        {
            _logger?.Warning(AppLogCategory.Editing, "ImproveProject called with null project; skipping.");
            return result;
        }

        using var _ = _logger?.BeginTimedOperation(AppLogCategory.Editing, "Chart Improvement",
            $"arrangements={project.Arrangements.Count}, beats={project.Beats.Count}, audioMs={totalAudioLengthMs:0.##}");

        ChartSpacingEnforcer.EnforceProject(project, config);

        result.Add("song-end cleanup", TrimProjectToAudioLength(project, totalAudioLengthMs));
        result.Add("extra beats", RemoveExtraBeats(project, totalAudioLengthMs));

        foreach (var arrangement in project.Arrangements)
        {
            result.ArrangementsProcessed++;
            result.Add("phrase-start anchors", EnsurePhraseStartAnchors(arrangement));
            result.Add("end phrase", EnsureEndPhrase(arrangement, project.Beats, totalAudioLengthMs));

            foreach (var level in arrangement.Levels)
            {
                result.Add("chord sustains", NormalizeChordNoteSustains(level));
                result.Add("dangling link-next targets", RemoveUnnecessaryLinkNextTargets(level));
                result.Add("invalid link-nexts", RemoveInvalidLinkNextPatterns(level));
                result.Add("slide handshapes", FixChordSlideHandShapes(level));
                result.Add("handshape spacing", ImproveHandShapeLengths(level, project.Beats));
            }
        }

        ChartSpacingEnforcer.EnforceProject(project, config);
        _logger?.Info(AppLogCategory.Editing, $"Chart improvement complete: {result.ToLogDetails()}");
        return result;
    }

    private static int TrimProjectToAudioLength(ChartProject project, double totalAudioLengthMs)
    {
        if (totalAudioLengthMs <= 0 || project.Beats.Count == 0) return 0;

        var changes = 0;
        foreach (var arrangement in project.Arrangements)
        {
            changes += RemoveAll(arrangement.EventPoints,
                e => e.Position.GetPositionMs(project.Beats) > totalAudioLengthMs);
            changes += RemoveAll(arrangement.ToneChanges,
                t => t.Position.GetPositionMs(project.Beats) > totalAudioLengthMs);

            foreach (var level in arrangement.Levels)
            {
                changes += RemoveAll(level.Notes, n => n.Position.GetPositionMs(project.Beats) > totalAudioLengthMs);
                changes += RemoveAll(level.Chords, c => c.Position.GetPositionMs(project.Beats) > totalAudioLengthMs);
                changes += RemoveAll(level.Anchors, a => a.Position.GetPositionMs(project.Beats) > totalAudioLengthMs);
                changes += RemoveAll(level.HandShapes,
                    h => h.Position.GetPositionMs(project.Beats) > totalAudioLengthMs);

                foreach (var note in level.Notes)
                {
                    var endMs = note.EndPosition.GetPositionMs(project.Beats);
                    if (endMs <= totalAudioLengthMs) continue;

                    var endPosition = TimelineMath.PositionFromMs(totalAudioLengthMs, project.Beats);
                    note.Sustain = FractionalPosition.Max(FractionalPosition.Zero, endPosition - note.Position);
                    changes++;
                }

                foreach (var chord in level.Chords)
                {
                    foreach (var chordNote in chord.ChordNotes)
                    {
                        var endMs = (chord.Position + chordNote.Sustain).GetPositionMs(project.Beats);
                        if (endMs <= totalAudioLengthMs) continue;

                        var endPosition = TimelineMath.PositionFromMs(totalAudioLengthMs, project.Beats);
                        chordNote.Sustain = FractionalPosition.Max(FractionalPosition.Zero, endPosition - chord.Position);
                        changes++;
                    }
                }

                foreach (var handShape in level.HandShapes)
                {
                    var endMs = handShape.EndPosition.GetPositionMs(project.Beats);
                    if (endMs <= totalAudioLengthMs) continue;

                    handShape.EndPosition = TimelineMath.PositionFromMs(totalAudioLengthMs, project.Beats);
                    if (handShape.EndPosition < handShape.Position)
                        handShape.EndPosition = handShape.Position;
                    changes++;
                }
            }
        }

        return changes;
    }

    private static int RemoveExtraBeats(ChartProject project, double totalAudioLengthMs)
    {
        if (totalAudioLengthMs <= 0 || project.Beats.Count < 2) return 0;

        var changes = RemoveAll(project.Beats, b => b.Time > totalAudioLengthMs);
        if (project.Beats.Count == 0) return changes;

        var lastBeat = project.Beats[^1];
        if (Math.Abs(lastBeat.Time - totalAudioLengthMs) > 0.001)
        {
            project.Beats.Add(new BeatMarker
            {
                Time = totalAudioLengthMs,
                Bpm = lastBeat.Bpm,
                BeatsInMeasure = lastBeat.BeatsInMeasure,
                NoteDenominator = lastBeat.NoteDenominator
            });
            changes++;
        }

        return changes;
    }

    /// <summary>
    ///     Restates the hand position in force at every phrase and section start.
    ///     <para>
    ///         <b>BOTH phrase stores.</b> Keyed on <c>EventPoint.Phrase</c> alone
    ///         this found ZERO phrases in every imported chart (0 markers via
    ///         <c>EventPoint</c> against 2,596 via <c>PhraseIterations</c> over the
    ///         89 hand-finished RS2 exports in <c>TestData/EOF</c>), so the whole
    ///         improvement was silently dead on exactly the charts that need it.
    ///         The fourth site to hit that trap — read phrases through
    ///         <see cref="PhraseMarkers" />. Structural phrases (COUNT / END /
    ///         noguitar) are skipped for the same reason
    ///         <c>ArrangementAutoFixService</c> skips them: they carry no sounds,
    ///         so Rocksmith asks the hand for nothing there.
    ///     </para>
    ///     <para>
    ///         <b>The floor is <c>capo + 1</c>.</b> The source anchor is copied
    ///         verbatim, and an imported chart can legitimately carry an anchor
    ///         at or below the capo — writing that forward mints the very defect
    ///         inspector rule L02 (<c>CapoAnchorRangeRule</c>) reports. Capo 0 is
    ///         a strict no-op.
    ///     </para>
    /// </summary>
    private static int EnsurePhraseStartAnchors(Arrangement arrangement)
    {
        var phrasePositions = PhraseMarkers.Playable(arrangement)
            .Select(m => m.Position)
            .Concat(arrangement.EventPoints
                .Where(e => !string.IsNullOrWhiteSpace(e.Section))
                .Select(e => e.Position))
            .Distinct()
            .OrderBy(p => p)
            .ToList();

        if (phrasePositions.Count == 0) return 0;

        var minFret = MinAnchorFret(arrangement.Capo);

        var changes = 0;
        foreach (var level in arrangement.Levels)
        {
            if (level.Anchors.Count == 0) continue;

            foreach (var phrasePosition in phrasePositions)
            {
                if (level.Anchors.Any(a => a.Position == phrasePosition)) continue;

                var source = level.Anchors.LastOrDefault(a => a.Position <= phrasePosition)
                             ?? level.Anchors.FirstOrDefault(a => a.Position > phrasePosition);
                if (source == null) continue;

                InsertAnchorSorted(level.Anchors,
                    new FretAnchor(phrasePosition, Math.Max(source.Fret, minFret), source.Width));
                changes++;
            }
        }

        return changes;
    }

    /// <summary>The lowest fret an FHP anchor may open on — <c>capo + 1</c>, and
    /// never past the neck (an unclamped <c>capo + 1</c> can invert a
    /// <see cref="Math.Clamp(int,int,int)" /> range and THROW). Mirrors
    /// <c>FretAnchorGenerationService.MinAnchorFret</c>.</summary>
    internal static int MinAnchorFret(int capo) => Math.Clamp(capo + 1, 1, MaxFret);

    private const int MaxFret = 24;

    private static int NormalizeChordNoteSustains(ChartLevel level)
    {
        var changes = 0;
        foreach (var chord in level.Chords.Where(c => c.ChordNotes.Count > 1))
        {
            var maxSustain = chord.ChordNotes.Max(n => n.Sustain);
            foreach (var chordNote in chord.ChordNotes)
            {
                if (chordNote.Sustain == maxSustain) continue;

                chordNote.Sustain = maxSustain;
                changes++;
            }
        }

        return changes;
    }

    private static int EnsureEndPhrase(Arrangement arrangement, IList<BeatMarker> beats, double totalAudioLengthMs)
    {
        // BOTH phrase stores. Reading EventPoint alone found zero phrases in
        // every imported chart (they live in PhraseIterations), so this never
        // added END to one. An END added as an EventPoint is exported with the
        // imported phrases: the writers merge the two stores.
        var phrases = PhraseMarkers.All(arrangement);

        if (phrases.Count < 2 ||
            phrases.Any(m => string.Equals(m.Name, "END", StringComparison.OrdinalIgnoreCase)))
        {
            return 0;
        }

        if (beats.Count == 0) return 0;

        var endMs = totalAudioLengthMs > 0
            ? totalAudioLengthMs
            : GetArrangementEndMs(arrangement, beats);
        var endPosition = TimelineMath.PositionFromMs(endMs, beats);
        var existing = arrangement.EventPoints.FirstOrDefault(ep => ep.Position == endPosition && string.IsNullOrWhiteSpace(ep.Phrase));
        if (existing != null)
        {
            existing.Phrase = "END";
            return 1;
        }

        InsertEventPointSorted(arrangement.EventPoints, new EventPoint
        {
            Position = endPosition,
            Phrase = "END"
        });
        return 1;
    }

    /// <summary>
    ///     Drops a link-next whose continuation is a bare zero-sustain stub —
    ///     the link carries nothing into it — and removes the stub.
    /// </summary>
    /// <remarks>
    ///     Two defects, either of which DELETED an arbitrary note the charter
    ///     had authored:
    ///     <list type="number">
    ///         <item>
    ///             No proximity check whatsoever. The old helper returned the
    ///             first standalone note ANYWHERE after the source, so a link
    ///             whose real target sat bars away still nominated — and deleted
    ///             — that note. A link target is a specific position: the
    ///             enforcer's own rule (<c>LinkNextRepair.FixLinkNexts</c>)
    ///             is that the continuation must land EXACTLY on
    ///             <c>position + sustain</c>, so that is the rule here too.
    ///         </item>
    ///         <item>
    ///             The index was built from <c>level.Notes</c> only, so a CHORD
    ///             occupying the string was invisible. A note linked into a chord
    ///             survives <c>EnforceProject</c> with <c>LinkNext == true</c>
    ///             (the enforcer's own index does include chord notes), so this
    ///             pass looked straight past the chord, found some standalone
    ///             note possibly many bars later, and deleted it. Both loops —
    ///             notes and chord notes — had it.
    ///         </item>
    ///     </list>
    ///     A continuation that IS a chord note is left alone, and NOTHING here
    ///     clears the link either: a linked chord is legal to author and the
    ///     landing is repaired at the export boundary. (<see cref="RemoveInvalidLinkNextPatterns" /> does not clear a link
    ///     into a chord either; a pass that did would be the destructive
    ///     fixer wearing another name.) A chord note also cannot be removed on
    ///     its own without corrupting the chord.
    /// </remarks>
    private static int RemoveUnnecessaryLinkNextTargets(ChartLevel level)
    {
        var changes = 0;
        var notesToRemove = new HashSet<GuitarNote>();
        var soundsByString = BuildLinkTargetIndex(level);

        foreach (var note in level.Notes.Where(n => n.LinkNext))
        {
            var stub = FindZeroSustainContinuation(soundsByString, note.StringIndex, note.Position, note.Sustain);
            if (stub == null) continue;

            note.LinkNext = false;
            changes++;
            notesToRemove.Add(stub);
        }

        foreach (var chord in level.Chords)
        {
            foreach (var chordNote in chord.ChordNotes.Where(n => n.LinkNext))
            {
                var stub = FindZeroSustainContinuation(soundsByString, chordNote.StringIndex, chord.Position,
                    chordNote.Sustain);
                if (stub == null) continue;

                chordNote.LinkNext = false;
                changes++;
                notesToRemove.Add(stub);
            }
        }

        foreach (var note in notesToRemove)
        {
            if (level.Notes.Remove(note))
                changes++;
        }

        return changes;
    }

    /// <summary>Every SOUND on a string — standalone note or chord note — at its
    /// chart position. A chord note carries a null <c>Note</c>: it occupies the
    /// string (so it can be the continuation, and hide whatever comes later) but
    /// must never be removed individually.</summary>
    private static Dictionary<int, List<LinkTargetSound>> BuildLinkTargetIndex(ChartLevel level)
    {
        var byString = new Dictionary<int, List<LinkTargetSound>>();

        foreach (var note in level.Notes)
        {
            if (!byString.TryGetValue(note.StringIndex, out var list))
                byString[note.StringIndex] = list = [];
            list.Add(new LinkTargetSound(note.Position, note));
        }

        foreach (var chord in level.Chords)
        {
            foreach (var chordNote in chord.ChordNotes)
            {
                if (!byString.TryGetValue(chordNote.StringIndex, out var list))
                    byString[chordNote.StringIndex] = list = [];
                list.Add(new LinkTargetSound(chord.Position, null));
            }
        }

        return byString;
    }

    /// <summary>The sound sitting EXACTLY at the link's continuation position,
    /// when it is a removable zero-sustain standalone note. Null for anything
    /// else — including a zero-sustain source, which has no continuation to
    /// speak of and would otherwise resolve to itself.</summary>
    private static GuitarNote? FindZeroSustainContinuation(
        IReadOnlyDictionary<int, List<LinkTargetSound>> soundsByString,
        int stringIndex,
        FractionalPosition sourcePosition,
        FractionalPosition sustain)
    {
        if (sustain == FractionalPosition.Zero) return null;
        if (!soundsByString.TryGetValue(stringIndex, out var sounds)) return null;

        var continuation = sourcePosition + sustain;
        foreach (var sound in sounds)
        {
            if (sound.Position != continuation) continue;
            return sound.Note is { } note && note.Sustain == FractionalPosition.Zero ? note : null;
        }

        return null;
    }

    private readonly record struct LinkTargetSound(FractionalPosition Position, GuitarNote? Note);

    private static int RemoveInvalidLinkNextPatterns(ChartLevel level)
    {
        var changes = 0;

        foreach (var note in level.Notes)
        {
            // An UNPITCHED slide cannot carry a link (inspector I09) — that is a
            // real, reported defect and clearing the flag is the whole repair.
            //
            // A note linked INTO A CHORD is NOT one, and must not be cleared
            // here. A LINKED CHORD IS LEGAL TO AUTHOR; what RS cannot spell is
            // the LANDING, and that is repaired at the EXPORT boundary by
            // Parsing/Xml/Song/LinkLandingBreakup, never in the chart. The
            // identical pass is absent from ArrangementAutoFixService, where it
            // cleared 27 links in one run on the owner's chart; it must not live
            // here either, reachable from Tools > Improve Chart. It reads as safe
            // because it only drops a flag, but it leaves the SUSTAIN: the tie
            // becomes a bare sustain running past the end of its own hand shape
            // and every linked slide chord comes apart at once. The inspector
            // does not report it either (LinkNextRule stopped raising
            // NoteLinkedToChord for the same reason), so this was an Improve
            // Chart run inventing a finding the editor never showed.
            if (!note.LinkNext || !note.UnpitchedSlide) continue;

            note.LinkNext = false;
            changes++;
        }

        // A PARTIALLY linked chord is not one either. This pass cleared every
        // link on a chord where only some notes were linked, and no rule reported
        // it: LinkNextRule and DLC Builder check each chord note's link on its own,
        // and an SNG chord note carries its own per-string mask. A chord note whose
        // link is actually broken (no target, misaligned) IS reported by
        // LinkNextRule, so the charter sees it and decides.
        foreach (var chord in level.Chords)
        {
            foreach (var chordNote in chord.ChordNotes)
            {
                if (!chordNote.LinkNext || !chordNote.UnpitchedSlide) continue;
                chordNote.LinkNext = false;
                changes++;
            }
        }

        return changes;
    }

    /// <summary>
    ///     <b>Indexed, not scanned.</b> This ran
    ///     <c>level.HandShapes.FirstOrDefault(h =&gt; h.Position == chord.Position)</c>
    ///     per chord — O(chords × shapes) on a user-triggered Tools ▸ Improve
    ///     Chart. The map keeps the FIRST shape at each position, which is what
    ///     <c>FirstOrDefault</c> returned, and only <c>EndPosition</c> is written
    ///     below, so the map stays valid for the whole pass.
    /// </summary>
    private static int FixChordSlideHandShapes(ChartLevel level)
    {
        var changes = 0;
        var firstShapeAt = new Dictionary<FractionalPosition, HandShape>();
        foreach (var hs in level.HandShapes) firstShapeAt.TryAdd(hs.Position, hs);

        foreach (var chord in level.Chords)
        {
            if (!chord.ChordNotes.Any(n => n.LinkNext && n.SlideTo.HasValue)) continue;

            if (!firstShapeAt.TryGetValue(chord.Position, out var handShape)) continue;

            var chordEnd = chord.Position + chord.GetMaxSustain();
            if (handShape.EndPosition <= chordEnd) continue;

            handShape.EndPosition = chordEnd;
            changes++;
        }

        return changes;
    }

    private static int ImproveHandShapeLengths(ChartLevel level, IList<BeatMarker> beats)
    {
        if (beats.Count < 2 || level.HandShapes.Count == 0) return 0;

        var changes = 0;
        var orderedHandShapes = level.HandShapes.OrderBy(h => h.Position).ToList();
        // Sorted position arrays, built ONCE. Every query below reads only
        // Position, and this pass writes only HandShape.EndPosition, so nothing
        // it does can invalidate them.
        var index = new HandShapeLengthIndex(level);

        for (var i = 0; i < orderedHandShapes.Count; i++)
        {
            var handShape = orderedHandShapes[i];
            var originalEnd = handShape.EndPosition;
            // The greatest chord position inside the bar — the old
            // Where(...).OrderByDescending(...).FirstOrDefault() used only the
            // POSITION of what it found, so ties never mattered.
            var lastChordPosition = index.LastChordPositionIn(handShape.Position, handShape.EndPosition);

            if (lastChordPosition is { } lastPos && handShape.EndPosition - lastPos <=
                GetBeatFraction(lastPos, beats, 4))
            {
                var extendedEnd = handShape.EndPosition + GetBeatFraction(lastPos, beats, 4);
                var nextLimit = GetNextHandShapeLimit(orderedHandShapes, i, index);
                handShape.EndPosition = nextLimit.HasValue
                    ? FractionalPosition.Min(extendedEnd, nextLimit.Value)
                    : extendedEnd;
            }

            if (i + 1 < orderedHandShapes.Count)
            {
                var nextHandShape = orderedHandShapes[i + 1];
                var minGap = GetBeatFraction(handShape.EndPosition, beats, 8);
                var latestEnd = nextHandShape.Position - minGap;
                if (latestEnd > handShape.Position && handShape.EndPosition > latestEnd)
                    handShape.EndPosition = latestEnd;
            }

            if (handShape.EndPosition < handShape.Position)
                handShape.EndPosition = handShape.Position;

            if (handShape.EndPosition != originalEnd)
                changes++;
        }

        return changes;
    }

    /// <summary>
    ///     The earliest thing the bar may not grow past. The old body added EVERY
    ///     anchor after the bar to a list and then took its <c>Min</c>, and did
    ///     the same walk over notes + chords, once per hand shape — the minimum
    ///     of a filtered set is a binary search on a sorted array, so that is
    ///     what it is now. The <c>!= default</c> quirk on the content candidate
    ///     is preserved deliberately: it is how "nothing after the bar" was
    ///     spelled, and dropping it would add a position-zero candidate.
    /// </summary>
    private static FractionalPosition? GetNextHandShapeLimit(
        IReadOnlyList<HandShape> handShapes,
        int currentIndex,
        HandShapeLengthIndex index)
    {
        var current = handShapes[currentIndex];
        FractionalPosition? best = null;

        if (currentIndex + 1 < handShapes.Count)
            best = handShapes[currentIndex + 1].Position;

        if (index.FirstAnchorAfter(current.EndPosition) is { } anchor)
            best = best is { } b && b.CompareTo(anchor) <= 0 ? b : anchor;

        var nextContent = index.FirstContentAfter(current.EndPosition) ?? default;
        if (nextContent != default)
            best = best is { } b2 && b2.CompareTo(nextContent) <= 0 ? b2 : nextContent;

        return best;
    }

    private static FractionalPosition GetBeatFraction(FractionalPosition position, IList<BeatMarker> beats,
        int denominator)
    {
        var beatIndex = Math.Clamp(position.BeatId, 0, beats.Count - 1);
        var beatsInMeasure = beats[beatIndex].BeatsInMeasure > 0 ? beats[beatIndex].BeatsInMeasure : 4;
        return new FractionalPosition(new Fraction(beatsInMeasure, denominator));
    }

    private static void InsertAnchorSorted(IList<FretAnchor> anchors, FretAnchor anchor)
    {
        var index = 0;
        while (index < anchors.Count && anchors[index].Position < anchor.Position) index++;
        anchors.Insert(index, anchor);
    }

    private static void InsertEventPointSorted(IList<EventPoint> eventPoints, EventPoint eventPoint)
    {
        var index = 0;
        while (index < eventPoints.Count && eventPoints[index].Position < eventPoint.Position) index++;
        eventPoints.Insert(index, eventPoint);
    }

    private static double GetArrangementEndMs(Arrangement arrangement, IList<BeatMarker> beats)
    {
        var endMs = beats.Count > 0 ? beats[^1].Time : 0.0;
        foreach (var level in arrangement.Levels)
        {
            foreach (var note in level.Notes)
                endMs = Math.Max(endMs, note.EndPosition.GetPositionMs(beats));
            foreach (var chord in level.Chords)
                endMs = Math.Max(endMs, chord.EndPosition.GetPositionMs(beats));
            foreach (var handShape in level.HandShapes)
                endMs = Math.Max(endMs, handShape.EndPosition.GetPositionMs(beats));
        }

        return endMs;
    }

    private static int RemoveAll<T>(IList<T> items, Func<T, bool> predicate)
    {
        var count = 0;
        for (var i = items.Count - 1; i >= 0; i--)
        {
            if (!predicate(items[i])) continue;

            items.RemoveAt(i);
            count++;
        }

        return count;
    }
}
