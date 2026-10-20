using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using DotCharter.Services.Export.Rocksmith;
using DotCharter.Services.Export.Sng.Model;

namespace DotCharter.Services.Export.Package;

/// <summary>
/// Generates the per-arrangement manifest JSON and the aggregate .hsan header
/// for a Rocksmith 2014 package.
/// Ported from Rocksmith2014.NET AttributesCreation.fs / Manifest.fs
/// (iteration version 2, PC).
/// </summary>
internal static class RsManifestGenerator
{
    /// <summary>Rocksmith's own default scroll speed, used when an arrangement
    /// carries none. Matches DLC Builder.</summary>
    private const double DefaultScrollSpeed = 1.3;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>Creates the manifest JSON for a single arrangement
    /// ("manifests/songs_dlc_{key}/{key}_{part}.json").</summary>
    public static string CreateArrangementManifest(RsPackageMetadata meta, RsPackageArrangement arrangement)
    {
        var attributes = arrangement.IsVocals
            ? CreateVocalsAttributes(meta, arrangement, isHeader: false)
            : CreateInstrumentalAttributes(meta, arrangement, isHeader: false);

        var root = new JsonObject
        {
            ["Entries"] = new JsonObject
            {
                [PersistentId(arrangement)] = new JsonObject { ["Attributes"] = attributes }
            },
            ["ModelName"] = "RSEnumerable_Song",
            ["IterationVersion"] = 2,
            ["InsertRoot"] = "Static.Songs.Entries"
        };

        return root.ToJsonString(SerializerOptions);
    }

    /// <summary>Creates the aggregate .hsan header
    /// ("manifests/songs_dlc_{key}/songs_dlc_{key}.hsan").</summary>
    public static string CreateHeader(RsPackageMetadata meta, IReadOnlyList<RsPackageArrangement> arrangements)
    {
        var entries = new JsonObject();
        foreach (var arrangement in arrangements)
        {
            var attributes = arrangement.IsVocals
                ? CreateVocalsAttributes(meta, arrangement, isHeader: true)
                : CreateInstrumentalAttributes(meta, arrangement, isHeader: true);
            entries[PersistentId(arrangement)] = new JsonObject { ["Attributes"] = attributes };
        }

        var root = new JsonObject
        {
            ["Entries"] = entries,
            ["InsertRoot"] = "Static.Songs.Headers"
        };

        return root.ToJsonString(SerializerOptions);
    }

    private static string PersistentId(RsPackageArrangement arrangement) => arrangement.PersistentId.ToString("N").ToUpperInvariant();

    // ---- attribute construction ---------------------------------------------

    private static JsonObject CreateVocalsAttributes(
        RsPackageMetadata meta, RsPackageArrangement arrangement, bool isHeader)
    {
        var attr = new JsonObject();
        AddBase(attr, meta, arrangement);
        if (!isHeader)
        {
            AddCommon(attr, meta, arrangement, 0);
            attr["InputEvent"] = "Play_Tone_Standard_Mic";
        }

        return Sorted(attr);
    }

    private static JsonObject CreateInstrumentalAttributes(
        RsPackageMetadata meta, RsPackageArrangement arrangement, bool isHeader)
    {
        var sng = arrangement.Sng;
        var counts = arrangement.NoteCounts!;

        var attr = new JsonObject();
        AddBase(attr, meta, arrangement);
        AddSongCommon(attr, meta, arrangement);

        if (isHeader)
        {
            // Through the resolved properties, so the .hsan header and the
            // per-arrangement manifest's "bassPick" cannot disagree — the resolver
            // gates picked bass on the arrangement really being bass.
            if (arrangement.Properties!.BassPick != 0) attr["BassPick"] = 1;
            attr["Representative"] = arrangement.IsRepresentative ? 1 : 0;
            attr["RouteMask"] = arrangement.RouteMaskValue;
            // Deviation from Rocksmith2014.NET: the tone slots are repeated in
            // the header (like old-toolkit packages) so importers that only
            // read the .hsan — including DotCharter's own — can resolve tone
            // change names. The game ignores unknown header fields.
            attr["Tone_A"] = GetToneSlot(arrangement, 0);
            attr["Tone_B"] = GetToneSlot(arrangement, 1);
            attr["Tone_Base"] = arrangement.ToneBase;
            attr["Tone_C"] = GetToneSlot(arrangement, 2);
            attr["Tone_D"] = GetToneSlot(arrangement, 3);
            return Sorted(attr);
        }

        AddCommon(attr, meta, arrangement, sng.Levels.Count);

        attr["ArrangementProperties"] = CreateArrangementProperties(arrangement);
        attr["ArrangementType"] = arrangement.ArrangementTypeValue;
        attr["Chords"] = CreateChordMap(sng);
        attr["ChordTemplates"] = CreateChordTemplates(sng);
        attr["LastConversionDateTime"] = sng.MetaData.LastConversionDateTime;
        attr["MaxPhraseDifficulty"] = Math.Max(0, sng.Levels.Count - 1);
        attr["PhraseIterations"] = CreatePhraseIterations(sng);
        attr["Phrases"] = CreatePhrases(sng);
        attr["Score_MaxNotes"] = (float)Math.Max(0, counts.Hard);
        attr["Score_PNV"] = 100_000f / Math.Max(1f, counts.Hard);
        attr["Sections"] = CreateSections(sng);
        attr["SongAverageTempo"] = meta.AverageTempo <= 0f ? 120f : meta.AverageTempo;
        attr["SongOffset"] = -sng.MetaData.StartTime;
        attr["SongPartition"] = arrangement.SongPartition;
        attr["TargetScore"] = 100_000;
        attr["Techniques"] = CreateTechniqueMap(sng);
        attr["Tone_A"] = GetToneSlot(arrangement, 0);
        attr["Tone_B"] = GetToneSlot(arrangement, 1);
        attr["Tone_Base"] = arrangement.ToneBase;
        attr["Tone_C"] = GetToneSlot(arrangement, 2);
        attr["Tone_D"] = GetToneSlot(arrangement, 3);
        attr["Tone_Multiplayer"] = "";
        // Full tone definitions come from the tone designer (imported from an
        // existing package); without them the game falls back to a default tone.
        attr["Tones"] = CreateTones(arrangement);

        return Sorted(attr);
    }

    private static string GetToneSlot(RsPackageArrangement arrangement, int index) => index < arrangement.ToneSlots.Length ? arrangement.ToneSlots[index] : "";

    /// <summary>Embeds the full tone definitions this arrangement references.
    /// Each body is re-parsed per manifest because a <c>JsonNode</c> can only
    /// have one parent; an unparsable body is skipped (the game then falls
    /// back to its default tone) rather than failing the whole build.</summary>
    private static JsonArray CreateTones(RsPackageArrangement arrangement)
    {
        var tones = new JsonArray();
        foreach (var definition in arrangement.ToneDefinitions)
        {
            try
            {
                tones.Add(JsonNode.Parse(definition));
            }
            catch (JsonException)
            {
                // Skip invalid definitions; the slot name still exports.
            }
        }

        return tones;
    }

    /// <summary>Fields shared by every manifest and header entry
    /// (AttributesCreation.fs initBase).</summary>
    private static void AddBase(JsonObject attr, RsPackageMetadata meta, RsPackageArrangement arrangement)
    {
        var dlcKey = meta.DlcKey.ToLowerInvariant();
        attr["AlbumArt"] = $"urn:image:dds:album_{dlcKey}";
        attr["ArrangementName"] = arrangement.ArrangementName;
        attr["DLC"] = true;
        attr["DLCKey"] = meta.DlcKey;
        attr["LeaderboardChallengeRating"] = 0;
        attr["ManifestUrn"] = $"urn:database:json-db:{dlcKey}_{arrangement.PartitionName}";
        attr["MasterID_RDV"] = arrangement.MasterId;
        attr["PersistentID"] = PersistentId(arrangement);
        attr["SKU"] = "RS2";
        attr["Shipping"] = true;
        attr["SongKey"] = meta.DlcKey;
    }

    /// <summary>Fields shared by all non-header entries
    /// (AttributesCreation.fs initAttributesCommon).</summary>
    private static void AddCommon(
        JsonObject attr, RsPackageMetadata meta, RsPackageArrangement arrangement, int levels)
    {
        var dlcKey = meta.DlcKey.ToLowerInvariant();
        attr["ArrangementSort"] = 0;
        attr["BlockAsset"] = $"urn:emergent-world:{dlcKey}";
        attr["DynamicVisualDensity"] = CreateDynamicVisualDensity(levels, arrangement);
        attr["FullName"] = $"{meta.DlcKey}_{arrangement.ArrangementName}";
        attr["MasterID_PS3"] = -1;
        attr["MasterID_XBox360"] = -1;
        attr["PreviewBankPath"] = $"song_{dlcKey}_preview.bnk";
        attr["RelativeDifficulty"] = 0;
        attr["ShowlightsXML"] = $"urn:application:xml:{dlcKey}_showlights";
        attr["SongAsset"] = $"urn:application:musicgame-song:{dlcKey}_{arrangement.PartitionName}";
        attr["SongBank"] = $"song_{dlcKey}.bnk";
        attr["SongEvent"] = $"Play_{meta.DlcKey}";
        attr["SongXml"] = $"urn:application:xml:{dlcKey}_{arrangement.PartitionName}";
    }

    /// <summary>
    ///     The song's title and artist in their ORIGINAL script. Both attributes
    ///     are OPTIONAL and official DLC omits them entirely for Latin-titled
    ///     songs, so a blank one is left OUT rather than written as an empty
    ///     string — an empty key is not the same as an absent one to the game's
    ///     reader. There is no album counterpart in the manifest.
    ///     <para>Internal rather than inlined so it can be asserted without
    ///     building a whole packaged arrangement.</para>
    /// </summary>
    internal static void AddOriginalScriptNames(JsonObject attr, string? title, string? artist)
    {
        if (!string.IsNullOrWhiteSpace(artist)) attr["JapaneseArtistName"] = artist.Trim();
        if (!string.IsNullOrWhiteSpace(title)) attr["JapaneseSongName"] = title.Trim();
    }

    /// <summary>Fields shared by instrumental headers and manifests
    /// (AttributesCreation.fs initSongCommon).</summary>
    private static void AddSongCommon(JsonObject attr, RsPackageMetadata meta, RsPackageArrangement arrangement)
    {
        var sng = arrangement.Sng;
        var counts = arrangement.NoteCounts!;
        var domain = arrangement.Domain!;

        var difficulties = CalculateDifficulties(arrangement);
        var (dnaChords, dnaRiffs, dnaSolo) = CalculateDnas(sng);
        var hardCount = Math.Max(1.0, counts.Hard);

        attr["AlbumName"] = meta.Album;
        attr["AlbumNameSort"] = meta.AlbumSort;
        attr["ArtistName"] = meta.Artist;
        attr["ArtistNameSort"] = meta.ArtistSort;
        if (domain.Capo > 0) attr["CapoFret"] = (double)domain.Capo;
        attr["CentOffset"] = domain.CentOffset;
        attr["DNA_Chords"] = dnaChords;
        attr["DNA_Riffs"] = dnaRiffs;
        attr["DNA_Solo"] = dnaSolo;
        attr["EasyMastery"] = Math.Round(counts.Easy / hardCount, 9);
        attr["MediumMastery"] = Math.Round(counts.Medium / hardCount, 9);
        attr["NotesEasy"] = (float)counts.Easy;
        attr["NotesHard"] = (float)counts.Hard;
        attr["NotesMedium"] = (float)counts.Medium;
        attr["SongDiffEasy"] = difficulties.Easy;
        attr["SongDiffHard"] = difficulties.Hard;
        attr["SongDiffMed"] = difficulties.Medium;
        attr["SongDifficulty"] = difficulties.Hard;
        attr["SongLength"] = sng.MetaData.SongLength;
        attr["SongName"] = meta.Title;
        attr["SongNameSort"] = meta.TitleSort;
        AddOriginalScriptNames(attr, meta.JapaneseTitle, meta.JapaneseArtist);
        if (meta.Year.HasValue) attr["SongYear"] = meta.Year.Value;
        attr["Tuning"] = CreateTuning(sng);
    }

    private static JsonObject CreateTuning(RocksmithSng sng)
    {
        var tuning = new JsonObject();
        for (var i = 0; i < 6; i++)
            tuning[$"string{i}"] = i < sng.MetaData.Tuning.Length ? sng.MetaData.Tuning[i] : (short)0;
        // A 7-/8-string guitar: the rs7string tuner reads Rs7String6/7 after
        // string5. Never "string6": the game's song list reads six ints and overflows.
        for (var i = 6; i < sng.MetaData.Tuning.Length; i++)
            tuning[Rs7StringLayout.ManifestKey(i)] = sng.MetaData.Tuning[i];
        return tuning;
    }

    /// <summary>Sorts the attribute keys alphabetically so the output matches
    /// the reference field ordering regardless of insertion order.</summary>
    private static JsonObject Sorted(JsonObject attr)
    {
        var sorted = new JsonObject();
        foreach (var pair in attr.OrderBy(p => p.Key, StringComparer.Ordinal).ToList())
        {
            attr.Remove(pair.Key);
            sorted[pair.Key] = pair.Value;
        }

        return sorted;
    }

    // ---- arrangement properties ---------------------------------------------

    /// <summary>
    ///     The flags the GAME reads. They are not copied VERBATIM out of
    ///     <c>Domain.Properties</c> — which nothing in the editor can author and
    ///     only the RS-XML writer derives, into a clone this never sees — or a chart
    ///     built from scratch would declare no techniques here and
    ///     <see cref="CalculateDifficulties" /> would rate it as such. Both read the
    ///     one resolved <see cref="RsPackageArrangement.Properties" />.
    /// </summary>
    private static JsonObject CreateArrangementProperties(RsPackageArrangement arrangement)
    {
        var p = arrangement.Properties!;
        var props = new JsonObject
        {
            ["represent"] = Bit(p.Represent != 0),
            ["bonusArr"] = Bit(p.BonusArr != 0),
            ["standardTuning"] = Bit(p.StandardTuning != 0),
            ["nonStandardChords"] = Bit(p.NonStandardChords != 0),
            ["barreChords"] = Bit(p.BarreChords != 0),
            ["powerChords"] = Bit(p.PowerChords != 0),
            ["dropDPower"] = Bit(p.DropDPower != 0),
            ["openChords"] = Bit(p.OpenChords != 0),
            ["fingerPicking"] = Bit(p.FingerPicking != 0),
            ["pickDirection"] = Bit(p.PickDirection != 0),
            ["doubleStops"] = Bit(p.DoubleStops != 0),
            ["palmMutes"] = Bit(p.PalmMutes != 0),
            ["harmonics"] = Bit(p.Harmonics != 0),
            ["pinchHarmonics"] = Bit(p.PinchHarmonics != 0),
            ["hopo"] = Bit(p.Hopo != 0),
            ["tremolo"] = Bit(p.Tremolo != 0),
            ["slides"] = Bit(p.Slides != 0),
            ["unpitchedSlides"] = Bit(p.UnpitchedSlides != 0),
            ["bends"] = Bit(p.Bends != 0),
            ["tapping"] = Bit(p.Tapping != 0),
            ["vibrato"] = Bit(p.Vibrato != 0),
            ["fretHandMutes"] = Bit(p.FretHandMutes != 0),
            ["slapPop"] = Bit(p.SlapPop != 0),
            ["twoFingerPicking"] = Bit(p.TwoFingerPicking != 0),
            ["fifthsAndOctaves"] = Bit(p.FifthsAndOctaves != 0),
            ["syncopation"] = Bit(p.Syncopation != 0),
            ["bassPick"] = Bit(p.BassPick != 0),
            ["sustain"] = Bit(p.Sustain != 0),
            ["pathLead"] = Bit(p.PathLead != 0),
            ["pathRhythm"] = Bit(p.PathRhythm != 0),
            ["pathBass"] = Bit(p.PathBass != 0),
            ["routeMask"] = arrangement.RouteMaskValue
        };
        return props;
    }

    private static int Bit(bool value) => value ? 1 : 0;

    // ---- difficulty / DNA ----------------------------------------------------

    private static (double Easy, double Medium, double Hard) CalculateDifficulties(
        RsPackageArrangement arrangement)
    {
        var p = arrangement.Properties!;
        var isBass = arrangement.RouteMaskValue == 4;

        static int Bti(int flag) => flag != 0 ? 1 : 0;

        var techCoeff =
            Bti(p.NonStandardChords) +
            3 * Bti(p.BarreChords) +
            (Bti(p.PowerChords) | Bti(p.DoubleStops)) +
            Bti(p.DropDPower) +
            2 * Bti(p.OpenChords) +
            Bti(p.FingerPicking) +
            Bti(p.TwoFingerPicking) +
            Bti(p.PalmMutes) +
            2 * Bti(p.Harmonics) +
            3 * Bti(p.PinchHarmonics) +
            Bti(p.Hopo) +
            Bti(p.Tremolo) +
            (isBass ? 4 : 1) * Bti(p.Slides) +
            Bti(p.UnpitchedSlides) +
            3 * Bti(p.Bends) +
            4 * Bti(p.Tapping) +
            2 * Bti(p.Vibrato) +
            Bti(p.FretHandMutes) +
            Bti(p.SlapPop) +
            Bti(p.Sustain) +
            Bti(p.FifthsAndOctaves) +
            Bti(p.Syncopation);

        // Arrangements with few/no techniques get very low values otherwise.
        if (techCoeff <= 5) techCoeff += 4;

        var counts = arrangement.NoteCounts!;
        var songLength = Math.Max(1.0, arrangement.Sng.MetaData.SongLength);

        return (
            Math.Round(techCoeff * counts.Easy / songLength / 25.0, 9),
            Math.Round(techCoeff * counts.Medium / songLength / 50.0, 9),
            Math.Round(techCoeff * counts.Hard / songLength / 100.0, 9));
    }

    /// <summary>Sum of all regions of DNA ID → DNA None (or the end of the
    /// song) — the iteration version 2 algorithm.</summary>
    private static double GetDnaTime(RocksmithSng sng, int dnaId)
    {
        float GetTotal(int i, float time)
        {
            var next = sng.Dnas.FindIndex(i, dna => dna.DnaId == dnaId);
            if (next < 0) return time;

            var none = sng.Dnas.FindIndex(next, dna => dna.DnaId == 0);
            if (none < 0) return time + (sng.MetaData.SongLength - sng.Dnas[next].Time);

            return time + GetTotal(none, sng.Dnas[none].Time - sng.Dnas[next].Time);
        }

        return Math.Round((double)GetTotal(0, 0f), 3);
    }

    private static (double Chords, double Riffs, double Solo) CalculateDnas(RocksmithSng sng) => (GetDnaTime(sng, 3), GetDnaTime(sng, 2), GetDnaTime(sng, 1));

    // ---- SNG-derived sections -------------------------------------------------

    private static JsonArray CreatePhrases(RocksmithSng sng)
    {
        var phrases = new JsonArray();
        foreach (var phrase in sng.Phrases)
        {
            phrases.Add(new JsonObject
            {
                ["MaxDifficulty"] = phrase.MaxDifficulty,
                ["Name"] = phrase.Name,
                ["IterationCount"] = phrase.IterationCount
            });
        }

        return phrases;
    }

    private static JsonArray CreatePhraseIterations(RocksmithSng sng)
    {
        var iterations = new JsonArray();
        foreach (var pi in sng.PhraseIterations)
        {
            var phrase = sng.Phrases[pi.PhraseId];
            iterations.Add(new JsonObject
            {
                ["PhraseIndex"] = pi.PhraseId,
                ["MaxDifficulty"] = phrase.MaxDifficulty,
                ["Name"] = phrase.Name,
                ["StartTime"] = pi.StartTime,
                ["EndTime"] = pi.EndTime
            });
        }

        return iterations;
    }

    private static JsonArray CreateSections(RocksmithSng sng)
    {
        var sections = new JsonArray();
        foreach (var section in sng.Sections)
        {
            sections.Add(new JsonObject
            {
                ["Name"] = section.Name,
                ["UIName"] = GetSectionUiName(section.Name),
                ["Number"] = section.Number,
                ["StartTime"] = section.StartTime,
                ["EndTime"] = section.EndTime,
                ["StartPhraseIterationIndex"] = section.StartPhraseIterationId,
                ["EndPhraseIterationIndex"] = section.EndPhraseIterationId,
                ["IsSolo"] = section.Name.StartsWith("solo", StringComparison.Ordinal)
            });
        }

        return sections;
    }

    /// <summary>Localized UI name lookup for section names
    /// (AttributesCreation.fs getSectionUIName). Unknown names fall back to
    /// Riff instead of failing the build; the export validator reports them.</summary>
    internal static string GetSectionUiName(string name)
    {
        var letters = new string(name.ToLowerInvariant().Where(char.IsLetter).ToArray());
        return letters switch
        {
            "fadein" => "$[34276] Fade In [1]",
            "fadeout" => "$[34277] Fade Out [1]",
            "buildup" => "$[34278] Buildup [1]",
            "chorus" => "$[34279] Chorus [1]",
            "hook" => "$[34280] Hook [1]",
            "head" => "$[34281] Head [1]",
            "bridge" => "$[34282] Bridge [1]",
            "ambient" => "$[34283] Ambient [1]",
            "breakdown" => "$[34284] Breakdown [1]",
            "interlude" => "$[34285] Interlude [1]",
            "intro" => "$[34286] Intro [1]",
            "melody" => "$[34287] Melody [1]",
            "modbridge" => "$[34288] Modulated Bridge [1]",
            "modchorus" => "$[34289] Modulated Chorus [1]",
            "modverse" => "$[34290] Modulated Verse [1]",
            "outro" => "$[34291] Outro [1]",
            "postbrdg" => "$[34292] Post Bridge [1]",
            "postchorus" => "$[34293] Post Chorus [1]",
            "postvs" => "$[34294] Post Verse [1]",
            "prebrdg" => "$[34295] Pre Bridge [1]",
            "prechorus" => "$[34296] Pre Chorus [1]",
            "preverse" => "$[34297] Pre Verse [1]",
            "riff" => "$[34298] Riff [1]",
            "rifff" => "$[34298] Riff [1]", // Error in official file
            "silence" => "$[34299] Silence [1]",
            "solo" => "$[34300] Solo [1]",
            "transition" => "$[34301] Transition [1]",
            "vamp" => "$[34302] Vamp [1]",
            "variation" => "$[34303] Variation [1]",
            "verse" => "$[34304] Verse [1]",
            "tapping" => "$[34305] Tapping [1]",
            "noguitar" => "$[6091] No Guitar [1]",
            _ => "$[34298] Riff [1]"
        };
    }

    private static JsonArray CreateChordTemplates(RocksmithSng sng)
    {
        var templates = new JsonArray();
        for (var id = 0; id < sng.Chords.Count; id++)
        {
            var chord = sng.Chords[id];
            // Nameless templates and pure arpeggios are excluded.
            if (string.IsNullOrEmpty(chord.Name) || chord.Mask == 0x1u) continue;

            templates.Add(new JsonObject
            {
                ["ChordId"] = (short)id,
                ["ChordName"] = chord.Name,
                ["Fingers"] = new JsonArray(chord.Fingers.Select(f => (JsonNode)(int)f).ToArray()),
                ["Frets"] = new JsonArray(chord.Frets.Select(f => (JsonNode)(int)f).ToArray())
            });
        }

        return templates;
    }

    private static JsonArray CreateDynamicVisualDensity(int levels, RsPackageArrangement arrangement)
    {
        var density = new JsonArray();

        if (arrangement.IsVocals)
        {
            for (var i = 0; i < 20; i++) density.Add(2f);
            return density;
        }

        const double floorLimit = 0.5; // fastest allowed speed
        const double beginSpeed = 5.0;

        // This array IS how Rocksmith stores scroll speed — there is no
        // ScrollSpeed attribute. The tail entries carry the authored speed and
        // the head ramps down from beginSpeed across the DD levels; the game (and
        // DLC Builder, on re-import) reads the entry for the hardest phrase
        // difficulty back out. So writing the arrangement's real value here is
        // what makes an exported CDLC scroll the way it was charted; the old
        // hardcoded 1.3 silently republished every chart at the default.
        var scrollSpeed = arrangement.Domain?.ScrollSpeed ?? DefaultScrollSpeed;
        var endSpeed = Math.Clamp(scrollSpeed, floorLimit, beginSpeed);
        var maxLevel = Math.Min(levels, 20) - 1;
        var factor = maxLevel > 0 ? Math.Pow(endSpeed / beginSpeed, 1.0 / maxLevel) : 1.0;

        for (var i = 0; i < 20; i++)
        {
            density.Add(i >= maxLevel
                ? (float)endSpeed
                : (float)Math.Round(beginSpeed * Math.Pow(factor, i), 1));
        }

        return density;
    }

    // ---- chord / technique maps -----------------------------------------------

    private static JsonObject CreateChordMap(RocksmithSng sng)
    {
        var chords = new JsonObject();

        for (var lvl = 0; lvl < sng.Levels.Count; lvl++)
        {
            var diffIds = new JsonObject();

            for (var i = 0; i < sng.PhraseIterations.Count; i++)
            {
                var pi = sng.PhraseIterations[i];
                var chordIds = new SortedSet<int>();
                foreach (var hs in sng.Levels[lvl].HandShapes)
                {
                    if (hs.ChordId >= 0 && hs.ChordId < sng.Chords.Count &&
                        !string.IsNullOrEmpty(sng.Chords[hs.ChordId].Name) &&
                        hs.StartTime >= pi.StartTime && hs.StartTime < pi.EndTime)
                    {
                        chordIds.Add(hs.ChordId);
                    }
                }

                if (chordIds.Count > 0)
                    diffIds[i.ToString()] = new JsonArray(chordIds.Select(id => (JsonNode)id).ToArray());
            }

            if (diffIds.Count > 0)
                chords[lvl.ToString()] = diffIds;
        }

        return chords;
    }

    private static JsonObject CreateTechniqueMap(RocksmithSng sng)
    {
        var techniques = new JsonObject();

        for (var lvl = 0; lvl < sng.Levels.Count; lvl++)
        {
            var diffIds = new JsonObject();

            for (var i = 0; i < sng.PhraseIterations.Count - 1; i++)
            {
                var pi = sng.PhraseIterations[i];
                var techIds = new SortedSet<int>();
                foreach (var note in sng.Levels[lvl].Notes)
                {
                    // Weird division into phrase iterations intentional.
                    if (note.Time > pi.StartTime && note.Time <= pi.EndTime)
                    {
                        foreach (var tech in RsTechniques.GetTechniques(sng, note))
                            techIds.Add(tech);
                    }
                }

                if (techIds.Count > 0)
                    diffIds[i.ToString()] = new JsonArray(techIds.Select(id => (JsonNode)id).ToArray());
            }

            if (diffIds.Count > 0)
                techniques[lvl.ToString()] = diffIds;
        }

        return techniques;
    }
}
