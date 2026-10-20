using System.Xml.Linq;
using DotCharter.Services.Parsing.Rscp;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Parsing.Roundtrip;

[Collection("CwdSerialized")]
public sealed class RscpChartSerializerRoundtripTests : IDisposable
{
    private readonly string _tempDir;

    public RscpChartSerializerRoundtripTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "DotCharterTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose() => BestEffortDelete.Directory(_tempDir);

    private string TempPath(string name) => Path.Combine(_tempDir, name);

    private static ChartProject BuildSampleProject()
    {
        var project = new ChartProject
        {
            AudioFileName = "song.wav",
            Bpm = 132.0,
            TextContent = "demo notes"
        };
        project.Chart.Artist = "Test Artist";
        project.Chart.SongName = "Test Song";
        project.Chart.AlbumName = "Test Album";
        project.Chart.AlbumYear = "2024";

        for (var i = 0; i < 8; i++)
            project.Beats.Add(new BeatMarker { Time = i * 500, Bpm = 120, IsMeasure = i % 4 == 0 });

        var arr = new Arrangement
        {
            Name = "Lead Guitar",
            ArrangementType = ArrangementType.Lead,
            Capo = 2
        };
        for (var s = 0; s < 6; s++) arr.TuningPitches.Add(new TuningOffset(0));

        var level = new ChartLevel { DifficultyIndex = 0 };
        level.Notes.Add(new GuitarNote
        {
            Position = new FractionalPosition(1),
            Fret = 5,
            StringIndex = 2,
            Sustain = new FractionalPosition(0, new Fraction(1, 2))
        });
        var chord = new Chord { Position = new FractionalPosition(2) };
        chord.ChordNotes.Add(new ChordNote { StringIndex = 0, Fret = 3 });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 5 });
        level.Chords.Add(chord);
        arr.Levels.Add(level);

        project.Arrangements.Add(arr);
        return project;
    }

    /// <summary>
    ///     A phrase that stops at level 0 must come back stopping at level 0. The
    ///     writer used to leave a 0 max out, and an absent max reads back as NO
    ///     PhraseInfo — which every consumer treats as FULLY levelled — so a saved
    ///     imported ladder re-exported that phrase's copies into every level (#323).
    ///     A phrase that never had an info still writes no attribute.
    /// </summary>
    [Fact]
    public void A_phrase_max_difficulty_of_zero_survives_a_save()
    {
        var ser = new RscpChartSerializer();
        var project = BuildSampleProject();
        var arr = project.Arrangements[0];
        arr.Phrases.AddRange(["COUNT", "riff", "plain"]);
        arr.PhraseInfos["COUNT"] = new PhraseInfo(0, false);
        arr.PhraseInfos["riff"] = new PhraseInfo(3, false);
        var path = TempPath("phrase-max-zero.rscp");

        ser.Save(path, project);
        var loaded = ser.Load(path).Arrangements[0];

        Assert.True(loaded.PhraseInfos.TryGetValue("COUNT", out var count), "a max of 0 was dropped on save");
        Assert.Equal(0, count!.MaxDifficulty);
        Assert.Equal(3, loaded.PhraseInfos["riff"].MaxDifficulty);
        Assert.False(loaded.PhraseInfos.ContainsKey("plain"));
    }

    [Fact]
    public void Charter_round_trips_and_is_omitted_when_empty()
    {
        var ser = new RscpChartSerializer();

        var project = BuildSampleProject();
        project.Chart.Charter = "SomeName";
        var path = TempPath("charter.rscp");
        ser.Save(path, project);
        Assert.Equal("SomeName", ser.Load(path).Chart.Charter);

        // Empty charter writes no dcCharter element at all — plain rscharter
        // files must stay byte-identical.
        var barePath = TempPath("charter_bare.rscp");
        ser.Save(barePath, BuildSampleProject());
        Assert.DoesNotContain("dcCharter", File.ReadAllText(barePath));
    }

    [Fact]
    public void GenreReleaseDateAndCoverArt_RoundTrip_AndAreOmittedWhenEmpty()
    {
        var ser = new RscpChartSerializer();

        var project = BuildSampleProject();
        project.Chart.Genre = "Progressive Rock";
        project.Chart.ReleaseDate = "2007-08-15";
        project.Chart.CoverArtPath = "cover.png";
        var path = TempPath("metadata.rscp");
        ser.Save(path, project);

        var loaded = ser.Load(path);
        Assert.Equal("Progressive Rock", loaded.Chart.Genre);
        Assert.Equal("2007-08-15", loaded.Chart.ReleaseDate);
        Assert.Equal("cover.png", loaded.Chart.CoverArtPath);

        // Empty metadata writes no extension elements at all — plain rscharter
        // files stay byte-identical (additive-when-non-empty contract).
        var barePath = TempPath("metadata_bare.rscp");
        ser.Save(barePath, BuildSampleProject());
        var bare = File.ReadAllText(barePath);
        Assert.DoesNotContain("dcGenre", bare);
        Assert.DoesNotContain("dcReleaseDate", bare);
        Assert.DoesNotContain("dcCoverArt", bare);
    }

    [Fact]
    public void Version_RoundTrips_DefaultsToOne_AndIsOmittedAtOne()
    {
        var ser = new RscpChartSerializer();

        var project = BuildSampleProject();
        Assert.Equal("1", project.Chart.Version);
        var barePath = TempPath("version_bare.rscp");
        ser.Save(barePath, project);
        Assert.DoesNotContain("dcVersion", File.ReadAllText(barePath));
        Assert.Equal("1", ser.Load(barePath).Chart.Version);

        project.Chart.Version = " 2.5 ";
        var path = TempPath("version.rscp");
        ser.Save(path, project);
        Assert.Contains("<dcVersion>2.5</dcVersion>", File.ReadAllText(path));
        Assert.Equal("2.5", ser.Load(path).Chart.Version);

        // A blank element reads as the default, and a clone carries the version.
        var doc = System.Xml.Linq.XDocument.Load(path);
        doc.Root!.Element("dcVersion")!.Value = "  ";
        doc.Save(path);
        Assert.Equal("1", ser.Load(path).Chart.Version);
        project.Chart.Version = "7";
        Assert.Equal("7", DotCharter.Core.Utilities.ChartProjectCloner.Clone(project).Chart.Version);
    }

    /// <summary>The forward-compatibility half of the v3 stability contract
    /// (docs/RSCP-FORMAT-GUARANTEES.md): a NEWER build's additive fields — the
    /// nd/gf precedent — must be IGNORED by a build that predates them, never
    /// an error, with everything this build understands preserved.</summary>
    [Fact]
    public void Load_ToleratesUnknownElementsAndAttributes_FromANewerBuild()
    {
        var ser = new RscpChartSerializer();
        var path = TempPath("project.rscp");
        var original = BuildSampleProject();
        ser.Save(path, original);

        // Simulate the newer build: an unknown header block plus an unknown
        // attribute on an existing content element.
        var doc = System.Xml.Linq.XDocument.Load(path);
        doc.Root!.Add(new System.Xml.Linq.XElement("futureFeatureBlock",
            new System.Xml.Linq.XElement("futureChild", 1)));
        var attributed = doc.Root.Descendants().First(e => e.HasAttributes);
        attributed.Add(new System.Xml.Linq.XAttribute("futureAttr", "42"));
        doc.Save(path);

        var loaded = ser.Load(path);

        Assert.Equal(original.Arrangements.Count, loaded.Arrangements.Count);
        Assert.Equal(original.Arrangements[0].Levels[0].Notes.Count,
            loaded.Arrangements[0].Levels[0].Notes.Count);
        Assert.Equal(original.Arrangements[0].Levels[0].Chords.Count,
            loaded.Arrangements[0].Levels[0].Chords.Count);
        Assert.Equal(original.Beats.Count, loaded.Beats.Count);
        Assert.Equal(original.Chart.Artist, loaded.Chart.Artist);
    }

    [Fact]
    public void SaveLoad_PreservesHeaderAndCounts()
    {
        var ser = new RscpChartSerializer();
        var path = TempPath("project.rscp");
        var original = BuildSampleProject();

        ser.Save(path, original);
        Assert.True(File.Exists(path));

        var loaded = ser.Load(path);

        Assert.Equal(original.Chart.Artist, loaded.Chart.Artist);
        Assert.Equal(original.Chart.SongName, loaded.Chart.SongName);
        Assert.Equal(original.Chart.AlbumName, loaded.Chart.AlbumName);
        Assert.Equal(original.Chart.AlbumYear, loaded.Chart.AlbumYear);
        Assert.Equal(original.Beats.Count, loaded.Beats.Count);
        Assert.Equal(original.Arrangements.Count, loaded.Arrangements.Count);
    }

    [Fact]
    public void SaveLoad_PreservesArrangementContent()
    {
        var ser = new RscpChartSerializer();
        var path = TempPath("project.rscp");
        var original = BuildSampleProject();

        ser.Save(path, original);
        var loaded = ser.Load(path);

        var origArr = original.Arrangements[0];
        var loadedArr = loaded.Arrangements[0];

        Assert.Equal(origArr.ArrangementType, loadedArr.ArrangementType);
        Assert.Equal(origArr.Capo, loadedArr.Capo);
        Assert.Equal(origArr.Levels.Count, loadedArr.Levels.Count);

        var origLevel = origArr.Levels[0];
        var loadedLevel = loadedArr.Levels[0];
        Assert.Equal(origLevel.Notes.Count, loadedLevel.Notes.Count);
        Assert.Equal(origLevel.Chords.Count, loadedLevel.Chords.Count);

        var origNote = origLevel.Notes[0];
        var loadedNote = loadedLevel.Notes[0];
        Assert.Equal(origNote.Fret, loadedNote.Fret);
        Assert.Equal(origNote.StringIndex, loadedNote.StringIndex);
        Assert.Equal(origNote.Position, loadedNote.Position);
    }

    [Fact]
    public void SaveLoad_PreservesBeatTimes()
    {
        var ser = new RscpChartSerializer();
        var path = TempPath("project.rscp");
        var original = BuildSampleProject();

        ser.Save(path, original);
        var loaded = ser.Load(path);

        for (var i = 0; i < original.Beats.Count; i++)
            Assert.Equal(original.Beats[i].Time, loaded.Beats[i].Time, 3);
    }

    [Fact]
    public void Save_WritesValidXml()
    {
        var ser = new RscpChartSerializer();
        var path = TempPath("project.rscp");
        ser.Save(path, BuildSampleProject());

        var doc = XDocument.Load(path);
        Assert.NotNull(doc.Root);
        Assert.Equal("project", doc.Root!.Name.LocalName);
    }

    [Fact]
    public void SaveLoad_PreservesToneDefinitions()
    {
        var ser = new RscpChartSerializer();
        var path = TempPath("project.rscp");
        var original = BuildSampleProject();
        // Realistic body: quotes, braces and unicode must survive XML escaping.
        const string body = """{"GearList":{"Amp":{"Type":"Amps","KnobValues":{"Amp_Test_Gain":7.5},"Key":"Amp_Test"}},"Volume":"-12.000","Key":"dc_clean","Name":"Clean & Bright <ä>"}""";
        original.ToneDefinitions.Add(new ToneDefinition
        {
            Key = "dc_clean",
            Name = "Clean & Bright <ä>",
            Json = body
        });

        ser.Save(path, original);
        var loaded = ser.Load(path);

        var tone = Assert.Single(loaded.ToneDefinitions);
        Assert.Equal("dc_clean", tone.Key);
        Assert.Equal("Clean & Bright <ä>", tone.Name);
        Assert.Equal(body, tone.Json);
    }

    [Fact]
    public void Save_WithoutToneDefinitions_OmitsTheExtensionNode()
    {
        // rscharter's XStream reader may reject unknown elements — the
        // dcToneDefinitions node must only exist when there is content.
        var ser = new RscpChartSerializer();
        var path = TempPath("project.rscp");
        ser.Save(path, BuildSampleProject());

        var doc = XDocument.Load(path);
        Assert.Empty(doc.Descendants("dcToneDefinitions"));
    }

    [Fact]
    public void Load_NonexistentFile_Throws()
    {
        var ser = new RscpChartSerializer();
        Assert.Throws<FileNotFoundException>(() => ser.Load(TempPath("does-not-exist.rscp")));
    }

    [Fact]
    public void Load_EmptyArrangements_FabricatesDefault()
    {
        var ser = new RscpChartSerializer();
        var path = TempPath("project.rscp");
        var p = new ChartProject();
        p.Beats.Add(new BeatMarker { Time = 0 });
        ser.Save(path, p);

        var loaded = ser.Load(path);

        // Parser inserts a default Lead arrangement when the file has none.
        Assert.NotEmpty(loaded.Arrangements);
        Assert.Equal("Lead Guitar", loaded.Arrangements[0].Name);
    }

    [Fact]
    public void Save_DoesNotPersistInvalidChordTemplateFingers()
    {
        var ser = new RscpChartSerializer();
        var path = TempPath("project.rscp");
        var project = BuildSampleProject();
        var arrangement = project.Arrangements[0];

        arrangement.ChordTemplates.Add(new ChordTemplate
        {
            Id = 0,
            Name = "Em",
            Frets = new Dictionary<int, int> { [0] = 0, [1] = 2, [2] = 2 },
            Fingers = new Dictionary<int, int> { [0] = -1, [1] = 1, [2] = 2, [5] = 7 }
        });

        ser.Save(path, project);

        var doc = XDocument.Load(path);
        var chordTemplate = Assert.Single(
            doc.Descendants("chordTemplate"),
            node => node.Attribute("chordName")?.Value == "Em");

        Assert.Equal("1=1,2=2", chordTemplate.Attribute("fingers")?.Value);
    }

    [Fact]
    public void Load_IgnoresInvalidChordTemplateFingers()
    {
        var ser = new RscpChartSerializer();
        var path = TempPath("project.rscp");
        var project = BuildSampleProject();
        var arrangement = project.Arrangements[0];

        arrangement.ChordTemplates.Add(new ChordTemplate
        {
            Id = 0,
            Name = "Em",
            Frets = new Dictionary<int, int> { [0] = 0, [1] = 2, [2] = 2 },
            Fingers = new Dictionary<int, int> { [1] = 1, [2] = 2 }
        });

        ser.Save(path, project);

        var xml = File.ReadAllText(path);
        xml = xml.Replace("fingers=\"1=1,2=2\"", "fingers=\"0=-1,1=1,2=2,5=7\"");
        File.WriteAllText(path, xml);

        var loaded = ser.Load(path);
        var template = Assert.Single(loaded.Arrangements[0].ChordTemplates, t => t.Name == "Em");

        Assert.False(template.Fingers.ContainsKey(0));
        Assert.Equal(1, template.Fingers[1]);
        Assert.Equal(2, template.Fingers[2]);
        Assert.False(template.Fingers.ContainsKey(5));
    }

    // ─── Corrupt / truncated input: the crown-jewel save must fail LOUDLY ──────
    //     A torn .rscp (power cut, disk error, wrong file) has to raise a
    //     catchable exception, never hang or hand back a half-built project the
    //     caller would then save over the user's good data.

    [Fact]
    public void Load_EmptyFile_Throws()
    {
        var path = TempPath("empty.rscp");
        File.WriteAllBytes(path, []);
        Assert.ThrowsAny<System.Xml.XmlException>(() => new RscpChartSerializer().Load(path));
    }

    [Fact]
    public void Load_GarbageBytes_Throws()
    {
        var path = TempPath("garbage.rscp");
        File.WriteAllBytes(path, [0x00, 0xFF, 0x42, 0x13, 0x37, 0x00, 0x99]);
        Assert.ThrowsAny<System.Xml.XmlException>(() => new RscpChartSerializer().Load(path));
    }

    [Fact]
    public void Load_TruncatedMidElement_Throws()
    {
        var ser = new RscpChartSerializer();
        var path = TempPath("torn.rscp");
        ser.Save(path, BuildSampleProject());

        // Cut the file in half — a classic torn write: a valid prologue and an
        // unterminated element tree.
        var full = File.ReadAllText(path);
        File.WriteAllText(path, full[..(full.Length / 2)]);

        Assert.ThrowsAny<System.Xml.XmlException>(() => ser.Load(path));
    }

    [Fact]
    public void Load_DtdDoctype_IsRejected_NoXxe()
    {
        // The RSCP loader runs through SafeXml, so an external-entity payload
        // smuggled into a .rscp is refused at parse time (DtdProcessing.Prohibit)
        // rather than resolving a local file. Regression net for the XXE guard.
        var path = TempPath("xxe.rscp");
        File.WriteAllText(path,
            "<?xml version=\"1.0\"?><!DOCTYPE project [<!ENTITY x SYSTEM \"file:///etc/passwd\">]>" +
            "<project><chart>&x;</chart></project>");
        Assert.ThrowsAny<System.Xml.XmlException>(() => new RscpChartSerializer().Load(path));
    }
}
