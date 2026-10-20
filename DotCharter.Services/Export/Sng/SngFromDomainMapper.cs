using System.Globalization;
using DotCharter.Services.Export.Rocksmith;
using DotCharter.Services.Export.Sng.Model;
using DotCharter.Services.Parsing.Xml.Song;

namespace DotCharter.Services.Export.Sng;

/// <summary>Hero-level note counts accumulated during conversion; feeds the
/// SNG metadata and the manifest attributes. Mirrors Rocksmith2014.NET's
/// <c>NoteCounts</c>.</summary>
internal sealed record SngNoteCounts(int Easy, int Medium, int Hard, int Ignored);

/// <summary>Result of a domain → SNG conversion.</summary>
internal sealed record SngBuildResult(RocksmithSng Sng, SngNoteCounts NoteCounts);

/// <summary>
/// Converts the DotCharter domain model (Arrangement + beat map) into a
/// <see cref="RocksmithSng"/> — the inverse of the package reader's SNG → domain mapper.
/// All derived-field semantics (note masks, link-next child chains, phrase
/// iteration attribution, anchor first/last note times, per-level note count
/// statistics, string masks, metadata scoring) are ported from
/// Rocksmith2014.NET's Rocksmith2014.Conversion (XmlToSng/XmlToSngNote/
/// XmlToSngLevel/AccuData).
/// </summary>
internal static class SngFromDomainMapper
{
    // Uninitialized values found in anchors that have no notes.
    // Reference: XmlToSng.fs UninitFirstNote/UninitLastNote.
    private const float UninitFirstNote = 3.4028234663852886e+38f;
    private const float UninitLastNote = 1.1754943508222875e-38f;

    /// <param name="arrangement">The arrangement to convert.</param>
    /// <param name="beats">The project's beat grid.</param>
    /// <param name="songLengthMs">Song length, resolved against the chart and the audio.</param>
    /// <param name="conversionTime">Stamped into the SNG metadata; defaults to now.</param>
    /// <param name="chordNameOptions">
    ///     Whether a two-string shape may ship with a chord name. Defaults to
    ///     <see cref="DoubleStopNameOptions.Default" /> — what
    ///     <c>AppConfig.SuppressDoubleStopChordNames</c> ships as — so the SNG and
    ///     the RS XML built from one chart cannot disagree about a name just
    ///     because one caller threaded the setting and the other did not.
    /// </param>
    /// <param name="toneSlots">
    ///     The tone slot table the PACKAGE will declare as <c>Tone_A</c>..<c>Tone_D</c>
    ///     in the manifest. A tone-change event in the SNG carries nothing but an
    ///     INDEX into that table, so the two must be the same list: the builder lets
    ///     a caller override the slots (<c>RocksmithBuildOptions.ToneMap</c>, which is
    ///     what the tone designer supplies), and resolving them here from the
    ///     arrangement's own fields instead pointed every tone change at whatever
    ///     tone happened to sit at that index in the OTHER list. Null falls back to
    ///     <see cref="RsToneSlots.Resolve" />, which is the same answer when no override
    ///     exists.
    /// </param>
    /// <param name="rs7ChordMode">How a 7-string guitar's chord on the 7th string ships;
    /// ignored for six strings or fewer.</param>
    public static SngBuildResult BuildInstrumental(
        Arrangement arrangement,
        IList<BeatMarker> beats,
        double songLengthMs,
        DateTime? conversionTime = null,
        DoubleStopNameOptions? chordNameOptions = null,
        IReadOnlyList<string>? toneSlots = null,
        Rs7ChordMode rs7ChordMode = Rs7ChordMode.Projection)
    {
        if (beats.Count == 0)
            throw new InvalidDataException("Cannot export an arrangement without a beat map.");

        if (Rs7StringLayout.ExportRefusal(arrangement) is { } refusal) throw new InvalidDataException(refusal);

        // A 7-string guitar maps through the rs7string layout first; the
        // strings past the sixth go to the RS7X block.
        arrangement = Rs7Projection.TryCreate(arrangement, rs7ChordMode)?.Arrangement ?? arrangement;
        var context = new BuildContext(arrangement, beats, ResolveSongLengthMs(arrangement, beats, songLengthMs))
        {
            ChordNameOptions = chordNameOptions ?? DoubleStopNameOptions.Default,
            ToneSlots = toneSlots,
            Rs7Block = Rs7BlockBuilder.For(arrangement),
        };

        var sng = new RocksmithSng();

        BuildPhrasesAndIterations(context, sng);
        BuildChordTemplates(context, sng);
        BuildEventsTonesAndDnas(context, sng);
        BuildLevels(context, sng);
        BuildSections(context, sng);
        BuildBeats(context, sng);
        BuildMetaData(context, sng, conversionTime ?? DateTime.Now);
        sng.Extension = context.Rs7Block?.Build();

        return new SngBuildResult(sng, new SngNoteCounts(
            context.NoteCountEasy, context.NoteCountMedium, context.NoteCountHard, context.NoteCountIgnored));
    }

    /// <summary>Builds a vocals SNG from a vocal track, using the game's
    /// default lyric font. Reference: Rocksmith2014.NET ConvertVocals.fs.</summary>
    public static RocksmithSng BuildVocals(VocalTrack track, IList<BeatMarker> beats, DateTime? conversionTime = null)
    {
        var sng = new RocksmithSng();

        foreach (var phrase in track.Phrases.OrderBy(p => p.Position))
        {
            // A blank syllable is not exportable, so it is filtered out BEFORE the
            // index that decides the phrase terminator. Skipping it with a
            // `continue` inside the loop while `i == lyrics.Count - 1` still counts
            // it would leave a phrase whose LAST syllable has blank text (and no earlier
            // `IsPhraseEnd`) with no `+` on any vocal — and in Rocksmith the `+` IS the
            // line break, so that phrase would merge into the next one in-game with nothing
            // wrong anywhere in the editor.
            var lyrics = phrase.Lyrics
                .OrderBy(l => l.Position)
                .Where(l => !string.IsNullOrEmpty(l.Text.TrimEnd(' ')))
                .ToList();
            for (var i = 0; i < lyrics.Count; i++)
            {
                var lyric = lyrics[i];
                var text = lyric.Text.TrimEnd(' ');

                if (lyric.IsWordPart && !text.EndsWith('-')) text += "-";
                var isPhraseEnd = lyric.IsPhraseEnd || i == lyrics.Count - 1;
                if (isPhraseEnd && !text.EndsWith('+')) text += "+";

                var startMs = lyric.Position.GetPositionMs(beats);
                var endMs = lyric.EndPosition.GetPositionMs(beats);
                sng.Vocals.Add(new RsVocal(
                    MsToSec(startMs),
                    lyric.Note is > 0 and < 255 ? lyric.Note : 254,
                    MsToSec(Math.Max(0.0, endMs - startMs)),
                    text));
            }
        }

        sng.Vocals.Sort((a, b) => a.Time.CompareTo(b.Time));

        // Default font: two headers (IDs 0 and 1), the standard texture and
        // the embedded default glyph set. Reference: ConvertVocals.fs.
        sng.SymbolsHeaders.Add(new RsSymbolsHeader(0));
        sng.SymbolsHeaders.Add(new RsSymbolsHeader(1));
        sng.SymbolsTextures.Add(new RsSymbolsTexture(@"assets\ui\lyrics\lyrics.dds", 27, 1024, 512));
        sng.SymbolDefinitions.AddRange(DefaultVocalSymbols.Load());

        sng.MetaData = new RsMetaData
        {
            CapoFretId = -1,
            LastConversionDateTime = (conversionTime ?? DateTime.Now).ToString("MM-d-yy HH:mm", CultureInfo.InvariantCulture),
            Part = 1,
            SongLength = sng.Vocals.Count > 0 ? sng.Vocals[^1].Time + sng.Vocals[^1].Length : 0f,
            Tuning = [],
            FirstNoteTime = sng.Vocals.Count > 0 ? sng.Vocals[0].Time : 0f,
            MaxDifficulty = 0
        };

        return sng;
    }

    // ---- context ----------------------------------------------------------

    private sealed class BuildContext
    {
        public BuildContext(Arrangement arrangement, IList<BeatMarker> beats, double songLengthMs)
        {
            Arrangement = arrangement;
            Beats = beats;
            SongLengthMs = (int)Math.Round(songLengthMs);

            // The FILE shape of the ladder (RsLevelLadder): a level above a
            // phrase's max leaves that phrase out, as the game expects and as the
            // RS-XML writer does from the same call.
            Ladder = RsLevelLadder.ForExport(arrangement);
            OrderedLevels = Ladder.Levels.OrderBy(l => l.DifficultyIndex).ToList();

            var (iterations, phraseNames) = ExtractIterations(arrangement, beats, SongLengthMs);
            Iterations = iterations;
            PhraseNames = phraseNames;
            PiTimes = new int[Iterations.Count + 1];
            for (var i = 0; i < Iterations.Count; i++) PiTimes[i] = Iterations[i].StartMs;
            PiTimes[^1] = SongLengthMs;

            Sections = ExtractSections(arrangement, beats, Iterations, SongLengthMs);
            StringMasks = new sbyte[Sections.Count][];
            for (var i = 0; i < Sections.Count; i++) StringMasks[i] = new sbyte[36];

            NotesInIterationsAll = CreateCountTable();
            NotesInIterationsExclIgnored = CreateCountTable();

            TemplateIndexById = [];
        }

        /// <summary>Whether a two-string shape may ship with a chord name.</summary>
        public DoubleStopNameOptions ChordNameOptions { get; init; } = DoubleStopNameOptions.Default;

        /// <summary>The slot table the manifest will declare, when the caller
        /// supplies one. See the <c>toneSlots</c> parameter.</summary>
        public IReadOnlyList<string>? ToneSlots { get; init; }

        /// <summary>The RS7X block of a 7-string guitar; null when the SNG stays stock.</summary>
        public Rs7BlockBuilder? Rs7Block { get; init; }

        public Arrangement Arrangement { get; }
        public IList<BeatMarker> Beats { get; }
        public int SongLengthMs { get; }
        public List<ChartLevel> OrderedLevels { get; }
        public RsLevelLadderExport Ladder { get; }
        public List<string> PhraseNames { get; }
        public List<IterationInfo> Iterations { get; }
        public int[] PiTimes { get; }
        public List<SectionInfo> Sections { get; }
        public sbyte[][] StringMasks { get; }
        public int[][] NotesInIterationsAll { get; }
        public int[][] NotesInIterationsExclIgnored { get; }
        public Dictionary<int, int> TemplateIndexById { get; }
        /// <summary>Chords whose domain TemplateId did not resolve; keyed by
        /// reference (Chord does not override equality).</summary>
        public Dictionary<Chord, int> ChordIdOverrides { get; } = [];

        /// <summary>Chords the game draws as a compact repeat rather than a
        /// full chord panel. Filled once the templates exist, because the
        /// decision compares the chord ids that will be WRITTEN. Same rule
        /// and same source of truth as the XML writer — see RsChordDensity;
        /// keyed by reference like the overrides above.</summary>
        public HashSet<Chord> CompactRepeats { get; } = [];
        public List<RsChordTemplate> Templates { get; } = [];
        public Dictionary<string, int> ChordNotesIds { get; } = new(ChordNotesComparer.Instance);
        public int NoteCountEasy;
        public int NoteCountMedium;
        public int NoteCountHard;
        public int NoteCountIgnored;

        public int MaxDifficulty => Math.Max(0, OrderedLevels.Count - 1);

        private int[][] CreateCountTable()
        {
            var table = new int[Math.Max(1, OrderedLevels.Count)][];
            for (var i = 0; i < table.Length; i++) table[i] = new int[Iterations.Count];
            return table;
        }
    }

    private sealed record IterationInfo(int PhraseId, int StartMs, int EndMs, int Easy, int Medium, int Hard);

    private sealed record SectionInfo(string Name, int Number, int StartMs, int EndMs);

    // ---- phrases / iterations ---------------------------------------------

    private static (List<IterationInfo> Iterations, List<string> PhraseNames) ExtractIterations(
        Arrangement arrangement, IList<BeatMarker> beats, int songLengthMs)
    {
        var phraseNames = arrangement.Phrases.ToList();
        var raw = new List<(int timeMs, int phraseId)>();

        if (arrangement.PhraseIterations.Count > 0)
        {
            foreach (var iteration in arrangement.PhraseIterations)
                raw.Add((MsInt(iteration.Position.GetPositionMs(beats)), iteration.PhraseId));
        }
        else
        {
            foreach (var ep in arrangement.EventPoints)
            {
                if (string.IsNullOrEmpty(ep.Phrase)) continue;
                var phraseId = phraseNames.IndexOf(ep.Phrase);
                if (phraseId < 0) continue;
                raw.Add((MsInt(ep.Position.GetPositionMs(beats)), phraseId));
            }
        }

        raw = raw
            .Where(it => it.phraseId >= 0 && it.phraseId < phraseNames.Count)
            .OrderBy(it => it.timeMs)
            .ToList();

        if (raw.Count == 0)
        {
            // No phrase structure in the chart: synthesize COUNT/content/END
            // so the SNG is structurally valid.
            phraseNames = ["COUNT", "p0", "END"];
            var firstBeatMs = MsInt(beats[0].Time);
            var contentMs = Math.Min(songLengthMs, firstBeatMs + 1);
            var endMs = Math.Max(contentMs + 1, MsInt(beats[^1].Time));
            // The END iteration must start after the last content ends
            // (sustains and handshapes included), or trailing notes land
            // inside the END phrase.
            if (LastContentEndMs(arrangement, beats) is { } lastContentEnd && lastContentEnd >= endMs)
                endMs = Math.Min(songLengthMs, lastContentEnd + 1);
            raw =
            [
                (firstBeatMs, 0),
                (contentMs, 1),
                (endMs, 2)
            ];
        }

        var result = new List<IterationInfo>(raw.Count);
        for (var i = 0; i < raw.Count; i++)
        {
            var endMs = i + 1 < raw.Count ? raw[i + 1].timeMs : songLengthMs;
            result.Add(new IterationInfo(raw[i].phraseId, raw[i].timeMs, endMs, 0, 0, 0));
        }

        return (result, phraseNames);
    }

    private static void BuildPhrasesAndIterations(BuildContext context, RocksmithSng sng)
    {
        var arrangement = context.Arrangement;
        var maxDiff = context.MaxDifficulty;
        // The SNG (and the manifest, which copies these names) gets a legal,
        // unique spelling; PhraseInfos stay keyed by the AUTHORED name.
        var exportedNames = RsPhraseName.SanitizeAll(context.PhraseNames, RsPhraseName.SngMaxLength);

        for (var phraseId = 0; phraseId < context.PhraseNames.Count; phraseId++)
        {
            var name = context.PhraseNames[phraseId];
            var iterationCount = context.Iterations.Count(it => it.PhraseId == phraseId);

            var phraseMaxDiff = maxDiff;
            var solo = (sbyte)0;
            if (arrangement.PhraseInfos.TryGetValue(name, out var info))
            {
                // Raised where a charter edited a phrase above its authored max;
                // otherwise the authored value.
                var shipped = context.Ladder.ShippedMaxDifficulty.TryGetValue(name, out var raised)
                    ? raised
                    : info.MaxDifficulty;
                phraseMaxDiff = Math.Clamp(shipped, 0, maxDiff);
                solo = (sbyte)(info.Solo ? 1 : 0);
            }

            sng.Phrases.Add(new RsPhrase(phraseMaxDiff, iterationCount, exportedNames[phraseId], solo));
        }

        for (var i = 0; i < context.Iterations.Count; i++)
        {
            var iteration = context.Iterations[i];
            var phraseMaxDiff = iteration.PhraseId < sng.Phrases.Count
                ? sng.Phrases[iteration.PhraseId].MaxDifficulty
                : 0;

            // The domain has no per-iteration hero levels; derive the
            // conventional easy/medium ramp from the phrase difficulty. A
            // 2-level phrase must pin easy=medium=hard — intermediate hero
            // levels there crash score attack (Rocksmith2014.NET
            // PhraseCombiner rule).
            var hero = phraseMaxDiff == 1
                ? new[] { 1, 1, 1 }
                : [phraseMaxDiff / 4, phraseMaxDiff / 2, phraseMaxDiff];

            context.Iterations[i] = iteration with { Easy = hero[0], Medium = hero[1], Hard = hero[2] };

            sng.PhraseIterations.Add(new RsPhraseIteration(
                iteration.PhraseId,
                MsToSec(iteration.StartMs),
                MsToSec(context.PiTimes[i + 1]),
                hero));
        }
    }

    // ---- chord templates ---------------------------------------------------

    private static void BuildChordTemplates(BuildContext context, RocksmithSng sng)
    {
        // THE emitted list, built by the same table the RS-XML writer uses
        // (RsChordTemplateExport): the arrangement's templates minus the ones
        // only split chords play, then one synthesized nameless template per
        // chord whose id resolves to nothing. The SNG does not build its own copy
        // of that loop; one table means the two artifacts cannot disagree about
        // which shape an index names. The domain model is left untouched.
        var export = RsChordTemplateExport.Build(context.Arrangement.ChordTemplates,
            context.OrderedLevels.Select(l => l.Chords), context.OrderedLevels.SelectMany(l => l.HandShapes));
        foreach (var template in export.Templates) AddTemplate(context, template);
        foreach (var (id, index) in export.IndexById) context.TemplateIndexById[id] = index;
        foreach (var (chord, index) in export.ChordIndexOverrides) context.ChordIdOverrides[chord] = index;

        sng.Chords.AddRange(context.Templates);

        // The compact-repeat decision compares the ids just assigned, so it
        // has to run here rather than in the constructor.
        foreach (var level in context.OrderedLevels)
        {
            context.CompactRepeats.UnionWith(
                RsChordDensity.Compute(level, context.Beats, chord => ChordIdOf(context, chord)));
        }
    }

    /// <summary>The chord id this chord will be written with — the same
    /// resolution the note loop performs.</summary>
    private static int ChordIdOf(BuildContext context, Chord chord) =>
        context.ChordIdOverrides.TryGetValue(chord, out var overrideId)
            ? overrideId
            : context.TemplateIndexById.GetValueOrDefault(chord.TemplateId, -1);

    private static void AddTemplate(BuildContext context, ChordTemplate template)
    {
        var frets = new sbyte[6];
        var fingers = new sbyte[6];
        Array.Fill(frets, (sbyte)-1);
        Array.Fill(fingers, (sbyte)-1);
        foreach (var (stringIndex, fret) in template.Frets)
        {
            if (stringIndex is >= 0 and < 6)
                frets[stringIndex] = (sbyte)fret;
        }

        foreach (var (stringIndex, finger) in template.Fingers)
        {
            if (stringIndex is >= 0 and < 6)
                fingers[stringIndex] = (sbyte)finger;
        }

        // A two-string shape ships unnamed when the suppression is on, however the
        // name got there — the same strip RsXmlSongWriter applies, so the SNG the
        // game reads and the XML DLC Builder reads say the same thing.
        var (exportName, nop) = RsChordDisplaySuffix.ForExport(template);
        var name = ChordNameSuggester.SuppressesAutoName(
            context.Arrangement, template.Frets, context.ChordNameOptions)
            ? ""
            : exportName;

        var mask = 0u;
        if (template.Arpeggio || template.ForceArpeggioInRs) mask |= 0x1; // ChordMask.Arpeggio
        // Nop is the NopInRs FIELD, so the double-stop name strip above
        // cannot drop it, and the suffix never ships inside the SNG name.
        if (nop) mask |= 0x2; // ChordMask.Nop

        context.Templates.Add(new RsChordTemplate(mask, frets, fingers, name)
        {
            Notes = MapToMidiNotes(context.Arrangement, frets)
        });
        context.Rs7Block?.AddTemplate(template);
    }

    /// <summary>Per-string MIDI notes for a chord template.
    /// Reference: Rocksmith2014.Conversion Midi.fs.</summary>
    private static int[] MapToMidiNotes(Arrangement arrangement, sbyte[] frets)
    {
        int[] standardTuningMidiNotes = [40, 45, 50, 55, 59, 64];
        var isBass = arrangement.ArrangementType == ArrangementType.Bass;
        var capo = (sbyte)Math.Max(0, arrangement.Capo);

        var notes = new int[6];
        for (var stringIndex = 0; stringIndex < 6; stringIndex++)
        {
            if (frets[stringIndex] == -1)
            {
                notes[stringIndex] = -1;
                continue;
            }

            var fret = capo > 0 && frets[stringIndex] == 0 ? capo : frets[stringIndex];
            var tuningOffset = TuningOffsetResolution.For(arrangement, stringIndex);
            notes[stringIndex] = standardTuningMidiNotes[stringIndex] + tuningOffset + fret + (isBass ? -12 : 0);
        }

        return notes;
    }

    // ---- events / tones / DNAs ---------------------------------------------

    private static void BuildEventsTonesAndDnas(BuildContext context, RocksmithSng sng)
    {
        var arrangement = context.Arrangement;

        var events = new List<(int TimeMs, string Code)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Add(int timeMs, string code)
        {
            if (string.IsNullOrWhiteSpace(code)) return;
            var normalized = code.Trim();
            if (seen.Add($"{timeMs}:{normalized}"))
                events.Add((timeMs, normalized));
        }

        foreach (var ep in arrangement.EventPoints.OrderBy(e => e.Position))
        {
            if (string.IsNullOrWhiteSpace(ep.Events)) continue;
            var timeMs = MsInt(ep.Position.GetPositionMs(context.Beats));
            foreach (var code in ep.Events.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                Add(timeMs, code);
        }

        AddTimeSignatureEvents(context, Add);
        AddCrowdEvents(context, events, Add);

        foreach (var (timeMs, code) in events.OrderBy(e => e.TimeMs).ThenBy(e => e.Code, StringComparer.Ordinal))
        {
            var timeSec = MsToSec(timeMs);
            sng.Events.Add(new RsEvent(timeSec, code));

            var dnaId = code switch
            {
                "dna_none" => 0,
                "dna_solo" => 1,
                "dna_riff" => 2,
                "dna_chord" => 3,
                _ => -1
            };
            if (dnaId >= 0)
                sng.Dnas.Add(new RsDna(timeSec, dnaId));
        }

        // The manifest's slot table wins when the build supplied one: an SNG
        // tone event is only an index into it.
        var slots = context.ToneSlots ?? RsToneSlots.Resolve(arrangement).Slots;
        var slotLookup = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < slots.Count; i++) slotLookup.TryAdd(slots[i], i);

        foreach (var change in arrangement.ToneChanges.OrderBy(t => t.Position))
        {
            if (!slotLookup.TryGetValue(change.ToneName, out var toneId)) continue;
            sng.Tones.Add(new RsTone(MsToSec(MsInt(change.Position.GetPositionMs(context.Beats))), toneId));
        }
    }

    /// <summary>Time-signature events derived from the beat map (one per
    /// change), matching what the DTRS XML bundle already emits
    /// (RsXmlSongWriter.WriteEvents) so the SNG and XML agree.</summary>
    private static void AddTimeSignatureEvents(BuildContext context, Action<int, string> add)
    {
        int? previousNumerator = null;
        int? previousDenominator = null;
        foreach (var beat in context.Beats)
        {
            var numerator = beat.BeatsInMeasure > 0 ? beat.BeatsInMeasure : 4;
            var denominator = beat.NoteDenominator > 0 ? beat.NoteDenominator : 4;
            if (previousNumerator == numerator && previousDenominator == denominator)
                continue;

            add(MsInt(beat.Time), $"TS:{numerator}/{denominator}");
            previousNumerator = numerator;
            previousDenominator = denominator;
        }
    }

    // Reference: Rocksmith2014.NET CrowdEventAdder.fs.
    private const int IntroCrowdReactionDelayMs = 600;
    private const int IntroApplauseLengthMs = 2_500;
    private const int OutroApplauseLengthMs = 4_000;
    private const int VenueFadeOutLengthMs = 5_000;

    /// <summary>Crowd events the game expects in every arrangement; official
    /// content and DLC Builder both ship them. Authored events win — each
    /// group is only synthesized when its code is absent.</summary>
    private static void AddCrowdEvents(
        BuildContext context, List<(int TimeMs, string Code)> existing, Action<int, string> add)
    {
        var startBeatMs = MsInt(context.Beats[0].Time);

        if (!existing.Any(e => e.Code is "e0" or "e1" or "e2"))
        {
            var tempo = AverageTempoBpm(context.Beats);
            add(startBeatMs, tempo < 90f ? "e0" : tempo < 170f ? "e1" : "e2");
        }

        if (existing.All(e => e.Code != "E3"))
        {
            var introStartMs = FirstContentTimeMs(context) is { } firstNoteMs
                ? firstNoteMs + IntroCrowdReactionDelayMs
                : startBeatMs;
            add(introStartMs, "E3");
            add(introStartMs + IntroApplauseLengthMs, "E13");
        }

        if (existing.All(e => e.Code != "D3"))
        {
            add(Math.Max(0, context.SongLengthMs - VenueFadeOutLengthMs - OutroApplauseLengthMs), "D3");
            add(context.SongLengthMs, "E13");
        }
    }

    private static int? FirstContentTimeMs(BuildContext context)
    {
        var min = double.MaxValue;
        foreach (var level in context.OrderedLevels)
        {
            foreach (var note in level.Notes)
                min = Math.Min(min, note.Position.GetPositionMs(context.Beats));
            foreach (var chord in level.Chords)
                min = Math.Min(min, chord.Position.GetPositionMs(context.Beats));
        }

        return min == double.MaxValue ? null : MsInt(min);
    }

    /// <summary>End of the last content in any level — note/chord-note
    /// sustains and handshape ends included.</summary>
    private static int? LastContentEndMs(Arrangement arrangement, IList<BeatMarker> beats)
    {
        var max = double.MinValue;
        foreach (var level in arrangement.Levels)
        {
            foreach (var note in level.Notes)
            {
                max = Math.Max(max,
                    note.Position.GetPositionMs(beats) + Math.Max(0, SustainMs(note.Position, note.Sustain, beats)));
            }

            foreach (var chord in level.Chords)
            {
                var chordMs = chord.Position.GetPositionMs(beats);
                max = Math.Max(max, chordMs);
                foreach (var chordNote in chord.ChordNotes)
                {
                    max = Math.Max(max, chordMs + Math.Max(0,
                        SustainMsFromDelta(MsInt(chordMs), chordNote.Sustain, chord.Position, beats)));
                }
            }

            foreach (var handShape in level.HandShapes)
                max = Math.Max(max, handShape.EndPosition.GetPositionMs(beats));
        }

        return max == double.MinValue ? null : MsInt(max);
    }

    private static float AverageTempoBpm(IList<BeatMarker> beats)
    {
        if (beats.Count < 2)
            return beats.Count == 1 && beats[0].Bpm > 0 ? (float)beats[0].Bpm : 120f;

        var spanMs = beats[^1].Time - beats[0].Time;
        return spanMs <= 0 ? 120f : (float)((beats.Count - 1) * 60_000.0 / spanMs);
    }

    // ---- sections ----------------------------------------------------------

    private static List<SectionInfo> ExtractSections(
        Arrangement arrangement, IList<BeatMarker> beats, List<IterationInfo> iterations, int songLengthMs)
    {
        var raw = arrangement.EventPoints
            .Where(ep => !string.IsNullOrEmpty(ep.Section))
            .OrderBy(ep => ep.Position)
            .Select(ep => (name: ep.Section, timeMs: MsInt(ep.Position.GetPositionMs(beats))))
            .ToList();

        if (raw.Count == 0)
        {
            var startMs = iterations.Count > 0 ? iterations[0].StartMs : 0;
            raw = [("riff", startMs)];
        }

        // EOF's closing noguitar section, at the same spot as the RS-XML writer's.
        if (EndNoGuitarSection.StartMs(arrangement, beats) is { } noGuitarMs)
            raw = [.. raw.Append((name: EndNoGuitarSection.Name, timeMs: MsInt(noGuitarMs))).OrderBy(r => r.timeMs)];

        var numberByName = new Dictionary<string, int>(StringComparer.Ordinal);
        var result = new List<SectionInfo>(raw.Count);
        for (var i = 0; i < raw.Count; i++)
        {
            var endMs = i + 1 < raw.Count ? raw[i + 1].timeMs : songLengthMs;
            var number = numberByName.TryGetValue(raw[i].name, out var n) ? n + 1 : 1;
            numberByName[raw[i].name] = number;
            result.Add(new SectionInfo(raw[i].name, number, raw[i].timeMs, endMs));
        }

        return result;
    }

    private static void BuildSections(BuildContext context, RocksmithSng sng)
    {
        // "For whatever reason, the string masks from a section need to be
        // included in the section before it." — ConvertInstrumental.fs.
        var masks = context.StringMasks;
        var levelCount = context.OrderedLevels.Count;
        for (var s = 0; s < masks.Length - 1; s++)
        {
            for (var d = 0; d < levelCount; d++)
                masks[s][d] |= masks[s + 1][d];
        }

        for (var i = 0; i < context.Sections.Count; i++)
        {
            var section = context.Sections[i];
            var startPi = FindPhraseIterationId(context, section.StartMs);

            var endPi = startPi + 1;
            while (endPi < context.Iterations.Count && context.Iterations[endPi].StartMs < section.EndMs)
                endPi++;
            endPi--;

            sng.Sections.Add(new RsSection(
                section.Name,
                section.Number,
                MsToSec(section.StartMs),
                MsToSec(section.EndMs),
                startPi,
                endPi)
            {
                StringMask = masks[i]
            });
        }
    }

    // ---- beats -------------------------------------------------------------

    /// <summary>
    ///     Numbers measures from ONE, which is what every other spelling of this
    ///     chart already does.
    ///     <para>
    ///         It must not start at <c>-1</c> and increment on the first beat: bar
    ///         1 would ship as <c>Measure = 0</c> — and since the mask sets
    ///         <c>EvenMeasure</c> on <c>currentMeasure % 2 == 0</c>, the PARITY would be
    ///         inverted for the whole song. Worse, the psarc's SNG would then disagree by
    ///         one with the RS-XML bundle the same save writes:
    ///         <c>RsXmlSongWriter.WriteEbeats</c> counts from 1, all 98
    ///         hand-corrected exports in <c>TestData/EOF</c> open at
    ///         <c>measure="1"</c>, and the reference converter does not count at all
    ///         — <c>Rocksmith2014.Conversion/XmlToSng.fs</c> copies the XML's own
    ///         number straight through.
    ///     </para>
    ///     <para>
    ///         <c>0</c> therefore means "no measure has started yet", which is what
    ///         gives an opening beat with no measure marker a bar of its own.
    ///     </para>
    /// </summary>
    private static void BuildBeats(BuildContext context, RocksmithSng sng)
    {
        short beatCounter = 0;
        short currentMeasure = 0;

        foreach (var beat in context.Beats)
        {
            var timeMs = MsInt(beat.Time);
            if (beat.IsMeasure || currentMeasure == 0)
            {
                currentMeasure++;
                beatCounter = 0;
            }
            else
            {
                beatCounter++;
            }

            var mask = 0u;
            if (beatCounter == 0)
            {
                mask |= 0x1; // FirstBeatOfMeasure
                if (currentMeasure % 2 == 0) mask |= 0x2; // EvenMeasure
            }

            sng.Beats.Add(new RsBeat(
                MsToSec(timeMs),
                currentMeasure,
                beatCounter,
                mask,
                FindBeatPhraseIterationId(context, timeMs)));
        }
    }

    // ---- levels ------------------------------------------------------------

    private static void BuildLevels(BuildContext context, RocksmithSng sng)
    {
        for (var difficulty = 0; difficulty < context.OrderedLevels.Count; difficulty++)
            sng.Levels.Add(ConvertLevel(context, sng, context.OrderedLevels[difficulty], difficulty));
    }

    private sealed record LevelEntity(
        int TimeMs,
        int SustainMs,
        GuitarNote? Note,
        Chord? Chord);

    private static RsLevel ConvertLevel(BuildContext context, RocksmithSng sng, ChartLevel level, int difficulty)
    {
        var beats = context.Beats;

        // A chord a LinkNext sound lands on ships as loose single notes — the
        // SAME projection the RS-XML writer consumes, for the same reason: RS
        // reads a chord as a STRUM and a shape slid into was never re-picked.
        // The SNG is what the GAME reads, so leaving it out of this path meant
        // the `.psarc` shipped exactly the spelling the XML refuses to write.
        // Pure: the domain collections are never touched, and the templates and
        // hand shapes below still come from the level itself so the shape keeps
        // displaying (EOF_SRC/rs.c:2010 does it the same way).
        var (projectedNotes, projectedChords) = LinkLandingBreakup.Project(level);

        var entities = projectedNotes
            .Select(n => new LevelEntity(
                MsInt(n.Position.GetPositionMs(beats)),
                SustainMs(n.Position, n.Sustain, beats),
                n,
                null))
            .Concat(projectedChords
                // A chord with no template and no chord notes would be written
                // with ChordId -1 — an out-of-bounds template index the game
                // crashes on. Nothing playable is lost by dropping it.
                .Where(c => context.ChordIdOverrides.ContainsKey(c) ||
                            context.TemplateIndexById.ContainsKey(c.TemplateId))
                .Select(c =>
                {
                    var firstNote = c.ChordNotes.OrderBy(cn => cn.StringIndex).FirstOrDefault();
                    var sustain = firstNote != null ? SustainMs(c.Position, firstNote.Sustain, beats) : 0;
                    return new LevelEntity(MsInt(c.Position.GetPositionMs(beats)), sustain, null, c);
                }))
            .OrderBy(e => e.TimeMs)
            .ToList();

        var noteTimes = entities.Select(e => e.TimeMs).ToArray();

        // Handshapes → fingerprints, partitioned into regular and arpeggio.
        var handShapes = new List<RsFingerPrint>();
        var arpeggios = new List<RsFingerPrint>();
        foreach (var hs in level.HandShapes.OrderBy(h => h.Position))
        {
            var startMs = MsInt(hs.Position.GetPositionMs(beats));
            var endMs = MsInt(hs.EndPosition.GetPositionMs(beats));
            var fingerPrint = ConvertHandShape(hs, startMs, endMs, noteTimes, entities, context);

            var isArpeggio = context.TemplateIndexById.TryGetValue(hs.TemplateId, out var templateIndex) &&
                             (context.Templates[templateIndex].Mask & 0x1) != 0;
            (isArpeggio ? arpeggios : handShapes).Add(fingerPrint);
        }

        RsFingerPrint[][] fingerPrints = [handShapes.ToArray(), arpeggios.ToArray()];

        var anchors = level.Anchors.OrderBy(a => a.Position).ToList();
        if (anchors.Count == 0 && entities.Count > 0)
        {
            // A level with notes but no FHP anchors writes an empty SNG anchor
            // array, which the game crashes on (auto-charts and drafts
            // routinely have none). Cover the level with one anchor at the
            // lowest fretted position; all-open content anchors at fret 1.
            var lowestFret = entities
                .SelectMany(e => e.Note is { } n
                    ? Enumerable.Repeat(n.Fret, 1)
                    : e.Chord!.ChordNotes.Select(cn => cn.Fret))
                .Where(f => f > 0)
                .DefaultIfEmpty(1)
                .Min();
            anchors.Add(new FretAnchor(
                TimelineMath.PositionFromMs(entities[0].TimeMs, beats),
                Math.Clamp(lowestFret, 1, 21), 4));
        }

        var anchorTimes = anchors.Select(a => MsInt(a.Position.GetPositionMs(beats))).ToArray();

        var result = new RsLevel { Difficulty = difficulty };

        // Link-next chains: string → (parent end ms, parent note index).
        var pendingLinkNexts = new (int EndMs, short Id)[Rs7StringLayout.MaxStrings];
        Array.Fill(pendingLinkNexts, (int.MinValue, (short)-1));
        RsNote? previousNote = null;

        for (var index = 0; index < entities.Count; index++)
        {
            var entity = entities[index];
            var note = ConvertEntity(
                context, sng, entity, index, noteTimes, fingerPrints, anchors, anchorTimes,
                pendingLinkNexts, previousNote, difficulty, result);
            previousNote = note;
            result.Notes.Add(note);
        }

        // Anchors are converted after the notes because their first/last note
        // times reference the converted notes. Reference: XmlToSngLevel.fs.
        for (var i = 0; i < anchors.Count; i++)
            result.Anchors.Add(ConvertAnchor(context, i, anchors, anchorTimes, noteTimes, result.Notes));

        result.HandShapes.AddRange(handShapes);
        result.Arpeggios.AddRange(arpeggios);

        var allCounts = context.NotesInIterationsAll[difficulty];
        result.NotesInPhraseIterationsAll = allCounts.ToArray();
        result.NotesInPhraseIterationsExclIgnored = context.NotesInIterationsExclIgnored[difficulty].ToArray();

        // Average notes per iteration, indexed by phrase.
        var averages = new float[sng.Phrases.Count];
        for (var phraseId = 0; phraseId < sng.Phrases.Count; phraseId++)
        {
            var counts = new List<int>();
            for (var pi = 0; pi < context.Iterations.Count; pi++)
            {
                if (context.Iterations[pi].PhraseId == phraseId)
                    counts.Add(allCounts[pi]);
            }

            averages[phraseId] = counts.Count > 0 ? (float)counts.Average() : 0f;
        }

        result.AverageNotesPerIteration = averages;
        return result;
    }

    private static RsFingerPrint ConvertHandShape(
        HandShape hs, int startMs, int endMs, int[] noteTimes, List<LevelEntity> entities, BuildContext context)
    {
        float firstNoteTime = -1f, lastNoteTime = -1f;
        if (FindFirstAndLastInRange(noteTimes, startMs, endMs, out var first, out var last))
        {
            firstNoteTime = MsToSec(noteTimes[first]);
            var t = noteTimes[last] + entities[last].SustainMs;
            lastNoteTime = t >= endMs ? -1f : MsToSec(t);
        }

        var chordId = context.TemplateIndexById.TryGetValue(hs.TemplateId, out var index) ? index : -1;
        return new RsFingerPrint(chordId, MsToSec(startMs), MsToSec(endMs), firstNoteTime, lastNoteTime);
    }

    private static RsNote ConvertEntity(
        BuildContext context,
        RocksmithSng sng,
        LevelEntity entity,
        int index,
        int[] noteTimes,
        RsFingerPrint[][] fingerPrints,
        List<FretAnchor> anchors,
        int[] anchorTimes,
        (int EndMs, short Id)[] pendingLinkNexts,
        RsNote? previousNote,
        int difficulty,
        RsLevel level)
    {
        var timeMs = entity.TimeMs;
        var timeSec = MsToSec(timeMs);

        var piId = FindPhraseIterationId(context, timeMs);
        var iteration = context.Iterations[piId];
        var sectionId = FindSectionId(context, timeMs);
        var (anchorFret, anchorWidth) = FindAnchor(anchors, anchorTimes, timeMs);

        var thisId = (short)index;
        var previous = index == 0 || noteTimes[index - 1] < iteration.StartMs ? (short)-1 : (short)(thisId - 1);
        var next = index == noteTimes.Length - 1 || noteTimes[index + 1] >= context.PiTimes[piId + 1]
            ? (short)-1
            : (short)(thisId + 1);

        var fingerPrintId0 = (short)FindFingerPrintId(timeSec, fingerPrints[0]);
        var fingerPrintId1 = (short)FindFingerPrintId(timeSec, fingerPrints[1]);
        var isArpeggio = fingerPrintId1 != -1;

        RsNote initial;
        short parentPrev = -1;

        if (entity.Note is { } note)
        {
            // Past the arrangement's strings is a last resort only: the validator
            // makes it an Error (SngStringLimits).
            var stringIndex = Math.Clamp(note.StringIndex, 0, SngStringLimits.MaxNoteStringIndex(context.Arrangement));

            var (parentEndMs, parentId) = pendingLinkNexts[stringIndex];
            pendingLinkNexts[stringIndex] = (int.MinValue, -1);
            // A child note must start at the end of its parent's sustain.
            if (parentId != -1 && timeMs - parentEndMs <= 2)
                parentPrev = parentId;

            var sustainMs = entity.SustainMs;
            if (note.LinkNext)
                pendingLinkNexts[stringIndex] = (timeMs + sustainMs, thisId);

            var bendValues = note.BendValues
                .OrderBy(b => b.Position)
                .Take(32) // more than 32 bend values will crash the game
                .Select(b => new RsBendValue(MsToSec(MsInt(b.Position.GetPositionMs(context.Beats))), (float)b.Step))
                .ToList();

            var (slideTo, slideUnpitchTo) = ResolveSlides(note.SlideTo, note.UnpitchedSlide);

            var mask = CreateMaskForNote(note, parentPrev, isArpeggio, sustainMs, bendValues.Count, slideTo,
                slideUnpitchTo);

            // Pitched slides get an anchor extension at the slide target.
            if (slideTo != -1)
                level.AnchorExtensions.Add(new RsAnchorExtension(MsToSec(timeMs + sustainMs), slideTo));

            UpdateStringMask(context, sectionId, difficulty, stringIndex);

            initial = new RsNote
            {
                Mask = mask,
                Time = timeSec,
                StringIndex = (sbyte)stringIndex,
                Fret = (sbyte)Math.Clamp(note.Fret, 0, 127),
                AnchorFret = anchorFret,
                AnchorWidth = anchorWidth,
                ChordId = -1,
                ChordNotesId = -1,
                PhraseId = iteration.PhraseId,
                PhraseIterationId = piId,
                FingerPrintId0 = fingerPrintId0,
                FingerPrintId1 = fingerPrintId1,
                SlideTo = slideTo,
                SlideUnpitchTo = slideUnpitchTo,
                LeftHand = (sbyte)Math.Clamp(note.LeftHandFinger, -1, 4),
                Tap = note.Hopo == HopoType.Tap ? (sbyte)1 : (sbyte)-1,
                PickDirection = note.PickDirection == PickDirectionType.Up ? (sbyte)1 : (sbyte)0,
                Slap = note.BassPicking == BassPickingType.Slap ? (sbyte)1 : (sbyte)-1,
                Pluck = note.BassPicking == BassPickingType.Pop ? (sbyte)1 : (sbyte)-1,
                Vibrato = (short)(note.Vibrato ? Math.Max(note.VibratoStrength, 80) : 0),
                Sustain = MsToSec(sustainMs),
                MaxBend = bendValues.Count > 0 ? bendValues.Max(b => b.Step) : 0f,
                BendData = bendValues
            };
        }
        else
        {
            var chord = entity.Chord!;
            var chordId = ChordIdOf(context, chord);
            // A compact repeat ships with no chord notes at all: that absence
            // IS how the game knows not to draw the full panel and its chord
            // name (RsChordDensity). The XML writer makes the same call on the
            // same chart, so a psarc built here and one built by DLC Builder
            // from our XML now agree.
            var hasChordNotes = chord.ChordNotes.Count > 0 && !chord.ForceNoNotes &&
                                !context.CompactRepeats.Contains(chord);

            var chordNotesId = -1;
            if (hasChordNotes)
            {
                // See the note-path comment: out-of-range strings are refused by
                // the export validator, never silently shipped.
                foreach (var chordNote in chord.ChordNotes)
                {
                    UpdateStringMask(context, sectionId, difficulty,
                        Math.Clamp(chordNote.StringIndex, 0, SngStringLimits.MaxNoteStringIndex(context.Arrangement)));
                }

                chordNotesId = CreateChordNotes(context, sng, chord, thisId, timeMs, pendingLinkNexts);
            }

            // A chord's SNG sustain comes from its chord notes and from nothing
            // else — XmlToSngNote.fs reads `chord.ChordNotes[0].Sustain` inside
            // `if chord.HasChordNotes`, and 0 otherwise. A chord that ships
            // WITHOUT chord notes (a compact repeat, or ForceNoNotes) therefore
            // has no sustain to state: the `<chord>` tag has no sustain attribute
            // at all, so the RS-XML we write beside this psarc says nothing, and
            // DLC Builder building from that XML computes 0. Carrying the
            // dropped children's length into the SNG anyway made our psarc and
            // one built from our own XML disagree about how long the same strum
            // rings, and set NoteMask.Sustain on a chord the game draws as a
            // bare repeat placeholder.
            var sustainMs = hasChordNotes ? entity.SustainMs : 0;
            var template = chordId >= 0 ? context.Templates[chordId] : null;
            var mask = CreateMaskForChord(chord, template, sustainMs, chordNotesId, hasChordNotes, isArpeggio);
            mask = context.Rs7Block?.WithDoubleStop(mask, template, chordId) ?? mask;

            initial = new RsNote
            {
                Mask = mask,
                Time = timeSec,
                StringIndex = -1,
                Fret = -1,
                AnchorFret = anchorFret,
                AnchorWidth = anchorWidth,
                ChordId = chordId,
                ChordNotesId = chordNotesId,
                PhraseId = iteration.PhraseId,
                PhraseIterationId = piId,
                FingerPrintId0 = fingerPrintId0,
                FingerPrintId1 = fingerPrintId1,
                SlideTo = -1,
                SlideUnpitchTo = -1,
                LeftHand = -1,
                Tap = -1,
                PickDirection = chord.Strum switch
                {
                    PickDirectionType.Up => 1,
                    PickDirectionType.Down => 0,
                    _ => -1
                },
                Slap = -1,
                Pluck = -1,
                Vibrato = 0,
                Sustain = MsToSec(sustainMs),
                MaxBend = 0f,
                BendData = []
            };
        }

        var isIgnore = (initial.Mask & RsNoteMask.Ignore) != 0;
        AddNoteToCounts(context, piId, difficulty, iteration, isIgnore);

        var flags = ComputeNoteFlags(previousNote, initial);

        return new RsNote
        {
            Mask = initial.Mask,
            Flags = flags,
            Hash = HashNote(initial),
            Time = initial.Time,
            StringIndex = initial.StringIndex,
            Fret = initial.Fret,
            AnchorFret = initial.AnchorFret,
            AnchorWidth = initial.AnchorWidth,
            ChordId = initial.ChordId,
            ChordNotesId = initial.ChordNotesId,
            PhraseId = initial.PhraseId,
            PhraseIterationId = initial.PhraseIterationId,
            FingerPrintId0 = initial.FingerPrintId0,
            FingerPrintId1 = initial.FingerPrintId1,
            NextIterNote = next,
            PrevIterNote = previous,
            ParentPrevNote = parentPrev,
            SlideTo = initial.SlideTo,
            SlideUnpitchTo = initial.SlideUnpitchTo,
            LeftHand = initial.LeftHand,
            Tap = initial.Tap,
            PickDirection = initial.PickDirection,
            Slap = initial.Slap,
            Pluck = initial.Pluck,
            Vibrato = initial.Vibrato,
            Sustain = initial.Sustain,
            MaxBend = initial.MaxBend,
            BendData = initial.BendData
        };
    }

    private static (sbyte SlideTo, sbyte SlideUnpitchTo) ResolveSlides(int? slideTo, bool unpitched)
    {
        if (!slideTo.HasValue) return (-1, -1);
        var target = (sbyte)Math.Clamp(slideTo.Value, 0, 127);
        return unpitched ? ((sbyte)-1, target) : (target, (sbyte)-1);
    }

    /// <summary>SNG note mask for a single note.
    /// Reference: XmlToSngNote.fs createMaskForNote.</summary>
    private static uint CreateMaskForNote(
        GuitarNote note, short parentPrev, bool isArpeggio, int sustainMs, int bendCount,
        sbyte slideTo, sbyte slideUnpitchTo)
    {
        var mask = RsNoteMask.Single;
        if (note.Fret == 0) mask |= RsNoteMask.Open;
        if (sustainMs > 0) mask |= RsNoteMask.Sustain;
        if (slideTo != -1) mask |= RsNoteMask.Slide;
        if (slideUnpitchTo != -1) mask |= RsNoteMask.UnpitchedSlide;
        if (note.Hopo == HopoType.Tap) mask |= RsNoteMask.Tap;
        if (note.Vibrato || note.VibratoStrength > 0) mask |= RsNoteMask.Vibrato;
        if (bendCount > 0) mask |= RsNoteMask.Bend;
        if (parentPrev != -1) mask |= RsNoteMask.Child;
        if (isArpeggio) mask |= RsNoteMask.Arpeggio;

        if (note.LinkNext) mask |= RsNoteMask.Parent;
        if (note.Accent) mask |= RsNoteMask.Accent;
        if (note.Tremolo) mask |= RsNoteMask.Tremolo;
        if (note.Mute == MuteType.Full) mask |= RsNoteMask.Mute;
        if (note.Mute == MuteType.Palm) mask |= RsNoteMask.PalmMute;
        if (note.Hopo == HopoType.HammerOn) mask |= RsNoteMask.HammerOn;
        if (note.Hopo == HopoType.PullOff) mask |= RsNoteMask.PullOff;
        if (note.Harmonic == HarmonicType.Normal) mask |= RsNoteMask.Harmonic;
        if (note.Harmonic == HarmonicType.Pinch) mask |= RsNoteMask.PinchHarmonic;
        if (note.Ignore) mask |= RsNoteMask.Ignore;
        if (note.BassPicking == BassPickingType.Pop) mask |= RsNoteMask.Pluck;
        if (note.BassPicking == BassPickingType.Slap) mask |= RsNoteMask.Slap;

        return mask;
    }

    /// <summary>SNG note mask for a chord.
    /// Reference: XmlToSngNote.fs createMaskForChord.</summary>
    private static uint CreateMaskForChord(
        Chord chord, RsChordTemplate? template, int sustainMs, int chordNotesId, bool hasChordNotes, bool isArpeggio)
    {
        var mask = RsNoteMask.Chord;
        if (template != null && template.Frets.Count(f => f != -1) == 2) mask |= RsNoteMask.DoubleStop;
        if (hasChordNotes) mask |= RsNoteMask.ChordPanel;
        if (template != null && (template.Mask & 0x1) != 0) mask |= RsNoteMask.Arpeggio;
        if (sustainMs > 0) mask |= RsNoteMask.Sustain;
        if (chordNotesId != -1) mask |= RsNoteMask.ChordNotes;
        if (isArpeggio) mask |= RsNoteMask.Arpeggio;

        if (chord.Accent) mask |= RsNoteMask.Accent;
        if (chord.IsFullyMuted()) mask |= RsNoteMask.FretHandMute;
        if (chord.Ignore) mask |= RsNoteMask.Ignore;
        if (chord.HasLinkNext()) mask |= RsNoteMask.Parent;
        if (chord.IsPalmMuted()) mask |= RsNoteMask.PalmMute;

        return mask;
    }

    /// <summary>SNG note mask for a chord note (no Single/Ignore/Child bits).
    /// Reference: XmlToSngNote.fs createMaskForChordNote.</summary>
    private static uint CreateMaskForChordNote(ChordNote note, int sustainMs)
    {
        var mask = 0u;
        if (note.Fret == 0) mask |= RsNoteMask.Open;
        if (sustainMs > 0) mask |= RsNoteMask.Sustain;
        if (note.SlideTo.HasValue && !note.UnpitchedSlide) mask |= RsNoteMask.Slide;
        if (note.SlideTo.HasValue && note.UnpitchedSlide) mask |= RsNoteMask.UnpitchedSlide;
        if (note.Vibrato || note.VibratoStrength > 0) mask |= RsNoteMask.Vibrato;
        if (note.BendValues.Count > 0) mask |= RsNoteMask.Bend;
        if (note.Hopo == HopoType.Tap) mask |= RsNoteMask.Tap;

        if (note.LinkNext) mask |= RsNoteMask.Parent;
        // Accent IS modeled per chord note (ChordNote : IGuitarSound) — the rscp
        // and XML writers both emit it, so dropping it here made an accent
        // survive a save but vanish from the exported psarc.
        if (note.Accent) mask |= RsNoteMask.Accent;
        if (note.Tremolo) mask |= RsNoteMask.Tremolo;
        if (note.Mute == MuteType.Full) mask |= RsNoteMask.Mute;
        if (note.Mute == MuteType.Palm) mask |= RsNoteMask.PalmMute;
        if (note.Hopo == HopoType.HammerOn) mask |= RsNoteMask.HammerOn;
        if (note.Hopo == HopoType.PullOff) mask |= RsNoteMask.PullOff;
        if (note.Harmonic == HarmonicType.Normal) mask |= RsNoteMask.Harmonic;
        if (note.Harmonic == HarmonicType.Pinch) mask |= RsNoteMask.PinchHarmonic;
        if (note.BassPicking == BassPickingType.Pop) mask |= RsNoteMask.Pluck;
        if (note.BassPicking == BassPickingType.Slap) mask |= RsNoteMask.Slap;

        return mask;
    }

    /// <summary>Creates (or reuses) the deduplicated chord-notes record.
    /// Reference: XmlToSngNote.fs createChordNotes.</summary>
    private static int CreateChordNotes(
        BuildContext context,
        RocksmithSng sng,
        Chord chord,
        short thisId,
        int chordTimeMs,
        (int EndMs, short Id)[] pendingLinkNexts)
    {
        var chordNotes = new RsChordNotes();
        var extended = context.Rs7Block?.NewChordNotesRow();
        var anyMask = false;

        Array.Fill(chordNotes.SlideTo, (sbyte)-1);
        Array.Fill(chordNotes.SlideUnpitchTo, (sbyte)-1);

        foreach (var note in chord.ChordNotes)
        {
            // See the note-path comment: out-of-range strings are refused by the export
            // validator, never silently shipped. Strings past the sixth go to RS7X.
            var stringIndex = Math.Clamp(note.StringIndex, 0, SngStringLimits.MaxNoteStringIndex(context.Arrangement));
            var sustainMs = SustainMsFromDelta(chordTimeMs, note.Sustain, chord.Position, context.Beats);

            var mask = CreateMaskForChordNote(note, sustainMs);
            var (slideTo, slideUnpitchTo) = ResolveSlides(note.SlideTo, note.UnpitchedSlide);
            var vibrato = (short)(note.Vibrato ? Math.Max(note.VibratoStrength, 80) : 0);
            var bends = note.BendValues.OrderBy(b => b.Position).Take(32).Select(bend =>
                new RsBendValue(MsToSec(MsInt(bend.Position.GetPositionMs(context.Beats))), (float)bend.Step)).ToList();
            if (stringIndex > SngStringLimits.MaxStringIndex)
            {
                anyMask |= context.Rs7Block!.Set(extended!, stringIndex, mask, slideTo, slideUnpitchTo, vibrato, bends);
            }
            else
            {
                chordNotes.Mask[stringIndex] = mask;
                if (mask != 0) anyMask = true;
                chordNotes.SlideTo[stringIndex] = slideTo;
                chordNotes.SlideUnpitchTo[stringIndex] = slideUnpitchTo;
                chordNotes.Vibrato[stringIndex] = vibrato;
                chordNotes.BendData[stringIndex].AddRange(bends);
            }

            if (note.LinkNext)
                pendingLinkNexts[stringIndex] = (chordTimeMs + sustainMs, thisId);
        }

        if (!anyMask) return -1;

        var key = ChordNotesComparer.CreateKey(chordNotes) + Rs7BlockBuilder.Key(extended);
        if (context.ChordNotesIds.TryGetValue(key, out var existingId))
            return existingId;

        var id = sng.ChordNotes.Count;
        sng.ChordNotes.Add(chordNotes);
        context.Rs7Block?.AddChordNotes(extended);
        context.ChordNotesIds[key] = id;
        return id;
    }

    /// <summary>Structural dedupe key for chord notes, mirroring the F#
    /// custom equality on ChordNotes.</summary>
    private sealed class ChordNotesComparer : IEqualityComparer<string>
    {
        public static readonly ChordNotesComparer Instance = new();

        public static string CreateKey(RsChordNotes cn)
        {
            var builder = new System.Text.StringBuilder(128);
            for (var i = 0; i < 6; i++)
            {
                builder.Append(cn.Mask[i]).Append('|')
                    .Append(cn.SlideTo[i]).Append('|')
                    .Append(cn.SlideUnpitchTo[i]).Append('|')
                    .Append(cn.Vibrato[i]).Append('|');
                foreach (var bend in cn.BendData[i])
                {
                    builder.Append(bend.Time.ToString("R", CultureInfo.InvariantCulture)).Append(',')
                        .Append(bend.Step.ToString("R", CultureInfo.InvariantCulture)).Append(';');
                }

                builder.Append('#');
            }

            return builder.ToString();
        }

        public bool Equals(string? x, string? y) => string.Equals(x, y, StringComparison.Ordinal);

        public int GetHashCode(string obj) => obj.GetHashCode(StringComparison.Ordinal);
    }

    // ---- anchors -----------------------------------------------------------

    private static RsAnchor ConvertAnchor(
        BuildContext context,
        int index,
        List<FretAnchor> anchors,
        int[] anchorTimes,
        int[] noteTimes,
        List<RsNote> notes)
    {
        var startMs = anchorTimes[index];
        var endMs = index == anchors.Count - 1
            ? context.Iterations.Count > 0 ? context.Iterations[^1].StartMs : context.SongLengthMs
            : anchorTimes[index + 1];

        var startTime = MsToSec(startMs);
        float firstNoteTime, lastNoteTime;

        if (FindFirstAndLastInRange(noteTimes, startMs, endMs, out var firstIndex, out var lastIndex))
        {
            var firstNote = notes[firstIndex];
            var lastNote = notes[lastIndex];

            firstNoteTime = firstNote.Time;
            if (firstIndex > 0)
            {
                var prevNote = notes[firstIndex - 1];
                // If this anchor is at the end of a slide note, use the time
                // where the target note would be.
                if ((prevNote.Mask & RsNoteMask.Slide) != 0 &&
                    prevNote.Time + prevNote.Sustain - startTime < 0.001f)
                {
                    firstNoteTime = startTime;
                }
            }

            // The sustain of the last note is included, unless it slides or links.
            lastNoteTime = (lastNote.Mask & (RsNoteMask.Slide | RsNoteMask.Parent)) != 0
                ? lastNote.Time
                : lastNote.Time + lastNote.Sustain;
        }
        else
        {
            firstNoteTime = UninitFirstNote;
            lastNoteTime = UninitLastNote;
        }

        var anchor = anchors[index];
        return new RsAnchor(
            startTime,
            (sbyte)Math.Clamp(anchor.Fret, 0, 127),
            Math.Max(1, anchor.Width),
            MsToSec(endMs),
            firstNoteTime,
            lastNoteTime,
            FindPhraseIterationId(context, startMs));
    }

    // ---- metadata ----------------------------------------------------------

    private static void BuildMetaData(BuildContext context, RocksmithSng sng, DateTime conversionTime)
    {
        const double maxScore = 100_000.0;

        var firstNoteTime = float.MaxValue;
        foreach (var level in sng.Levels)
        {
            if (level.Notes.Count > 0 && level.Notes[0].Time < firstNoteTime)
                firstNoteTime = level.Notes[0].Time;
        }

        if (firstNoteTime >= float.MaxValue) firstNoteTime = 0f;

        var beats = context.Beats;
        var firstBeatLength = beats.Count > 1
            ? MsToSec(MsInt(beats[1].Time) - MsInt(beats[0].Time))
            : 0.5f;

        var tuning = new short[SngStringLimits.MaxNoteStringIndex(context.Arrangement) + 1];
        var tuningOffsets = TuningOffsetResolution.Resolve(context.Arrangement);
        for (var i = 0; i < Math.Min(tuning.Length, tuningOffsets.Count); i++)
            tuning[i] = (short)tuningOffsets[i];

        var capo = context.Arrangement.Capo;

        sng.MetaData = new RsMetaData
        {
            MaxScore = maxScore,
            MaxNotesAndChords = context.NoteCountHard,
            MaxNotesAndChordsReal = context.NoteCountHard - context.NoteCountIgnored,
            PointsPerNote = maxScore / Math.Max(1.0, context.NoteCountHard),
            FirstBeatLength = firstBeatLength,
            StartTime = MsToSec(MsInt(beats[0].Time)),
            CapoFretId = capo <= 0 ? (sbyte)-1 : (sbyte)capo,
            LastConversionDateTime = conversionTime.ToString("MM-d-yy HH:mm", CultureInfo.InvariantCulture),
            Part = 1,
            SongLength = MsToSec(context.SongLengthMs),
            Tuning = tuning,
            FirstNoteTime = firstNoteTime,
            MaxDifficulty = context.MaxDifficulty
        };
    }

    // ---- shared helpers ----------------------------------------------------

    private static double ResolveSongLengthMs(Arrangement arrangement, IList<BeatMarker> beats, double songLengthMs)
    {
        if (songLengthMs > 0) return songLengthMs;

        var last = beats.Count > 0 ? beats[^1].Time : 0.0;
        foreach (var level in arrangement.Levels)
        {
            foreach (var note in level.Notes)
                last = Math.Max(last, note.EndPosition.GetPositionMs(beats));
            foreach (var chord in level.Chords)
                last = Math.Max(last, chord.EndPosition.GetPositionMs(beats));
        }

        return last;
    }

    private static void AddNoteToCounts(
        BuildContext context, int piId, int difficulty, IterationInfo iteration, bool ignored)
    {
        context.NotesInIterationsAll[difficulty][piId]++;
        if (!ignored)
            context.NotesInIterationsExclIgnored[difficulty][piId]++;

        if (iteration.Easy == difficulty) context.NoteCountEasy++;
        if (iteration.Medium == difficulty) context.NoteCountMedium++;
        if (iteration.Hard == difficulty)
        {
            context.NoteCountHard++;
            if (ignored) context.NoteCountIgnored++;
        }
    }

    /// <summary>Note numbering: a note is numbered when the anchor changes
    /// (never open notes). Reference: NoteFlagFunctions.fs onAnchorChange.</summary>
    private static uint ComputeNoteFlags(RsNote? previousNote, RsNote currentNote)
    {
        if (currentNote.Fret == 0) return 0u;
        if (previousNote == null) return 1u;
        return previousNote.AnchorFret != currentNote.AnchorFret ? 1u : 0u;
    }

    /// <summary>Deterministic per-note hash over the note's serialized form
    /// with hash/flags/link indices zeroed (the F# reference hashes the
    /// equivalent record structurally). FNV-1a.</summary>
    private static uint HashNote(RsNote note)
    {
        var hash = 2166136261u;

        void Add(uint value)
        {
            for (var i = 0; i < 4; i++)
            {
                hash = (hash ^ ((value >> (i * 8)) & 0xFF)) * 16777619u;
            }
        }

        void AddFloat(float value) => Add(BitConverter.SingleToUInt32Bits(value));

        Add(note.Mask);
        AddFloat(note.Time);
        Add((byte)note.StringIndex);
        Add((byte)note.Fret);
        Add((byte)note.AnchorFret);
        Add((byte)note.AnchorWidth);
        Add((uint)note.ChordId);
        Add((uint)note.ChordNotesId);
        Add((uint)note.PhraseId);
        Add((uint)note.PhraseIterationId);
        Add((ushort)note.FingerPrintId0);
        Add((ushort)note.FingerPrintId1);
        Add((byte)note.SlideTo);
        Add((byte)note.SlideUnpitchTo);
        Add((byte)note.Tap);
        Add((byte)note.Slap);
        Add((byte)note.Pluck);
        Add((ushort)note.Vibrato);
        AddFloat(note.Sustain);
        AddFloat(note.MaxBend);
        foreach (var bend in note.BendData)
        {
            AddFloat(bend.Time);
            AddFloat(bend.Step);
        }

        return hash;
    }

    private static void UpdateStringMask(BuildContext context, int sectionId, int difficulty, int stringIndex)
    {
        if (difficulty >= 36) return;
        context.StringMasks[sectionId][difficulty] |= (sbyte)(1 << stringIndex);
    }

    /// <summary>Last iteration with StartTime &lt;= time (inclusive).
    /// Reference: Utils.fs findPhraseIterationId.</summary>
    private static int FindPhraseIterationId(BuildContext context, int timeMs)
    {
        var iterations = context.Iterations;
        var id = iterations.Count - 1;
        while (id > 0 && iterations[id].StartMs > timeMs) id--;
        return id;
    }

    /// <summary>Beats exactly on an iteration start belong to the previous
    /// iteration (exclusive). Reference: Utils.fs findBeatPhraseIterationId.</summary>
    private static int FindBeatPhraseIterationId(BuildContext context, int timeMs)
    {
        var iterations = context.Iterations;
        var id = iterations.Count - 1;
        while (id > 0 && iterations[id].StartMs >= timeMs) id--;
        return id;
    }

    private static int FindSectionId(BuildContext context, int timeMs)
    {
        var sections = context.Sections;
        var id = sections.Count - 1;
        while (id > 0 && sections[id].StartMs > timeMs) id--;
        return id;
    }

    private static (sbyte Fret, sbyte Width) FindAnchor(List<FretAnchor> anchors, int[] anchorTimes, int timeMs)
    {
        for (var i = anchors.Count - 1; i >= 0; i--)
        {
            if (anchorTimes[i] <= timeMs)
                return ((sbyte)Math.Clamp(anchors[i].Fret, 0, 127), (sbyte)Math.Max(1, anchors[i].Width));
        }

        // The chart has no anchor covering this note; the validator reports it,
        // the export falls back to a standard first-position anchor.
        return (1, 4);
    }

    private static int FindFingerPrintId(float timeSec, RsFingerPrint[] fingerPrints)
    {
        for (var i = 0; i < fingerPrints.Length; i++)
        {
            if (fingerPrints[i].StartTime > timeSec) return -1;
            if (timeSec >= fingerPrints[i].StartTime && timeSec < fingerPrints[i].EndTime) return i;
        }

        return -1;
    }

    /// <summary>Finds the first and last note indices within [startMs, endMs).
    /// Reference: Utils.fs findFirstAndLastTime.</summary>
    private static bool FindFirstAndLastInRange(
        int[] noteTimes, int startMs, int endMs, out int firstIndex, out int lastIndex)
    {
        firstIndex = -1;
        lastIndex = -1;

        for (var i = 0; i < noteTimes.Length; i++)
        {
            if (noteTimes[i] >= endMs) break;
            if (noteTimes[i] < startMs) continue;
            if (firstIndex == -1) firstIndex = i;
            lastIndex = i;
        }

        return firstIndex != -1;
    }

    private static int SustainMs(FractionalPosition position, FractionalPosition sustain, IList<BeatMarker> beats)
    {
        if (sustain <= FractionalPosition.Zero) return 0;
        var startMs = position.GetPositionMs(beats);
        var endMs = (position + sustain).GetPositionMs(beats);
        return Math.Max(0, MsInt(endMs) - MsInt(startMs));
    }

    private static int SustainMsFromDelta(
        int startMs, FractionalPosition sustain, FractionalPosition position, IList<BeatMarker> beats)
    {
        if (sustain <= FractionalPosition.Zero) return 0;
        var endMs = MsInt((position + sustain).GetPositionMs(beats));
        return Math.Max(0, endMs - startMs);
    }

    private static int MsInt(double ms) => TimelineMath.ToWholeMs(ms);

    private static float MsToSec(int ms) => ms / 1000.0f;

    private static float MsToSec(double ms) => (float)(ms / 1000.0);
}
