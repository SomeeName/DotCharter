using DotCharter.Services.State;
using Chord = DotCharter.Core.Domain.Guitar.Chord;
using FretAnchor = DotCharter.Core.Domain.Events.FretAnchor;
using GuitarNote = DotCharter.Core.Domain.Guitar.GuitarNote;
using HandShape = DotCharter.Core.Domain.Guitar.HandShape;

namespace DotCharter.Services.Editing.Sync;

/// <summary>What a sync did, or would do.</summary>
/// <param name="SharedSounds">Sounds the two arrangements play identically.</param>
/// <param name="SoundsChanged">…of those, how many the target did not already match.</param>
/// <param name="HandShapes">Hand shapes copied across.</param>
/// <param name="Anchors">FHP anchors copied across.</param>
/// <param name="Templates">Chord templates minted in the target to carry a copied grip.</param>
public sealed record ArrangementSyncSummary(
    int SharedSounds, int SoundsChanged, int HandShapes, int Anchors, int Templates)
{
    public static readonly ArrangementSyncSummary Empty = new(0, 0, 0, 0, 0);

    public bool WouldChangeAnything => SoundsChanged > 0 || HandShapes > 0 || Anchors > 0;
}

/// <summary>
///     Carries the charting work done on one arrangement over to another,
///     wherever the two actually play the same thing.
///     <para>
///         A Lead chart is usually Lead AND rhythm parts — the same riff is in
///         both arrangements, note for note — so every fret-hand position, every
///         fingering and every hand shape gets authored twice. This does the
///         second pass.
///     </para>
///     <para>
///         The unit is the SOUND, never the measure. A note matches on
///         (position, string, fret) and a chord on (position, its exact set of
///         string→fret pairs); anything the target does not play identically is
///         left completely alone. That is what keeps a Lead-only lick — the slide
///         chord at the end of a bar the two otherwise share — out of the Rhythm
///         chart while the rest of that bar comes across.
///     </para>
/// </summary>
public static class ArrangementSoundSync
{
    /// <summary>What <see cref="Apply" /> would do, without touching anything.</summary>
    public static ArrangementSyncSummary Preview(Arrangement? source, Arrangement? target) =>
        Run(source, target, apply: false);

    /// <summary>
    ///     Copies the source's expression onto every sound the target plays
    ///     identically, then the hand shapes and anchors that govern only shared
    ///     ground. OVERWRITES: a shared sound the target had charted differently
    ///     takes the source's answer.
    /// </summary>
    public static ArrangementSyncSummary Apply(Arrangement? source, Arrangement? target) =>
        Run(source, target, apply: true);

    private static ArrangementSyncSummary Run(Arrangement? source, Arrangement? target, bool apply)
    {
        if (source == null || target == null || ReferenceEquals(source, target))
            return ArrangementSyncSummary.Empty;

        // The HARDEST level is the whole song and where charting happens; the
        // easier levels are generated FROM it, so syncing them would be writing
        // into derived data (the caller regenerates them, see SyncArrangement).
        var sourceLevel = ChartLevels.Hardest(source);
        var targetLevel = ChartLevels.Hardest(target);
        if (sourceLevel == null || targetLevel == null) return ArrangementSyncSummary.Empty;

        var targetNotes = new Dictionary<string, GuitarNote>(StringComparer.Ordinal);
        foreach (var note in targetLevel.Notes) targetNotes.TryAdd(KeyOf(note), note);

        var targetChords = new Dictionary<string, Chord>(StringComparer.Ordinal);
        foreach (var chord in targetLevel.Chords) targetChords.TryAdd(KeyOf(chord), chord);

        var shared = 0;
        var changed = 0;
        var minted = new List<ChordTemplate>();
        // Positions the two arrangements agree on, which is what decides whether
        // a hand shape or an anchor may travel.
        var sharedPositions = new HashSet<FractionalPosition>();
        var sourceSharedPositions = new HashSet<FractionalPosition>();

        foreach (var note in sourceLevel.Notes)
        {
            if (!targetNotes.TryGetValue(KeyOf(note), out var twin)) continue;
            shared++;
            sharedPositions.Add(twin.Position);
            sourceSharedPositions.Add(note.Position);
            if (ChartActionStateComparer.GuitarNotesEqual(note, twin)) continue;
            changed++;
            if (apply) twin.CopyFrom(note);
        }

        foreach (var chord in sourceLevel.Chords)
        {
            if (!targetChords.TryGetValue(KeyOf(chord), out var twin)) continue;
            shared++;
            sharedPositions.Add(twin.Position);
            sourceSharedPositions.Add(chord.Position);

            var differs = !ChartActionStateComparer.ChordsEqual(chord, twin);
            if (differs)
            {
                changed++;
                if (apply) twin.CopyFrom(chord);
            }

            // TemplateId is arrangement-LOCAL: id 7 in Lead is a different grip
            // from id 7 in Rhythm, so a copied id has to be resolved into the
            // target by SHAPE. Doing it even when nothing else differed is what
            // carries a fingering-only edit across.
            if (apply)
            {
                var resolved = ResolveTemplate(source, target, chord.TemplateId, minted);
                if (resolved != null) twin.TemplateId = resolved.Id;
            }
        }

        var handShapes = SyncHandShapes(source, target, sourceLevel, targetLevel, sourceSharedPositions, minted, apply);
        var anchors = SyncAnchors(sourceLevel, targetLevel, sharedPositions, apply);

        if (apply)
        {
            foreach (var template in minted)
            {
                if (!target.ChordTemplates.Contains(template))
                    target.ChordTemplates.Add(template);
            }
        }

        return new ArrangementSyncSummary(shared, changed, handShapes, anchors, minted.Count);
    }

    /// <summary>
    ///     A hand shape travels only when EVERY sound under it in the source is
    ///     shared. A bar that covers even one sound the target does not play
    ///     would declare a string the target never sounds — which is the exact
    ///     shape of the Lead-only slide chord this is written to protect.
    /// </summary>
    private static int SyncHandShapes(
        Arrangement source, Arrangement target, ChartLevel sourceLevel, ChartLevel targetLevel,
        HashSet<FractionalPosition> sourceSharedPositions, List<ChordTemplate> minted, bool apply)
    {
        var copied = 0;

        foreach (var shape in sourceLevel.HandShapes.OrderBy(h => h.Position))
        {
            var covered = 0;
            var allShared = true;
            foreach (var position in SourcePositionsIn(sourceLevel, shape.Position, shape.EndPosition))
            {
                covered++;
                if (sourceSharedPositions.Contains(position)) continue;
                allShared = false;
                break;
            }

            if (covered == 0 || !allShared) continue;
            copied++;
            if (!apply) continue;

            // Overwrite: whatever the target had over this span goes, so the two
            // never end up with overlapping bars.
            var doomed = targetLevel.HandShapes
                .Where(h => h.Position < shape.EndPosition && h.EndPosition > shape.Position)
                .ToList();
            foreach (var h in doomed) targetLevel.HandShapes.Remove(h);

            var resolved = ResolveTemplate(source, target, shape.TemplateId, minted);
            targetLevel.HandShapes.Add(new HandShape
            {
                Position = shape.Position,
                EndPosition = shape.EndPosition,
                Name = shape.Name,
                TemplateId = resolved?.Id ?? -1
            });
        }

        if (apply && copied > 0)
        {
            var ordered = targetLevel.HandShapes.OrderBy(h => h.Position).ToList();
            targetLevel.HandShapes.Clear();
            foreach (var h in ordered) targetLevel.HandShapes.Add(h);
        }

        return copied;
    }

    /// <summary>
    ///     An anchor travels when the target has at least one shared sound in the
    ///     region it governs — its own position up to the next source anchor.
    ///     An anchor over ground the target does not play says nothing there, and
    ///     copying it would move a hand that is not playing.
    /// </summary>
    private static int SyncAnchors(
        ChartLevel sourceLevel, ChartLevel targetLevel,
        HashSet<FractionalPosition> sharedPositions, bool apply)
    {
        var copied = 0;
        var ordered = sourceLevel.Anchors.OrderBy(a => a.Position).ToList();

        for (var i = 0; i < ordered.Count; i++)
        {
            var anchor = ordered[i];
            var regionEnd = i + 1 < ordered.Count ? ordered[i + 1].Position : (FractionalPosition?)null;

            var governsSharedGround = sharedPositions.Any(p =>
                p >= anchor.Position && (regionEnd == null || p < regionEnd.Value));
            if (!governsSharedGround) continue;

            copied++;
            if (!apply) continue;

            // One beat holds ONE fret-hand position — same rule the highway's
            // anchor collision enforces.
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

        return copied;
    }

    /// <summary>
    ///     Finds or mints the target-arrangement template for the grip
    ///     <paramref name="sourceTemplateId" /> names in the source — and carries
    ///     the source's FINGERING onto it.
    ///     <para>
    ///         The fingering is the point: the same grip is played with the same
    ///         fingers, which is the rule the auto-fingering propagation already
    ///         follows, and re-deriving it in the target would throw away exactly
    ///         the hand-authored answer this is here to copy.
    ///     </para>
    /// </summary>
    private static ChordTemplate? ResolveTemplate(
        Arrangement source, Arrangement target, int sourceTemplateId, IList<ChordTemplate> minted)
    {
        if (sourceTemplateId < 0) return null;
        var sourceTemplate = ChordTemplateResolver.FindById(source, sourceTemplateId);
        if (sourceTemplate == null) return null;

        var before = minted.Count;
        var resolved = ChordTemplateResolver.Resolve(target, sourceTemplate.Frets,
            ArpeggioFlavour.Of(sourceTemplate), sourceTemplate.Name, minted);
        // A copy of a shape the charter left unnamed on purpose is unnamed too.
        if (minted.Count > before && sourceTemplate.Unnamed)
        {
            resolved.Name = string.Empty;
            resolved.Unnamed = true;
        }

        if (sourceTemplate.Fingers.Count > 0)
            resolved.Fingers = new Dictionary<int, int>(sourceTemplate.Fingers);

        return resolved;
    }

    private static IEnumerable<FractionalPosition> SourcePositionsIn(
        ChartLevel level, FractionalPosition start, FractionalPosition end)
    {
        foreach (var note in level.Notes)
        {
            if (note.Position >= start && note.Position < end)
                yield return note.Position;
        }

        foreach (var chord in level.Chords)
        {
            if (chord.Position >= start && chord.Position < end)
                yield return chord.Position;
        }
    }

    /// <summary>
    ///     A sound's identity for matching: WHAT IS PLAYED, nothing else. Notes
    ///     and chords never cross-match — a single note and a one-note chord are
    ///     different objects in RS-XML and in the editor.
    /// </summary>
    private static string KeyOf(GuitarNote note) => $"n|{note.Position}|{note.StringIndex}:{note.Fret}";

    private static string KeyOf(Chord chord)
    {
        var shape = string.Join(",", chord.ChordNotes
            .OrderBy(n => n.StringIndex)
            .Select(n => $"{n.StringIndex}:{n.Fret}"));
        return $"c|{chord.Position}|{shape}";
    }
}
