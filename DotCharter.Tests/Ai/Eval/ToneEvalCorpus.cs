using System.Text.Json;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Tone.Audio.Dsp.Tones;

namespace DotCharter.Tests.Ai.Eval;

/// <summary>
///     Ground truth for the AI tone-design eval, in two halves.
///     <list type="bullet">
///         <item>
///             <see cref="Fixtures" /> — five hand-picked tones committed to the
///             repo. Always present, so every scorer/runner test runs in the
///             DEFAULT gate with no corpus and no network.
///         </item>
///         <item>
///             <see cref="TryLoadLocal" /> — up to
///             <see cref="DefaultLimit" /> real songs off the owner's local
///             corpus, ONE tone per song. Absent on CI and in an external
///             worktree; callers <c>Assert.Skip</c> rather than passing vacuously.
///         </item>
///     </list>
///     <para>
///         Both halves stop at the same place: a parsed <see cref="RsTone" /> put
///         through <see cref="RsToneConverter" />. A tone the converter cannot map
///         onto any module is DROPPED — grading a model against an empty truth
///         would report the converter's gap as the model's.
///     </para>
/// </summary>
public static class ToneEvalCorpus
{
    /// <summary>How many songs <see cref="TryLoadLocal" /> takes when the caller
    /// names no limit. 60 is the brief's ceiling; the corpus is smaller today.</summary>
    public const int DefaultLimit = 60;

    /// <summary>Points the loader at an rs2dlc tree other than
    /// <c>TestData/</c> — e.g. the owner's real CDLC folder. Optional.</summary>
    public const string CorpusRootVariable = "DOTCHARTER_AI_EVAL_CORPUS";

    /// <summary>The message a caller passes to <c>Assert.Skip</c> when
    /// <see cref="TryLoadLocal" /> comes back empty.</summary>
    public const string MissingCorpus =
        "No local tone corpus: TestData/**/*.rs2dlc is absent " +
        "(TestData/ is git-ignored). Set " + CorpusRootVariable + " to point at one.";

    // ---------------------------------------------------------------- fixtures

    private static readonly Lazy<IReadOnlyList<ToneEvalEntry>> FixturesLazy =
        new(BuildFixtures, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    ///     The committed five: Rocksmith's own Lead / Rhythm / Bass defaults (the
    ///     three <c>default_*.json</c> golden vectors this repo already ships) plus
    ///     two hand-written tones that reach families the defaults do not — a
    ///     modulated clean (chorus + delay, no drive) and a high-gain stack
    ///     (distortion + gate, no time effects). Five rows, ten distinct families
    ///     between them, so a scorer bug cannot hide behind a corpus that only
    ///     ever contains "amp + cab + drive".
    /// </summary>
    public static IReadOnlyList<ToneEvalEntry> Fixtures => FixturesLazy.Value;

    private static IReadOnlyList<ToneEvalEntry> BuildFixtures()
    {
        var rows = new (string Title, string Artist, string Tone, string Json)[]
        {
            ("Default Lead", "Rocksmith", "Lead", ReadFixtureJson("default_lead.json")),
            ("Default Rhythm", "Rocksmith", "Rhythm", ReadFixtureJson("default_rhythm.json")),
            ("Default Bass", "Rocksmith", "Bass", ReadFixtureJson("default_bass.json")),
            ("Fixture Shimmer", "DotCharter", "Clean", ModulatedCleanJson),
            ("Fixture Wall", "DotCharter", "Distortion", HighGainJson)
        };

        var entries = new List<ToneEvalEntry>(rows.Length);
        foreach (var row in rows)
        {
            var entry = TryBuild(row.Title, row.Artist, row.Tone, row.Tone, row.Json, "fixture");
            if (entry is not null) entries.Add(entry);
        }

        if (entries.Count != rows.Length)
        {
            throw new InvalidOperationException(
                $"The committed fixture corpus must convert in full: {entries.Count}/{rows.Length} " +
                "rows survived. A fixture the converter cannot map is a broken fixture, not a skip.");
        }

        return entries;
    }

    private static string ReadFixtureJson(string name)
        => File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "Audio", "Dsp", "Tones", name));

    // Hand-written, in the exact shape RsToneJsonParser eats. Gear keys and pot
    // names are real catalogue entries (tone_gear_data.json) — an invented key
    // would convert to nothing and BuildFixtures would throw.
    private const string ModulatedCleanJson =
        """
        {"GearList":{
          "Amp":{"Type":"Amps","Key":"Amp_TwinReverb","KnobValues":{
            "Amp_TwinReverb_Bass":55,"Amp_TwinReverb_Mid":60,"Amp_TwinReverb_Treble":70,
            "Amp_TwinReverb_Loudness1":62}},
          "Cabinet":{"Type":"Cabinets","Key":"Cab_Twin2x12_Condenser_Cone","KnobValues":{}},
          "PostPedal1":{"Type":"Pedals","Key":"Pedal_ElectricChorus","Category":"Modulation",
            "KnobValues":{"Pedal_ElectricChorus_Rate":38,"Pedal_ElectricChorus_Depth":55,
            "Pedal_ElectricChorus_Mix":45}},
          "PostPedal2":{"Type":"Pedals","Key":"Pedal_DigitalDelay","Category":"Delay",
            "KnobValues":{"Pedal_DigitalDelay_Time":420,"Pedal_DigitalDelay_Feedback":30,
            "Pedal_DigitalDelay_Mix":28}}},
         "ToneDescriptors":["$[35720]CLEAN"],"Volume":"-18.0",
         "Key":"Fixture_Clean","Name":"Clean"}
        """;

    private const string HighGainJson =
        """
        {"GearList":{
          "Amp":{"Type":"Amps","Key":"Amp_MarshallJCM800","KnobValues":{
            "Amp_MarshallJCM800_Gain":88,"Amp_MarshallJCM800_Bass":60,
            "Amp_MarshallJCM800_Mid":45,"Amp_MarshallJCM800_Treble":72,
            "Amp_MarshallJCM800_Loudness1":58}},
          "Cabinet":{"Type":"Cabinets","Key":"Cab_Marshall1960TV_Ribbon_Cone","KnobValues":{}},
          "PrePedal1":{"Type":"Pedals","Key":"Pedal_NoiseGate","Category":"Dynamics",
            "KnobValues":{"Pedal_NoiseGate_Threshold":-42,"Pedal_NoiseGate_Release":120}},
          "PrePedal2":{"Type":"Pedals","Key":"Pedal_GermaniumDrive","Category":"Distortion",
            "KnobValues":{"Pedal_GermaniumDrive_Gain":75,"Pedal_GermaniumDrive_Tone":52}}},
         "ToneDescriptors":["$[35722]DISTORTION"],"Volume":"-20.0",
         "Key":"Fixture_Wall","Name":"Distortion"}
        """;

    // ------------------------------------------------------------------- local

    /// <summary>
    ///     Walks the local corpus and returns ONE tone per song, newest-first by
    ///     nothing at all — the order is a stable ordinal sort on
    ///     <see cref="ToneEvalEntry.SongId" />, because an eval whose row order
    ///     moves between runs cannot be diffed.
    /// </summary>
    /// <param name="entries">The rows found; empty when the corpus is absent.</param>
    /// <param name="limit">Hard ceiling on songs. Defaults to <see cref="DefaultLimit" />.</param>
    /// <returns>True when at least one row was built.</returns>
    public static bool TryLoadLocal(out IReadOnlyList<ToneEvalEntry> entries, int limit = DefaultLimit)
    {
        entries = LoadLocal(limit);
        return entries.Count > 0;
    }

    /// <summary>
    ///     <see cref="TryLoadLocal" />'s body. Never throws: one unreadable
    ///     package must not take the whole corpus down.
    /// </summary>
    public static IReadOnlyList<ToneEvalEntry> LoadLocal(int limit = DefaultLimit)
    {
        var root = ResolveCorpusRoot();
        return root is null ? Array.Empty<ToneEvalEntry>() : LoadLocalFrom(root, limit);
    }

    /// <summary>
    ///     <see cref="LoadLocal" /> against an EXPLICIT root.
    ///     <para>
    ///         It exists so a test can drive a synthetic corpus without writing
    ///         <see cref="CorpusRootVariable" />, which is process-global state that a
    ///         parallel eval run would see.
    ///     </para>
    /// </summary>
    /// <param name="root">The directory to walk.</param>
    /// <param name="limit">Hard ceiling on songs.</param>
    /// <returns>One elected tone per song, already converted.</returns>
    public static IReadOnlyList<ToneEvalEntry> LoadLocalFrom(string root, int limit = DefaultLimit)
    {
        ArgumentNullException.ThrowIfNull(root);

        var bySong = new Dictionary<string, ToneEvalEntry>(StringComparer.Ordinal);

        // .rs2dlc only: it is plain JSON carrying both the metadata and the tone
        // bodies, and the EOF corpus ships one per song. Built packages need the
        // package reader, an optional plugin this project does not reference (#889).
        foreach (var file in SafeEnumerate(root, "*.rs2dlc"))
            HarvestRs2Dlc(file, bySong);

        return bySong.Values
            .OrderBy(e => e.SongId, StringComparer.Ordinal)
            .Take(Math.Max(0, limit))
            .ToList();
    }

    /// <summary>
    ///     The corpus root: <see cref="CorpusRootVariable" /> when set, else the
    ///     nearest <c>TestData</c> directory walking up from the test binaries —
    ///     the same ancestor walk every other corpus test in this project does, and
    ///     the reason a worktree under <c>.claude/worktrees/</c> still finds the
    ///     main checkout's junction.
    /// </summary>
    public static string? ResolveCorpusRoot()
    {
        var configured = Environment.GetEnvironmentVariable(CorpusRootVariable);
        if (!string.IsNullOrWhiteSpace(configured))
            return Directory.Exists(configured) ? configured : null;

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "TestData");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }

        return null;
    }

    /// <summary>
    ///     The song identity a corpus package carries, read from the SAME fields this
    ///     loader builds its entries from.
    ///     <para>
    ///         <b>Shared on purpose.</b> A consumer that needs "which folder is this
    ///         entry's song in" must ask the identity question with the same
    ///         normalisation the entry's own <see cref="ToneEvalEntry.SongId" /> uses —
    ///         a second, private answer is how
    ///         <c>AudioEvalCorpus</c> ended up matching on tone JSON and resolving
    ///         several songs to one folder.
    ///     </para>
    /// </summary>
    /// <param name="path">A <c>.rs2dlc</c> under the corpus root (anything else reads as no identity).</param>
    /// <param name="songId">The <see cref="ToneEvalEntry.MakeSongId" /> key; empty when
    /// the file carries no usable title/artist pair.</param>
    /// <returns>True when both a title and an artist were read.</returns>
    public static bool TryReadPackageSongId(string path, out string songId)
    {
        ArgumentNullException.ThrowIfNull(path);
        songId = string.Empty;

        string title, artist;
        if (path.EndsWith(".rs2dlc", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var stream = File.OpenRead(path);
                using var document = JsonDocument.Parse(stream,
                    new JsonDocumentOptions { AllowTrailingCommas = true });
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return false;
                title = ReadNamed(root, "Title");
                artist = ReadNamed(root, "ArtistName");
            }
            catch (JsonException) { return false; }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }
        else
        {
            return false;
        }

        if (title.Length == 0 || artist.Length == 0) return false;
        songId = ToneEvalEntry.MakeSongId(artist, title);
        return true;
    }

    private static IEnumerable<string> SafeEnumerate(string root, string pattern)
    {
        try
        {
            return Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (IOException) { return Array.Empty<string>(); }
        catch (UnauthorizedAccessException) { return Array.Empty<string>(); }
    }

    private static void HarvestRs2Dlc(string path, Dictionary<string, ToneEvalEntry> bySong)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var document = JsonDocument.Parse(stream,
                new JsonDocumentOptions { AllowTrailingCommas = true });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;

            var title = ReadNamed(root, "Title");
            var artist = ReadNamed(root, "ArtistName");
            if (title.Length == 0 || artist.Length == 0) return;
            if (!root.TryGetProperty("Tones", out var tones) ||
                tones.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            var candidates = new List<ToneEvalEntry>();
            foreach (var tone in tones.EnumerateArray())
            {
                if (tone.ValueKind != JsonValueKind.Object) continue;
                var name = ReadString(tone, "Name");
                var key = ReadString(tone, "Key");
                if (name.Length == 0) name = key;
                if (name.Length == 0) continue;

                var entry = TryBuild(title, artist, name, key.Length == 0 ? name : key,
                    tone.GetRawText(), "rs2dlc");
                if (entry is not null) candidates.Add(entry);
            }

            Elect(candidates, bySong);
        }
        catch (JsonException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    ///     Picks the ONE tone that represents a song: the richest converted chain,
    ///     tie-broken on tone name ordinal. Richest, because a song's "Bass" tone is
    ///     usually amp+cab only and grading a model on that measures almost nothing;
    ///     deterministic, because the eval's row set must not move between runs.
    ///     An already-elected song is never replaced — the first source wins, and
    ///     <c>.rs2dlc</c> is enumerated first on purpose.
    /// </summary>
    private static void Elect(List<ToneEvalEntry> candidates, Dictionary<string, ToneEvalEntry> bySong)
    {
        if (candidates.Count == 0) return;

        var best = candidates
            .OrderByDescending(e => e.Truth.Modules.Count)
            .ThenBy(e => e.ToneName, StringComparer.Ordinal)
            .First();

        bySong.TryAdd(best.SongId, best);
    }

    private static ToneEvalEntry? TryBuild(
        string title, string artist, string toneName, string toneKey, string json, string source)
    {
        if (!RsToneJsonParser.TryParse(json, out var tone) || tone is null) return null;

        var truth = new RsToneConverter().Convert(tone, out _);

        // "The converter cannot map it" means: nothing but the loudness
        // normalizer's trailing master gain came out the other end. Grading a
        // model against that chain would score the plumbing, not the tone.
        if (ToneDesignScorer.Canonicalize(truth).Modules.Count == 0) return null;

        return new ToneEvalEntry(title, artist, toneName, toneKey, json, truth, source);
    }

    private static string ReadNamed(JsonElement root, string property)
    {
        // The toolkit serializes Title/ArtistName as {"Value":…,"SortValue":…}.
        if (!root.TryGetProperty(property, out var value)) return "";
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Object => ReadString(value, "Value"),
            _ => ""
        };
    }

    private static string ReadString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
}
