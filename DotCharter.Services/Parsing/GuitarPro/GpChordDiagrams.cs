using System.Globalization;
using System.Xml.Linq;
using Chord = DotCharter.Core.Domain.Guitar.Chord;

namespace DotCharter.Services.Parsing.GuitarPro;

/// <summary>
///     One gpif track's chord DIAGRAMS (<c>DiagramCollection</c>), and the rule
///     that turns them into the import-only authored NAME and FINGERING of the
/// strums that reference them.
///     <para>
///     A GP6-8 diagram carries the name the arranger typed and a finger per
///     fretted string; a beat points at one with <c>&lt;Chord&gt;id&lt;/Chord&gt;</c>.
///     Frets are <c>baseFret + fret</c> (fret <c>0</c> is the open string) and,
///     like every gpif fret, RELATIVE to the capo — they go through
///     <see cref="GpCapoFrets.ToAbsolute" /> exactly as the notes do, so a
///     diagram compares with the strum it labels.
///     </para>
///     <para>
///     Arrangers reuse a diagram loosely (on the owner's 12 GP7/8 tabs, 241 of
///     1,184 referencing strums sound a pitch the diagram does not), so the
///     reference is EVIDENCE, not an order:
///     <list type="bullet">
///         <item>The NAME applies when the strum's sounding pitch classes are a
///         subset of the diagram's — a partial strum of the shape is still that
///         chord; a strum a semitone off is not.</item>
///         <item>The FINGERING applies only when the strum's sounding frets EQUAL
///         the diagram's (a fingering for other frets is worse than a
///         suggestion), and only from a diagram that fingers EVERY fretted
///         string — half a fingering mixed with the suggester's other half is
///         a grip nobody wrote.</item>
///         <item>A strum WITHOUT a reference (or whose reference fails the
///         name rule) borrows a same-track diagram whose
///         frets equal its sounding frets, when every such diagram agrees.
///         Templates are shared by shape and the FIRST strum of a shape names
///         it, so without this the authored name depended on whether the very
///         first strum of the shape happened to carry the reference (2,767
///         unreferenced strums on the owner's tabs match a diagram exactly, 48
///         of them before any referenced strum of the same shape; 30 match two
///         diagrams with different names and borrow nothing).</item>
///     </list>
///     A fully MUTED string sounds no pitch, so it is left out of both
///     comparisons (an <c>x</c> in the strum does not change the hand); the
///     template still lists its fret and the suggester fingers it. A strum
///     muted on EVERY string (a dead chug over the shape) compares its written
///     frets instead — it shares its template with the sounding strums of
///     those frets, and is often the first of them.
///     </para>
///     <para>
///     Everything lands in the existing side channels — <see cref="GpAuthoredChordNames" />
///     and <see cref="GpAuthoredFingering" /> — so the template builder's rules
///     hold unchanged: creation only, first value wins, and a template that took
///     an authored finger is marked so the import FHP pass never re-fingers it.
///     </para>
/// </summary>
internal sealed class GpChordDiagrams
{
    private sealed record Diagram(
        string? Name,
        IReadOnlyDictionary<int, int> Frets,
        IReadOnlyDictionary<int, int>? Fingers,
        HashSet<int> PitchClasses);

    private readonly Dictionary<int, Diagram> _byId;
    private readonly int[] _openPitches;
    private readonly int _capo;

    private GpChordDiagrams(Dictionary<int, Diagram> byId, int[] openPitches, int capo)
    {
        _byId = byId;
        _openPitches = openPitches;
        _capo = capo;
    }

    public static GpChordDiagrams Empty { get; } = new([], [], 0);

    /// <summary>
    ///     Reads the diagrams of <paramref name="trackNode" />: the first staff's
    ///     properties (GP7/GP8), else the track's own (GP6). A diagram for a
    ///     different string count, or with no fretted shape at all, is skipped —
    ///     its string numbers would not mean this track's strings, or it has
    ///     nothing a strum could be compared with.
    /// </summary>
    public static GpChordDiagrams Read(XElement trackNode, int stringCount, int capo)
    {
        var staffProps = GpArrangementParser.StaffProperties(trackNode);
        var trackProps = trackNode.Element("Properties");
        var collection = FindProperty(staffProps, "DiagramCollection") ?? FindProperty(trackProps, "DiagramCollection");
        if (collection == null || stringCount <= 0) return Empty;

        var pitchesNode = (FindProperty(staffProps, "Tuning") ?? FindProperty(trackProps, "Tuning"))?.Element("Pitches");
        var openPitches = (pitchesNode?.Value ?? "")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Take(stringCount)
            .Select(p => int.TryParse(p, CultureInfo.InvariantCulture, out var midi) ? midi : 0)
            .ToArray();
        capo = GpCapoFrets.Sanitize(capo);

        var byId = new Dictionary<int, Diagram>();
        foreach (var item in collection.Element("Items")?.Elements("Item") ?? [])
        {
            if (!TryInt(item.Attribute("id"), out var id) || byId.ContainsKey(id)) continue;
            var diagram = item.Element("Diagram");
            if (diagram == null) continue;
            if (TryInt(diagram.Attribute("stringCount"), out var declared) && declared != stringCount) continue;

            var baseFret = TryInt(diagram.Attribute("baseFret"), out var b)
                ? Math.Clamp(b, 0, GpCapoFrets.MaxAbsoluteFret)
                : 0;
            var relative = new Dictionary<int, int>();
            var frets = new Dictionary<int, int>();
            foreach (var fret in diagram.Elements("Fret"))
            {
                if (!TryInt(fret.Attribute("string"), out var s) || s < 0 || s >= stringCount) continue;
                if (!TryInt(fret.Attribute("fret"), out var f) || f < 0) continue;
                f = Math.Min(f, GpCapoFrets.MaxAbsoluteFret);
                relative[s] = f;
                frets[s] = f == 0 ? 0 : GpCapoFrets.ToAbsolute(baseFret + f, capo);
            }

            if (frets.Count == 0) continue;
            byId[id] = new Diagram(
                GpAuthoredChordNames.Normalize(item.Attribute("name")?.Value),
                frets,
                ReadFingers(diagram, relative),
                PitchClasses(frets, openPitches, capo));
        }

        return byId.Count == 0 ? Empty : new GpChordDiagrams(byId, openPitches, capo);
    }

    /// <summary>
    ///     Records the authored name and fingering <paramref name="beatNode" />'s
    ///     diagram gives <paramref name="chord" /> (already parsed, absolute
    ///     frets), under the rules in the type summary. Never overrides a value
    ///     already recorded for that position — a note's own
    ///     <c>LeftFingering</c> is read first and wins.
    /// </summary>
    public void Record(XElement beatNode, Chord chord, GpAuthoredChordNames names, GpAuthoredFingering fingers)
    {
        if (_byId.Count == 0) return;

        var sounding = new Dictionary<int, int>();
        foreach (var cn in chord.ChordNotes)
        {
            if (cn.Mute != MuteType.Full) sounding[cn.StringIndex] = cn.Fret;
        }

        // A strum struck entirely dead still shows where the hand is, and its
        // template is the one the sounding strums of those frets share.
        if (sounding.Count == 0)
        {
            foreach (var cn in chord.ChordNotes) sounding[cn.StringIndex] = cn.Fret;
        }

        if (sounding.Count == 0) return;

        Diagram? referenced = null;
        if (int.TryParse(beatNode.Element("Chord")?.Value.Trim(), CultureInfo.InvariantCulture, out var id))
            _byId.TryGetValue(id, out referenced);

        var borrowed = Borrow(sounding);

        // A reference that does not fit the strum is a stale one: the strum is
        // then as good as unreferenced.
        if (referenced?.Name != null &&
            PitchClasses(sounding, _openPitches, _capo).IsSubsetOf(referenced.PitchClasses))
        {
            names.Set(chord.Position, referenced.Name);
        }
        else if (borrowed.Name != null)
        {
            names.Set(chord.Position, borrowed.Name);
        }

        // Fingering is a property of the FRETS, not of the label: the referenced
        // diagram's when it is fretted like the strum, else the borrowed one.
        var fingering = referenced != null && SameFrets(referenced.Frets, sounding) && referenced.Fingers != null
            ? referenced.Fingers
            : borrowed.Fingers;
        if (fingering == null) return;
        foreach (var (stringIndex, finger) in fingering)
            fingers.Set(stringIndex, chord.Position, finger);
    }

    /// <summary>
    ///     The name and fingering the track's diagrams fretted EXACTLY like
    ///     <paramref name="frets" /> agree on (either may be null) — what a strum
    ///     without a usable reference, or a let-ring run of single notes
    ///     (<see cref="GpHandShapeBuilder" />), takes from the tab. Agreement is the
    ///     price of using a diagram nobody pointed at: two diagrams of one shape
    ///     with different names (Am7 / C6) say nothing about which one this is.
    /// </summary>
    public (string? Name, IReadOnlyDictionary<int, int>? Fingers) Borrow(IReadOnlyDictionary<int, int> frets)
    {
        if (_byId.Count == 0 || frets.Count == 0) return (null, null);
        var exact = _byId.Values.Where(d => SameFrets(d.Frets, frets)).ToList();
        return (Unanimous(exact.Select(d => d.Name), SameName, out var name) ? name : null,
            Unanimous(exact.Select(d => d.Fingers), SameFingers, out var fingers) ? fingers : null);
    }

    // Every fretted string must be fingered, and a position must name the fret
    // the diagram frets on that string; anything less is no fingering at all.
    private static IReadOnlyDictionary<int, int>? ReadFingers(XElement diagram, Dictionary<int, int> relative)
    {
        var fingers = new Dictionary<int, int>();
        foreach (var position in diagram.Element("Fingering")?.Elements("Position") ?? [])
        {
            if (!TryInt(position.Attribute("string"), out var s) || !relative.TryGetValue(s, out var fret) || fret == 0)
                continue;
            if (TryInt(position.Attribute("fret"), out var at) && at != fret) continue;
            if (GpAuthoredFingering.MapDiagramFinger(position.Attribute("finger")?.Value) is { } finger)
                fingers.TryAdd(s, finger);
        }

        foreach (var (s, fret) in relative)
        {
            if (fret > 0 && !fingers.ContainsKey(s)) return null;
        }

        return fingers.Count == 0 ? null : fingers;
    }

    // Sounding pitch classes: an open string sounds the capo (frets are absolute
    // and fret 0 IS the capo). A string with no known open pitch is unknowable,
    // so it makes the set unusable rather than silently skipped.
    private static HashSet<int> PitchClasses(IReadOnlyDictionary<int, int> frets, int[] openPitches, int capo)
    {
        var set = new HashSet<int>();
        foreach (var (s, fret) in frets)
        {
            if (s >= openPitches.Length) return [-1];
            set.Add(((openPitches[s] + (fret == 0 ? capo : fret)) % 12 + 12) % 12);
        }

        return set;
    }

    private static bool SameFrets(IReadOnlyDictionary<int, int> diagram, IReadOnlyDictionary<int, int> sounding)
    {
        if (diagram.Count != sounding.Count) return false;
        foreach (var (s, fret) in sounding)
        {
            if (!diagram.TryGetValue(s, out var d) || d != fret) return false;
        }

        return true;
    }

    // True with the one value the candidates carry, when at least one carries
    // a value and none carries a different one. A candidate with no value (an
    // unlabelled diagram, an unfingered one) says nothing either way.
    private static bool Unanimous<T>(IEnumerable<T?> values, Func<T, T, bool> same, out T value)
        where T : class
    {
        T? found = null;
        foreach (var v in values)
        {
            if (v == null) continue;
            if (found == null)
            {
                found = v;
            }
            else if (!same(found, v))
            {
                found = null;
                break;
            }
        }

        value = found!;
        return found != null;
    }

    private static bool SameName(string a, string b) => string.Equals(a, b, StringComparison.Ordinal);

    private static bool SameFingers(IReadOnlyDictionary<int, int> a, IReadOnlyDictionary<int, int> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var finger) && finger == kv.Value);

    private static XElement? FindProperty(XElement? properties, string name) =>
        properties?.Elements("Property").FirstOrDefault(p => p.Attribute("name")?.Value == name);

    private static bool TryInt(XAttribute? attribute, out int value) =>
        int.TryParse(attribute?.Value, CultureInfo.InvariantCulture, out value);
}
