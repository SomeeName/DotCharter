using System.Xml.Linq;

namespace DotCharter.Services.Export.DynamicDifficulty;

/// <summary>Reduces mixed-muted chords without editing authored templates or splitting hand shapes.</summary>
internal static class DdMutedChordCleanup
{
    public static Dictionary<DdChord, int> ReduceChords(DdArrangement arr)
    {
        var authoredCount = arr.ChordTemplates.Count;
        var reboundChords = new Dictionary<DdChord, int>();
        foreach (var chord in arr.Levels.SelectMany(l => l.Chords))
        {
            if (!chord.HasChordNotes || chord.IsFretHandMute) continue;
            var muted = chord.ChordNotes!.FindAll(n => n.IsFretHandMute);
            if (muted.Count == 0 || muted.Count == chord.ChordNotes.Count) continue;
            chord.ChordNotes.RemoveAll(n => n.IsFretHandMute);
            if (Template(arr, chord.ChordId) is not { } template) continue;

            var frets = (int[])template.Frets.Clone();
            var fingers = (int[])template.Fingers.Clone();
            foreach (var note in muted)
            {
                frets[note.String] = -1;
                fingers[note.String] = -1;
            }

            var reduced = new DdChordTemplate(template.Name, template.DisplayName, fingers, frets) { Source = template.Source, ReducedFrom = template };
            var reducedId = arr.ChordTemplates.FindIndex(authoredCount, t => SameTemplate(t, reduced));
            if (reducedId == -1)
            {
                reducedId = arr.ChordTemplates.Count;
                arr.ChordTemplates.Add(reduced);
            }

            reboundChords.Add(chord, chord.ChordId);
            chord.ChordId = reducedId;
        }

        return reboundChords;
    }

    public static void RebindShapes(DdArrangement arr, Dictionary<DdChord, int> reboundChords)
    {
        if (reboundChords.Count == 0) return;
        foreach (var level in arr.Levels)
        {
            foreach (var shape in level.HandShapes)
            {
                if (IsArpeggioShape(arr, level, shape)) continue;
                var covered = level.Chords.Where(c => c.Time >= shape.StartTime && c.Time < shape.EndTime).ToList();
                if (covered.Exists(c => !reboundChords.ContainsKey(c) || reboundChords[c] != shape.ChordId)) continue;
                var candidates = covered.Where(reboundChords.ContainsKey).Select(c => c.ChordId).Distinct();
                foreach (var candidate in candidates)
                {
                    if (!CoversSounds(arr, level, shape, covered, arr.ChordTemplates[candidate])) continue;
                    shape.ChordId = candidate;
                    break;
                }
            }
        }
    }

    private static bool SameTemplate(DdChordTemplate left, DdChordTemplate right) =>
        left.Name == right.Name && left.DisplayName == right.DisplayName
        && left.Frets.SequenceEqual(right.Frets) && left.Fingers.SequenceEqual(right.Fingers)
        && XNode.DeepEquals(left.Source, right.Source);

    private static bool CoversSounds(DdArrangement arr, DdLevel level, DdHandShape shape,
        List<DdChord> covered, DdChordTemplate candidate)
    {
        foreach (var chord in covered)
        {
            var strings = chord.HasChordNotes
                ? chord.ChordNotes!.Select(n => n.String)
                : Enumerable.Range(0, arr.StringSlots).Where(s => arr.ChordTemplates[chord.ChordId].Frets[s] >= 0);
            if (strings.Any(s => candidate.Frets[s] < 0)) return false;
        }

        return !level.Notes.Any(n => n.Time < shape.EndTime
            && (n.Time >= shape.StartTime || n.Time + n.Sustain > shape.StartTime)
            && candidate.Frets[n.String] < 0);
    }

    private static bool IsArpeggioShape(DdArrangement arr, DdLevel level, DdHandShape shape)
    {
        if (Template(arr, shape.ChordId)?.DisplayName.EndsWith("-arp", StringComparison.OrdinalIgnoreCase) == true) return true;
        var notes = level.Notes.Where(n => n.Time >= shape.StartTime && n.Time < shape.EndTime).ToList();
        return notes.Count > 0 && !notes.All(n => n.String == notes[0].String || n.Time == notes[0].Time);
    }

    private static DdChordTemplate? Template(DdArrangement arr, int id) =>
        id >= 0 && id < arr.ChordTemplates.Count ? arr.ChordTemplates[id] : null;
}
