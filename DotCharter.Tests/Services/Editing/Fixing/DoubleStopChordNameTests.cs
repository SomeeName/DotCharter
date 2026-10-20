using DotCharter.Services.Editing.Fixing;
using DotCharter.Services.Export.Sng;
using DotCharter.Services.Parsing.Xml.Song;

namespace DotCharter.Tests.Services.Editing.Fixing;

/// <summary>
///     Two pitches do not determine a chord, so a detected name for a two-string
///     shape is a guess. These pin the three places that has to hold: the shape
///     never gets an automatic name, a name that arrived some other way can be
///     cleared chart-wide, and neither export ships one.
/// </summary>
public sealed class DoubleStopChordNameTests
{
    private static Arrangement Guitar() => new()
    {
        StringCount = 6,
        Tuning = ["E", "A", "D", "G", "B", "E"],
    };

    private static ChordTemplate Template(int id, string name, params (int String, int Fret)[] frets)
    {
        var template = new ChordTemplate { Id = id, Name = name };
        foreach (var (stringIndex, fret) in frets) template.Frets[stringIndex] = fret;
        return template;
    }

    // ---- the chart-wide tool ----------------------------------------------

    [Fact]
    public void StripClearsATwoStringNameAndLeavesTheShapeAlone()
    {
        var arrangement = Guitar();
        var template = Template(0, "Bmin", (3, 4), (4, 3));
        arrangement.ChordTemplates.Add(template);

        var cleared = DoubleStopChordNames.Strip(arrangement, DoubleStopNameOptions.Default);

        Assert.Equal(1, cleared);
        Assert.Equal("", template.Name);
        // The name is the ONLY thing that may change — no fret moves, which is
        // what makes editing a shared template in place safe here.
        Assert.Equal(new Dictionary<int, int> { [3] = 4, [4] = 3 }, template.Frets);
    }

    [Fact]
    public void AThreeStringChordKeepsItsName()
    {
        var arrangement = Guitar();
        var template = Template(0, "C7sus4", (1, 3), (2, 3), (3, 3), (4, 8));
        arrangement.ChordTemplates.Add(template);

        Assert.Equal(0, DoubleStopChordNames.Strip(arrangement, DoubleStopNameOptions.Default));
        Assert.Equal("C7sus4", template.Name);
    }

    [Fact]
    public void ByDefaultAPowerChordLosesItsNameToo()
    {
        var arrangement = Guitar();
        var template = Template(0, "G5", (2, 0), (3, 0));
        arrangement.ChordTemplates.Add(template);

        Assert.Equal(1, DoubleStopChordNames.Strip(arrangement, DoubleStopNameOptions.Default));
        Assert.Equal("", template.Name);
    }

    [Fact]
    public void PowerChordsSurviveWhenTheyAreExempted()
    {
        var arrangement = Guitar();
        var power = Template(0, "G5", (2, 0), (3, 0));
        var third = Template(1, "Bmin", (3, 4), (4, 3));
        arrangement.ChordTemplates.Add(power);
        arrangement.ChordTemplates.Add(third);

        var cleared = DoubleStopChordNames.Strip(
            arrangement, new DoubleStopNameOptions(Suppress: true, KeepPowerChordNames: true));

        Assert.Equal(1, cleared);
        Assert.Equal("G5", power.Name);
        Assert.Equal("", third.Name);
    }

    [Fact]
    public void AnAlreadyBlankNameIsNotCounted()
    {
        var arrangement = Guitar();
        arrangement.ChordTemplates.Add(Template(0, "", (3, 4), (4, 3)));

        Assert.Equal(0, DoubleStopChordNames.Strip(arrangement, DoubleStopNameOptions.Default));
    }

    /// <summary>The writer emits mute templates through the same element, so they need the same strip.</summary>
    [Fact]
    public void FretHandMuteTemplatesAreCoveredToo()
    {
        var arrangement = Guitar();
        arrangement.FretHandMuteTemplates.Add(Template(0, "Bmin", (3, 4), (4, 3)));

        Assert.Equal(1, DoubleStopChordNames.Strip(arrangement, DoubleStopNameOptions.Default));
    }

    [Fact]
    public void StripRunsOverEveryArrangementOfTheProject()
    {
        var project = new ChartProject();
        for (var i = 0; i < 3; i++)
        {
            var arrangement = Guitar();
            arrangement.ChordTemplates.Add(Template(0, "Bmin", (3, 4), (4, 3)));
            project.Arrangements.Add(arrangement);
        }

        Assert.Equal(3, DoubleStopChordNames.Strip(project, DoubleStopNameOptions.Default));
    }

    // ---- the export boundary ----------------------------------------------
    //
    // A name reaches a template from an import, a paste, or a chart authored
    // before the suppression existed. Turning the setting on has to affect what
    // SHIPS, not only what the namer mints next.

    private static ChartProject ProjectWith(params ChordTemplate[] templates)
    {
        var arrangement = Guitar();
        foreach (var template in templates) arrangement.ChordTemplates.Add(template);

        var project = new ChartProject();
        project.Arrangements.Add(arrangement);
        project.Beats.Add(new BeatMarker { Time = 0, Bpm = 120 });
        project.Beats.Add(new BeatMarker { Time = 500, Bpm = 120 });
        return project;
    }

    [Fact]
    public void TheRsXmlWriterShipsADoubleStopUnnamed()
    {
        var project = ProjectWith(Template(0, "Bmin", (3, 4), (4, 3)));

        var doc = new RsXmlSongWriter().Write(project);

        var written = doc.Descendants("chordTemplate").Single();
        Assert.Equal("", written.Attribute("chordName")!.Value);
        Assert.Equal("", written.Attribute("displayName")!.Value);
    }

    /// <summary>
    ///     The "-arp" suffix is derived FROM the name, so the strip has to happen
    ///     first — otherwise an arpeggio double stop exports as the bare marker.
    /// </summary>
    [Fact]
    public void AnArpeggioDoubleStopDoesNotShipAsABareArpMarker()
    {
        var template = Template(0, "Bmin", (3, 4), (4, 3));
        template.Arpeggio = true;

        var doc = new RsXmlSongWriter().Write(ProjectWith(template));

        var written = doc.Descendants("chordTemplate").Single();
        Assert.Equal("", written.Attribute("chordName")!.Value);
        Assert.Equal("-arp", written.Attribute("displayName")!.Value);
    }

    [Fact]
    public void TheRsXmlWriterKeepsAThreeStringName()
    {
        var project = ProjectWith(Template(0, "C7sus4", (1, 3), (2, 3), (3, 3), (4, 8)));

        var doc = new RsXmlSongWriter().Write(project);

        Assert.Equal("C7sus4", doc.Descendants("chordTemplate").Single().Attribute("chordName")!.Value);
    }

    [Fact]
    public void TheRsXmlWriterHonoursThePowerChordExemption()
    {
        var project = ProjectWith(Template(0, "G5", (2, 0), (3, 0)));
        var writer = new RsXmlSongWriter
        {
            ChordNameOptions = new DoubleStopNameOptions(Suppress: true, KeepPowerChordNames: true),
        };

        var doc = writer.Write(project);

        Assert.Equal("G5", doc.Descendants("chordTemplate").Single().Attribute("chordName")!.Value);
    }

    [Fact]
    public void TheRsXmlWriterCanBeToldToShipEveryName()
    {
        var project = ProjectWith(Template(0, "Bmin", (3, 4), (4, 3)));
        var writer = new RsXmlSongWriter { ChordNameOptions = DoubleStopNameOptions.NameEverything };

        var doc = writer.Write(project);

        Assert.Equal("Bmin", doc.Descendants("chordTemplate").Single().Attribute("chordName")!.Value);
    }

    /// <summary>
    ///     The SNG is what the GAME reads and the XML is what DLC Builder reads —
    ///     built from one chart, they must not disagree about a name.
    /// </summary>
    [Fact]
    public void TheSngShipsTheSameNameAsTheXml()
    {
        var project = ProjectWith(Template(0, "Bmin", (3, 4), (4, 3)));
        var arrangement = project.Arrangements[0];

        var sng = SngFromDomainMapper.BuildInstrumental(arrangement, project.Beats, 1000).Sng;
        var xml = new RsXmlSongWriter().Write(project);

        Assert.Equal("", sng.Chords.Single().Name);
        Assert.Equal("", xml.Descendants("chordTemplate").Single().Attribute("chordName")!.Value);
    }

    /// <summary>
    ///     "-nop" is a NAME suffix carrying a mask bit, so it has to be read off the
    ///     authored name — reading the stripped one would silently drop the flag.
    /// </summary>
    [Fact]
    public void TheNopMaskSurvivesTheStrip()
    {
        var project = ProjectWith(Template(0, "Bmin-nop", (3, 4), (4, 3)));

        var sng = SngFromDomainMapper.BuildInstrumental(
            project.Arrangements[0], project.Beats, 1000).Sng;

        var chord = sng.Chords.Single();
        Assert.Equal("", chord.Name);
        Assert.Equal(0x2u, chord.Mask & 0x2u);
    }
}
