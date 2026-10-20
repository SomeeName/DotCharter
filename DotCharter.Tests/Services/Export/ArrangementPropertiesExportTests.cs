using System.Text.Json;
using System.Xml.Linq;
using DotCharter.Services.Export.Package;
using DotCharter.Services.Export.Sng;
using DotCharter.Services.Parsing.Xml;
using DotCharter.Services.Parsing.Xml.Song;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Export;

/// <summary>
///     What the two RS artifacts actually SAY about a chart's techniques.
///     <para>
///     The defect: the psarc manifest copied <c>Arrangement.Properties</c> verbatim
///     while nothing in the UI could author a flag, and the only derivation lived in
///     the RS-XML writer's private clone. So a chart authored entirely in DotCharter
///     told the game it used no techniques, and the XML built from the same chart
///     said something else again.
///     </para>
/// </summary>
public sealed class ArrangementPropertiesExportTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dotcharter_arrprops_").FullName;

    public void Dispose() => BestEffortDelete.Directory(_dir);

    /// <summary>Every flag name the RS-XML element and the manifest object share.</summary>
    private static readonly string[] SharedFlagNames =
    [
        "represent", "bonusArr", "standardTuning", "nonStandardChords", "barreChords",
        "powerChords", "dropDPower", "openChords", "fingerPicking", "pickDirection",
        "doubleStops", "palmMutes", "harmonics", "pinchHarmonics", "hopo", "tremolo",
        "slides", "unpitchedSlides", "bends", "tapping", "vibrato", "fretHandMutes",
        "slapPop", "twoFingerPicking", "fifthsAndOctaves", "syncopation", "bassPick",
        "sustain", "pathLead", "pathRhythm", "pathBass"
    ];

    private static List<BeatMarker> Beats(int count = 64)
    {
        var beats = new List<BeatMarker>(count);
        for (var i = 0; i < count; i++)
        {
            beats.Add(new BeatMarker
            {
                Time = i * 500.0, IsMeasure = i % 4 == 0, Bpm = 120,
                BeatsInMeasure = 4, NoteDenominator = 4
            });
        }

        return beats;
    }

    /// <summary>A chart nobody imported: authored note by note inside DotCharter.</summary>
    private static ChartProject FromScratchProject(bool withTechniques)
    {
        var project = new ChartProject { Bpm = 120 };
        project.Chart.SongName = "From Scratch";
        project.Chart.Artist = "DotCharter";
        foreach (var beat in Beats()) project.Beats.Add(beat);

        var arrangement = new Arrangement { Name = "Lead", ArrangementType = ArrangementType.Lead };
        for (var i = 0; i < 6; i++) arrangement.TuningPitches.Add(new TuningOffset(0));
        var level = new ChartLevel { DifficultyIndex = 0 };
        arrangement.Levels.Add(level);

        for (var i = 0; i < 8; i++)
        {
            level.Notes.Add(new GuitarNote
            {
                Position = new FractionalPosition(i * 2), StringIndex = i % 6, Fret = 5 + i % 3
            });
        }

        if (withTechniques)
        {
            var bend = level.Notes[0];
            bend.BendValues.Add(new BendValue(bend.Position, 1.0));
            level.Notes[1].Sustain = new FractionalPosition(1);
            level.Notes[2].Mute = MuteType.Palm;
            level.Notes[3].Hopo = HopoType.HammerOn;
            level.Notes[4].SlideTo = 9;
            level.Notes[5].Vibrato = true;
            level.Notes[6].Harmonic = HarmonicType.Normal;
            level.Notes[7].Hopo = HopoType.Tap;
        }

        project.Arrangements.Add(arrangement);
        return project;
    }

    private static JsonElement ManifestProperties(ChartProject project, out JsonElement attributes)
    {
        var arrangement = project.Arrangements[0];
        var build = SngFromDomainMapper.BuildInstrumental(arrangement, project.Beats, 32_000);
        var meta = new RsPackageMetadata
        {
            DlcKey = "DCArrProps", Title = project.Chart.SongName, Artist = project.Chart.Artist,
            AverageTempo = 120f
        };
        var packageArr = new RsPackageArrangement
        {
            ArrangementName = arrangement.ArrangementType.ToString(),
            PartitionName = arrangement.ArrangementType.ToString().ToLowerInvariant(),
            PersistentId = Guid.Parse("11111111-2222-3333-4444-555555555555"),
            MasterId = 1, Sng = build.Sng, NoteCounts = build.NoteCounts, Domain = arrangement
        };

        var doc = JsonDocument.Parse(RsManifestGenerator.CreateArrangementManifest(meta, packageArr));
        attributes = doc.RootElement.GetProperty("Entries")
            .GetProperty("11111111222233334444555555555555").GetProperty("Attributes").Clone();
        var props = attributes.GetProperty("ArrangementProperties").Clone();
        doc.Dispose();
        return props;
    }

    // ── the artifact the game reads ─────────────────────────────────────────

    /// <summary>
    ///     The headline claim, asserted on the psarc manifest itself rather than on
    ///     the domain: a chart authored from scratch reports its techniques.
    /// </summary>
    [Fact]
    public void TheManifest_ReportsTheTechniquesOfAFromScratchChart()
    {
        var props = ManifestProperties(FromScratchProject(withTechniques: true), out _);

        foreach (var flag in new[] { "bends", "sustain", "palmMutes", "hopo", "slides", "vibrato", "harmonics", "tapping" })
            Assert.True(props.GetProperty(flag).GetInt32() == 1, $"manifest {flag} should be 1");

        // …and only those. A flag with nothing behind it must stay 0, or the chart
        // claims a technique it does not have.
        foreach (var flag in new[] { "pinchHarmonics", "tremolo", "unpitchedSlides", "slapPop", "barreChords" })
            Assert.True(props.GetProperty(flag).GetInt32() == 0, $"manifest {flag} should be 0");

        Assert.Equal(1, props.GetProperty("pathLead").GetInt32());
        Assert.Equal(1, props.GetProperty("routeMask").GetInt32());
    }

    /// <summary>
    ///     `CalculateDifficulties` floors `techCoeff` at 4 when the flags claim five
    ///     or fewer techniques, so a chart declaring none rates identically to a
    ///     trivial one. Same notes, same length, only the techniques differ.
    /// </summary>
    [Fact]
    public void SongDifficulty_NoLongerCollapsesForAFromScratchChart()
    {
        ManifestProperties(FromScratchProject(withTechniques: true), out var rich);
        ManifestProperties(FromScratchProject(withTechniques: false), out var plain);

        Assert.Equal(plain.GetProperty("NotesHard").GetSingle(), rich.GetProperty("NotesHard").GetSingle());
        Assert.True(rich.GetProperty("SongDiffHard").GetDouble() > plain.GetProperty("SongDiffHard").GetDouble(),
            $"technique-rich {rich.GetProperty("SongDiffHard").GetDouble()} should outrate " +
            $"technique-free {plain.GetProperty("SongDiffHard").GetDouble()}");
    }

    /// <summary>
    ///     The RS-XML bundle and the psarc are built from ONE chart by two writers
    ///     that used to disagree — the XML derived (set-only), the manifest copied
    ///     verbatim.
    /// </summary>
    [Fact]
    public void TheXmlAndTheManifest_AgreeFlagForFlag()
    {
        var project = FromScratchProject(withTechniques: true);
        // A stale claim from a hypothetical import: the XML used to promote it and
        // keep it, the manifest used to ship it raw. Neither may now.
        project.Arrangements[0].Properties.PinchHarmonics = 1;

        var manifest = ManifestProperties(project, out _);

        var xmlPath = Path.Combine(_dir, "song.xml");
        new XmlChartSerializer().Save(xmlPath, project);
        var xml = XDocument.Load(xmlPath).Root!.Element("arrangementProperties")!;

        foreach (var flag in SharedFlagNames)
        {
            var xmlValue = int.Parse(xml.Attribute(flag)!.Value);
            var manifestValue = manifest.GetProperty(flag).GetInt32();
            Assert.True(xmlValue == manifestValue,
                $"'{flag}': RS-XML says {xmlValue}, the manifest says {manifestValue}");
        }

        Assert.Equal(0, manifest.GetProperty("pinchHarmonics").GetInt32());
    }

    /// <summary>
    ///     The pin reaches the artifacts too — otherwise "override" would only mean
    ///     "override until you export".
    /// </summary>
    [Fact]
    public void APinnedFlag_SurvivesIntoBothArtifacts()
    {
        var project = FromScratchProject(withTechniques: false);
        project.Arrangements[0].Properties.ManualOverride = true;
        project.Arrangements[0].Properties.Syncopation = 1;
        project.Arrangements[0].Properties.PinchHarmonics = 1;

        var manifest = ManifestProperties(project, out _);
        Assert.Equal(1, manifest.GetProperty("syncopation").GetInt32());
        Assert.Equal(1, manifest.GetProperty("pinchHarmonics").GetInt32());

        var xmlPath = Path.Combine(_dir, "pinned.xml");
        new XmlChartSerializer().Save(xmlPath, project);
        var xml = XDocument.Load(xmlPath).Root!.Element("arrangementProperties")!;
        Assert.Equal("1", xml.Attribute("syncopation")!.Value);
        Assert.Equal("1", xml.Attribute("pinchHarmonics")!.Value);
    }

    // ── measured against Editor on Fire's own output ────────────────────────

    private const string MissingCorpus =
        "No RS-XML corpus: TestData/EOF/**/*_RS2.xml is absent (TestData/ is git-ignored).";

    private static string? FindTestDataRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "TestData");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }

        return null;
    }

    private static List<string> CorpusFiles()
    {
        var root = FindTestDataRoot();
        if (root == null) return [];
        var eof = Path.Combine(root, "EOF");
        if (!Directory.Exists(eof)) return [];

        return Directory.EnumerateFiles(eof, "*_RS2.xml", SearchOption.AllDirectories)
            .Where(f => !f.Contains("debug", StringComparison.OrdinalIgnoreCase))
            .Where(f => !f.Contains("VOCAL", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    ///     The 21 flags where our derivation reproduces Editor on Fire's own answer
    ///     EXACTLY, measured over the 89 comparable arrangements of the hand-corrected
    ///     <c>TestData/EOF</c> corpus — 1,869 agreements, zero disagreements.
    /// </summary>
    private static readonly string[] MustMatchEof =
    [
        "represent", "bonusArr", "pathBass", "bassPick", "standardTuning",
        "nonStandardChords", "openChords", "palmMutes", "harmonics", "pinchHarmonics",
        "hopo", "tremolo", "slides", "tapping", "vibrato", "slapPop",
        "fifthsAndOctaves", "fingerPicking", "pickDirection", "twoFingerPicking",
        "syncopation"
    ];

    /// <summary>
    ///     Flags we deliberately report MORE often than EOF. Never fewer — under-
    ///     reporting would be a technique the chart really has and the game is not
    ///     told about. Measured over the same 89 arrangements; the counts are how
    ///     often we add one EOF missed:
    ///     <list type="bullet">
    ///         <item><c>unpitchedSlides</c> (70) and <c>fretHandMutes</c> (68) — EOF
    ///         hardcodes both to <c>"0"</c> in its export format string
    ///         (<c>rs.c:1988</c>) despite owning the data; the same files carry
    ///         <c>slideUnpitchTo</c> attributes and full mutes.</item>
    ///         <item><c>bends</c> (5) — EOF reads only the parent note's bend flag, so
    ///         a bend on a CHORD NOTE never reaches it (e.g. Tricot - Otozure, whose
    ///         two <c>&lt;chordNote … bend="1"&gt;</c> with step 1.0 export as
    ///         <c>bends="0"</c>).</item>
    ///         <item><c>barreChords</c> (30) — EOF's geometric test wants the lowest
    ///         fret to RETURN after a higher-fretted string, so a two-string barre is
    ///         invisible to it; we honour the fingering EOF itself wrote
    ///         (<c>finger0="1" finger1="1" fret0="2" fret1="2"</c> is one finger over
    ///         two strings).</item>
    ///         <item><c>pathLead</c> (42) / <c>pathRhythm</c> (16) — EOF emits 0 for
    ///         all three paths when the charter never set its per-track arrangement
    ///         type, which leaves the game with no route at all; we fall back to
    ///         <c>Arrangement.ArrangementType</c>, which the <c>&lt;arrangement&gt;</c>
    ///         element does carry.</item>
    ///         <item><c>sustain</c> (1) — a sustained CHORD counts here.</item>
    ///         <item><c>dropDPower</c> (5) — a per-chord flat-shape test instead of a
    ///         whole-track tuning AND.</item>
    ///         <item><c>doubleStops</c> (1) — otherwise exact.</item>
    ///     </list>
    ///     <c>powerChords</c> is the one flag where each tool finds a case the other
    ///     misses (we add 54, EOF adds 1: Yorushika - Snake, whose <c>E5</c> template
    ///     is never fully struck by any <c>&lt;chord&gt;</c>'s own chord notes, and
    ///     EOF reads its dictionary against the whole shape). So neither direction is
    ///     assertable for it — but <c>powerChords | doubleStops</c> is, and that OR is
    ///     exactly the term <c>CalculateDifficulties</c> weighs.
    /// </summary>
    private static readonly string[] NeverBelowEof =
    [
        "unpitchedSlides", "fretHandMutes", "bends", "barreChords", "pathLead",
        "pathRhythm", "sustain", "dropDPower", "doubleStops"
    ];

    /// <summary>
    ///     Re-derives every arrangement of the hand-corrected EOF corpus and compares
    ///     against the flags EOF itself wrote into the same file. This is the check
    ///     that keeps the predicates honest — a unit test can only prove the rule I
    ///     wrote matches the rule I meant, and this one caught a real over-claim (the
    ///     barre fallback ignoring authored fingering) that the hand-written tests
    ///     could not.
    /// </summary>
    [Fact]
    public void OnTheEofCorpus_TheDerivationReproducesEofsOwnAnswer()
    {
        var files = CorpusFiles();
        Assert.SkipWhen(files.Count == 0, MissingCorpus);

        var reader = new RsXmlSongReader();
        var mismatches = new List<string>();
        var compared = 0;

        foreach (var file in files)
        {
            ChartProject project;
            try { project = reader.Read(file); }
            catch (Exception) { continue; }
            if (project.Arrangements.Count == 0) continue;

            var authored = XDocument.Load(file).Root!.Element("arrangementProperties");
            if (authored == null) continue;

            var arrangement = project.Arrangements[0];
            // The reader fills Properties from the file, so re-deriving over it is
            // exactly the "does a save/export keep EOF's answer" question.
            var derived = ArrangementPropertiesDeriver.Derive(arrangement);
            compared++;
            var label = $"{Path.GetFileName(Path.GetDirectoryName(file))}/{Path.GetFileName(file)}";

            foreach (var flag in MustMatchEof)
            {
                var expected = int.Parse(authored.Attribute(flag)!.Value);
                var actual = FlagValue(derived, flag);
                if (expected != actual)
                    mismatches.Add($"{label}: {flag} EOF={expected} ours={actual} (must match)");
            }

            foreach (var flag in NeverBelowEof)
            {
                var expected = int.Parse(authored.Attribute(flag)!.Value);
                var actual = FlagValue(derived, flag);
                if (actual < expected)
                    mismatches.Add($"{label}: {flag} EOF={expected} ours={actual} (must never be lower)");
            }

            // The pair the difficulty formula ORs together — neither tool's individual
            // shape test wins, but the combined claim must not shrink.
            var eofChordish = Math.Max(int.Parse(authored.Attribute("powerChords")!.Value),
                int.Parse(authored.Attribute("doubleStops")!.Value));
            var ourChordish = Math.Max(derived.PowerChords, derived.DoubleStops);
            if (ourChordish < eofChordish)
                mismatches.Add($"{label}: powerChords|doubleStops EOF={eofChordish} ours={ourChordish}");
        }

        Assert.True(compared > 0, "the corpus produced no comparable arrangement");
        Assert.True(mismatches.Count == 0,
            $"{mismatches.Count} flag disagreements over {compared} arrangements:{Environment.NewLine}" +
            string.Join(Environment.NewLine, mismatches.Take(40)));
    }

    private static int FlagValue(ArrangementProperties p, string name) => name switch
    {
        "represent" => p.Represent,
        "bonusArr" => p.BonusArr,
        "standardTuning" => p.StandardTuning,
        "nonStandardChords" => p.NonStandardChords,
        "barreChords" => p.BarreChords,
        "powerChords" => p.PowerChords,
        "dropDPower" => p.DropDPower,
        "openChords" => p.OpenChords,
        "fingerPicking" => p.FingerPicking,
        "pickDirection" => p.PickDirection,
        "doubleStops" => p.DoubleStops,
        "palmMutes" => p.PalmMutes,
        "harmonics" => p.Harmonics,
        "pinchHarmonics" => p.PinchHarmonics,
        "hopo" => p.Hopo,
        "tremolo" => p.Tremolo,
        "slides" => p.Slides,
        "unpitchedSlides" => p.UnpitchedSlides,
        "bends" => p.Bends,
        "tapping" => p.Tapping,
        "vibrato" => p.Vibrato,
        "fretHandMutes" => p.FretHandMutes,
        "slapPop" => p.SlapPop,
        "twoFingerPicking" => p.TwoFingerPicking,
        "fifthsAndOctaves" => p.FifthsAndOctaves,
        "syncopation" => p.Syncopation,
        "bassPick" => p.BassPick,
        "sustain" => p.Sustain,
        "pathLead" => p.PathLead,
        "pathRhythm" => p.PathRhythm,
        "pathBass" => p.PathBass,
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "unknown flag")
    };
}
