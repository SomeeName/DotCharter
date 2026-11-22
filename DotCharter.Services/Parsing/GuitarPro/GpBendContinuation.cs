using System.Globalization;
using System.Xml.Linq;

namespace DotCharter.Services.Parsing.GuitarPro;

/// <summary>
///     Links the bend continuations Guitar Pro writes WITHOUT a tie. GP spells
///     "bend, hold, release, shake it" on one pick stroke as: the bend (tied to its
///     held beats), then an UNTIED release whose bend starts at the held value, then
///     often a ghost note (the parenthesised fret) on the same fret. Read literally,
///     the release and the ghost tail are fresh picks; they are the same ringing
///     string, so the sound before each one gets <see cref="IGuitarSound.LinkNext" />
///     and <see cref="GpPostProcessor.MergeTiedNotes" /> treats it like a written tie.
///     One decision for both importers (gpif and GP3-5 binary) and for single notes
///     and chords. A chord continues the chord before it only as a WHOLE, because
///     <see cref="GpPostProcessor.MergeTiedNotes" /> merges a chord pair only when every
///     string links: every string must either continue by this rule or already carry a
///     written tie, over the same strings and frets. One instance per voice slot: a
///     continuation never jumps voices.
/// </summary>
internal sealed class GpBendContinuation
{
    /// <summary>One raw GP bend unit in steps (both importers divide by 50): anything closer is the same pitch.</summary>
    private const double SamePitch = 1.0 / 50;

    private readonly Dictionary<int, Sounding> _byString = [];

    /// <summary>A sound as written: its bend (in steps, <see langword="null" /> when unbent), whether it is
    /// a written tie destination, and whether it is a ghost note.</summary>
    public readonly record struct Struck(IGuitarSound Sound, Bend? Bend, bool TieDestination, bool Ghost);

    /// <summary>A written bend: the pitch it starts at and the pitch it ends (and holds) at, in steps.</summary>
    public readonly record struct Bend(double Origin, double Destination);

    /// <summary>Records a single note and links the single note before it when it continues it.</summary>
    public void ObserveNote(GuitarNote note, Bend? bend, bool tieDestination, bool ghost)
    {
        var struck = new Struck(note, bend, tieDestination, ghost);
        var previous = Adjacent(struck, note.Position);
        if (previous is { Chord: null } && !tieDestination && Continues(struck, previous)) previous.Sound.LinkNext = true;
        Record(struck, note.Position, previous, null);
    }

    /// <summary>Records a chord and links the chord before it when every string continues it.</summary>
    public void ObserveChord(Chord chord, IReadOnlyList<Struck> strings)
    {
        var previous = strings.Select(s => Adjacent(s, chord.Position)).ToList();
        if (ChordContinues(strings, previous))
        {
            foreach (var sounding in previous) sounding!.Sound.LinkNext = true;
        }

        for (var i = 0; i < strings.Count; i++) Record(strings[i], chord.Position, previous[i], chord);
    }

    /// <summary>Forgets the string: a sound the tracker did not see (an expanded trill) broke the line.</summary>
    public void Forget(int stringIndex) => _byString.Remove(stringIndex);

    /// <summary>Reads a gpif note's bend as steps, on the same scale <c>GpNoteParser</c> converts to.</summary>
    public static Bend? ReadGpifBend(XElement noteNode)
    {
        var properties = noteNode.Element("Properties")?.Elements("Property").ToList();
        if (properties == null || !properties.Any(p => p.Attribute("name")?.Value == "Bended")) return null;
        return new Bend(Float(properties, "BendOriginValue") / 50.0, Float(properties, "BendDestinationValue") / 50.0);
    }

    /// <summary>A gpif ghost note: the parenthesised fret, written as <c>AntiAccent</c>.</summary>
    public static bool IsGpifGhost(XElement noteNode) => noteNode.Element("AntiAccent") != null;

    /// <summary>Reads a GP3-5 note's bend points (offset, step) as the pitch it starts and ends at.</summary>
    public static Bend? LegacyBend(IReadOnlyList<(int Offset, double Step)>? points)
    {
        if (points is not { Count: > 0 }) return null;
        var ordered = points.OrderBy(p => p.Offset).ToList();
        return new Bend(ordered[0].Step, ordered[^1].Step);
    }

    private Sounding? Adjacent(Struck struck, FractionalPosition start)
    {
        _byString.TryGetValue(struck.Sound.StringIndex, out var previous);
        return previous != null && previous.Sound.Fret == struck.Sound.Fret &&
               (previous.Start + previous.Sound.Sustain).CompareTo(start) == 0
            ? previous
            : null;
    }

    private static bool ChordContinues(IReadOnlyList<Struck> strings, List<Sounding?> previous)
    {
        if (previous.Count == 0 || previous[0]?.Chord is not { } before) return false;
        if (before.ChordNotes.Count != strings.Count) return false;
        var continued = false;
        for (var i = 0; i < strings.Count; i++)
        {
            var sounding = previous[i];
            if (sounding == null || !ReferenceEquals(sounding.Chord, before)) return false;
            if (strings[i].TieDestination)
            {
                // A written tie already links this string; anything else here is a fresh pick.
                if (!sounding.Sound.LinkNext) return false;
            }
            else if (Continues(strings[i], sounding))
            {
                continued = true;
            }
            else
            {
                return false;
            }
        }

        return continued;
    }

    private static bool Continues(Struck struck, Sounding previous)
    {
        if (previous.Sound.LinkNext || previous.Sound.SlideTo.HasValue) return false;
        var origin = struck.Bend?.Origin ?? 0;
        if (Math.Abs(origin - previous.Held) >= SamePitch) return false;

        // The untied release: a bend that STARTS bent, at exactly the pitch still held.
        if (origin >= SamePitch) return true;

        // The ghost tail after a bend or release. Only there: a ghost note after a
        // plain note is a soft re-pick (palm-muted chug patterns are written so).
        return previous.Bent && struck.Ghost && struck.Sound.Mute == MuteType.None;
    }

    private void Record(Struck struck, FractionalPosition start, Sounding? previous, Chord? chord)
    {
        // A tie destination without a bend of its own holds whatever pitch the tie carried.
        var carried = struck.TieDestination && previous != null;
        var held = struck.Bend?.Destination ?? (carried ? previous!.Held : 0);
        var bent = struck.Bend != null || carried && previous!.Bent;
        _byString[struck.Sound.StringIndex] = new Sounding(struck.Sound, start, held, bent, chord);
    }

    private static double Float(List<XElement> properties, string name)
    {
        var text = properties.FirstOrDefault(p => p.Attribute("name")?.Value == name)?.Element("Float")?.Value;
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0;
    }

    /// <summary>The last sound on a string, where it starts, the pitch (steps) it ends at, whether it was
    /// bent, and the chord it belongs to (<see langword="null" /> for a single note).</summary>
    private sealed record Sounding(IGuitarSound Sound, FractionalPosition Start, double Held, bool Bent, Chord? Chord);
}
