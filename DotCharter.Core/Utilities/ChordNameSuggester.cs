using DotCharter.Core.Domain.Project;

namespace DotCharter.Core.Utilities;

public static class ChordNameSuggester
{
    // Spelling follows the Editor on Fire / RS2014 convention that the corpus
    // ground truth uses: "min" (not "m"), "Maj" (not the Δ glyph), "dim" (not °),
    // and "sus4"/"sus2" spelled out. Measured against the hand-fingered EOF
    // corpus these lifted chord-name exact agreement from 47% toward ~86%.
    // ChordNamingStyle.Musical re-spells each suffix (ChordNamePattern.MusicalSuffix).
    private const string Min = "min";
    private const string Maj = "Maj";
    private const string Dim = "dim";
    private const string Add = "add";
    private const string Aug = "aug";
    private const string Sus = "sus";

    private static readonly string[] PitchClassNamesSharp = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];
    private static readonly string[] PitchClassNamesFlat = ["C", "Db", "D", "Eb", "E", "F", "Gb", "G", "Ab", "A", "Bb", "B"];

    // Mirrors rscharter's changeToneName tables, which are A-based rather than
    // pitch-class-indexed from C.
    private static readonly string[] ToneCycleSharp = ["A", "A#", "B", "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#"];
    private static readonly string[] ToneCycleFlat = ["A", "Bb", "B", "C", "Db", "D", "Eb", "E", "F", "Gb", "G", "Ab"];

    private static readonly Dictionary<string, int> PitchClassByName =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["C"] = 0,
            ["C#"] = 1,
            ["Db"] = 1,
            ["D"] = 2,
            ["D#"] = 3,
            ["Eb"] = 3,
            ["E"] = 4,
            ["F"] = 5,
            ["F#"] = 6,
            ["Gb"] = 6,
            ["G"] = 7,
            ["G#"] = 8,
            ["Ab"] = 8,
            ["A"] = 9,
            ["A#"] = 10,
            ["Bb"] = 10,
            ["B"] = 11
        };

    // Order matters only between patterns that match the SAME root of the same
    // note set: the first one listed is the first name offered.
    private static readonly ChordNamePattern[] Patterns =
    [
        new([1, 4, 7, 10], "7(b9)"),
        new([1, 4, 8, 10], "7(b9,b13)"),
        new([1, 4, 9, 10], "13(b9)"),
        new([1, 5, 10], "7" + Sus + "(b9)"),

        new([2, 3, 7], Min + Add + "9"),
        new([2, 3, 7, 10], Min + "9"),
        new([2, 3, 9], Min + "6(9)"),
        new([2, 4, 7], Add + "9"),
        new([2, 4, 7, 10], "9"),
        new([2, 4, 7, 11], Maj + "9"),
        new([2, 4, 9], "6(9)"),
        new([2, 4, 9, 10], "9(13)"),
        new([2, 4, 9, 11], Maj + "9(13)"),
        new([2, 5, 9, 10], "13" + Sus),
        new([2, 5, 10], "9" + Sus),
        new([2, 7], Sus + "2"),

        new([3], Min),
        new([3, 4, 10], "7(#9)"),
        new([3, 5, 6, 10], Min + "11(b5)"),
        new([3, 5, 7], Min + Add + "4"),
        new([3, 5, 10], Min + "11"),
        new([3, 6], Dim),
        new([3, 6, 8, 9], Dim + "b13"),
        new([3, 6, 9], Dim + "7"),
        new([3, 6, 10], Min + "7(b5)", "m7b5"),
        new([3, 7], Min),
        new([3, 7, 8, 10], Min + "7(b13)"),
        new([3, 7, 9], Min + "6"),
        new([3, 7, 9, 10], Min + "13"),
        new([3, 7, 10], Min + "7"),
        new([3, 7, 11], Min + Maj + "7", "m(maj7)"),
        new([3, 8, 10], Min + "7(b13)"),
        new([3, 9], Min + "6"),
        new([3, 9, 10], Min + "13"),
        new([3, 10], Min + "7"),
        new([3, 11], Min + Maj + "7", "m(maj7)"),

        // EOF writes a bare major third as "X3"; that is not a chord name, so
        // the musical spelling says what the shape is.
        new([4], "3", "(no5)"),
        new([4, 5, 7], Add + "4"),
        new([4, 6], "(b5)"),
        new([4, 6, 10], "7(#11)"),
        new([4, 6, 10], "7(b5)"),
        new([4, 6, 11], Maj + "7(#11)"),
        new([4, 7], ""),
        new([4, 7, 9], "6"),
        new([4, 7, 10], "7"),
        new([4, 7, 11], Maj + "7"),
        new([4, 8], Aug),
        new([4, 8, 10], "7(#5)"),
        new([4, 8, 10], "7(b13)"),
        new([4, 8, 11], Maj + "7(#5)"),
        new([4, 9], "6"),
        new([4, 9, 10], "13"),
        new([4, 9, 11], Maj + "13"),
        new([4, 10], "7"),
        new([4, 11], Maj + "7"),

        new([5, 7], Sus + "4"),
        new([5, 7, 10], "7" + Sus + "4"),
        new([5, 10], "7" + Sus + "4"),

        // A root and its tritone alone: the tabs write it as a flattened power
        // chord ("G5(b5)").
        new([6], "(b5)", "5(b5)"),
        new([7], "5")
    ];

    /// <summary>
    ///     The extended vocabulary: shells without the 5th, the sus2 sevenths and the
    ///     b9s that the owner's tabs type and the namer could not name.
    ///     <para>
    ///         <b>In EOF style these are a FALLBACK</b>, consulted only when
    ///         <see cref="Patterns" /> names no root of the shape at all. Ranked
    ///         with the rest, the root-position <c>Maj7sus2</c> took first place
    ///         from the inversion <c>Fadd4</c> / <c>Cadd4</c> that EOF writes —
    ///         10 of the 1,688 named templates in the owner's finished charts — so
    ///         as a fallback they can only fill a name that was empty, and EOF
    ///         parity cannot drop. Musical style ranks them with the rest.
    ///     </para>
    /// </summary>
    private static readonly ChordNamePattern[] ExtendedPatterns =
    [
        new([2, 4, 10], "9"),
        new([2, 3, 10], Min + "9"),
        new([2, 4, 11], Maj + "9"),
        new([2, 7, 10], "7" + Sus + "2"),
        new([2, 10], "7" + Sus + "2"),
        new([2, 7, 11], Maj + "7" + Sus + "2"),
        new([1, 4, 10], "7(b9)"),
        new([1, 4, 7], "(b9)"),
        new([3, 8, 9], Min + "6(#5)")
    ];

    private static readonly ChordNamePattern[] AllPatterns = [.. Patterns, .. ExtendedPatterns];

    /// <summary>
    ///     Suggests chord names for a shape, at the pitch the player actually
    ///     HEARS — <see cref="Arrangement.Tuning" /> and
    ///     <see cref="Arrangement.Capo" /> are both applied. EOF style, no key.
    /// </summary>
    /// <param name="arrangement">
    ///     Arrangement the shape belongs to; supplies the tuning and the capo
    ///     fret.
    /// </param>
    /// <param name="templateFrets">
    ///     String index → fret, as stored on a <c>ChordTemplate</c>: ABSOLUTE
    ///     frets on the un-capoed neck, <c>0</c> = open, negative = string
    ///     unused.
    /// </param>
    /// <returns>Candidate names, best first; empty when nothing matches.</returns>
    public static IReadOnlyList<string> SuggestChordNames(Arrangement arrangement, IReadOnlyDictionary<int, int> templateFrets) =>
        SuggestChordNames(arrangement, templateFrets, ChordNamingOptions.Default);

    /// <summary>
    ///     Suggests chord names for a shape in a naming style
    ///     (<see cref="ChordNamingOptions" />), tuning and capo applied.
    /// </summary>
    /// <param name="arrangement">Supplies the tuning and the capo fret.</param>
    /// <param name="templateFrets">String index → fret; negative = string unused.</param>
    /// <param name="naming">
    ///     Style and key. Build it ONCE per batch with
    ///     <see cref="ChordNamingOptions.For" />: resolving the key reads the
    ///     whole arrangement.
    /// </param>
    /// <returns>Candidate names, best first; empty when nothing matches.</returns>
    public static IReadOnlyList<string> SuggestChordNames(
        Arrangement arrangement, IReadOnlyDictionary<int, int> templateFrets, ChordNamingOptions naming)
    {
        ArgumentNullException.ThrowIfNull(arrangement);
        return SuggestChordNames(arrangement.Tuning, templateFrets, arrangement.Capo, naming);
    }

    /// <summary>
    ///     Suggests chord names for a shape under an explicit tuning and capo.
    /// </summary>
    /// <param name="tuning">
    ///     Open-string note letters, low string first (e.g. E A D G B E).
    /// </param>
    /// <param name="templateFrets">
    ///     String index → fret: ABSOLUTE frets on the un-capoed neck, <c>0</c> =
    ///     open, negative = string unused.
    /// </param>
    /// <param name="capo">
    ///     Capo fret; <c>0</c> (the default) means no capo and is a strict
    ///     no-op. A capo stops every open string at this fret, so it RAISES the
    ///     sounding pitch of the whole shape.
    /// </param>
    /// <returns>Candidate names, best first; empty when nothing matches.</returns>
    public static IReadOnlyList<string> SuggestChordNames(
        IReadOnlyList<string> tuning,
        IReadOnlyDictionary<int, int> templateFrets,
        int capo = 0) =>
        SuggestChordNames(tuning, templateFrets, capo, ChordNamingOptions.Default);

    /// <summary>
    ///     Suggests chord names for a shape under an explicit tuning and capo, in
    ///     a naming style.
    /// </summary>
    /// <param name="tuning">Open-string note letters, low string first.</param>
    /// <param name="templateFrets">String index → fret; negative = string unused.</param>
    /// <param name="capo">Capo fret; <c>0</c> is a strict no-op.</param>
    /// <param name="naming">Style and key (<see cref="ChordNamingOptions" />).</param>
    /// <returns>Candidate names, best first; empty when nothing matches.</returns>
    public static IReadOnlyList<string> SuggestChordNames(
        IReadOnlyList<string> tuning,
        IReadOnlyDictionary<int, int> templateFrets,
        int capo,
        ChordNamingOptions naming)
    {
        ArgumentNullException.ThrowIfNull(tuning);
        ArgumentNullException.ThrowIfNull(templateFrets);

        var sounds = GetSortedSounds(tuning, templateFrets, capo);
        if (sounds.Count == 0) return Array.Empty<string>();

        var musical = naming.Style == ChordNamingStyle.Musical;
        // EOF never spells in a key; Musical without a key keeps the old order.
        var key = musical ? naming.Key : null;

        if (sounds.Count == 1) return [SpellNote(Mod12(sounds[0]), key)[0]];

        var uniqueNotes = SoundsToPitchClasses(sounds);
        if (uniqueNotes.Count == 1) return [SpellNote(uniqueNotes[0], key)[0]];

        var bassPitchClass = Mod12(sounds[0]);

        // EOF style consults the extended vocabulary only when the original one
        // names nothing (see ExtendedPatterns); Musical ranks them together.
        if (musical) return NameByTiers(uniqueNotes, bassPitchClass, AllPatterns, musical, key);
        var names = NameByTiers(uniqueNotes, bassPitchClass, Patterns, musical, key);
        return names.Count > 0 ? names : NameByTiers(uniqueNotes, bassPitchClass, ExtendedPatterns, musical, key);
    }

    private static List<string> NameByTiers(
        IReadOnlyList<int> uniqueNotes, int bassPitchClass, ChordNamePattern[] patterns,
        bool musical, ChordKeySignature? key)
    {
        var bassName = SpellNote(bassPitchClass, key)[0];

        // Three tiers, best first. A match on the BASS root is a root-position
        // name and always wins. A match on a NON-bass root describes an
        // inversion; the corpus ground truth (and RS2014/EOF convention) spells
        // those by the PLAIN chord name far more often than the slash form
        // (G over a B bass is written "G", not "G/B"), so in EOF style the plain
        // inversion name outranks its slash spelling. The slash form is still
        // appended so the chord-template editor's full alternatives list keeps
        // it. Measured against the EOF corpus this preference recovers ~113 of
        // 123 shapes where a slash would otherwise be emitted that the human left plain.
        // MUSICAL style is the other way round: the tabs' typed names write an
        // inversion over its bass (Cm7/Bb, E/G#, Eb/G), so the slash tier leads.
        var rootPosition = new List<string>();
        var invertedPlain = new List<string>();
        var slashNames = new List<string>();

        foreach (var root in uniqueNotes)
        {
            var namesForRoot = GetSuggestedNamesForRoot(root, uniqueNotes, patterns, musical, key);
            if (namesForRoot.Count == 0) continue;

            if (root == bassPitchClass)
            {
                rootPosition.AddRange(namesForRoot);
                continue;
            }

            invertedPlain.AddRange(namesForRoot);
            foreach (var name in namesForRoot)
                slashNames.Add($"{name}/{bassName}");
        }

        var foundNames = new List<string>(
            rootPosition.Count + invertedPlain.Count + slashNames.Count);
        foundNames.AddRange(rootPosition);
        foundNames.AddRange(musical ? slashNames : invertedPlain);
        foundNames.AddRange(musical ? invertedPlain : slashNames);
        return foundNames;
    }

    /// <summary>
    ///     Whether a shape sounds on exactly two strings.
    ///     <para>
    ///         <b>Two pitches under-determine a chord</b>, which is the whole
    ///         reason a detected name for one is worth suppressing: the same two
    ///         notes sit inside a dozen different chords, so whichever the
    ///         matcher reaches first is a guess wearing a confident label. Editor
    ///         on Fire gates on exactly this — <c>stringctr == 2</c> in
    ///         <c>eof_lookup_chord</c> (<c>EOF_SRC/tuning.c:425</c>), behind its
    ///         <c>eof_dont_auto_name_double_stops</c> preference.
    ///     </para>
    ///     <para>
    ///         <b>This is NOT the same predicate as the arrangement property.</b>
    ///         EOF's <c>eof_pro_guitar_note_is_double_stop</c> — and our
    ///         <c>RsTechniques.IsDoubleStopAdjacentStrings</c> — additionally
    ///         require the two strings to be ADJACENT, because that flag describes
    ///         a playing technique. Naming does not care where the strings are; it
    ///         cares that there are only two pitches to name from. Keep them
    ///         separate.
    ///     </para>
    /// </summary>
    /// <param name="templateFrets">String index → fret; negative = string unused.</param>
    public static bool IsDoubleStop(IReadOnlyDictionary<int, int> templateFrets)
    {
        ArgumentNullException.ThrowIfNull(templateFrets);

        var sounding = 0;
        foreach (var fret in templateFrets.Values)
        {
            if (fret >= 0 && ++sounding > 2)
                return false;
        }

        return sounding == 2;
    }

    /// <summary>
    ///     Whether a two-string shape is a power chord — a root and its fifth, and
    ///     nothing else.
    ///     <para>
    ///         The interval is checked at SOUNDING pitch (tuning and capo folded
    ///         in, via the same <see cref="GetSortedSounds" /> every name goes
    ///         through) so a shape on a dropped or capoed neck is judged by what it
    ///         plays rather than by its fret numbers. A fifth is 7 semitones and
    ///         its inversion, the fourth, is 5 — both spell the same two pitch
    ///         classes, so both count. A unison or an octave (0) does not: that is
    ///         one note played twice, and the namer already names it after the note.
    ///     </para>
    /// </summary>
    public static bool IsPowerChord(
        IReadOnlyList<string> tuning, IReadOnlyDictionary<int, int> templateFrets, int capo = 0)
    {
        ArgumentNullException.ThrowIfNull(tuning);
        ArgumentNullException.ThrowIfNull(templateFrets);

        if (!IsDoubleStop(templateFrets)) return false;

        var sounds = GetSortedSounds(tuning, templateFrets, capo);
        if (sounds.Count != 2) return false;

        var interval = Mod12(sounds[1] - sounds[0]);
        return interval is 5 or 7;
    }

    /// <summary>
    ///     Whether this shape should be left UNNAMED by the automatic namer.
    ///     See <see cref="DoubleStopNameOptions" /> for what the options mean.
    /// </summary>
    public static bool SuppressesAutoName(
        IReadOnlyList<string> tuning, IReadOnlyDictionary<int, int> templateFrets, int capo,
        DoubleStopNameOptions options)
    {
        if (!options.Suppress) return false;
        if (!IsDoubleStop(templateFrets)) return false;
        return !options.KeepPowerChordNames || !IsPowerChord(tuning, templateFrets, capo);
    }

    /// <inheritdoc cref="SuppressesAutoName(IReadOnlyList{string}, IReadOnlyDictionary{int, int}, int, DoubleStopNameOptions)" />
    public static bool SuppressesAutoName(
        Arrangement arrangement, IReadOnlyDictionary<int, int> templateFrets, DoubleStopNameOptions options)
    {
        ArgumentNullException.ThrowIfNull(arrangement);
        return SuppressesAutoName(arrangement.Tuning, templateFrets, arrangement.Capo, options);
    }

    /// <param name="arrangement">Supplies the tuning and the capo fret.</param>
    /// <param name="templateFrets">String index → fret; negative = string unused.</param>
    /// <param name="options">
    ///     Null uses <see cref="DoubleStopNameOptions.Default" />, which suppresses
    ///     the name of every two-string shape. Pass
    ///     <see cref="DoubleStopNameOptions.NameEverything" /> to get the
    ///     pre-0.41 behaviour — the ALTERNATIVES list
    ///     (<see cref="SuggestChordNames(Arrangement, IReadOnlyDictionary{int, int})" />)
    ///     is deliberately never gated, so the inspector can still offer a name for
    ///     a double stop that the charter wants to label by hand.
    /// </param>
    /// <param name="naming">
    ///     Style and key; null is <see cref="ChordNamingOptions.Default" /> (EOF
    ///     style). The double-stop gate is the same in every style.
    /// </param>
    public static string SuggestFirstChordName(
        Arrangement arrangement, IReadOnlyDictionary<int, int> templateFrets,
        DoubleStopNameOptions? options = null, ChordNamingOptions? naming = null)
    {
        if (SuppressesAutoName(arrangement, templateFrets, options ?? DoubleStopNameOptions.Default))
            return string.Empty;

        var suggestions = SuggestChordNames(arrangement, templateFrets, naming ?? ChordNamingOptions.Default);
        return suggestions.Count == 0 ? string.Empty : suggestions[0];
    }

    public static string ChangeToneName(string tone, int change)
    {
        if (string.IsNullOrWhiteSpace(tone)) return "?";

        var trimmedTone = tone.Trim();
        if (change % 12 == 0) return trimmedTone;

        var preferredCycles = change > 0
            ? new[] { ToneCycleSharp, ToneCycleFlat }
            : [ToneCycleFlat, ToneCycleSharp];

        foreach (var cycle in preferredCycles)
        {
            var id = Array.FindIndex(cycle, candidate => string.Equals(candidate, trimmedTone, StringComparison.Ordinal));
            if (id < 0) continue;
            return cycle[Mod12(id + change)];
        }

        return "?";
    }

    private static List<int> GetSortedSounds(
        IReadOnlyList<string> tuning,
        IReadOnlyDictionary<int, int> templateFrets,
        int capo)
    {
        var openStrings = BuildAscendingOpenStringPitches(tuning);
        var sounds = new List<int>(templateFrets.Count);
        var capoFret = Math.Max(0, capo);

        foreach (var (stringIndex, fret) in templateFrets)
        {
            if (stringIndex < 0 || stringIndex >= openStrings.Count) continue;
            if (openStrings[stringIndex] == UnparseableOpenString) continue;
            if (fret < 0) continue;

            // Frets are ABSOLUTE on the un-capoed neck and fret 0 means "open",
            // which a capo stops at the capo fret — so the sounding offset is
            // max(fret, capo). This is the same derivation as
            // RefretPlanner.SoundingMidi / LivePitchAcceptance.SoundingMidi.
            // A fret strictly between 0 and the capo is physically unplayable
            // (inspector L01 flags it); fold it up to the capo rather than
            // dropping the string, so a malformed shape still names with the
            // right number of notes instead of silently losing one.
            // With capo 0 this is Math.Max(fret, 0) == fret for every fret that
            // reaches here — a strict no-op.
            sounds.Add(openStrings[stringIndex] + Math.Max(fret, capoFret));
        }

        sounds.Sort();
        return sounds;
    }

    /// <summary>Slot marker for a tuning entry that is not a note name.</summary>
    private const int UnparseableOpenString = int.MinValue;

    /// <summary>
    ///     Open-string pitches, ascending, ONE ENTRY PER TUNING INDEX — the result is
    ///     indexed by string index, so an unparseable entry must hold its slot. It used
    ///     to be skipped, which shifted every higher string's open pitch down one and
    ///     left the list one short, so the top string was dropped too and the suggester
    ///     returned a confidently wrong chord name instead of none.
    ///     <see cref="Arrangement.Tuning" /> is a bare <c>string[]</c> filled from
    ///     rscp/RS-XML/GP input with no validation anywhere in Core.
    /// </summary>
    private static List<int> BuildAscendingOpenStringPitches(IReadOnlyList<string> tuning)
    {
        var result = new List<int>(tuning.Count);
        var previous = int.MinValue;

        foreach (var stringName in tuning)
        {
            if (!TryGetPitchClass(stringName, out var pitchClass))
            {
                result.Add(UnparseableOpenString);
                continue;
            }

            var candidate = pitchClass;
            if (previous != int.MinValue)
            {
                while (candidate <= previous)
                    candidate += 12;
            }

            result.Add(candidate);
            previous = candidate;
        }

        return result;
    }

    private static List<int> SoundsToPitchClasses(IEnumerable<int> sounds)
    {
        var notes = new List<int>();
        foreach (var sound in sounds)
        {
            var note = Mod12(sound);
            if (!notes.Contains(note))
                notes.Add(note);
        }

        return notes;
    }

    private static List<string> GetSuggestedNamesForRoot(
        int root, IReadOnlyList<int> notes, ChordNamePattern[] patterns, bool musical, ChordKeySignature? key)
    {
        var foundNames = new List<string>();
        foreach (var pattern in patterns)
        {
            if (!pattern.Matches(root, notes)) continue;
            var suffix = musical ? pattern.MusicalSuffix : pattern.EofSuffix;
            foreach (var rootName in SpellNote(root, key))
                foundNames.Add(rootName + suffix);
        }

        return foundNames;
    }

    /// <summary>
    ///     A pitch class's name(s): in a key, the one spelling that key gives it
    ///     (<see cref="ChordKeyEstimator.SpellsFlat" />); with no key, the sharp
    ///     spelling first and the flat one second, as the namer always did.
    /// </summary>
    private static string[] SpellNote(int pitchClass, ChordKeySignature? key)
    {
        var sharp = PitchClassToName(pitchClass, useSharp: true);
        var flat = PitchClassToName(pitchClass, useSharp: false);
        if (string.Equals(sharp, flat, StringComparison.Ordinal)) return [sharp];
        if (key is { } k) return [ChordKeyEstimator.SpellsFlat(k, pitchClass) ? flat : sharp];
        return [sharp, flat];
    }

    /// <summary>
    ///     The pitch class of each open string, indexed by string; null where the
    ///     tuning entry is not a note name.
    /// </summary>
    internal static IReadOnlyList<int?> OpenStringPitchClasses(IReadOnlyList<string> tuning)
    {
        ArgumentNullException.ThrowIfNull(tuning);
        var result = new int?[tuning.Count];
        for (var i = 0; i < tuning.Count; i++)
            result[i] = TryGetPitchClass(tuning[i], out var pc) ? pc : null;
        return result;
    }

    private static bool TryGetPitchClass(string? tone, out int pitchClass)
    {
        pitchClass = 0;
        if (string.IsNullOrWhiteSpace(tone)) return false;
        return PitchClassByName.TryGetValue(tone.Trim(), out pitchClass);
    }

    private static string PitchClassToName(int pitchClass, bool useSharp)
    {
        var normalized = Mod12(pitchClass);
        return useSharp ? PitchClassNamesSharp[normalized] : PitchClassNamesFlat[normalized];
    }

    private static int Mod12(int value)
    {
        var result = value % 12;
        return result < 0 ? result + 12 : result;
    }

    private sealed class ChordNamePattern(int[] requiredTones, string eofSuffix, string? musicalSuffix = null)
    {
        public string EofSuffix { get; } = eofSuffix;

        public string MusicalSuffix { get; } = musicalSuffix ?? MusicalSpelling(eofSuffix);

        public bool Matches(int root, IReadOnlyList<int> notes)
        {
            if (notes.Count != requiredTones.Length + 1) return false;

            foreach (var requiredTone in requiredTones)
            {
                if (!notes.Contains((root + requiredTone) % 12))
                    return false;
            }

            return true;
        }

        // "min" → "m" and "Maj" → "maj": Cmin7 → Cm7, CMaj7 → Cmaj7,
        // Cminadd9 → Cmadd9; everything else is spelled the same in both styles.
        private static string MusicalSpelling(string suffix) =>
            suffix.Replace(Min, "m", StringComparison.Ordinal).Replace(Maj, "maj", StringComparison.Ordinal);
    }
}
