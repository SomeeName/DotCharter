using DotCharter.Core.Domain.Guitar;
using DotCharter.Core.Domain.Primitives;
using DotCharter.Core.Domain.Project;

namespace DotCharter.Core.Utilities;

/// <summary>
///     Resolves the <see cref="ArrangementProperties" /> an export should ship, from
///     the arrangement's own content plus its authored first-class fields. Pure and
///     deterministic; it never mutates the arrangement — the caller gets a fresh
///     object.
///     <para>
///     <b>Why this exists.</b> Rocksmith reads these flags out of the psarc manifest
///     and folds them into <c>SongDiffEasy/Med/Hard</c>, so an arrangement that
///     declares no techniques is rated as if it had none. Deriving them only in the
///     RS-XML writer would fill a
///     local clone that the manifest never sees, so a chart built from scratch in
///     DotCharter would tell the game it used no techniques at all.
///     </para>
///     <para>
///     <b>Provenance — three groups, and every flag is in exactly one.</b>
///     </para>
///     <list type="number">
///         <item>
///             <b>Authored elsewhere</b> (<see cref="AuthoredFlags" />):
///             <c>represent</c>/<c>bonusArr</c> mirror <see cref="Arrangement.Subtype" />,
///             the three <c>path*</c> flags mirror the resolved route
///             (<see cref="ResolveRouteMask" />), <c>bassPick</c> mirrors
///             <see cref="Arrangement.PickedBass" />. These are folded in on every
///             derive and are NOT pinnable — the first-class field is the truth.
///         </item>
///         <item>
///             <b>Derived from content</b> (<see cref="DerivedFlags" />): the 20
///             technique flags plus <c>standardTuning</c>. Computed on every derive,
///             SET <em>and</em> CLEARED, unless <see cref="ArrangementProperties.ManualOverride" />
///             pins them. <c>standardTuning</c> is deliberately outside the pin: the
///             tuning is a first-class field, so pinning its summary flag could only
///             ever produce a lie.
///         </item>
///         <item>
///             <b>Undecidable</b> (<see cref="UndecidableFlags" />): <c>fingerPicking</c>,
///             <c>twoFingerPicking</c>, <c>pickDirection</c> and <c>syncopation</c>.
///             No note field encodes them, so they are carried verbatim from the
///             arrangement — authored or import-carried — whether or not the pin is on.
///         </item>
///     </list>
///     <para>
///     <b>Provenance of the design.</b> Editor on Fire derives the technique flags at
///     RS-XML export from one accumulating pass over every note
///     (<c>EOF_SRC/src/rs.c:1836-1988</c>) and lets the user author only the
///     arrangement type, the normal/bonus/alternate priority and picked bass
///     (<c>EOF_SRC/src/menu/track.c:177-189</c>, <c>:2917</c>). Log Charter does the
///     same at write time, rebuilding the whole object each save
///     (<c>RsCharter/io/rs/xml/song/SongArrangement.java:355-386</c>).
///     Rocksmith2014.NET / DLC Builder derives <em>nothing</em> — it trusts EOF's XML
///     ("These properties are normally set by EOF automatically.") and offers an
///     opt-in override instead: <c>ArrangementProperties: ArrPropFlags option</c>,
///     <c>None</c> = pass through, <c>Some</c> = a full overwrite of 23 flags seeded
///     from the current values, applied at psarc build time
///     (<c>Rocksmith2014.DLCProject/ArrangementPropertiesOverride.fs:64-89</c>,
///     <c>Manifest/AttributesCreation.fs:200-237</c>). DotCharter occupies EOF's seat
///     in that chain — nothing upstream of us derives anything — so it derives like
///     EOF, and carries DLC Builder's pin so a charter can still correct a wrong call
///     without hand-editing XML.
///     </para>
///     <para>
///     <b>Deliberate divergences from EOF</b>, each matching Log Charter instead:
///     <c>sustain</c> counts sustained CHORDS too (EOF only counts single notes,
///     <c>rs.c:1932-1938</c>); <c>unpitchedSlides</c> and <c>fretHandMutes</c> are
///     really derived (EOF hardcodes both to <c>"0"</c> at <c>rs.c:1988</c> despite
///     owning the data); <c>dropDPower</c> is a per-chord shape test rather than
///     EOF's whole-track "is the tuning drop D" AND.
///     </para>
///     <para>
///     <b>A split chord counts as its notes</b> (<see cref="Chord.SplitIntoNotes" />):
///     its strings' techniques count, its shape (double stop, open, power,
///     barre, unnamed) does not — the same answer the chord template list and the
///     repeat decision give, where the export follows EOF. EOF itself derives
///     these flags BEFORE it breaks split chords up (<c>rs.c:1872</c> runs before
///     <c>rs.c:2082-2124</c>), so its own export still counts a split chord's
///     shape; this is a deliberate divergence, because the game only ever sees
///     the notes.
///     </para>
/// </summary>
public static class ArrangementPropertiesDeriver
{
    /// <summary>Rocksmith <c>RouteMask</c> bits. <c>Lead|Rhythm == 3</c> is "any".</summary>
    public const int RouteLead = 1;

    /// <inheritdoc cref="RouteLead" />
    public const int RouteRhythm = 2;

    /// <inheritdoc cref="RouteLead" />
    public const int RouteBass = 4;

    /// <summary>Flags folded from a first-class field on every derive; never pinnable.</summary>
    public static IReadOnlyList<string> AuthoredFlags { get; } =
        ["represent", "bonusArr", "pathLead", "pathRhythm", "pathBass", "bassPick"];

    /// <summary>
    ///     Flags computed from chart content. All but <c>standardTuning</c> are
    ///     suppressed by <see cref="ArrangementProperties.ManualOverride" />.
    /// </summary>
    public static IReadOnlyList<string> DerivedFlags { get; } =
    [
        "standardTuning", "nonStandardChords", "barreChords", "powerChords", "dropDPower",
        "openChords", "doubleStops", "palmMutes", "harmonics", "pinchHarmonics", "hopo",
        "tremolo", "slides", "unpitchedSlides", "bends", "tapping", "vibrato",
        "fretHandMutes", "slapPop", "fifthsAndOctaves", "sustain"
    ];

    /// <summary>Flags no chart content encodes; carried verbatim, always authored.</summary>
    public static IReadOnlyList<string> UndecidableFlags { get; } =
        ["fingerPicking", "pickDirection", "twoFingerPicking", "syncopation"];

    /// <summary>The route an arrangement of this type takes when nothing is authored.</summary>
    public static int DefaultRouteMask(ArrangementType type) => type switch
    {
        ArrangementType.Lead => RouteLead,
        ArrangementType.Rhythm => RouteRhythm,
        ArrangementType.Combo => RouteLead | RouteRhythm,
        ArrangementType.Bass => RouteBass,
        _ => 0
    };

    /// <summary>
    ///     The arrangement's Rocksmith route, authored <c>path*</c> flags first and the
    ///     <see cref="Arrangement.ArrangementType" /> default second.
    ///     <para>
    ///     The authored value has to win: the official base game names BOTH of Satch
    ///     Boogie's guitar arrangements "Combo" and only these flags say which one is
    ///     the Lead path — the same reason
    ///     the package reader's manifest parser refuses to fall back to
    ///     the name while flags are present.
    ///     </para>
    /// </summary>
    public static int ResolveRouteMask(Arrangement arrangement)
    {
        var p = arrangement.Properties;
        var authored = (p.PathLead != 0 ? RouteLead : 0)
                       | (p.PathRhythm != 0 ? RouteRhythm : 0)
                       | (p.PathBass != 0 ? RouteBass : 0);
        return authored != 0 ? authored : DefaultRouteMask(arrangement.ArrangementType);
    }

    /// <summary>
    ///     The properties this arrangement should export with. Returns a fresh object;
    ///     <paramref name="arrangement" /> is never touched.
    /// </summary>
    public static ArrangementProperties Derive(Arrangement arrangement)
    {
        var p = arrangement.Properties.Clone();

        // ── 1. Authored elsewhere ────────────────────────────────────────────
        p.Represent = arrangement.Subtype == ArrangementSubtype.Main ? 1 : 0;
        p.BonusArr = arrangement.Subtype == ArrangementSubtype.Bonus ? 1 : 0;

        var route = ResolveRouteMask(arrangement);
        p.PathLead = (route & RouteLead) != 0 ? 1 : 0;
        p.PathRhythm = (route & RouteRhythm) != 0 ? 1 : 0;
        p.PathBass = (route & RouteBass) != 0 ? 1 : 0;

        // EOF gates picked bass on the arrangement really being bass (rs.c:1788-1792)
        // and so does DLC Builder's GUI; a guitar arrangement claiming it is noise.
        p.BassPick = arrangement.PickedBass && arrangement.ArrangementType == ArrangementType.Bass ? 1 : 0;

        // ── 2. standardTuning — derived even when the rest is pinned ─────────
        p.StandardTuning = IsStandardTuning(arrangement) ? 1 : 0;

        // ── 3. The technique flags ───────────────────────────────────────────
        if (!p.ManualOverride) ApplyContentFlags(arrangement, p);

        return p;
    }

    /// <summary>
    ///     EOF's rule (<c>rs.c:1852-1855</c>): standard means every string's tuning
    ///     offset is 0. Log Charter instead accepts a table of named "…Standard"
    ///     tunings, so Eb Standard reads as standard there and not here; EOF's is the
    ///     one the RS-XML corpus was written with.
    /// </summary>
    public static bool IsStandardTuning(Arrangement arrangement)
    {
        var stringCount = arrangement.StringCount > 0 ? arrangement.StringCount : arrangement.TuningPitches.Count;
        if (stringCount <= 0) return true;

        for (var i = 0; i < stringCount; i++)
        {
            if (i >= arrangement.TuningPitches.Count) continue;
            if (arrangement.TuningPitches[i].Step != 0) return false;
        }

        return true;
    }

    private static void ApplyContentFlags(Arrangement arrangement, ArrangementProperties p)
    {
        var isBass = arrangement.ArrangementType == ArrangementType.Bass;

        p.Sustain = 0;
        p.PalmMutes = 0;
        p.FretHandMutes = 0;
        p.Harmonics = 0;
        p.PinchHarmonics = 0;
        p.Hopo = 0;
        p.Tapping = 0;
        p.Tremolo = 0;
        p.Slides = 0;
        p.UnpitchedSlides = 0;
        p.Bends = 0;
        p.Vibrato = 0;
        p.SlapPop = 0;
        p.NonStandardChords = 0;
        p.BarreChords = 0;
        p.OpenChords = 0;
        p.PowerChords = 0;
        p.DropDPower = 0;
        p.DoubleStops = 0;
        p.FifthsAndOctaves = 0;

        var openSemitones = BuildOpenSemitones(arrangement);
        var templatesById = BuildTemplateIndex(arrangement);
        var bassOctaves = false;

        foreach (var level in arrangement.Levels)
        {
            // A fret-hand mute marker is an authored technique in its own right, and
            // the RS-XML writer emits it — EOF hardcodes fretHandMutes to 0 and never
            // sees these at all.
            if (level.FretHandMutes.Count > 0) p.FretHandMutes = 1;

            foreach (var note in level.Notes) ApplySoundFlags(p, note);

            foreach (var chord in level.Chords)
            {
                foreach (var chordNote in chord.ChordNotes) ApplySoundFlags(p, chordNote);

                // A "Split notes" chord ships as single notes (LinkLandingBreakup),
                // so it counts as those notes: their techniques above, and no chord
                // SHAPE below — the game never sees a chord there.
                if (chord.SplitIntoNotes) continue;

                // A double stop is counted over the USED strings, string mutes
                // included: EOF walks the note's string bitmask, which a mute does
                // not clear (note.c:3006-3032). Computed before the sounding gate
                // for exactly that reason — a fully muted dyad is still a double
                // stop, and dropping it was the last disagreement with the corpus.
                if (chord.ChordNotes.Count == 2 && AreAdjacentStrings(chord.ChordNotes))
                    p.DoubleStops = 1;

                var sounding = new List<ChordNote>(chord.ChordNotes.Count);
                foreach (var chordNote in chord.ChordNotes)
                {
                    if (chordNote.Mute != MuteType.Full)
                        sounding.Add(chordNote);
                }

                // EOF counts a strum as a chord at more than one non-ghosted gem
                // (rs.c:1875 via eof_note_count_rs_lanes).
                if (sounding.Count < 2) continue;

                templatesById.TryGetValue(chord.TemplateId, out var template);

                // EOF: "the chord has no defined or detected name" (rs.c:1877-1880).
                // A template we cannot resolve is equally unnameable.
                if (template == null || string.IsNullOrWhiteSpace(template.Name))
                    p.NonStandardChords = 1;
                if (IsBarreChord(chord, template)) p.BarreChords = 1;

                var hasOpen = false;
                foreach (var chordNote in sounding)
                {
                    if (chordNote.Fret == 0)
                        hasOpen = true;
                }

                if (hasOpen) p.OpenChords = 1;

                if (IsPowerChord(sounding, openSemitones, arrangement.Capo, out var flatShape))
                {
                    p.PowerChords = 1;
                    if (flatShape) p.DropDPower = 1;
                }

                if (isBass && AllSoundTheSamePitch(sounding, openSemitones, arrangement.Capo))
                    bassOctaves = true;
            }
        }

        // EOF rs.c:1980-1987 — on bass, double stops are re-expressed as fifths and
        // octaves and the doubleStops flag is force-cleared. On guitar,
        // fifthsAndOctaves stays 0.
        if (isBass)
        {
            if (p.DoubleStops != 0 || bassOctaves) p.FifthsAndOctaves = 1;
            p.DoubleStops = 0;
        }
    }

    private static void ApplySoundFlags(ArrangementProperties p, IGuitarSound sound)
    {
        // Log Charter counts a sustained chord (SongArrangement.java:343-345); EOF
        // only counts single notes (rs.c:1932-1938). A held chord IS a sustain.
        if (sound.Sustain > FractionalPosition.Zero) p.Sustain = 1;
        if (sound.Mute == MuteType.Palm) p.PalmMutes = 1;
        if (sound.Mute == MuteType.Full) p.FretHandMutes = 1;
        if (sound.Harmonic == HarmonicType.Normal) p.Harmonics = 1;
        if (sound.Harmonic == HarmonicType.Pinch) p.PinchHarmonics = 1;
        if (sound.Hopo is HopoType.HammerOn or HopoType.PullOff) p.Hopo = 1;
        if (sound.Hopo == HopoType.Tap) p.Tapping = 1;
        if (sound.Tremolo) p.Tremolo = 1;
        if (sound.SlideTo.HasValue && !sound.UnpitchedSlide) p.Slides = 1;
        if (sound.UnpitchedSlide) p.UnpitchedSlides = 1;
        if (sound.Vibrato) p.Vibrato = 1;
        if (sound.BassPicking != BassPickingType.None) p.SlapPop = 1;

        foreach (var bend in sound.BendValues)
        {
            if (Math.Abs(bend.Step) > 0.001)
            {
                p.Bends = 1;
                break;
            }
        }
    }

    /// <summary>
    ///     Templates are deduped by shape and their ids are POSITIONAL, so a chord can
    ///     only be matched by the id it currently carries. Last id wins on a duplicate,
    ///     which is the same thing <c>FirstOrDefault</c> avoided by taking the first —
    ///     either way a duplicated id is already malformed.
    /// </summary>
    private static Dictionary<int, ChordTemplate> BuildTemplateIndex(Arrangement arrangement)
    {
        var index = new Dictionary<int, ChordTemplate>(arrangement.ChordTemplates.Count);
        foreach (var template in arrangement.ChordTemplates) index[template.Id] = template;
        return index;
    }

    private static int[] BuildOpenSemitones(Arrangement arrangement)
    {
        // Relative open pitches (StandardOpenStrings, lowest string first; extra
        // strings extend DOWN). Only intervals matter here, so the base cancels out.
        var stringCount = Math.Clamp(
            arrangement.StringCount > 0 ? arrangement.StringCount : StandardOpenStrings.DefaultStringCount,
            1, StandardOpenStrings.MaxStringCount);
        var result = new int[stringCount];
        for (var s = 0; s < stringCount; s++)
        {
            var step = s < arrangement.TuningPitches.Count ? arrangement.TuningPitches[s].Step : 0;
            result[s] = StandardOpenStrings.SemitonesAboveLowE(stringCount, s) + step;
        }

        return result;
    }

    /// <summary>Sounding semitone of a chord note, or null when the string is off the neck.</summary>
    private static int? SoundingSemitone(ChordNote note, int[] openSemitones, int capo)
    {
        if (note.StringIndex < 0 || note.StringIndex >= openSemitones.Length) return null;
        if (note.Fret < 0) return null;
        // Frets are ABSOLUTE and fret 0 is stopped by the capo — the sounding offset is
        // max(fret, capo). Strict no-op at capo 0. Same rule as
        // ChordNameSuggester.GetSortedSounds / RefretPlanner.SoundingMidi.
        return openSemitones[note.StringIndex] + Math.Max(note.Fret, Math.Max(0, capo));
    }

    private static bool AreAdjacentStrings(IReadOnlyCollection<ChordNote> notes)
    {
        var min = int.MaxValue;
        var max = int.MinValue;
        foreach (var note in notes)
        {
            if (note.StringIndex < min) min = note.StringIndex;
            if (note.StringIndex > max) max = note.StringIndex;
        }

        // EOF requires the played strings to be contiguous (note.c:3006-3032).
        return max - min == notes.Count - 1;
    }

    /// <summary>
    ///     Root + fifth, optionally + octave, on adjacent strings. This is Log Charter's
    ///     interval test (<c>SongArrangement.java:197-259</c>) rather than EOF's chord
    ///     dictionary lookup, and it carries the drop-D answer with it:
    ///     <paramref name="flatShape" /> is true when every string of the power chord
    ///     sits on the SAME fret, which only a dropped lowest string makes possible.
    ///     EOF instead ANDs any power chord with a whole-track "is the tuning drop D"
    ///     test (<c>rs.c:1856-1895</c>), so it misses a drop-D shape in any other
    ///     tuning and claims one for every power chord in drop D.
    /// </summary>
    private static bool IsPowerChord(List<ChordNote> sounding, int[] openSemitones, int capo, out bool flatShape)
    {
        flatShape = false;
        if (sounding.Count is < 2 or > 3) return false;
        if (!AreAdjacentStrings(sounding)) return false;

        var ordered = new List<ChordNote>(sounding);
        ordered.Sort((a, b) => a.StringIndex.CompareTo(b.StringIndex));

        var pitches = new int[ordered.Count];
        for (var i = 0; i < ordered.Count; i++)
        {
            var pitch = SoundingSemitone(ordered[i], openSemitones, capo);
            if (pitch == null) return false;
            pitches[i] = pitch.Value;
        }

        if (pitches[1] - pitches[0] != 7) return false;
        if (ordered.Count == 3 && pitches[2] - pitches[0] != 12) return false;

        flatShape = true;
        for (var i = 1; i < ordered.Count; i++)
        {
            if (ordered[i].Fret != ordered[0].Fret)
                flatShape = false;
        }

        return true;
    }

    /// <summary>
    ///     EOF's bass "octaves" test (<c>rs.c:1910-1930</c>): every sounding string of
    ///     the strum plays the same note.
    /// </summary>
    private static bool AllSoundTheSamePitch(List<ChordNote> sounding, int[] openSemitones, int capo)
    {
        int? first = null;
        foreach (var note in sounding)
        {
            var pitch = SoundingSemitone(note, openSemitones, capo);
            if (pitch == null) return false;
            if (first == null) first = pitch;
            else if (first != pitch) return false;
        }

        return first != null;
    }

    /// <summary>
    ///     A barre is one finger stopping several strings — Log Charter's rule
    ///     (<c>SongArrangement.java:178-195</c>).
    ///     <para>
    ///     <b>Fingering, when the template has it, is the ANSWER — not a first
    ///     opinion.</b> The fret grouping is only a fallback for an unfingered shape.
    ///     The version this replaced ran the fallback anyway when the finger test
    ///     said no, so an open A shape (three strings on fret 2, fingered 1-2-3)
    ///     read as a barre; measured against the hand-fingered <c>TestData/EOF</c>
    ///     corpus that over-claimed <c>barreChords</c> on 30 of 146 arrangements, and
    ///     the flag weighs 3× in <c>RsManifestGenerator.CalculateDifficulties</c>.
    ///     </para>
    /// </summary>
    private static bool IsBarreChord(Chord chord, ChordTemplate? template)
    {
        var frets = template?.Frets.Count > 0
            ? template.Frets
            : chord.ChordNotes.ToDictionary(note => note.StringIndex, note => note.Fret);
        var fingers = template?.Fingers;

        // A finger id of 0 is the THUMB, which stops one string — never a barre.
        if (fingers != null && fingers.Any(kv => kv.Value is > 0 and <= 4))
        {
            return fingers
                .Where(kv => kv.Value is > 0 and <= 4 &&
                             frets.TryGetValue(kv.Key, out var fret) &&
                             fret > 0)
                .GroupBy(kv => new { kv.Value, Fret = frets[kv.Key] })
                .Any(group => group.Count() >= 2);
        }

        return frets
            .Where(kv => kv.Value > 0)
            .GroupBy(kv => kv.Value)
            .Any(group => group.Count() >= 2);
    }
}
