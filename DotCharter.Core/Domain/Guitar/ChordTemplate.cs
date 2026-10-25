using DotCharter.Core.Utilities;

namespace DotCharter.Core.Domain.Guitar;

public class ChordTemplate
{
    public static readonly Dictionary<string, int> FingerIds = new()
    {
        { "T", 0 }, { "1", 1 }, { "2", 2 }, { "3", 3 }, { "4", 4 }
    };

    public static readonly Dictionary<int, string> FingerNames = new()
    {
        { 0, "T" }, { 1, "1" }, { 2, "2" }, { 3, "3" }, { 4, "4" }
    };

    public bool Arpeggio { get; set; }
    public bool ForceArpeggioInRs { get; set; }

    /// <summary>
    ///     Rocksmith's <c>ChordMask.Nop</c> (0x2), which Rocksmith2014.NET documents
    ///     as the chord flag "for double stop panels". Rocksmith 2014's own charts
    ///     set it on 121 of 12,557 shapes, almost all of them unnamed two- and
    /// three-string double stops.
    ///     <para>
    ///         Interchange formats spell it as a <c>-nop</c> suffix on the chord's
    ///         DISPLAY name; here it is a field of its own rather than part of <see cref="Name" />, so
    ///         setting it never means typing the suffix. Every reader folds that
    ///         suffix into this field (<see cref="RsChordDisplaySuffix.FoldNop" />)
    ///         and every writer derives it back from here
    ///         (<see cref="RsChordDisplaySuffix.ForExport" />), the way
    ///         <see cref="ForceArpeggioInRs" /> carries <c>-arp</c>.
    ///     </para>
    /// </summary>
    public bool NopInRs { get; set; }

    /// <summary>
    ///     True when the FHP generator minted this fingering: the suggestion for
    ///     these frets shifted up the hand to follow the anchor
    /// (<c>ChordFingeringFollower</c>). Such a fingering is FREE — the
    ///     next Generate FHP may shift it again or put the base grip back —
    ///     where a charter's identical uniform shift (a double stop fingered 2-2)
    /// is AUTHORED and pins its anchor.
    ///     <para>
    ///         Provenance, not the grip: it is in no find-or-create key and not in
    ///         <see cref="Equals(object?)" />, and no exporter writes it. False is
    ///         the safe default — "nobody says the generator made this", so an old
    ///         project or an imported chart never has a hand-set fingering moved.
    ///         Every manual fingering edit clears it. A fingering that IS the
    ///         suggestion is free either way: that is read off the fingers.
    ///     </para>
    /// </summary>
    public bool FollowedFingering { get; set; }

    /// <summary>
    ///     The grip INTENTIONALLY has no fingering — EOF's "Fingerless" status
    ///     (<c>EOF_PRO_GUITAR_NOTE_EFLAG_FINGERLESS</c>, <c>song.h:137</c>), which
    ///     is not finger 0 and not "fingering missing": EOF calls such a chord's
    ///     fingering VALID (<c>rs.c:3593-3596</c>) and exports every finger as
    ///     <c>-1</c>.
    ///     <para>
    ///         An empty <see cref="Fingers" /> map already EXPORTS as <c>-1</c>;
    ///         what this adds is the statement that the gap is deliberate. L06/L07/L08
    ///         do not judge such a template, Generate fingering does not fill it,
    ///         and the EOF importer and exporter carry it as the eflag. A fingering
    ///         someone sets by hand clears it (<c>SetTemplateFingersAction</c>, the
    ///         template panel's fingering edit). Kept with an empty
    ///         <see cref="Fingers" /> map: the EOF importer drops any finger values
    ///         EOF stored under the flag, because EOF never exports them.
    ///     </para>
    ///     <para>
    ///         Part of <see cref="Equals(object?)" />: two templates that differ only
    ///         here validate differently and write a different EOF eflag.
    ///     </para>
    /// </summary>
    public bool Fingerless { get; set; }

    /// <summary>
    /// GHOST GEMS, EOF's per-note ghost bitmask (<c>song.h:200</c>, bit n = string
    ///     n, the same numbering as <see cref="Frets" />): the strings of this grip the hand
    ///     HOLDS but does not play — the fretting finger of a tapped harmonic, the full chord
    ///     a partial strum sits in. Such a string is in <see cref="Frets" /> and
    ///     <see cref="Fingers" /> (the chord box and the Rocksmith template show it) and is
    ///     never a note, so nothing plays, scores, detects or exports it as one.
    ///     <para>
    ///         Only a HAND SHAPE's template carries ghosts: EOF exports a partially ghosted
    ///         chord's chord tag on a ghost-less clone and its handshape tag on the full
    ///         shape (<c>rs.c:2369-2420</c>), and a chord here always names a template of
    ///         exactly what it plays. So a ghost template is never a find-or-create match
    ///         (<see cref="HasFrets" />). Part of <see cref="Equals(object?)" />: it changes
    ///         what the editor draws and what the <c>.eof</c> exporter writes.
    ///     </para>
    /// </summary>
    public int GhostMask { get; set; }

    /// <summary>
    /// The template was AUTHORED by hand: every automatic fingering or rebuild
    ///     flow leaves it as it is (<see cref="IsPinned" />). Provenance, not the grip — not
    ///     in <see cref="Equals(object?)" />, like <see cref="FollowedFingering" />, and no
    ///     interchange format writes it (only <c>.dcproj</c>, absent when false).
    /// </summary>
    public bool Pinned { get; set; }

    /// <summary>
    ///     The charter chose NO name for this grip (the chord editor's No name box): an empty
    ///     <see cref="Name" /> that no automatic flow fills back in. Setting a name clears it
    ///     (<c>SetTemplateNameAction</c>). Provenance, like <see cref="Pinned" />: not in
    ///     <see cref="Equals(object?)" />, and only <c>.dcproj</c> writes it (absent when false);
    ///     every interchange format already writes an empty name as empty.
    /// </summary>
    public bool Unnamed { get; set; }

    /// <summary>
    ///     "May an automatic flow replace or re-finger this template?" — no, when the user
    ///     pinned it or it holds ghost gems (a grip nothing plays cannot be re-derived from
    ///     the notes). The ONE pin answer: <c>ChordTemplateResolver</c>, the fingering style,
    ///     Refret, <c>ChordFingeringFollower</c>, <see cref="Utilities.HandShapeAutoTemplate" />
    ///     and the chord panel's Generate fingerings all ask it, and
    ///     <c>Services/Editing/TemplatePinReachabilityTests</c> fails a new flow that does not.
    /// </summary>
    public bool IsPinned => Pinned || GhostMask != 0;

    /// <summary>True when <paramref name="stringIndex" /> is a ghost gem of this grip.</summary>
    public bool IsGhost(int stringIndex) =>
        stringIndex is >= 0 and < 31 && (GhostMask & (1 << stringIndex)) != 0 && Frets.ContainsKey(stringIndex);

    /// <summary>The bit <see cref="GhostMask" /> uses for <paramref name="stringIndex" />, 0 when out of range.</summary>
    public static int GhostBit(int stringIndex) => stringIndex is >= 0 and < 31 ? 1 << stringIndex : 0;

    public int Id { get; set; } = -1;
    public string Name { get; set; } = "";

    /// <summary>
    ///     "Is this an arpeggio?" — the ONE answer, because TWO flags spell it
    ///     and each import path sets only one (see <see cref="ArpeggioFlavour" />
    ///     for which sets which). Reading a single flag works on psarc imports
    ///     and is silently dead on every DLC-Builder / EOF chart, or the reverse.
    ///     <para>
    ///         This is <b>not</b> the find-or-create key: two templates that are
    ///         both arpeggios but spell it differently are not interchangeable
    ///         (they export under different names), which is what
    ///         <see cref="ArpeggioFlavour.Matches" /> is for.
    ///     </para>
    /// </summary>
    public bool IsArpeggio => Arpeggio || ForceArpeggioInRs;

    public Dictionary<int, int> Frets { get; set; } = [];

    public Dictionary<int, int> Fingers { get; set; } = [];

    /// <summary>
    ///     Exactly these frets on exactly these strings, as a PLAYED grip. Fingers, name and
    ///     the arpeggio flags are not compared; callers that care pair this with
    ///     <see cref="ArpeggioFlavour.Matches" /> or <see cref="HasSameShapeAs" />.
    ///     <para>
    ///         A template holding ghost gems (<see cref="GhostMask" />) never matches: it is
    ///         the find-or-create key, and a chord handed a ghost grip would name strings it
    /// does not play.
    ///     </para>
    /// </summary>
    public bool HasFrets(IReadOnlyDictionary<int, int> frets) => GhostMask == 0 && MapEquality.SameEntries(Frets, frets);

    /// <summary>The same frets, the same fingers and the same ghost gems, string for
    /// string — the grip Rocksmith renders and the editor draws.</summary>
    public bool HasSameShapeAs(ChordTemplate other) =>
        GhostMask == other.GhostMask && MapEquality.SameEntries(Frets, other.Frets) && MapEquality.SameEntries(Fingers, other.Fingers);

    /// <summary>
    ///     "Do this chord's notes play exactly this grip?" — every string the
    ///     template declares, at that fret, and nothing else. It is NOT the
    ///     find-or-create key (that is frets + arpeggio flavour, see
    ///     <c>ChordTemplateResolver</c>); it answers whether a chord already
    ///     SOUNDS the shape, which is what decides whether an edit to that shape
    ///     may reshape the chord.
    ///     <para>
    ///         It lives here because two places must agree on the answer: the
    ///         inspector panel's write paths, which reshape only the chords that
    ///         carry the displayed shape, and the Shape card's rebind warning,
    ///         which states the count BEFORE the edit. The warning open-coded a
    ///         weaker test (enclosure + a differing template id) and over-counted
    ///         every enclosed chord of an unrelated shape.
    ///     </para>
    /// </summary>
    public bool DescribesChordNotes(IReadOnlyCollection<ChordNote> chordNotes)
    {
        // A ghost gem is held, never played: no chord's notes play a ghost grip.
        if (GhostMask != 0 || Frets.Count != chordNotes.Count) return false;
        foreach (var note in chordNotes)
        {
            if (!Frets.TryGetValue(note.StringIndex, out var fret) || fret != note.Fret)
                return false;
        }

        return true;
    }

    public int GetLowestString() => Frets.Count > 0 ? Frets.Keys.Min() : 0;

    public int GetHighestString() => Frets.Count > 0 ? Frets.Keys.Max() : 0;

    public int GetLowestFret() => Frets.Count > 0 ? Frets.Values.Min() : 0;

    public int GetHighestFret() => Frets.Count > 0 ? Frets.Values.Max() : 0;

    public int GetLowestNotOpenFret(int capo = 0)
    {
        var frets = Frets.Values.Where(f => f > capo).ToList();
        return frets.Count > 0 ? frets.Min() : 0;
    }

    public int GetFret(int stringIndex) => Frets.TryGetValue(stringIndex, out var fret) ? fret : -1;

    public int GetFinger(int stringIndex) => Fingers.TryGetValue(stringIndex, out var finger) ? finger : -1;

    public string GetTemplateFrets(int stringCount = 6)
    {
        var fretNames = new string[stringCount];
        var allFretsBelow10 = true;

        for (var i = 0; i < stringCount; i++)
        {
            if (Frets.TryGetValue(i, out var fret))
            {
                fretNames[i] = fret.ToString();
                if (fret >= 10) allFretsBelow10 = false;
            }
            else
            {
                fretNames[i] = "-";
            }
        }

        return string.Join(allFretsBelow10 ? "" : " ", fretNames);
    }

    public string GetTemplateFingers(int stringCount = 6)
    {
        var fingerNames = new string[stringCount];
        for (var i = 0; i < stringCount; i++)
        {
            fingerNames[i] = Fingers.TryGetValue(i, out var finger) && FingerNames.TryGetValue(finger, out var name)
                ? name
                : " ";
        }

        return string.Join("", fingerNames);
    }

    public string GetDisplayName() => Name + (IsArpeggio ? "(arp)" : "");

    public string GetNameWithFrets(int stringCount = 6) => $"{Name}{(IsArpeggio ? "(arpeggio)" : "")} ({GetTemplateFrets(stringCount)})";

    public ChordTemplate Clone()
    {
        return new ChordTemplate
        {
            Id = Id,
            Name = Name,
            Arpeggio = Arpeggio,
            ForceArpeggioInRs = ForceArpeggioInRs,
            NopInRs = NopInRs,
            FollowedFingering = FollowedFingering,
            Fingerless = Fingerless,
            GhostMask = GhostMask,
            Pinned = Pinned,
            Unnamed = Unnamed,
            Frets = new Dictionary<int, int>(Frets),
            Fingers = new Dictionary<int, int>(Fingers)
        };
    }

    public override bool Equals(object? obj)
    {
        if (obj is not ChordTemplate other) return false;
        if (!HasSameShapeAs(other)) return false;

        // ForceArpeggioInRs is part of the identity: it is load-bearing on
        // export (it drives the RS "-arp" name suffix), so two templates that
        // differ only in that flag are NOT interchangeable. Omitting it let
        // find-or-create collapse them onto one instance and silently change
        // the exported chord name of whichever lost. NopInRs likewise sets an
        // exported mask bit and display-name suffix. FollowedFingering is NOT
        // here: it says who made the fingering, not what the grip is.
        return Name == other.Name
               && Arpeggio == other.Arpeggio
               && ForceArpeggioInRs == other.ForceArpeggioInRs
               && NopInRs == other.NopInRs
               && Fingerless == other.Fingerless;
    }

    public override int GetHashCode()
    {
        // ReSharper disable NonReadOnlyMemberInGetHashCode
        var hash = new HashCode();
        hash.Add(Name);
        hash.Add(Arpeggio);
        hash.Add(ForceArpeggioInRs); // must agree with Equals
        hash.Add(NopInRs);
        hash.Add(Fingerless);
        hash.Add(GhostMask);

        foreach (var kvp in Frets.OrderBy(k => k.Key))
        {
            hash.Add(kvp.Key);
            hash.Add(kvp.Value);
        }

        foreach (var kvp in Fingers.OrderBy(k => k.Key))
        {
            hash.Add(kvp.Key);
            hash.Add(kvp.Value);
        }

        return hash.ToHashCode();
        // ReSharper restore NonReadOnlyMemberInGetHashCode
    }
}
