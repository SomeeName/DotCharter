using DotCharter.Core.Linking;

namespace DotCharter.Services.Import.GuitarPro.Report;

/// <summary>
///     Explains a Guitar Pro round trip's unequal measures: for each measure whose
///     sounds-only key changed, the classes of sound-level difference inside it (a missing
///     or extra sound, a changed fret, sustain, legato, technique…). A measure counts once
///     per class it carries, and once under <c>only:&lt;class&gt;</c> when that class is the
///     sole difference — the number a fix for that class can win back. A measure with no
///     sound-level difference is <c>key-only</c> (held-to-next or timing semantics).
/// </summary>
public static class GpRoundTripDiff
{
    /// <summary>Ticks per beat when matching positions (the export grid is 48).</summary>
    private const int Ticks = 960;

    private readonly record struct Slot(int Beat, int Tick, int String);

    private sealed record Sound(IGuitarSound Value, bool InChord, bool FromNothing);

    public static Dictionary<string, int> Classify(MeasureContentIndex before, MeasureContentIndex after)
    {
        var classes = new Dictionary<string, int>(StringComparer.Ordinal);
        var beforeSounds = Sounds(before.Level);
        var afterSounds = Sounds(after.Level);
        var measures = Math.Min(before.Measures.Count, after.Measures.Count);
        for (var m = 0; m < measures; m++)
        {
            if (before.KeyFor(m).Equals(after.KeyFor(m))) continue;
            var found = Compare(Window(beforeSounds, before.Measures[m]), Window(afterSounds, after.Measures[m]));
            if (found.Count == 0) found.Add("key-only");
            foreach (var c in found) Add(classes, c);
            if (found.Count == 1) Add(classes, "only:" + found.First());
        }

        if (before.Measures.Count != after.Measures.Count) Add(classes, "measure-count");
        return classes;
    }

    private static void Add(Dictionary<string, int> classes, string name) =>
        classes[name] = classes.GetValueOrDefault(name) + 1;

    private static Dictionary<Slot, Sound> Window(SortedDictionary<Slot, Sound> sounds, MeasureSpan span)
    {
        var window = new Dictionary<Slot, Sound>();
        foreach (var (slot, sound) in sounds)
        {
            if (slot.Beat < span.StartBeat) continue;
            if (slot.Beat >= span.EndBeat) break;
            window[slot with { Beat = slot.Beat - span.StartBeat }] = sound;
        }

        return window;
    }

    private static HashSet<string> Compare(Dictionary<Slot, Sound> before, Dictionary<Slot, Sound> after)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (slot, b) in before)
        {
            if (!after.TryGetValue(slot, out var a))
            {
                found.Add("missing");
                continue;
            }

            Diff(b, a, found);
        }

        foreach (var slot in after.Keys)
        {
            if (!before.ContainsKey(slot)) found.Add("extra");
        }

        return found;
    }

    private static void Diff(Sound before, Sound after, HashSet<string> found)
    {
        var b = before.Value;
        var a = after.Value;
        var chord = before.InChord ? ":chord" : "";
        if (b.Fret != a.Fret) found.Add("fret");
        if (before.InChord != after.InChord) found.Add("note-vs-chord");
        // Sustain error in 48ths of a beat, the export grid: under one tick is rounding.
        var sustainError = Math.Abs(b.Sustain.DoubleValue - a.Sustain.DoubleValue) * 48;
        if (sustainError * Ticks / 48 > 1)
            found.Add(sustainError < 1 ? "sustain<1/48" : sustainError < 6 ? "sustain<1/8" : "sustain");
        if (b.Hopo != a.Hopo)
        {
            found.Add(b.Hopo == HopoType.HammerOn && before.FromNothing ? "hopo:from-nothing"
                : b.Hopo == HopoType.Tap || a.Hopo == HopoType.Tap ? "tap" : $"hopo:{b.Hopo}>{a.Hopo}");
        }

        if (b.BassPicking != a.BassPicking) found.Add("slap-pop");
        if (b.Harmonic != a.Harmonic) found.Add("harmonic" + chord);
        if (b.Mute != a.Mute) found.Add("mute");
        if (b.SlideTo != a.SlideTo || b.UnpitchedSlide != a.UnpitchedSlide) found.Add("slide");
        if (b.BendValues.Count != a.BendValues.Count) found.Add("bend");
        if (b.Vibrato != a.Vibrato) found.Add("vibrato");
        if (b.Tremolo != a.Tremolo) found.Add("tremolo");
        if (b.Accent != a.Accent) found.Add(b.Accent ? "accent:lost" + chord : "accent:gained" + chord);
    }

    private static SortedDictionary<Slot, Sound> Sounds(ChartLevel level)
    {
        var events = new List<(FractionalPosition Position, IGuitarSound Sound, bool InChord)>();
        foreach (var note in level.Notes) events.Add((note.Position, note, false));
        foreach (var chord in level.Chords)
        {
            foreach (var chordNote in chord.ChordNotes) events.Add((chord.Position, chordNote, true));
        }

        events.Sort(static (x, y) => x.Position.CompareTo(y.Position));
        var sounds = new SortedDictionary<Slot, Sound>(Comparer<Slot>.Create(static (x, y) =>
        {
            var c = x.Beat.CompareTo(y.Beat);
            if (c != 0) return c;
            c = x.Tick.CompareTo(y.Tick);
            return c != 0 ? c : x.String.CompareTo(y.String);
        }));
        var sounded = new HashSet<int>();
        foreach (var (position, sound, inChord) in events)
        {
            var slot = new Slot(position.BeatId, (int)Math.Round(position.Fraction.DoubleValue * Ticks), sound.StringIndex);
            sounds.TryAdd(slot, new Sound(sound, inChord, !sounded.Contains(sound.StringIndex)));
            sounded.Add(sound.StringIndex);
        }

        return sounds;
    }
}
