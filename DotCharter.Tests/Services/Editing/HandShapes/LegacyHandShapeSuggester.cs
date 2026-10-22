using DotCharter.Services.Editing;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Editing.HandShapes;
using GuitarNote = DotCharter.Core.Domain.Guitar.GuitarNote;

namespace DotCharter.Tests.Services.Editing.HandShapes;

/// <summary>
///     The suggester as it was before the pooled merge (a position-keyed sorted map of sounds, one
///     dictionary per sound, a grip copied per join, a probe template per candidate), kept VERBATIM as
///     the oracle for <see cref="HandShapeSuggesterEquivalenceTests" />. Keep it dumb: it is the
///     answer the new path must give, so it is never optimised and never edited to match.
/// </summary>
internal static class LegacyHandShapeSuggester
{
    /// <summary>How far a bar reaches past a tail-less last sound with nothing after it.</summary>
    private static readonly FractionalPosition OneBeat = new(1);

    public static List<SuggestedHandShape> Suggest(
        Arrangement arrangement, ChartLevel level, HandShapeSuggestOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(arrangement);
        ArgumentNullException.ThrowIfNull(level);
        options ??= HandShapeSuggestOptions.Default;
        // Bass charters never bar single notes: 0 of 1,101 proposed bass notes sat under one of
        // their bars (tools/chart-eval/HandShapeEval.cs --kind bass, both splits).
        if (arrangement.ArrangementType == ArrangementType.Bass) return [];

        var templates = ChordTemplateResolver.IndexById(arrangement.ChordTemplates);
        var sounds = BuildSounds(level, templates);
        var coverage = new HandShapeCoverageIndex(level.HandShapes);
        var barStarts = level.HandShapes.Select(h => h.Position).OrderBy(p => p).ToList();
        var anchors = level.Anchors.Select(a => a.Position).OrderBy(p => p).ToList();
        var maxGap = new FractionalPosition(new Fraction((long)Math.Round(options.MaxGap * 48), 48));
        var capo = arrangement.Capo;
        var stringCount = arrangement.StringCount > 0 ? arrangement.StringCount : 6;

        var found = new List<SuggestedHandShape>();
        var i = 0;
        while (i < sounds.Count)
        {
            if (coverage.Covers(sounds[i].Position) || sounds[i].Frets.Count == 0)
            {
                i++;
                continue;
            }

            var grip = new Dictionary<int, int>(sounds[i].Frets);
            var chordShape = sounds[i].IsChord ? sounds[i].Frets : null;
            var runEnd = i;
            var reach = sounds[i].End;
            for (var j = i + 1; j < sounds.Count; j++)
            {
                var next = sounds[j];
                if (coverage.Covers(next.Position)) break;
                if (sounds[runEnd].Slides) break;
                if (next.Position.CompareTo(reach + maxGap) > 0) break;
                if (StartsIn(barStarts, sounds[i].Position, next.Position)) break;
                if (StartsIn(anchors, sounds[i].Position, next.Position)) break;
                if (next.IsChord && next.Frets.Count > 0)
                {
                    if (chordShape == null) chordShape = next.Frets;
                    else if (options.OneChordShape && !Nested(chordShape, next.Frets)) break;
                }

                if (!TryJoin(grip, next.Frets, capo, stringCount, options.MaxFretSpread)) break;

                runEnd = j;
                if (next.End.CompareTo(reach) > 0) reach = next.End;
            }

            var held = Count(sounds, i, runEnd);
            if (held.Sounds >= options.MinSounds && held.Notes > 0 && grip.Count >= options.MinStrings)
            {
                var existing = ExistingTemplate(arrangement, grip);
                if (existing != null || grip.Count >= options.MinStringsAlone)
                {
                    var end = BarEnd(sounds, runEnd, reach, barStarts, anchors);
                    found.Add(new SuggestedHandShape(sounds[i].Position, end, grip, existing, held.Sounds, held.Notes));
                }
            }

            i = runEnd + 1;
        }

        return found;
    }

    /// <summary>
    ///     The plain template to reuse: one with exactly these frets, else the closest declared
    ///     shape that CONTAINS them — the full chord an arpeggio picks a few strings of.
    /// </summary>
    public static ChordTemplate? ExistingTemplate(Arrangement arrangement, IReadOnlyDictionary<int, int> grip)
    {
        if (ChordTemplateResolver.FindByShape(arrangement, grip, ArpeggioFlavour.Plain) is { } exact) return exact;
        var seed = new ChordTemplate();
        foreach (var (stringIndex, fret) in grip) seed.Frets[stringIndex] = fret;
        var containing = ChordShapeSuperset.FindContaining(arrangement, seed, 1);
        return containing.Count > 0 ? containing[0] : null;
    }

    /// <summary>One shape contains the other: a full strum and a partial strum of it are the same chord.</summary>
    private static bool Nested(Dictionary<int, int> a, Dictionary<int, int> b) => Within(a, b) || Within(b, a);

    private static bool Within(Dictionary<int, int> inner, Dictionary<int, int> outer)
    {
        foreach (var (stringIndex, fret) in inner)
        {
            if (!outer.TryGetValue(stringIndex, out var held) || held != fret) return false;
        }

        return true;
    }

    /// <summary>Adds <paramref name="frets" /> to <paramref name="grip" /> when one hand can still
    /// hold the result; leaves <paramref name="grip" /> untouched otherwise.</summary>
    private static bool TryJoin(Dictionary<int, int> grip, Dictionary<int, int> frets, int capo, int stringCount,
        int maxSpread)
    {
        var grows = false;
        foreach (var (stringIndex, fret) in frets)
        {
            if (grip.TryGetValue(stringIndex, out var held))
            {
                if (held != fret) return false;
            }
            else
            {
                grows = true;
            }
        }

        if (!grows) return true;

        var candidate = new Dictionary<int, int>(grip);
        foreach (var (stringIndex, fret) in frets) candidate[stringIndex] = fret;
        if (!CanHold(candidate, capo, stringCount, maxSpread)) return false;

        foreach (var (stringIndex, fret) in frets) grip[stringIndex] = fret;
        return true;
    }

    /// <summary>
    ///     The grip fits one hand position: its fretted frets span at most
    ///     <paramref name="maxSpread" />, and the fingering heuristic gives every fretted string a
    ///     finger (it gives up on a shape it cannot finger, e.g. five separate press points).
    /// </summary>
    internal static bool CanHold(IReadOnlyDictionary<int, int> grip, int capo, int stringCount, int maxSpread)
    {
        int low = int.MaxValue, high = int.MinValue;
        foreach (var fret in grip.Values)
        {
            if (fret <= capo) continue;
            low = Math.Min(low, fret);
            high = Math.Max(high, fret);
        }

        if (high == int.MinValue) return true;
        if (high - low > maxSpread) return false;

        var probe = new ChordTemplate();
        foreach (var (stringIndex, fret) in grip) probe.Frets[stringIndex] = fret;
        ChordTemplateFingerSetter.SetSuggestedFingers(probe, stringCount);
        foreach (var (stringIndex, fret) in grip)
        {
            if (fret <= capo) continue;
            if (!probe.Fingers.TryGetValue(stringIndex, out var finger) || finger is < 0 or > 4) return false;
        }

        return true;
    }

    /// <summary>The bar ends at its last sound's tail — or, for a tail-less last sound, where the
    /// next sound starts (one beat at most) — and never past the next sound, bar or anchor.</summary>
    private static FractionalPosition BarEnd(List<Sound> sounds, int last, FractionalPosition reach,
        List<FractionalPosition> barStarts, List<FractionalPosition> anchors)
    {
        var lastPosition = sounds[last].Position;
        FractionalPosition? limit = last + 1 < sounds.Count ? sounds[last + 1].Position : null;
        limit = Earliest(limit, FirstAfter(barStarts, lastPosition));
        limit = Earliest(limit, FirstAfter(anchors, lastPosition));

        var end = reach.CompareTo(lastPosition) > 0 ? reach : lastPosition + OneBeat;
        if (limit is { } l && l.CompareTo(end) < 0) end = l;
        return end;
    }

    private static (int Sounds, int Notes) Count(List<Sound> sounds, int first, int last)
    {
        int held = 0, notes = 0;
        for (var k = first; k <= last; k++)
        {
            if (sounds[k].Frets.Count == 0) continue;
            held++;
            if (!sounds[k].IsChord) notes++;
        }

        return (held, notes);
    }

    /// <summary>
    ///     The level's sounds in time order: notes sharing a position are ONE sound (a chord spelled
    ///     as notes), a chord with no chord notes (Rocksmith's repeat box) plays its template, and a
    ///     fret-hand-muted note contributes no fret.
    /// </summary>
    private static List<Sound> BuildSounds(ChartLevel level, Dictionary<int, ChordTemplate> templates)
    {
        var byPosition = new SortedDictionary<FractionalPosition, Sound>();

        foreach (var note in level.Notes)
        {
            var sound = At(byPosition, note.Position);
            sound.Absorb(note.StringIndex, note.Fret, note.Mute, note.EndPosition, Slides(note));
        }

        foreach (var chord in level.Chords)
        {
            var sound = At(byPosition, chord.Position);
            sound.IsChord = true;
            if (chord.ChordNotes.Count > 0)
            {
                foreach (var cn in chord.ChordNotes)
                {
                    sound.Absorb(cn.StringIndex, cn.Fret, cn.Mute, chord.Position + cn.Sustain,
                        cn.SlideTo.HasValue || cn.UnpitchedSlide);
                }
            }
            else if (templates.TryGetValue(chord.TemplateId, out var template))
            {
                foreach (var (stringIndex, fret) in template.Frets)
                    sound.Absorb(stringIndex, fret, MuteType.None, chord.Position, false);
            }
        }

        return [.. byPosition.Values];
    }

    private static bool Slides(GuitarNote note) => note.SlideTo.HasValue || note.UnpitchedSlide;

    private static Sound At(SortedDictionary<FractionalPosition, Sound> sounds, FractionalPosition position)
    {
        if (!sounds.TryGetValue(position, out var sound))
        {
            sound = new Sound(position);
            sounds[position] = sound;
        }

        return sound;
    }

    /// <summary>True when a sorted list holds a position in (<paramref name="after" />,
    /// <paramref name="through" />].</summary>
    private static bool StartsIn(List<FractionalPosition> sorted, FractionalPosition after, FractionalPosition through) =>
        FirstAfter(sorted, after) is { } p && p.CompareTo(through) <= 0;

    private static FractionalPosition? FirstAfter(List<FractionalPosition> sorted, FractionalPosition after)
    {
        int lo = 0, hi = sorted.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) >> 1;
            if (sorted[mid].CompareTo(after) <= 0) lo = mid + 1;
            else hi = mid;
        }

        return lo < sorted.Count ? sorted[lo] : null;
    }

    private static FractionalPosition? Earliest(FractionalPosition? a, FractionalPosition? b)
    {
        if (a is not { } x) return b;
        if (b is not { } y) return a;
        return x.CompareTo(y) <= 0 ? x : y;
    }

    /// <summary>Everything struck at one position.</summary>
    private sealed class Sound(FractionalPosition position)
    {
        public FractionalPosition Position { get; } = position;

        public FractionalPosition End { get; private set; } = position;

        public Dictionary<int, int> Frets { get; } = [];

        public bool IsChord { get; set; }

        public bool Slides { get; private set; }

        public void Absorb(int stringIndex, int fret, MuteType mute, FractionalPosition end, bool slides)
        {
            if (end.CompareTo(End) > 0) End = end;
            Slides |= slides;
            if (mute == MuteType.Full || fret < 0) return;
            Frets[stringIndex] = fret;
        }
    }
}
