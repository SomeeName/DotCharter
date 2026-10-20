using DotCharter.Services.Parsing.Xml;

namespace DotCharter.Tests.Services.Parsing.Xml;

/// <summary>
/// Verifies <see cref="RsXmlImporter"/> merges song / vocals / showlights RS-XML
/// documents into an existing <see cref="ChartProject"/> without disturbing
/// the host's audio, beats or other arrangements.
/// </summary>
public class RsXmlImporterTests
{
    private const string MissingSongXml =
        "No RS-XML song fixture: TestData/EOF/**/*_RS2.xml is absent (TestData/ is git-ignored).";

    [Fact]
    public void ImportInto_SongXml_AppendsArrangement()
    {
        var path = FindFirstFile("*_RS2.xml", excludeVocals: true);
        if (path == null) Assert.Skip(MissingSongXml);

        var host = new ChartProject { AudioFileName = "host.ogg" };
        host.Arrangements.Add(new Arrangement { Name = "Existing" });

        var result = new RsXmlImporter().ImportInto(host, path);

        Assert.Equal(RsXmlImportKind.Song, result.Kind);
        Assert.Equal(2, host.Arrangements.Count);
        Assert.Equal("Existing", host.Arrangements[0].Name);
        // Beats came from the imported XML because host had none.
        Assert.True(result.ReplacedHostBeats);
        Assert.NotEmpty(host.Beats);
        // Audio name preserved.
        Assert.Equal("host.ogg", host.AudioFileName);
    }

    [Fact]
    public void ImportInto_SongXml_PreservesHostBeatsWhenAlreadySet()
    {
        var path = FindFirstFile("*_RS2.xml", excludeVocals: true);
        if (path == null) Assert.Skip(MissingSongXml);

        var host = new ChartProject();
        host.Beats.Add(new BeatMarker { Time = 0, IsMeasure = true });
        host.Beats.Add(new BeatMarker { Time = 500 });

        var result = new RsXmlImporter().ImportInto(host, path);

        Assert.Equal(RsXmlImportKind.Song, result.Kind);
        Assert.False(result.ReplacedHostBeats);
        Assert.Equal(2, host.Beats.Count);
    }

    [Fact]
    public void ImportInto_SongXml_DisambiguatesNameCollision()
    {
        var path = FindFirstFile("*_RS2.xml", excludeVocals: true);
        if (path == null) Assert.Skip(MissingSongXml);

        // Pre-load the same file via reader to learn the name it would import as.
        var sampleProject = new XmlChartSerializer().Load(path);
        var importedName = sampleProject.Arrangements.First().Name;

        var host = new ChartProject();
        host.Arrangements.Add(new Arrangement { Name = importedName });

        var result = new RsXmlImporter().ImportInto(host, path);

        Assert.Equal(2, host.Arrangements.Count);
        Assert.NotEqual(importedName, host.Arrangements[1].Name);
        Assert.Contains("(2)", host.Arrangements[1].Name);
        Assert.Equal(host.Arrangements[1].Name, result.AddedArrangementName);
    }

    [Fact]
    public void ImportInto_VocalsXml_AddsVocalTrack()
    {
        var path = FindFirstVocalFile();
        if (path == null)
            Assert.Skip("No RS-XML vocals fixture: TestData/EOF/**/*VOCAL*.xml is absent (TestData/ is git-ignored).");

        var host = new ChartProject();
        // Provide beats so vocals are positioned correctly.
        for (var i = 0; i < 2000; i++)
            host.Beats.Add(new BeatMarker { Time = i * 500, IsMeasure = i % 4 == 0 });

        var result = new RsXmlImporter().ImportInto(host, path);

        Assert.Equal(RsXmlImportKind.Vocals, result.Kind);
        Assert.Single(host.VocalTracks);
        var track = host.VocalTracks[0];
        Assert.NotEmpty(track.Phrases);
        // The first phrase should aggregate at least one lyric event.
        Assert.NotEmpty(track.Phrases[0].Lyrics);
        // With external beats provided, at least one lyric lands beyond beat 0.
        Assert.Contains(track.Phrases[0].Lyrics, e => e.Position.BeatId > 0);
    }

    [Fact]
    public void ImportInto_ShowlightsXml_AppendsShowlights()
    {
        var path = FindFirstFile("auto_showlights.xml", excludeVocals: false)
                   ?? FindFirstFile("*showlights*.xml", excludeVocals: false);
        if (path == null)
            Assert.Skip("No RS-XML showlights fixture: TestData/EOF/**/*showlights*.xml is absent (TestData/ is git-ignored).");

        var host = new ChartProject();
        for (var i = 0; i < 2000; i++)
            host.Beats.Add(new BeatMarker { Time = i * 500, IsMeasure = i % 4 == 0 });
        var existingCount = host.Showlights.Count;

        var result = new RsXmlImporter().ImportInto(host, path);

        Assert.Equal(RsXmlImportKind.Showlights, result.Kind);
        Assert.True(host.Showlights.Count > existingCount);
        Assert.Equal(host.Showlights.Count - existingCount, result.AddedShowlights);
    }

    /// <summary>
    ///     A NON-ABRIDGED <c>&lt;chordTemplate&gt;</c> — what DLC Builder writes
    ///     whenever <c>UseAbridgedXml</c> is false — spells an unused string as
    ///     <c>fret{i}="-1"</c> rather than omitting the attribute. That is a
    ///     sentinel, not a fret: stored verbatim it makes <c>Frets.Count</c> 6, so
    ///     the E05 rule's "declares nothing" guard can never fire,
    ///     <c>GetLowestFret()</c> returns −1 into the anchor and fingering paths,
    ///     and <c>ChordTemplateResolver.Resolve</c>'s frets-keyed find-or-create
    ///     can never match — so every edit mints a duplicate template.
    ///     <para>
    ///         The EOF corpus omits the attributes entirely, which is exactly why
    ///         no fixture-driven test in this file has ever seen the case.
    ///     </para>
    /// </summary>
    [Fact]
    public void ImportInto_NonAbridgedChordTemplate_DropsTheUnusedStringSentinels()
    {
        const string xml =
            """
            <?xml version="1.0" encoding="utf-8"?>
            <song>
              <title>Non-abridged</title>
              <arrangement>Lead</arrangement>
              <ebeats count="2">
                <ebeat time="0.000" measure="1" />
                <ebeat time="0.500" measure="-1" />
              </ebeats>
              <chordTemplates count="1">
                <chordTemplate chordName="A5" displayName="A5"
                               finger0="-1" finger1="1" finger2="3"
                               finger3="-1" finger4="-1" finger5="-1"
                               fret0="-1" fret1="0" fret2="2"
                               fret3="-1" fret4="-1" fret5="-1" />
              </chordTemplates>
            </song>
            """;

        var tempPath = Path.Combine(Path.GetTempPath(), $"rsxml_nonabridged_{Guid.NewGuid():N}.xml");
        File.WriteAllText(tempPath, xml);
        try
        {
            var host = new ChartProject();
            var result = new RsXmlImporter().ImportInto(host, tempPath);

            Assert.Equal(RsXmlImportKind.Song, result.Kind);
            var template = Assert.Single(host.Arrangements[^1].ChordTemplates);

            Assert.Equal(new Dictionary<int, int> { [1] = 0, [2] = 2 }, template.Frets);
            // −1 would have been the answer while the sentinel was stored, and it
            // is what fed the anchor/fingering paths and the E05 message.
            Assert.Equal(0, template.GetLowestFret());
            // The fingering half of the same element has always filtered its own
            // sentinel; assert the two stay symmetric.
            Assert.Equal(new Dictionary<int, int> { [1] = 1, [2] = 3 }, template.Fingers);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    [Fact]
    public void ImportInto_UnknownRoot_Throws()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"rsxml_unknown_{Guid.NewGuid():N}.xml");
        File.WriteAllText(tempPath, "<bogus />");
        try
        {
            Assert.Throws<InvalidDataException>(() =>
                new RsXmlImporter().ImportInto(new ChartProject(), tempPath));
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    private static string? FindFirstFile(string pattern, bool excludeVocals)
    {
        var root = FindTestDataRoot();
        if (root == null) return null;
        var eof = Path.Combine(root, "EOF");
        if (!Directory.Exists(eof)) return null;

        foreach (var file in Directory.EnumerateFiles(eof, pattern, SearchOption.AllDirectories))
        {
            if (file.Contains("debug", StringComparison.OrdinalIgnoreCase)) continue;
            if (excludeVocals && file.Contains("VOCAL", StringComparison.OrdinalIgnoreCase)) continue;
            return file;
        }
        return null;
    }

    private static string? FindFirstVocalFile()
    {
        var root = FindTestDataRoot();
        if (root == null) return null;
        var eof = Path.Combine(root, "EOF");
        if (!Directory.Exists(eof)) return null;

        foreach (var file in Directory.EnumerateFiles(eof, "*VOCAL*.xml", SearchOption.AllDirectories))
        {
            if (file.Contains("debug", StringComparison.OrdinalIgnoreCase)) continue;
            // Confirm root is <vocals>
            try
            {
                var first = File.ReadLines(file).Take(20).FirstOrDefault(l => l.Contains("<vocals"));
                if (first != null) return file;
            }
            catch { /* skip */ }
        }
        return null;
    }

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
}
