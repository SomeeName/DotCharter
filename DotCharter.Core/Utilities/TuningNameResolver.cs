using DotCharter.Core.Domain.Project;

namespace DotCharter.Core.Utilities;

/// <summary>
///     Pure resolver from a concrete guitar/bass tuning to its canonical, human
///     name ("E Standard", "Drop C", "DADGAD", "Open G", …). It is the display
///     counterpart to <see cref="TuningUtilities" />: where
///     <see cref="TuningUtilities.GetTuningNames" /> turns per-string offsets into
///     note letters and <see cref="TuningUtilities.GetOffsetsForTuningType" /> turns
///     a rscharter <c>TuningType</c> enum into offsets, this turns the concrete
///     PITCHES (offsets OR note letters) BACK into the name a player would recognise.
///
///     <para>Two input forms, one answer. Matching is on the sequence of PITCH
///     CLASSES (low→high), so it is independent of octave AND of enharmonic
///     spelling / letter case: offsets, "E A D G B E", "e a d g b e" and
///     "Eb G# C# F# Bb Eb" all resolve exactly as their tuning dictates. The known
///     table is keyed on (string count, pitch classes); an UNKNOWN tuning returns
///     <c>null</c> so callers keep showing the raw note letters.</para>
///
///     <para>Names are canonical English and deliberately NOT localized — "Drop C"
///     and "E Standard" are the terms guitarists use in every language. Standards
///     use the flat spelling ("Eb Standard", "Bb Standard") per convention; the
///     resolver still ACCEPTS the sharp spelling on input.</para>
/// </summary>
public static class TuningNameResolver
{
    // Standard (open-string) pitch classes, low→high, for the string counts we
    // name: 6-string guitar E-A-D-G-B-E and 4-string bass E-A-D-G. Every known
    // tuning below is expressed as semitone OFFSETS from these, exactly like the
    // rscharter tables in TuningUtilities, then folded to pitch classes.
    private static readonly int[] Guitar6Std = [4, 9, 2, 7, 11, 4];
    private static readonly int[] Bass4Std = [4, 9, 2, 7];

    /// <summary>(count, "pc,pc,…") → canonical name. Built once from the offset
    /// definitions so the note spelling can never drift from the pitch it names.</summary>
    private static readonly Dictionary<string, string> Table = Build();

    private static Dictionary<string, string> Build()
    {
        var table = new Dictionary<string, string>();

        // ---- 6-string guitar --------------------------------------------------
        // Uniform standards (every string shifted the same amount).
        Add(table, Guitar6Std, "F Standard", 1, 1, 1, 1, 1, 1);
        Add(table, Guitar6Std, "E Standard", 0, 0, 0, 0, 0, 0);
        Add(table, Guitar6Std, "Eb Standard", -1, -1, -1, -1, -1, -1);
        Add(table, Guitar6Std, "D Standard", -2, -2, -2, -2, -2, -2);
        Add(table, Guitar6Std, "Db Standard", -3, -3, -3, -3, -3, -3);
        Add(table, Guitar6Std, "C Standard", -4, -4, -4, -4, -4, -4);
        Add(table, Guitar6Std, "B Standard", -5, -5, -5, -5, -5, -5);
        Add(table, Guitar6Std, "Bb Standard", -6, -6, -6, -6, -6, -6);
        Add(table, Guitar6Std, "A Standard", -7, -7, -7, -7, -7, -7);
        // Drop tunings (standard, with the low string dropped a further whole step).
        Add(table, Guitar6Std, "Drop D", -2, 0, 0, 0, 0, 0);
        Add(table, Guitar6Std, "Drop C#", -3, -1, -1, -1, -1, -1);
        Add(table, Guitar6Std, "Drop C", -4, -2, -2, -2, -2, -2);
        Add(table, Guitar6Std, "Drop B", -5, -3, -3, -3, -3, -3);
        Add(table, Guitar6Std, "Drop A#", -6, -4, -4, -4, -4, -4);
        Add(table, Guitar6Std, "Drop A", -7, -5, -5, -5, -5, -5);
        // The family continues below Drop A on baritone/extended-range guitars.
        // Drop G# and Drop F are both in the owner's library; G and F# are the
        // rungs between them, and a family with holes in it reads as a bug.
        Add(table, Guitar6Std, "Drop G#", -8, -6, -6, -6, -6, -6);
        Add(table, Guitar6Std, "Drop G", -9, -7, -7, -7, -7, -7);
        Add(table, Guitar6Std, "Drop F#", -10, -8, -8, -8, -8, -8);
        Add(table, Guitar6Std, "Drop F", -11, -9, -9, -9, -9, -9);
        // Alternate / open tunings.
        Add(table, Guitar6Std, "DADGAD", -2, 0, 0, 0, -2, -2);
        Add(table, Guitar6Std, "Double Drop D", -2, 0, 0, 0, 0, -2);
        Add(table, Guitar6Std, "Open D", -2, 0, 0, -1, -2, -2);
        Add(table, Guitar6Std, "Open E", 0, 2, 2, 1, 0, 0);
        Add(table, Guitar6Std, "Open G", -2, -2, 0, 0, 0, -2);
        Add(table, Guitar6Std, "Open A", 0, 0, 2, 2, 2, 0);
        Add(table, Guitar6Std, "Open C", -4, -2, -2, 0, 1, 0);
        Add(table, Guitar6Std, "Open Eb", -1, 1, 1, 0, -1, -1);
        Add(table, Guitar6Std, "Open D Minor", -2, 0, 0, -2, -2, -2);

        // ---- 6-string bass ----------------------------------------------------
        // B-E-A-D-G-C. It shares the 6-entry bucket with the guitar tunings
        // above and collides with none of them, so it is keyed by absolute pitch
        // classes rather than as an offset from a guitar's open strings — it is
        // not a transposition of one. It is the 7th most common tuning in the
        // owner's library (58 songs) purely because Rocksmith stores every
        // tuning in six slots whatever the instrument.
        AddPitchClasses(table, "6-String Bass Standard", 11, 4, 9, 2, 7, 0);

        // ---- 5-string bass ----------------------------------------------------
        // B-E-A-D-G. Like the 6-string bass below it, this is NOT a transposition
        // of a guitar's open strings, so it is keyed by absolute pitch classes —
        // and it gets its own 5-entry bucket, which nothing else occupies.
        //
        // The names carry the "5-String Bass" prefix deliberately, exactly as
        // "6-String Bass Standard" does. A bare "B Standard" would collide in the
        // reader's mind with the SIX-string guitar tuning of that name, and it
        // be taken for that guitar tuning, filing a bass arrangement under it.
        // Prefixed, the names stay distinct and say what instrument they tune.
        AddPitchClasses(table, "5-String Bass Standard", 11, 4, 9, 2, 7);      // B E A D G
        AddPitchClasses(table, "5-String Bass Eb Standard", 10, 3, 8, 1, 6);   // Bb Eb Ab Db Gb
        AddPitchClasses(table, "5-String Bass D Standard", 9, 2, 7, 0, 5);     // A D G C F
        AddPitchClasses(table, "5-String Bass C Standard", 7, 0, 5, 10, 3);    // G C F Bb Eb
        AddPitchClasses(table, "5-String Bass Drop A", 9, 4, 9, 2, 7);         // A E A D G

        // ---- 4-string bass ----------------------------------------------------
        Add(table, Bass4Std, "E Standard", 0, 0, 0, 0);
        Add(table, Bass4Std, "Eb Standard", -1, -1, -1, -1);
        Add(table, Bass4Std, "D Standard", -2, -2, -2, -2);
        Add(table, Bass4Std, "C Standard", -4, -4, -4, -4);
        Add(table, Bass4Std, "B Standard", -5, -5, -5, -5);
        Add(table, Bass4Std, "Drop D", -2, 0, 0, 0);
        Add(table, Bass4Std, "Drop C#", -3, -1, -1, -1);
        Add(table, Bass4Std, "Drop C", -4, -2, -2, -2);
        Add(table, Bass4Std, "Drop B", -5, -3, -3, -3);
        Add(table, Bass4Std, "Drop A", -7, -5, -5, -5);

        return table;
    }

    private static void Add(Dictionary<string, string> table, int[] std, string name, params int[] offsets)
    {
        var pcs = new int[std.Length];
        for (var i = 0; i < std.Length; i++) pcs[i] = Mod12(std[i] + offsets[i]);
        table[Key(pcs)] = name;
    }

    /// <summary>For a tuning that is not a transposition of a standard open
    /// set (today: the 6-string bass), keyed by absolute pitch classes.</summary>
    private static void AddPitchClasses(Dictionary<string, string> table, string name, params int[] pitchClasses) => table[Key(pitchClasses)] = name;

    /// <summary>
    ///     Display form for a tuning: the canonical name with the actual notes
    ///     beside it — <c>"Drop C (C G C F A D)"</c> — falling back to the bare
    ///     notes when the tuning has no recognised name. This is what the
    ///     library and Play Mode filters show: the name alone hides which
    ///     strings actually move (three of the tunings in the owner's library
    ///     differ from a named one by a single string), and the notes alone are
    ///     six letters the reader has to decode. Returns an empty string for an
    ///     empty summary, never a stray "()".
    /// </summary>
    public static string Describe(string? noteSummary)
    {
        if (string.IsNullOrWhiteSpace(noteSummary)) return string.Empty;
        var notes = noteSummary.Trim();
        var name = Resolve(notes);
        return name is null ? notes : $"{name} ({notes})";
    }

    /// <summary>Canonical name for a tuning given by per-string NOTE LETTERS
    /// (low→high), or <c>null</c> when it isn't a recognised common tuning.
    /// Case- and enharmonic-insensitive; a null/empty/unparseable input is
    /// unknown.</summary>
    public static string? Resolve(IReadOnlyList<string>? noteNames)
    {
        if (noteNames is not { Count: > 0 }) return null;

        var pcs = new int[noteNames.Count];
        for (var i = 0; i < noteNames.Count; i++)
        {
            var pc = NoteToPitchClass(noteNames[i]);
            if (pc < 0) return null; // any unparseable string ⇒ unknown
            pcs[i] = pc;
        }

        return Table.GetValueOrDefault(Key(pcs));
    }

    /// <summary>Canonical name for a space-joined note summary ("E A D G B E",
    /// the form Song Select stores per song), or <c>null</c> when unknown.</summary>
    public static string? Resolve(string? noteSummary)
    {
        if (string.IsNullOrWhiteSpace(noteSummary)) return null;
        return Resolve(noteSummary.Split((char[]?)null, System.StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>Canonical name from rscharter-style per-string semitone OFFSETS
    /// (relative to standard), low→high, for a 6-string guitar or 4-string bass.
    /// Other string counts, or a length mismatch, are unknown (<c>null</c>).</summary>
    public static string? ResolveFromOffsets(int stringCount, IReadOnlyList<int>? offsets)
    {
        if (offsets is null || offsets.Count != stringCount) return null;
        var std = stringCount switch { 6 => Guitar6Std, 4 => Bass4Std, _ => null };
        if (std is null) return null;

        var pcs = new int[stringCount];
        for (var i = 0; i < stringCount; i++) pcs[i] = Mod12(std[i] + offsets[i]);
        return Table.GetValueOrDefault(Key(pcs));
    }

    /// <summary>
    ///     The canonical name of an ARRANGEMENT's tuning, or <c>null</c> when it has
    ///     none: its per-string offsets first (4 or 6 of them — the array's own
    ///     length is the string count, and offsets are the spelling-proof form),
    ///     then its note letters. A bass PART on a guitar path
    /// (<see cref="ArrangementInstrument.IsBassPart(Arrangement)" />) whose
    ///     six slots name nothing is then read as the 5- or 4-string bass it is: the
    ///     slots past its last string keep whatever the author left there (a
    ///     5-string bass at A220 is often <c>-5 -5 -5 -5 -4 0</c>), so only the low
    ///     strings carry the tuning. A 6-string bass needs no fallback — its
    ///     B-E-A-D-G-C has its own entry.
    /// </summary>
    /// <param name="arrangement">The arrangement.</param>
    /// <returns>The name, or null for an unnamed tuning.</returns>
    public static string? ResolveFor(Arrangement arrangement)
    {
        ArgumentNullException.ThrowIfNull(arrangement);
        var pitches = arrangement.TuningPitches;
        var offsets = new int[pitches.Count];
        for (var i = 0; i < offsets.Length; i++) offsets[i] = pitches[i].Step;

        var name = offsets.Length is 4 or 6 ? ResolveFromOffsets(offsets.Length, offsets) : null;
        name ??= Resolve(arrangement.Tuning);
        if (name is null && offsets.Length == 6 && arrangement.ArrangementType != ArrangementType.Bass &&
            ArrangementInstrument.IsBassPart(arrangement))
        {
            name = ResolveBassOnGuitarPath(offsets);
        }

        return name;
    }

    /// <summary>The low five strings as a 5-string bass, else the low four as a
    /// 4-string one (a guitar's low four ARE a bass's E-A-D-G).</summary>
    private static string? ResolveBassOnGuitarPath(int[] offsets)
    {
        var pcs = new int[5];
        for (var i = 0; i < pcs.Length; i++) pcs[i] = Mod12(Guitar6Std[i] + offsets[i]);
        return Table.GetValueOrDefault(Key(pcs)) ?? ResolveFromOffsets(4, offsets[..4]);
    }

    private static string Key(IReadOnlyList<int> pcs) => $"{pcs.Count}:{string.Join(",", pcs)}";

    private static int Mod12(int v) => ((v % 12) + 12) % 12;

    /// <summary>Parses one note letter (any case; sharps or flats;
    /// double-accidentals not needed) to a pitch class 0–11, or -1 if it isn't a
    /// note. Matches the spelling <see cref="TuningUtilities.GetTuningNames" />
    /// emits (C#, Eb, F#, G#, Bb) plus the common enharmonic aliases.</summary>
    private static int NoteToPitchClass(string? note)
    {
        if (string.IsNullOrWhiteSpace(note)) return -1;
        var t = note.Trim();
        // Normalise "eb" → "Eb", "F#" → "F#", "e" → "E".
        var norm = char.ToUpperInvariant(t[0]) + (t.Length > 1 ? t[1..].ToLowerInvariant() : "");
        return norm switch
        {
            "C" or "B#" => 0,
            "C#" or "Db" => 1,
            "D" => 2,
            "D#" or "Eb" => 3,
            "E" or "Fb" => 4,
            "F" or "E#" => 5,
            "F#" or "Gb" => 6,
            "G" => 7,
            "G#" or "Ab" => 8,
            "A" => 9,
            "A#" or "Bb" => 10,
            "B" or "Cb" => 11,
            _ => -1
        };
    }
}
