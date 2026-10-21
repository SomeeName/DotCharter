using DotCharter.Services.Parsing.MoverPhrases;

namespace DotCharter.Services.Parsing.Eof;

/// <summary>A moveR phrase read off an <c>.eof</c> beat, before it is resolved.</summary>
internal readonly record struct EofMoverPoint(EventPoint Point, int MoveBy, string OwnName);

/// <summary>Where an off-beat phrase is written in an <c>.eof</c>: on beat
/// <see cref="BeatIndex" />, named moveR&lt;<see cref="MoveBy" />&gt;.</summary>
internal readonly record struct EofMoverPlacement(int BeatIndex, int MoveBy);

/// <summary>
///     The moveR&lt;N&gt; phrase in an <c>.eof</c>. EOF writes it on a beat and only draws it
///     shifted (<c>beat.c</c> <c>eof_beat_contains_mover_rs_phrase</c>); the phrase and any
///     section on that beat really start at the N-th note counted from the beat, the note
///     ON the beat being the 1st (<c>song.c</c> <c>eof_get_pos_num_notes_after_timestamp</c>).
///     The count runs over the hardest level — EOF's "highest difficulty note at this
///     position" for a track with dynamic difficulty.
///     <para>
///         The import moves the phrase, its sections and the anchors with DLC Builder's
///         rules (<c>PhraseMover.fs</c>: an anchor on the beat is copied to the note when
///         none is there, and the one on the beat goes when no note or chord is left on
///         it), so the chart is what DLC Builder would build from EOF's XML. The export
///         writes an off-beat phrase back in that form.
///     </para>
/// </summary>
internal static class EofMoverPhrases
{
    /// <summary>
    ///     Moves each moveR phrase to its note and gives it a name: its own ("riff moveR2"
    ///     → "riff"), else the section it starts, else "phrase". A phrase without enough
    ///     notes after it stays on its beat under its moveR name, as EOF leaves it.
    /// </summary>
    public static void Resolve(Arrangement arrangement, IReadOnlyList<EofMoverPoint> movers)
    {
        if (movers.Count == 0) return;

        var level = Hardest(arrangement);
        if (level is null) return;

        var renamed = new HashSet<string>();
        foreach (var (point, moveBy, ownName) in movers)
        {
            var beat = point.Position;
            if (NthSoundFrom(level, beat, moveBy) is not { } target) continue;

            var sections = arrangement.EventPoints
                .Where(ep => ep.Position.Equals(beat) && !string.IsNullOrEmpty(ep.Section)).ToList();
            foreach (var lvl in arrangement.Levels) MoveAnchor(lvl, beat, target);
            foreach (var section in sections) section.Position = target;
            point.Position = target;

            var oldName = point.Phrase;
            var newName = ownName.Length > 0
                ? ownName
                : MoverPhraseName.FallbackName(sections.FirstOrDefault()?.Section, arrangement.Phrases);
            point.Phrase = newName;
            if (!arrangement.Phrases.Contains(newName)) arrangement.Phrases.Add(newName);
            if (arrangement.PhraseInfos.TryGetValue(oldName, out var info))
                arrangement.PhraseInfos[newName] = new PhraseInfo(info.MaxDifficulty, info.Solo);
            renamed.Add(oldName);
        }

        foreach (var oldName in renamed.Where(n => arrangement.EventPoints.All(ep => ep.Phrase != n)))
        {
            arrangement.Phrases.Remove(oldName);
            arrangement.PhraseInfos.Remove(oldName);
        }
    }

    /// <summary>
    ///     Which phrase positions the export writes as moveR: a phrase between two beats
    ///     that starts on a note of the hardest level, with no other phrase or section
    ///     between the beat before it and it (EOF keeps one per beat), and no level with an
    ///     anchor on that beat but none on the note (the import would move that anchor).
    ///     Any other phrase keeps its millisecond position.
    /// </summary>
    public static Dictionary<FractionalPosition, EofMoverPlacement> PlanExport(Arrangement arrangement,
        int beatCount)
    {
        var plan = new Dictionary<FractionalPosition, EofMoverPlacement>();
        var level = Hardest(arrangement);
        if (level is null) return plan;

        var phrasePositions = arrangement.EventPoints.Where(ep => !string.IsNullOrEmpty(ep.Phrase))
            .Select(ep => ep.Position)
            .Concat(arrangement.PhraseIterations.Select(pi => pi.Position))
            .Distinct().ToList();
        var markerPositions = phrasePositions
            .Concat(arrangement.EventPoints.Where(ep => !string.IsNullOrEmpty(ep.Section)).Select(ep => ep.Position))
            .Distinct().ToList();

        foreach (var position in phrasePositions)
        {
            if (position.Fraction.Numerator == 0 || position.BeatId < 0 || position.BeatId >= beatCount) continue;

            var beat = new FractionalPosition(position.BeatId);
            if (markerPositions.Any(p => p.CompareTo(beat) >= 0 && p.CompareTo(position) < 0)) continue;
            if (arrangement.Levels.Any(l => HasAnchorAt(l, beat) && !HasAnchorAt(l, position))) continue;
            if (CountTo(level, beat, position) is { } moveBy)
                plan[position] = new EofMoverPlacement(position.BeatId, moveBy);
        }

        return plan;
    }

    private static ChartLevel? Hardest(Arrangement arrangement) =>
        arrangement.Levels.Count == 0 ? null : arrangement.Levels.MaxBy(l => l.DifficultyIndex);

    private static List<FractionalPosition> SoundsFrom(ChartLevel level, FractionalPosition from) =>
        level.Notes.Select(n => n.Position).Concat(level.Chords.Select(c => c.Position))
            .Where(p => p.CompareTo(from) >= 0).Distinct().Order().ToList();

    private static FractionalPosition? NthSoundFrom(ChartLevel level, FractionalPosition from, int n)
    {
        var sounds = SoundsFrom(level, from);
        return n >= 1 && n <= sounds.Count ? sounds[n - 1] : null;
    }

    private static int? CountTo(ChartLevel level, FractionalPosition from, FractionalPosition target)
    {
        var index = SoundsFrom(level, from).IndexOf(target);
        return index < 0 ? null : index + 1;
    }

    private static bool HasAnchorAt(ChartLevel level, FractionalPosition position) =>
        level.Anchors.Any(a => a.Position.Equals(position));

    private static void MoveAnchor(ChartLevel level, FractionalPosition from, FractionalPosition to)
    {
        var original = level.Anchors.FirstOrDefault(a => a.Position.Equals(from));
        if (original is null || HasAnchorAt(level, to)) return;

        var moved = original.Clone();
        moved.Position = to;
        var insertAt = level.Anchors.TakeWhile(a => a.Position.CompareTo(to) < 0).Count();
        level.Anchors.Insert(insertAt, moved);

        var soundLeft = level.Notes.Any(n => n.Position.Equals(from)) || level.Chords.Any(c => c.Position.Equals(from));
        if (!soundLeft) level.Anchors.Remove(original);
    }
}
