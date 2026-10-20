using System.Xml.Linq;
using DotCharter.Services.Parsing.Xml.Song;

namespace DotCharter.Tests.Services.Export;

/// <summary>
/// RS-XML's <c>chordId</c> is a positional INDEX into <c>&lt;chordTemplates&gt;</c>,
/// not the domain <see cref="ChordTemplate.Id"/>: <c>RsXmlSongReader</c> assigns
/// <c>Id = id++</c> while walking the list. The writer used to emit the id, which
/// only agreed by coincidence — <c>ChordTemplateResolver.NextId</c> is
/// <c>max + 1</c> and undo paths remove templates by instance, so ids go
/// non-contiguous the moment a template is deleted. The SNG path has always used
/// an id-to-index map, and the two artifacts are built from the same chart.
/// </summary>
public sealed class RsXmlChordIdIndexTests
{
    private const double MsPerBeat = 500.0; // 120 BPM

    private static ChartProject BuildProject(int beatCount = 16)
    {
        var project = new ChartProject();
        for (var i = 0; i < beatCount; i++)
            project.Beats.Add(new BeatMarker { Time = i * MsPerBeat, Bpm = 120, IsMeasure = i % 4 == 0 });

        var arr = new Arrangement { ArrangementType = ArrangementType.Lead, StringCount = 6 };
        for (var s = 0; s < 6; s++) arr.TuningPitches.Add(new TuningOffset(0));
        arr.Levels.Add(new ChartLevel { DifficultyIndex = 0 });
        project.Arrangements.Add(arr);
        return project;
    }

    private static Arrangement Arr(ChartProject project) => project.Arrangements[0];

    private static ChartLevel Level(ChartProject project) => Arr(project).Levels[0];

    private static XElement Write(ChartProject project) => new RsXmlSongWriter().Write(project).Root!;

    private static List<XElement> Templates(XElement song)
        => song.Element("chordTemplates")!.Elements("chordTemplate").ToList();

    private static XElement ChordsEl(XElement song)
        => song.Element("levels")!.Element("level")!.Element("chords")!;

    private static List<XElement> Chords(XElement song) => ChordsEl(song).Elements("chord").ToList();

    private static List<XElement> HandShapes(XElement song)
        => song.Element("levels")!.Element("level")!.Element("handShapes")!.Elements("handShape").ToList();

    /// <remarks>
    ///     Fixtures here are THREE-string shapes on purpose.
    ///     <c>DoubleStopNameOptions.Default</c> suppresses the exported
    ///     <c>chordName</c> of every two-string shape (the 0.41 double-stop rule,
    ///     applied in <c>RsXmlSongWriter.WriteChordTemplate</c>), so a double stop
    ///     writes <c>chordName=""</c> however the <c>chordId</c> resolves — which
    ///     makes a name assertion over one vacuous in both directions.
    /// </remarks>
    private static ChordTemplate Template(int id, string name, params (int String, int Fret)[] frets)
    {
        var template = new ChordTemplate { Id = id, Name = name };
        foreach (var (stringIndex, fret) in frets) template.Frets[stringIndex] = fret;
        return template;
    }

    private static Chord MakeChord(int beat, int templateId, params (int String, int Fret)[] frets)
    {
        var chord = new Chord { Position = new FractionalPosition(beat), TemplateId = templateId };
        foreach (var (stringIndex, fret) in frets)
        {
            chord.ChordNotes.Add(new ChordNote
            {
                StringIndex = stringIndex,
                Fret = fret,
                Sustain = FractionalPosition.Zero
            });
        }

        return chord;
    }

    // ---- the sharp one: ids 0 and 5, the second template is INDEX 1 ----------

    [Fact]
    public void AChordOnANonContiguousTemplateId_WritesTheListIndex_NotTheId()
    {
        var project = BuildProject();
        // Ids are only contiguous by coincidence: NextId is max+1 and undo paths
        // remove templates by instance, so deleting templates 1..4 leaves 0 and 5.
        Arr(project).ChordTemplates.Add(Template(0, "E5", (0, 0), (1, 2), (2, 2)));
        Arr(project).ChordTemplates.Add(Template(5, "A5", (1, 0), (2, 2), (3, 2)));
        Level(project).Chords.Add(MakeChord(4, 5, (1, 0), (2, 2), (3, 2)));

        var song = Write(project);

        Assert.Equal(2, Templates(song).Count);
        Assert.Equal("A5", Templates(song)[1].Attribute("chordName")!.Value);

        var chord = Assert.Single(Chords(song));
        Assert.Equal("1", chord.Attribute("chordId")!.Value);
    }

    [Fact]
    public void AHandShapeOnANonContiguousTemplateId_WritesTheListIndex_NotTheId()
    {
        var project = BuildProject();
        Arr(project).ChordTemplates.Add(Template(0, "E5", (0, 0), (1, 2), (2, 2)));
        Arr(project).ChordTemplates.Add(Template(5, "A5", (1, 0), (2, 2), (3, 2)));
        Level(project).HandShapes.Add(new HandShape
        {
            Position = new FractionalPosition(4),
            EndPosition = new FractionalPosition(6),
            TemplateId = 5
        });

        var song = Write(project);

        var handShape = Assert.Single(HandShapes(song));
        Assert.Equal("1", handShape.Attribute("chordId")!.Value);
    }

    /// <summary>
    /// The whole point of the index: a chart written with the old id-as-index
    /// writer and read back names a DIFFERENT shape. This walks the real reader.
    /// </summary>
    [Fact]
    public void TheWrittenChordId_ResolvesToTheSameShape_AfterAReadBack()
    {
        var project = BuildProject();
        Arr(project).ChordTemplates.Add(Template(0, "E5", (0, 0), (1, 2), (2, 2)));
        Arr(project).ChordTemplates.Add(Template(5, "A5", (1, 0), (2, 2), (3, 2)));
        Level(project).Chords.Add(MakeChord(4, 5, (1, 0), (2, 2), (3, 2)));

        var path = Path.Combine(Path.GetTempPath(), $"dotcharter-chordid-{Guid.NewGuid():N}.xml");
        try
        {
            new RsXmlSongWriter().WriteTo(project, path);
            var reloaded = new RsXmlSongReader().Read(path);
            var arr = reloaded.Arrangements[0];

            var chord = Assert.Single(arr.Levels[0].Chords);
            var template = Assert.Single(arr.ChordTemplates, t => t.Id == chord.TemplateId);
            Assert.Equal("A5", template.Name);
            Assert.Equal(0, template.GetFret(1));
            Assert.Equal(2, template.GetFret(2));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // ---- the second half of the same line: a dangling id is not template 0 ---

    [Fact]
    public void AChordWithADanglingTemplateIdButChordNotes_GetsASynthesizedTemplate_NotChordIdZero()
    {
        var project = BuildProject();
        Arr(project).ChordTemplates.Add(Template(0, "E5", (0, 0), (1, 2), (2, 2)));
        // TemplateId 7 names nothing — the shape is only in the chord notes.
        Level(project).Chords.Add(MakeChord(4, 7, (2, 5), (3, 7)));

        var song = Write(project);

        // SngFromDomainMapper.BuildChordTemplates synthesizes a nameless template
        // for exactly this case; the XML now agrees instead of aliasing onto E5.
        var templates = Templates(song);
        Assert.Equal(2, templates.Count);
        Assert.Equal("", templates[1].Attribute("chordName")!.Value);
        Assert.Equal("5", templates[1].Attribute("fret2")!.Value);
        Assert.Equal("7", templates[1].Attribute("fret3")!.Value);

        var chord = Assert.Single(Chords(song));
        Assert.Equal("1", chord.Attribute("chordId")!.Value);
    }

    [Fact]
    public void AChordWithNeitherATemplateNorChordNotes_IsDropped_LikeTheSng()
    {
        var project = BuildProject();
        Arr(project).ChordTemplates.Add(Template(0, "E5", (0, 0), (1, 2), (2, 2)));
        Level(project).Chords.Add(new Chord { Position = new FractionalPosition(4), TemplateId = -1 });

        var song = Write(project);

        // The SNG drops it ("nothing playable is lost"); writing chordId="0"
        // shipped an unrelated shape at that beat instead.
        Assert.Empty(Chords(song));
        Assert.Equal("0", ChordsEl(song).Attribute("count")!.Value);
    }

    [Fact]
    public void AHandShapeWithADanglingTemplateId_WritesMinusOne_NotZero()
    {
        var project = BuildProject();
        Arr(project).ChordTemplates.Add(Template(0, "E5", (0, 0), (1, 2), (2, 2)));
        Level(project).HandShapes.Add(new HandShape
        {
            Position = new FractionalPosition(4),
            EndPosition = new FractionalPosition(6),
            TemplateId = 9
        });

        var song = Write(project);

        // SngFromDomainMapper.ConvertHandShape writes -1 into the fingerprint for
        // an unresolvable shape. "No shape" beats silently pointing at template 0.
        var handShape = Assert.Single(HandShapes(song));
        Assert.Equal("-1", handShape.Attribute("chordId")!.Value);
    }

    [Fact]
    public void ContiguousIdsStartingAtZero_AreUnchanged()
    {
        var project = BuildProject();
        Arr(project).ChordTemplates.Add(Template(0, "E5", (0, 0), (1, 2), (2, 2)));
        Arr(project).ChordTemplates.Add(Template(1, "A5", (1, 0), (2, 2)));
        Level(project).Chords.Add(MakeChord(4, 0, (0, 0), (1, 2)));
        Level(project).Chords.Add(MakeChord(8, 1, (1, 0), (2, 2)));

        var song = Write(project);

        Assert.Equal(new[] { "0", "1" }, Chords(song).Select(c => c.Attribute("chordId")!.Value).ToArray());
    }
}
