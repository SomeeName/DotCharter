namespace DotCharter.Services.Editing.Collisions;

/// <summary>
///     The link-next pass of <see cref="ChartSpacingEnforcer" />, on its own because it
///     owns its own index (sounds by string, by position) and shares nothing with the
///     spacing and anchor rules but the bend de-duplication. The enforcer runs it
///     between the Rocksmith compatibility passes and <c>EnforceSoundSpacing</c>.
/// </summary>
internal static class LinkNextRepair
{
    /// <summary>
    ///     Makes a <c>LinkNext</c> chain consistent: the source rings exactly into
    ///     its continuation, and the continuation is fretted where the link (or its
    ///     slide) lands.
    ///     <para>
    ///     Two things this pass must NOT do, on every
    ///     save AND every load:
    ///     </para>
    ///     <para>
    ///     (1) <b>A wrong link flag is not a reason to delete the authored
    ///     ring-out.</b> When the continuation does not land exactly on
    ///     <c>Position + Sustain</c> the flag is dropped — and the sustain must not
    ///     be zeroed with it, or a 2-beat sustain whose link target sits 1 ms off
    ///     would become a 0-length note on the next save, irreversibly. Log Charter and
    ///     RS2014.NET's equivalents only drop the flag; <c>ChartSpacingEnforcer.EnforceSoundSpacing</c>
    ///     runs immediately afterwards and clamps the surviving sustain to the room
    ///     actually available, so nothing overlaps either way.
    ///     </para>
    ///     <para>
    ///     (2) <b>The fret rewrite must not touch a CHORD note.</b> Overwriting a
    ///     <see cref="ChordNote" />'s fret leaves the chord playing a shape its
    ///     <see cref="ChordTemplate" /> does not describe, and nothing here repoints
    ///     <c>TemplateId</c> — a template is SHARED, so every other chord on it
    ///     would be misrepresented if it were edited instead. That is exactly the
    ///     "template drift" state <c>RefretPlanner.IsChordPinned</c> refuses to plan
    ///     around and <c>TemplateAndOverlapRule</c> flags, and nothing in the app
    ///     repairs it. Reached whenever a note slides into a chord that shares a
    ///     string with it — legal, and what the GP importers' <c>LinkSlides</c>
    ///     produces. The chord keeps its authored frets; the inspector's link-next
    ///     rules report the mismatch instead of the enforcer silently "fixing" it
    ///     into a worse state.
    ///     </para>
    /// </summary>
    internal static void FixLinkNexts(ChartLevel level)
    {
        var soundsByString = BuildSoundsByString(level);

        foreach (var note in level.Notes)
        {
            if (!note.LinkNext) continue;

            var nextSound = FindNextSoundOnSameString(soundsByString, note.StringIndex, note.Position);
            var continuationPosition = note.Position + note.Sustain;
            if (nextSound == null || nextSound.Position.CompareTo(continuationPosition) != 0)
            {
                note.LinkNext = false;
                continue;
            }

            note.Sustain = FractionalPosition.Max(FractionalPosition.Zero, nextSound.Position - note.Position);
            ApplyLinkTargetFret(nextSound.Sound, note.Fret, note.SlideTo);
            EnsureLinkTargetBend(note.BendValues, nextSound.Sound.BendValues, nextSound.Position);
        }

        foreach (var chord in level.Chords)
        {
            foreach (var chordNote in chord.ChordNotes)
            {
                if (!chordNote.LinkNext) continue;

                var nextSound = FindNextSoundOnSameString(soundsByString, chordNote.StringIndex, chord.Position);
                var continuationPosition = chord.Position + chordNote.Sustain;
                if (nextSound == null || nextSound.Position.CompareTo(continuationPosition) != 0)
                {
                    chordNote.LinkNext = false;
                    continue;
                }

                chordNote.Sustain = FractionalPosition.Max(FractionalPosition.Zero,
                    nextSound.Position - chord.Position);
                ApplyLinkTargetFret(nextSound.Sound, chordNote.Fret, chordNote.SlideTo);
                EnsureLinkTargetBend(chordNote.BendValues, nextSound.Sound.BendValues, nextSound.Position);
            }
        }
    }

    /// <summary>Frets a link continuation where the link lands — but never a
    /// <see cref="ChordNote" />, whose fret belongs to its shared template. See
    /// <see cref="FixLinkNexts" />.</summary>
    private static void ApplyLinkTargetFret(IGuitarSound target, int sourceFret, int? slideTo)
    {
        if (target is ChordNote) return;
        target.Fret = GetLinkTargetFret(sourceFret, slideTo);
    }

    private static Dictionary<int, List<PositionedSound>> BuildSoundsByString(ChartLevel level)
    {
        var byString = new Dictionary<int, List<PositionedSound>>();

        foreach (var note in level.Notes)
        {
            if (!byString.TryGetValue(note.StringIndex, out var list))
                byString[note.StringIndex] = list = [];
            list.Add(new PositionedSound(note, note.Position));
        }

        foreach (var chord in level.Chords)
        {
            foreach (var chordNote in chord.ChordNotes)
            {
                if (!byString.TryGetValue(chordNote.StringIndex, out var list))
                    byString[chordNote.StringIndex] = list = [];
                list.Add(new PositionedSound(chordNote, chord.Position));
            }
        }

        foreach (var list in byString.Values)
            list.Sort((l, r) => l.Position.CompareTo(r.Position));

        return byString;
    }

    private static PositionedSound? FindNextSoundOnSameString(
        IReadOnlyDictionary<int, List<PositionedSound>> soundsByString,
        int stringIndex,
        FractionalPosition position)
    {
        if (!soundsByString.TryGetValue(stringIndex, out var sounds)) return null;
        foreach (var s in sounds)
        {
            if (s.Position.CompareTo(position) > 0)
                return s;
        }

        return null;
    }

    private sealed class PositionedSound
    {
        public PositionedSound(IGuitarSound sound, FractionalPosition position)
        {
            Sound = sound;
            Position = position;
        }

        public IGuitarSound Sound { get; }
        public FractionalPosition Position { get; }
    }

    private static int GetLinkTargetFret(int fret, int? slideTo) => slideTo is > 0 ? slideTo.Value : fret;

    private static void EnsureLinkTargetBend(IList<BendValue> sourceBends, IList<BendValue> targetBends,
        FractionalPosition targetPosition)
    {
        if (sourceBends.Count == 0) return;

        var lastStep = sourceBends.OrderBy(b => b.Position).Last().Step;
        if (lastStep <= 0) return;
        if (targetBends.Any(b => b.Position == targetPosition)) return;

        targetBends.Add(new BendValue(targetPosition, lastStep));
        ChartSpacingEnforcer.RemoveDuplicateBendValues(targetBends);
    }
}
