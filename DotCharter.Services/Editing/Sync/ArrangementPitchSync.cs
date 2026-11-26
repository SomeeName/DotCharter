using DotCharter.Services.State;
using Chord = DotCharter.Core.Domain.Guitar.Chord;
using FretAnchor = DotCharter.Core.Domain.Events.FretAnchor;
using GuitarNote = DotCharter.Core.Domain.Guitar.GuitarNote;

namespace DotCharter.Services.Editing.Sync;

/// <summary>
///     <see cref="ArrangementSoundSync" /> between arrangements whose strings DIFFER — a
///     7-string in drop A and its 6-string twin in drop D, a 5-string bass and a 4-string
///     one. A string index means nothing across the pair, so a sound matches on
///     (position, sounding pitch, fret), each source string standing for the target string
///     with the same open pitch (<see cref="ArrangementStringMap" />).
///     <para>
///         Only the HAND travels: chord fingerings (the template, strings remapped, found or
///         minted in the target — never an edit of a shared one), hand shapes over ground
///         both play alike, and fret-hand positions whose whole span the target plays alike.
///         Note edits stay where they are: the two parts sit on different strings, and
///         whatever the target re-voices is left completely alone.
///     </para>
/// </summary>
internal static class ArrangementPitchSync
{
    public static ArrangementSyncSummary Run(
        Arrangement source, Arrangement target, ChartLevel sourceLevel, ChartLevel targetLevel,
        ArrangementStringMap strings, bool apply)
    {
        // The rule pass decides on the chart as it stands and touches only re-voiced
        // ground, which the pitch pass below never matches, so the preview is exact.
        var rules = ArrangementRuleSync.Run(source, target, sourceLevel, targetLevel, strings, apply);

        var targetNotes = new Dictionary<string, GuitarNote>(StringComparer.Ordinal);
        foreach (var note in targetLevel.Notes) targetNotes.TryAdd(NoteKey(note.Position, note.StringIndex, note.Fret), note);

        var targetChords = new Dictionary<string, Chord>(StringComparer.Ordinal);
        foreach (var chord in targetLevel.Chords)
            targetChords.TryAdd(ChordKey(chord.Position, chord.ChordNotes.Select(n => (n.StringIndex, n.Fret))), chord);

        var minted = new List<ChordTemplate>();
        var matchedTargets = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var sourceShared = new HashSet<FractionalPosition>();
        var shared = 0;
        var unmatched = 0;
        var fingerings = 0;

        foreach (var note in sourceLevel.Notes)
        {
            if (!strings.TryMap(note.StringIndex, out var t) ||
                !targetNotes.TryGetValue(NoteKey(note.Position, t, note.Fret), out var twin) ||
                !strings.SoundsAlike(note.StringIndex, t, note.Fret))
            {
                unmatched++;
                continue;
            }

            shared++;
            matchedTargets.Add(twin);
            sourceShared.Add(note.Position);
        }

        foreach (var chord in sourceLevel.Chords)
        {
            if (MatchChord(chord, targetChords, strings) is not { } twin)
            {
                unmatched++;
                continue;
            }

            shared++;
            matchedTargets.Add(twin);
            sourceShared.Add(chord.Position);

            if (ResolveRemapped(source, target, chord.TemplateId, strings, minted) is not { } resolved ||
                twin.TemplateId == resolved.Id)
            {
                continue;
            }

            fingerings++;
            if (apply) twin.TemplateId = resolved.Id;
        }

        var (handShapes, handShapesLeft) = SyncHandShapes(
            source, target, sourceLevel, targetLevel, sourceShared, matchedTargets, strings, minted, apply);
        var (anchors, anchorsLeft) = SyncAnchors(sourceLevel, targetLevel, matchedTargets, apply);

        if (apply)
        {
            foreach (var template in minted)
            {
                if (!ChordTemplateCollectionOps.ContainsInstance(target.ChordTemplates, template))
                    target.ChordTemplates.Add(template);
            }
        }

        return new ArrangementSyncSummary(shared, fingerings, handShapes, anchors, minted.Count)
        {
            ByPitch = true,
            UnmatchedSounds = unmatched,
            HandShapesLeft = handShapesLeft,
            AnchorsLeft = anchorsLeft,
            RuleMappings = rules.Mappings,
            RuleFingerings = rules.Fingerings,
            RuleHandShapes = rules.HandShapes,
            RuleAnchors = rules.Anchors,
            RuleConflicts = rules.Conflicts
        };
    }

    /// <summary>
    ///     The target chord at the same position whose notes are EXACTLY the source's, each
    ///     on its mapped string at the same fret and pitch; null when any note has no twin.
    /// </summary>
    private static Chord? MatchChord(Chord chord, Dictionary<string, Chord> targetChords, ArrangementStringMap strings)
    {
        var mapped = new List<(int StringIndex, int Fret)>(chord.ChordNotes.Count);
        foreach (var note in chord.ChordNotes)
        {
            if (!strings.TryMap(note.StringIndex, out var t) || !strings.SoundsAlike(note.StringIndex, t, note.Fret))
                return null;
            mapped.Add((t, note.Fret));
        }

        return mapped.Count > 0 && targetChords.TryGetValue(ChordKey(chord.Position, mapped), out var twin) ? twin : null;
    }

    /// <summary>
    ///     A hand shape travels when every sound under it in the source matched AND every
    ///     sound under it in the target is one of those twins: a bar over a re-voiced chord
    ///     would declare a grip the target's hand does not make.
    /// </summary>
    private static (int Copied, int Left) SyncHandShapes(
        Arrangement source, Arrangement target, ChartLevel sourceLevel, ChartLevel targetLevel,
        HashSet<FractionalPosition> sourceShared, HashSet<object> matchedTargets, ArrangementStringMap strings,
        List<ChordTemplate> minted, bool apply)
    {
        var copied = 0;
        var left = 0;
        foreach (var shape in sourceLevel.HandShapes.OrderBy(h => h.Position))
        {
            var sourcePositions = ArrangementSoundSync.PositionsIn(sourceLevel, shape.Position, shape.EndPosition).ToList();
            var travels = sourcePositions.Count > 0 && sourcePositions.All(sourceShared.Contains) &&
                          AllMatched(targetLevel, shape.Position, shape.EndPosition, matchedTargets);

            var templateId = -1;
            if (travels && shape.TemplateId >= 0 && source.FindChordTemplate(shape.TemplateId) != null)
            {
                var resolved = ResolveRemapped(source, target, shape.TemplateId, strings, minted);
                travels = resolved != null;
                templateId = resolved?.Id ?? -1;
            }

            if (!travels)
            {
                left++;
                continue;
            }

            copied++;
            if (!apply) continue;

            var doomed = targetLevel.HandShapes
                .Where(h => h.Position < shape.EndPosition && h.EndPosition > shape.Position)
                .ToList();
            foreach (var h in doomed) targetLevel.HandShapes.Remove(h);

            var copy = shape.Clone();
            copy.TemplateId = templateId;
            targetLevel.HandShapes.Add(copy);
        }

        if (apply && copied > 0)
        {
            var ordered = targetLevel.HandShapes.OrderBy(h => h.Position).ToList();
            targetLevel.HandShapes.Clear();
            foreach (var h in ordered) targetLevel.HandShapes.Add(h);
        }

        return (copied, left);
    }

    /// <summary>
    ///     An anchor travels when the target plays something in the region it governs (up to
    ///     the next source anchor) and EVERY target sound there matched — only then does the
    ///     hand really sit in the same place. Frets are absolute, so the fret copies as is.
    /// </summary>
    private static (int Copied, int Left) SyncAnchors(
        ChartLevel sourceLevel, ChartLevel targetLevel, HashSet<object> matchedTargets, bool apply)
    {
        var copied = 0;
        var left = 0;
        var ordered = sourceLevel.Anchors.OrderBy(a => a.Position).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            var anchor = ordered[i];
            var regionEnd = i + 1 < ordered.Count ? ordered[i + 1].Position : (FractionalPosition?)null;
            if (!AllMatched(targetLevel, anchor.Position, regionEnd, matchedTargets))
            {
                left++;
                continue;
            }

            copied++;
            if (!apply) continue;

            var doomed = targetLevel.Anchors.Where(a => a.Position == anchor.Position).ToList();
            foreach (var a in doomed) targetLevel.Anchors.Remove(a);
            targetLevel.Anchors.Add(new FretAnchor(anchor.Position, anchor.Fret, anchor.Width));
        }

        if (apply && copied > 0)
        {
            var sorted = targetLevel.Anchors.OrderBy(a => a.Position).ToList();
            targetLevel.Anchors.Clear();
            foreach (var a in sorted) targetLevel.Anchors.Add(a);
        }

        return (copied, left);
    }

    /// <summary>At least one target sound in [start, end) (open-ended when null), and every one of them matched.</summary>
    private static bool AllMatched(
        ChartLevel targetLevel, FractionalPosition start, FractionalPosition? end, HashSet<object> matchedTargets)
    {
        var any = false;
        foreach (var note in targetLevel.Notes)
        {
            if (note.Position < start || (end is { } e1 && note.Position >= e1)) continue;
            if (!matchedTargets.Contains(note)) return false;
            any = true;
        }

        foreach (var chord in targetLevel.Chords)
        {
            if (chord.Position < start || (end is { } e2 && chord.Position >= e2)) continue;
            if (!matchedTargets.Contains(chord)) return false;
            any = true;
        }

        return any;
    }

    /// <summary>
    ///     The target template for the source's grip on the target's strings: the source
    ///     template remapped (a fresh copy, never the shared instance) and then found or
    ///     minted through the authored find-or-create. Null when the source names no
    ///     template or one of its strings has no twin.
    /// </summary>
    private static ChordTemplate? ResolveRemapped(
        Arrangement source, Arrangement target, int sourceTemplateId, ArrangementStringMap strings,
        List<ChordTemplate> minted)
    {
        if (sourceTemplateId < 0 || source.FindChordTemplate(sourceTemplateId) is not { } sourceTemplate) return null;
        return strings.Remap(sourceTemplate) is { } remapped
            ? ChordTemplateResolver.ResolveAuthored(target, remapped, minted)
            : null;
    }

    private static string NoteKey(FractionalPosition position, int stringIndex, int fret) =>
        $"n|{position}|{stringIndex}:{fret}";

    private static string ChordKey(FractionalPosition position, IEnumerable<(int StringIndex, int Fret)> notes) =>
        $"c|{position}|{string.Join(",", notes.OrderBy(n => n.StringIndex).Select(n => $"{n.StringIndex}:{n.Fret}"))}";
}
