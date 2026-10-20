using System.Xml.Linq;
using DotCharter.Services.Editing.Collisions;
using DotCharter.Services.Parsing.Rscp;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Parsing.Rscp;

/// <summary>
///     A second batch of domain fields the rscp writer dropped, found by the
///     2026-07-25 export/save audit. Same shape of bug as the first batch
///     (<see cref="RscpLossyFieldRoundTripTests" />): the model carries the
///     field, the cloner clones it, importers populate it and exporters read
///     it — but "save, reopen" silently reset it to the default:
///     <list type="bullet">
///         <item><c>Arrangement.Name</c> — the parser had always READ the <c>name</c>
///               attribute; the writer refused to emit one.</item>
///         <item><c>ChartProject.IsBeatMapLocked</c> — a deliberate toggle guarding
///               eight destructive tempo-map operations.</item>
///         <item><c>ChartData.KeyAccidentals</c>/<c>KeyIsMinor</c> — the key signature.</item>
///         <item><c>Stem.Muted</c>/<c>Solo</c> — and a comment in StemTrackViewModel
///               asserting they WERE serialized.</item>
///         <item><c>ChordNote.BassPicking</c>/<c>Ignore</c> — written on soundNote
///               since forever, never on chordNote.</item>
///         <item><c>ChartLevel.FretHandMutes</c> — the templates were added in the
///               previous pass, the positions pointing at them were not.</item>
///         <item>The four per-arrangement metadata overrides.</item>
///     </list>
///     Every addition is ADDITIVE and absent at its default, so a chart that
///     never carried the field stays byte-identical — pinned below.
/// </summary>
public sealed class RscpLossyFieldRoundTripTests2 : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dotcharter_rscp_lossy2_").FullName;

    private string ProjectPath => Path.Combine(_dir, "project.rscp");

    public void Dispose() => BestEffortDelete.Directory(_dir);

    [Fact]
    public void ArrangementName_RoundTrips()
    {
        var project = new ChartProject();
        var arrangement = NewArrangement();
        arrangement.Name = "Lead (12-string)";
        arrangement.ArrangementType = ArrangementType.Lead;
        project.Arrangements.Add(arrangement);

        Assert.Equal("Lead (12-string)", SaveAndReload(project).Arrangements[0].Name);
    }

    /// <summary>
    ///     A name that already equals the type is what the parser falls back to,
    ///     so it must not be emitted — that is the byte-identical guarantee for
    ///     every chart written before the attribute existed.
    /// </summary>
    [Fact]
    public void ArrangementName_EqualToTheType_IsNotWritten()
    {
        var project = new ChartProject();
        var arrangement = NewArrangement();
        arrangement.ArrangementType = ArrangementType.Rhythm;
        arrangement.Name = "Rhythm";
        project.Arrangements.Add(arrangement);

        new RscpChartSerializer().Save(ProjectPath, project);

        Assert.Null(ArrangementNode().Attribute("name"));
        Assert.Equal("Rhythm", new RscpChartSerializer().Load(ProjectPath).Arrangements[0].Name);
    }

    [Fact]
    public void BeatMapLock_RoundTrips()
    {
        var project = new ChartProject { IsBeatMapLocked = true };
        project.Arrangements.Add(NewArrangement());

        Assert.True(SaveAndReload(project).IsBeatMapLocked);
    }

    [Fact]
    public void KeySignature_RoundTrips()
    {
        var project = new ChartProject();
        project.Chart.KeyAccidentals = -3;
        project.Chart.KeyIsMinor = true;
        project.Arrangements.Add(NewArrangement());

        var loaded = SaveAndReload(project);

        Assert.Equal(-3, loaded.Chart.KeyAccidentals);
        Assert.True(loaded.Chart.KeyIsMinor);
    }

    [Fact]
    public void StemMuteAndSolo_RoundTrip()
    {
        var project = new ChartProject();
        project.Arrangements.Add(NewArrangement());
        project.Stems.Add(new Stem { Name = "drums", Path = "drums.ogg", Muted = true });
        project.Stems.Add(new Stem { Name = "bass", Path = "bass.ogg", Solo = true });

        var loaded = SaveAndReload(project);

        Assert.True(loaded.Stems[0].Muted);
        Assert.False(loaded.Stems[0].Solo);
        Assert.False(loaded.Stems[1].Muted);
        Assert.True(loaded.Stems[1].Solo);
    }

    [Fact]
    public void ChordNoteBassPickingAndIgnore_RoundTrip()
    {
        var project = new ChartProject();
        var arrangement = NewArrangement();
        arrangement.ChordTemplates.Add(new ChordTemplate { Id = 0, Frets = { [0] = 3, [1] = 5 } });

        var chord = new Chord { TemplateId = 0 };
        chord.ChordNotes.Add(new ChordNote
        {
            StringIndex = 0, Fret = 3, BassPicking = BassPickingType.Slap, Ignore = true
        });
        chord.ChordNotes.Add(new ChordNote { StringIndex = 1, Fret = 5, BassPicking = BassPickingType.Pop });
        arrangement.Levels[0].Chords.Add(chord);
        project.Arrangements.Add(arrangement);

        var notes = SaveAndReload(project).Arrangements[0].Levels[0].Chords.Single().ChordNotes;

        Assert.Equal(BassPickingType.Slap, notes[0].BassPicking);
        Assert.True(notes[0].Ignore);
        Assert.Equal(BassPickingType.Pop, notes[1].BassPicking);
        Assert.False(notes[1].Ignore);
    }

    [Fact]
    public void LevelFretHandMutes_RoundTrip()
    {
        var project = new ChartProject();
        var arrangement = NewArrangement();
        arrangement.Levels[0].FretHandMutes.Add(new FretHandMute(new FractionalPosition(4)));
        arrangement.Levels[0].FretHandMutes.Add(new FretHandMute(new FractionalPosition(9)));
        project.Arrangements.Add(arrangement);

        var mutes = SaveAndReload(project).Arrangements[0].Levels[0].FretHandMutes;

        Assert.Equal(2, mutes.Count);
        Assert.Equal(4, mutes[0].Position.BeatId);
        Assert.Equal(9, mutes[1].Position.BeatId);
    }

    [Fact]
    public void PerArrangementMetadataOverrides_RoundTrip()
    {
        var project = new ChartProject();
        var arrangement = NewArrangement();
        arrangement.ArtistName = "The Band";
        arrangement.ArtistNameSort = "Band, The";
        arrangement.AlbumName = "Live At Home";
        arrangement.AlbumYear = 1974;
        project.Arrangements.Add(arrangement);

        var loaded = SaveAndReload(project).Arrangements[0];

        Assert.Equal("The Band", loaded.ArtistName);
        Assert.Equal("Band, The", loaded.ArtistNameSort);
        Assert.Equal("Live At Home", loaded.AlbumName);
        Assert.Equal(1974, loaded.AlbumYear);
    }

    [Fact]
    public void ChartsWithoutTheseFields_WriteNoNewNodesOrAttributes()
    {
        // The byte-identical guarantee: a plain chart must not grow any of the
        // new markup, or every existing project file churns on the next save.
        var project = new ChartProject();
        project.Arrangements.Add(NewArrangement());
        project.Stems.Add(new Stem());

        new RscpChartSerializer().Save(ProjectPath, project);
        var root = XDocument.Load(ProjectPath).Root!;

        Assert.Null(root.Element("dcBeatMapLocked"));
        Assert.Null(root.Element("dcKeyAccidentals"));
        Assert.Null(root.Element("dcKeyIsMinor"));

        var stem = root.Element("stems")!.Element("stem")!;
        Assert.Null(stem.Attribute("muted"));
        Assert.Null(stem.Attribute("solo"));

        var arrNode = ArrangementNode();
        foreach (var attribute in new[] { "name", "artistName", "artistNameSort", "albumName", "albumYear" })
            Assert.Null(arrNode.Attribute(attribute));
        Assert.Null(arrNode.Element("levels")!.Element("level")!.Element("dcFretHandMutes"));

        var text = File.ReadAllText(ProjectPath);
        Assert.DoesNotContain("bassPicking", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ignore", text, StringComparison.Ordinal);
    }

    /// <summary>
    ///     <c>ChordTemplate.ForceArpeggioInRs</c> is what makes a chord export as
    ///     an arpeggio (<c>RsXmlSongWriter</c> re-appends the <c>-arp</c> display
    ///     suffix from it, <c>SngFromDomainMapper</c> sets
    ///     <c>ChordMask.Arpeggio</c> from it) and the parser has always read
    ///     <c>forceRS</c> independently. The writer nested it inside
    ///     <c>if (tmpl.Arpeggio)</c> — so the ONE path that produces it,
    ///     <c>ChartSpacingEnforcer</c> folding a "-arp" name suffix into the flag
    ///     on every save, created and destroyed the marker in the same save.
    /// </summary>
    [Fact]
    public void ForceArpeggioInRs_RoundTrips_WithoutTheArpeggioFlag()
    {
        var project = new ChartProject();
        var arrangement = NewArrangement();
        arrangement.ChordTemplates.Add(new ChordTemplate
        {
            Id = 0, Name = "Cmaj", Arpeggio = false, ForceArpeggioInRs = true
        });
        project.Arrangements.Add(arrangement);

        var reloaded = SaveAndReload(project).Arrangements[0].ChordTemplates[0];

        Assert.True(reloaded.ForceArpeggioInRs);
        // …and it did NOT get promoted to a real arpeggio on the way: the two are
        // separate properties and only one of them was authored.
        Assert.False(reloaded.Arpeggio);
    }

    /// <summary>The whole gesture, end to end: an imported "-arp" name reaches
    /// the save, the enforcer folds it into the flag, and the flag survives the
    /// reload. This is the path that was losing the marker in production.</summary>
    [Fact]
    public void AnArpNameSuffix_BecomesTheFlag_AndSurvivesTheSave()
    {
        var arrangement = NewArrangement();
        arrangement.ChordTemplates.Add(new ChordTemplate { Id = 0, Name = "Cmaj-arp" });
        var project = new ChartProject();
        project.Arrangements.Add(arrangement);

        ChartSpacingEnforcer.EnforceProject(project, new AppConfig());

        var reloaded = SaveAndReload(project).Arrangements[0].ChordTemplates[0];

        Assert.Equal("Cmaj", reloaded.Name);
        Assert.True(reloaded.ForceArpeggioInRs);
    }

    /// <summary>Additive: absent at the default, so a chart that never carried an
    /// arpeggio marker stays byte-identical.</summary>
    [Fact]
    public void APlainTemplate_WritesNoArpeggioAttributes()
    {
        var project = new ChartProject();
        var arrangement = NewArrangement();
        arrangement.ChordTemplates.Add(new ChordTemplate { Id = 0, Name = "Cmaj" });
        project.Arrangements.Add(arrangement);

        new RscpChartSerializer().Save(ProjectPath, project);
        var template = ArrangementNode().Element("chordTemplates")!.Element("chordTemplate")!;

        Assert.Null(template.Attribute("arpeggio"));
        Assert.Null(template.Attribute("forceRS"));
    }

    private XElement ArrangementNode() => XDocument.Load(ProjectPath).Root!.Element("arrangements")!.Element("arrangement")!;

    private ChartProject SaveAndReload(ChartProject project)
    {
        new RscpChartSerializer().Save(ProjectPath, project);
        return new RscpChartSerializer().Load(ProjectPath);
    }

    private static Arrangement NewArrangement()
    {
        var arrangement = new Arrangement { Name = "Lead" };
        arrangement.Levels.Add(new ChartLevel());
        return arrangement;
    }
}
