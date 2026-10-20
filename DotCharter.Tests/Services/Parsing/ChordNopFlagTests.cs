using System.Xml.Linq;
using DotCharter.Services.Editing.Collisions;
using DotCharter.Services.Editing.Fingering;
using DotCharter.Services.Parsing.Eof;
using DotCharter.Services.Parsing.Rscp;
using DotCharter.Services.Parsing.Xml.Song;

namespace DotCharter.Tests.Services.Parsing;

/// <summary>The tests of <see cref="ChordNopFlagTests" /> that need no package reader; its fixtures are in <c>ChordNopFlagTests.Fixtures.cs</c>.</summary>
public sealed partial class ChordNopFlagTests
{
    // ---- RS-XML -------------------------------------------------------------

    [Fact]
    public void RsXml_ANopDisplayName_SetsTheField_AndTheNameStaysClean()
    {
        // What DLC Builder writes for a Nop shape.
        var doc = new RsXmlSongWriter().Write(ProjectWith(Shape("Bm")));
        var ct = doc.Descendants("chordTemplate").Single();
        ct.SetAttributeValue("chordName", "Bm");
        ct.SetAttributeValue("displayName", "Bm-nop");

        var read = new RsXmlSongReader().ReadDocument(doc, "song.xml").Arrangements[0].ChordTemplates.Single();

        Assert.Equal("Bm", read.Name);
        Assert.True(read.NopInRs);
    }

    [Fact]
    public void RsXml_RoundTrip_WritesTheSuffixBackFromTheField()
    {
        var project = ProjectWith(Shape("Bm", nop: true));

        var written = WrittenTemplate(project);
        Assert.Equal("Bm", written.Attribute("chordName")!.Value);
        Assert.Equal("Bm-nop", written.Attribute("displayName")!.Value);

        var read = new RsXmlSongReader().ReadDocument(new RsXmlSongWriter().Write(project), "song.xml")
            .Arrangements[0].ChordTemplates.Single();
        Assert.Equal("Bm", read.Name);
        Assert.True(read.NopInRs);
        Assert.Equal("Bm-nop", WrittenTemplate(ProjectWith(read)).Attribute("displayName")!.Value);
    }

    /// <summary>A "-nop" that reached chordName (some CDLC carry it there) is name
    /// text nowhere in the app: folded too.</summary>
    [Fact]
    public void RsXml_ANopChordName_IsFoldedToo()
    {
        var doc = new RsXmlSongWriter().Write(ProjectWith(Shape("Bm")));
        var ct = doc.Descendants("chordTemplate").Single();
        ct.SetAttributeValue("chordName", "Bm-nop");
        ct.SetAttributeValue("displayName", "Bm-nop");

        var read = new RsXmlSongReader().ReadDocument(doc, "song.xml").Arrangements[0].ChordTemplates.Single();

        Assert.Equal("Bm", read.Name);
        Assert.True(read.NopInRs);
    }

    [Fact]
    public void RsXml_APlainTemplate_WritesNoSuffix()
    {
        var written = WrittenTemplate(ProjectWith(Shape("Bm")));

        Assert.Equal("Bm", written.Attribute("displayName")!.Value);
    }

    /// <summary>One display name holds one suffix, and the game's converters test
    /// <c>-arp</c> first, so an arpeggio wins in the XML (the SNG keeps both bits).</summary>
    [Fact]
    public void RsXml_AnArpeggioWithNop_ShipsTheArpeggioSuffix()
    {
        var template = Shape("Bm", nop: true);
        template.ForceArpeggioInRs = true;

        Assert.Equal("Bm-arp", WrittenTemplate(ProjectWith(template)).Attribute("displayName")!.Value);
        Assert.Equal(0x3u, SngTemplate(ProjectWith(template)).Mask & 0x3u);
    }

    // ---- SNG ------------------------------------------------------------------

    /// <summary>The mask bit comes from the FIELD: a name with no suffix at all
    /// still ships Nop, and the SNG name the game prints carries no suffix.</summary>
    [Fact]
    public void Sng_TheMaskComesFromTheField_NotTheName()
    {
        var withField = SngTemplate(ProjectWith(Shape("Renamed", nop: true)));
        Assert.Equal(0x2u, withField.Mask & 0x2u);
        Assert.Equal("Renamed", withField.Name);

        var withoutField = SngTemplate(ProjectWith(Shape("Renamed")));
        Assert.Equal(0u, withoutField.Mask & 0x2u);
    }

    /// <summary>A "-nop" typed into a name that no save has folded yet still ships
    /// the flag, once, and never as name text.</summary>
    [Fact]
    public void Sng_AnUnfoldedNameSuffix_StillShipsTheFlag_WithoutTheText()
    {
        var sng = SngTemplate(ProjectWith(Shape("Bm-nop")));
        Assert.Equal(0x2u, sng.Mask & 0x2u);
        Assert.Equal("Bm", sng.Name);

        Assert.Equal("Bm-nop", WrittenTemplate(ProjectWith(Shape("Bm-nop"))).Attribute("displayName")!.Value);
    }

    // ---- .rscp ----------------------------------------------------------------

    [Fact]
    public void Rscp_RoundTripsTheField()
    {
        var path = Path.Combine(_dir, "p.rscp");
        new RscpChartSerializer().Save(path, ProjectWith(Shape("Bm", nop: true)));

        var reloaded = new RscpChartSerializer().Load(path).Arrangements[0].ChordTemplates.Single();

        Assert.True(reloaded.NopInRs);
        Assert.Equal("Bm", reloaded.Name);
    }

    /// <summary>Additive: a template without the flag writes no attribute, so a
    /// project that never had one stays byte-identical.</summary>
    [Fact]
    public void Rscp_APlainTemplate_WritesNoNopAttribute()
    {
        var path = Path.Combine(_dir, "p.rscp");
        new RscpChartSerializer().Save(path, ProjectWith(Shape("Bm")));

        var template = XDocument.Load(path).Descendants("chordTemplate").Single();
        Assert.Null(template.Attribute("nop"));
        Assert.False(new RscpChartSerializer().Load(path).Arrangements[0].ChordTemplates.Single().NopInRs);
    }

    /// <summary>An old project whose name carries the typed suffix: the save folds it
    /// into the field (the enforcer runs on every save) and the field survives.</summary>
    [Fact]
    public void Rscp_ALegacyNameSuffix_BecomesTheField_AndSurvivesTheSave()
    {
        var project = ProjectWith(Shape("Bm-nop"));
        ChartSpacingEnforcer.EnforceProject(project, new AppConfig());

        var path = Path.Combine(_dir, "p.rscp");
        new RscpChartSerializer().Save(path, project);
        var reloaded = new RscpChartSerializer().Load(path).Arrangements[0].ChordTemplates.Single();

        Assert.Equal("Bm", reloaded.Name);
        Assert.True(reloaded.NopInRs);
    }

    // ---- .eof ---------------------------------------------------------------------

    /// <summary>EOF has no Nop field: the save companion spells it as the "-nop" name
    /// text EOF's own RS export reads, and the import folds it back.</summary>
    [Fact]
    public void Eof_RoundTripsTheFlagThroughTheNameText()
    {
        var project = new ChartProject { AudioFileName = "song.wav" };
        for (var i = 0; i < 16; i++)
            project.Beats.Add(new BeatMarker { Time = i * 500.0, Bpm = 120.0, BeatsInMeasure = 4, NoteDenominator = 4, IsMeasure = i % 4 == 0 });
        var arrangement = new Arrangement { Name = "Lead", ArrangementType = ArrangementType.Lead, StringCount = 6 };
        arrangement.TuningPitches.Clear();
        for (var s = 0; s < 6; s++) arrangement.TuningPitches.Add(new TuningOffset(0));
        var template = Shape("Bm", nop: true);
        arrangement.ChordTemplates.Add(template);
        var level = new ChartLevel { DifficultyIndex = 0 };
        var chord = new Chord { Position = new FractionalPosition(4), TemplateId = 0 };
        foreach (var (stringIndex, fret) in template.Frets) chord.ChordNotes.Add(new ChordNote { StringIndex = stringIndex, Fret = fret });
        level.Chords.Add(chord);
        arrangement.Levels.Add(level);
        project.Arrangements.Add(arrangement);

        var path = Path.Combine(_dir, "p.eof");
        File.WriteAllBytes(path, EofExporter.Export(project).Bytes);
        var importer = new EofImporter();
        var imported = importer.ImportTracks(path, importer.GetAvailableTracks(path).Select(t => t.Id).ToList()).Single();
        var back = imported.ChordTemplates.Single(t => t.Id == imported.Levels[0].Chords.Single().TemplateId);

        Assert.Equal("Bm", back.Name);
        Assert.True(back.NopInRs);
    }

    // ---- identity ---------------------------------------------------------------

    /// <summary>The flag changes the export, so value equality and the panel's
    /// "identical template" key both see it — else a Nop-only edit resolved
    /// straight back onto the template it came from and was dropped.</summary>
    [Fact]
    public void Identity_TheFlagIsPartOfEqualityAndTheIdenticalTemplateKey()
    {
        var plain = Shape("Bm");
        var nop = Shape("Bm", nop: true);
        Assert.NotEqual(plain, nop);
        Assert.NotEqual(plain.GetHashCode(), nop.GetHashCode());
        Assert.Equal(nop, nop.Clone());

        var arrangement = Guitar();
        arrangement.ChordTemplates.Add(plain);
        var service = new FingeringGenerationService();
        Assert.Null(service.FindIdenticalTemplate(arrangement, nop));
        Assert.Same(plain, service.FindIdenticalTemplate(arrangement, Shape("Bm")));
    }
}
