using DotCharter.Core.Domain.Events;
using DotCharter.Core.Domain.Guitar;
using DotCharter.Core.Domain.Primitives;
using DotCharter.Core.Domain.Project;
using DotCharter.Core.Utilities;

namespace DotCharter.Core.Linking;

/// <summary>
///     Copies the content of one linked run onto another placement of its group: the notes,
///     chords, anchors, hand shapes and fret-hand mutes that START inside the source run, moved
///     by whole beats (both runs start on a barline and span the same measures). Pure planning:
///     <see cref="Plan" /> touches nothing, the returned <see cref="LinkedRunCopy" /> applies and
///     reverts itself by instance, so a history step can hold it.
///     <para>
///         <b>What is not copied verbatim</b> is exactly what the comparison rule treats as
///         context (<see cref="MeasureContentIndex" />): a sustain or hand shape the source holds
///         to what follows it is lengthened to the TARGET's own bound (in milliseconds mode a
///         different number of beats at a different tempo), and the caller then runs the
///         clean-up pass over the target level, which shortens by its own rule. A chord template is
///         resolved in the target arrangement by grip (frets, fingers, ghost gems, arpeggio
///         flags) and minted there when it has none; an existing template is never changed. The
///         fret-hand position in effect at the run's start is pinned with an anchor when the
///         target's surroundings would leave a different one in effect. A hand shape reaching
///         into the target run from before it is trimmed at the run's start (the run edge is a
///         hand-shape boundary).
///     </para>
///     <para>
///         Notation voices (Guitar Pro voices 2–4) are not copied: the comparison rule does not
///         compare them, so they are not part of what a link holds equal.
///     </para>
/// </summary>
public static class LinkedRunCopier
{
    public static LinkedRunCopy Plan(MeasureContentIndex source, FractionalPosition sourceStart, FractionalPosition sourceEnd,
        Arrangement target, ChartLevel targetLevel, FractionalPosition targetStart, IList<BeatMarker> beats,
        MeasureContentOptions rule)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(targetLevel);
        ArgumentNullException.ThrowIfNull(beats);

        var delta = targetStart.BeatId - sourceStart.BeatId;
        var targetEnd = sourceEnd + delta;
        var level = source.Level;
        var copy = new LinkedRunCopy(target, targetLevel);
        var templates = new TemplateMap(source.Arrangement, target, copy.MintedList);
        var held = new List<(IGuitarSound Sound, FractionalPosition Onset, bool PassOtherNotes, FractionalPosition Keep)>();

        foreach (var note in level.Notes)
        {
            if (!Inside(note.Position, sourceStart, sourceEnd)) continue;
            var clone = note.Clone();
            clone.Position = note.Position + delta;
            ShiftBends(clone.BendValues, delta);
            copy.Notes.Added.Add(clone);
            if (source.IsHeld(note, note.Position, sourceEnd, note.PassOtherNotes))
                held.Add((clone, clone.Position, clone.PassOtherNotes, source.HeldThreshold(note.Position, sourceEnd, note.PassOtherNotes) - note.Position));
        }

        foreach (var chord in level.Chords)
        {
            if (!Inside(chord.Position, sourceStart, sourceEnd)) continue;
            var clone = chord.Clone();
            clone.Position = chord.Position + delta;
            clone.TemplateId = templates.Map(chord.TemplateId);
            for (var i = 0; i < chord.ChordNotes.Count; i++)
            {
                ShiftBends(clone.ChordNotes[i].BendValues, delta);
                if (source.IsHeld(chord.ChordNotes[i], chord.Position, sourceEnd, chord.PassOtherNotes))
                {
                    held.Add((clone.ChordNotes[i], clone.Position, clone.PassOtherNotes,
                        source.HeldThreshold(chord.Position, sourceEnd, chord.PassOtherNotes) - chord.Position));
                }
            }

            copy.Chords.Added.Add(clone);
        }

        foreach (var anchor in level.Anchors)
        {
            if (!Inside(anchor.Position, sourceStart, sourceEnd)) continue;
            var clone = anchor.Clone();
            clone.Position = anchor.Position + delta;
            copy.Anchors.Added.Add(clone);
        }

        PinStartAnchor(copy, level, targetLevel, sourceStart, targetStart);

        var heldShapes = new List<HandShape>();
        var clampedShapes = new List<(HandShape Shape, FractionalPosition Onset)>();
        foreach (var shape in level.HandShapes)
        {
            var startsInside = Inside(shape.Position, sourceStart, sourceEnd);
            var reachesIn = shape.Position < sourceStart && shape.EndPosition > sourceStart;
            if (!startsInside && !reachesIn) continue;
            var clone = shape.Clone();
            clone.Position = startsInside ? shape.Position + delta : targetStart;
            clone.EndPosition = shape.EndPosition + delta;
            clone.TemplateId = templates.Map(shape.TemplateId);
            copy.HandShapes.Added.Add(clone);
            if (source.IsShapeHeld(shape, sourceEnd)) heldShapes.Add(clone);
            else if (source.ShapeEndOnset(shape, sourceEnd) is { } onset) clampedShapes.Add((clone, onset + delta));
        }

        foreach (var mute in level.FretHandMutes)
        {
            if (Inside(mute.Position, sourceStart, sourceEnd)) copy.Mutes.Added.Add(new FretHandMute(mute.Position + delta));
        }

        copy.Notes.Removed.AddRange(targetLevel.Notes.Where(n => Inside(n.Position, targetStart, targetEnd)));
        copy.Chords.Removed.AddRange(targetLevel.Chords.Where(c => Inside(c.Position, targetStart, targetEnd)));
        copy.Anchors.Removed.AddRange(targetLevel.Anchors.Where(a => Inside(a.Position, targetStart, targetEnd)));
        copy.HandShapes.Removed.AddRange(targetLevel.HandShapes.Where(h => Inside(h.Position, targetStart, targetEnd)));
        copy.Mutes.Removed.AddRange(targetLevel.FretHandMutes.Where(m => Inside(m.Position, targetStart, targetEnd)));
        foreach (var shape in targetLevel.HandShapes)
        {
            if (shape.Position < targetStart && shape.EndPosition > targetStart)
                copy.TrimmedList.Add((shape, shape.EndPosition, targetStart));
        }

        FitHeldEnds(copy, target, targetLevel, targetEnd, beats, rule, held, heldShapes, clampedShapes);
        return copy;
    }

    /// <summary>
    ///     Re-fits each held sustain and hand shape to the target's surroundings, measured on the
    ///     target level as it will be once the copy lands.
    /// </summary>
    private static void FitHeldEnds(LinkedRunCopy copy, Arrangement target, ChartLevel targetLevel,
        FractionalPosition targetEnd, IList<BeatMarker> beats, MeasureContentOptions rule,
        List<(IGuitarSound Sound, FractionalPosition Onset, bool PassOtherNotes, FractionalPosition Keep)> held, List<HandShape> heldShapes,
        List<(HandShape Shape, FractionalPosition Onset)> clampedShapes)
    {
        if (held.Count == 0 && heldShapes.Count == 0 && clampedShapes.Count == 0) return;

        var landed = new ChartLevel
        {
            DifficultyIndex = targetLevel.DifficultyIndex,
            Notes = [.. targetLevel.Notes.Except(copy.Notes.Removed), .. copy.Notes.Added],
            Chords = [.. targetLevel.Chords.Except(copy.Chords.Removed), .. copy.Chords.Added],
            HandShapes = [.. targetLevel.HandShapes.Except(copy.HandShapes.Removed), .. copy.HandShapes.Added]
        };
        var index = new MeasureContentIndex(target, landed, beats, rule);

        // Only ever LENGTHENED here, to the target's own bound: the clean-up pass the caller runs
        // over the target level afterwards does every shortening, by its own rule (a link-next
        // sustain is the distance to the next note on its string, which it recomputes too).
        foreach (var (sound, onset, passOtherNotes, keep) in held)
        {
            if (sound.LinkNext)
            {
                // Its continuation lies past the source run, so whether it links is context: it
                // does in the target only where the next sound on its string starts exactly where
                // it rings to (the rule the clean-up pass's link repair applies), and otherwise it
                // is a plain ring-out there.
                if (NextOnString(landed, sound.StringIndex, onset) == onset + sound.Sustain) continue;
                sound.LinkNext = false;
            }

            var heldEnd = index.HeldEnd(onset, targetEnd, passOtherNotes);
            if (onset + sound.Sustain < index.HeldThreshold(onset, targetEnd, passOtherNotes))
                sound.Sustain = FractionalPosition.Max(FractionalPosition.Zero, heldEnd - onset);

            // Bend points the source's clean-up cut off at ITS bound are not part of the content;
            // in the target they sit at the target's bound, where the comparison drops them too.
            foreach (var bend in sound.BendValues)
            {
                if (bend.Position - onset >= keep && bend.Position < heldEnd) bend.Position = heldEnd;
            }
        }

        foreach (var shape in heldShapes)
        {
            if (!index.IsShapeHeld(shape, targetEnd)) shape.EndPosition = index.ShapeHeldEnd(shape, targetEnd);
        }

        // A shape the clean-up pass cut short before a sound ends the gap before that sound —
        // a different number of beats at the target's tempo.
        foreach (var (shape, onset) in clampedShapes)
        {
            if (index.ShapeEndOnset(shape, targetEnd) != onset) shape.EndPosition = onset - index.GapAt(shape.Position);
        }
    }

    private static FractionalPosition? NextOnString(ChartLevel level, int stringIndex, FractionalPosition after)
    {
        FractionalPosition? next = null;
        foreach (var note in level.Notes)
        {
            if (note.StringIndex == stringIndex && note.Position > after && (next == null || note.Position < next)) next = note.Position;
        }

        foreach (var chord in level.Chords)
        {
            if (chord.Position > after && (next == null || chord.Position < next) && chord.ChordNotes.Any(n => n.StringIndex == stringIndex))
                next = chord.Position;
        }

        return next;
    }

    /// <summary>
    ///     The fret-hand position in effect at the source run's start, pinned at the target's start
    ///     when the target would otherwise inherit a different one from the bars before it.
    /// </summary>
    private static void PinStartAnchor(LinkedRunCopy copy, ChartLevel source, ChartLevel target,
        FractionalPosition sourceStart, FractionalPosition targetStart)
    {
        if (copy.Anchors.Added.Any(a => a.Position == targetStart)) return;
        var inEffect = source.Anchors.Where(a => a.Position < sourceStart).MaxBy(a => a.Position);
        if (inEffect == null) return;
        var inherited = target.Anchors.Where(a => a.Position < targetStart).MaxBy(a => a.Position);
        if (inherited != null && inherited.Fret == inEffect.Fret && inherited.Width == inEffect.Width) return;
        copy.Anchors.Added.Add(new FretAnchor(targetStart, inEffect.Fret, inEffect.Width));
    }

    private static bool Inside(FractionalPosition position, FractionalPosition start, FractionalPosition end) =>
        position >= start && position < end;

    private static void ShiftBends(IEnumerable<BendValue> bends, int delta)
    {
        foreach (var bend in bends) bend.Position += delta;
    }

    /// <summary>Source template id → target template id, by grip; mints into the target when it has none.</summary>
    private sealed class TemplateMap(Arrangement source, Arrangement target, List<ChordTemplate> minted)
    {
        private readonly Dictionary<int, int> _map = [];

        public int Map(int id)
        {
            if (id < 0 || ReferenceEquals(source, target)) return id;
            if (_map.TryGetValue(id, out var mapped)) return mapped;

            var template = source.ChordTemplates.FirstOrDefault(t => t.Id == id);
            if (template == null) return _map[id] = -1;

            var match = target.ChordTemplates.Concat(minted)
                .Where(t => SameGrip(t, template))
                .OrderBy(t => string.Equals(t.Name, template.Name, StringComparison.Ordinal) ? 0 : 1)
                .ThenBy(t => t.Id)
                .FirstOrDefault();
            if (match == null)
            {
                match = template.Clone();
                match.Id = target.ChordTemplates.Concat(minted).Select(t => t.Id).DefaultIfEmpty(-1).Max() + 1;
                minted.Add(match);
            }

            return _map[id] = match.Id;
        }

        /// <summary>Exactly what the comparison rule reads from a template.</summary>
        private static bool SameGrip(ChordTemplate a, ChordTemplate b) =>
            a.GhostMask == b.GhostMask && a.Arpeggio == b.Arpeggio && a.ForceArpeggioInRs == b.ForceArpeggioInRs &&
            a.NopInRs == b.NopInRs && MapEquality.SameEntries(a.Frets, b.Frets) &&
            MapEquality.SameEntries(Fingers(a), Fingers(b));

        private static Dictionary<int, int> Fingers(ChordTemplate t) =>
            t.Fingers.Where(f => f.Value >= 0).ToDictionary(f => f.Key, f => f.Value);
    }
}

/// <summary>
///     One planned copy onto a target run (<see cref="LinkedRunCopier.Plan" />): what it removes,
///     adds, mints and trims, by instance. <see cref="Apply" /> and <see cref="Revert" /> are exact
///     inverses on a chart in the state the other left.
/// </summary>
public sealed class LinkedRunCopy
{
    internal LinkedRunCopy(Arrangement target, ChartLevel level)
    {
        Target = target;
        Level = level;
    }

    public Arrangement Target { get; }
    public ChartLevel Level { get; }

    internal Swap<GuitarNote> Notes { get; } = new(n => n.Position);
    internal Swap<Chord> Chords { get; } = new(c => c.Position);
    internal Swap<FretAnchor> Anchors { get; } = new(a => a.Position);
    internal Swap<HandShape> HandShapes { get; } = new(h => h.Position);
    internal Swap<FretHandMute> Mutes { get; } = new(m => m.Position);
    internal List<ChordTemplate> MintedList { get; } = [];
    internal List<(HandShape Shape, FractionalPosition Before, FractionalPosition After)> TrimmedList { get; } = [];

    /// <summary>The templates this copy adds to the target arrangement.</summary>
    public IReadOnlyList<ChordTemplate> MintedTemplates => MintedList;

    /// <summary>The notes, chord notes and hand shapes the copy adds: what the clean-up pass run after it owns.</summary>
    public IEnumerable<object> AddedElements =>
        Notes.Added.Cast<object>().Concat(Chords.Added.SelectMany(c => c.ChordNotes)).Concat(HandShapes.Added);

    /// <summary>Notes and chords the copy puts into the target run.</summary>
    public int SoundsAdded => Notes.Added.Count + Chords.Added.Count;

    public void Apply()
    {
        foreach (var template in MintedList) Target.ChordTemplates.Add(template);
        foreach (var (shape, _, after) in TrimmedList) shape.EndPosition = after;
        Notes.Apply(Level.Notes);
        Chords.Apply(Level.Chords);
        Anchors.Apply(Level.Anchors);
        HandShapes.Apply(Level.HandShapes);
        Mutes.Apply(Level.FretHandMutes);
    }

    public void Revert()
    {
        Mutes.Revert(Level.FretHandMutes);
        HandShapes.Revert(Level.HandShapes);
        Anchors.Revert(Level.Anchors);
        Chords.Revert(Level.Chords);
        Notes.Revert(Level.Notes);
        foreach (var (shape, before, _) in TrimmedList) shape.EndPosition = before;
        foreach (var template in MintedList) Target.ChordTemplates.Remove(template);
    }

    internal sealed class Swap<T>(Func<T, FractionalPosition> position) where T : class
    {
        public List<T> Removed { get; } = [];
        public List<T> Added { get; } = [];

        public void Apply(IList<T> collection)
        {
            foreach (var item in Removed) collection.Remove(item);
            foreach (var item in Added) InsertSorted(collection, item);
        }

        public void Revert(IList<T> collection)
        {
            foreach (var item in Added) collection.Remove(item);
            foreach (var item in Removed) InsertSorted(collection, item);
        }

        private void InsertSorted(IList<T> collection, T item)
        {
            var at = position(item);
            var index = collection.Count;
            while (index > 0 && position(collection[index - 1]) > at) index--;
            collection.Insert(index, item);
        }
    }
}
