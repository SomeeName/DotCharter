using System.Xml.Linq;
using DotCharter.Services.Parsing.Rscp;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Parsing.Rscp;

/// <summary>
///     <c>RscpTemplateManager.LinkHandShapesToTemplates</c> runs from
///     <c>RscpWriter.BuildArrangementsNode</c> on EVERY save and used to reassign
///     every hand shape's <c>TemplateId</c> unconditionally — to the first chord
///     inside its span, or to <c>0</c> when no chord lay inside. Both halves
///     destroyed authored data, and the second one hit exactly the shapes 0.34.1
///     had just started building real templates for (let-ring runs over single
///     notes). It also renamed a SHARED <c>ChordTemplate</c> in place.
/// </summary>
public sealed class RscpHandShapeTemplateTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dotcharter_rscp_handshape_").FullName;

    private string ProjectPath => Path.Combine(_dir, "project.rscp");

    public void Dispose() => BestEffortDelete.Directory(_dir);

    /// <summary>
    ///     The headline loss: a let-ring / single-note-run hand shape carries a
    ///     find-or-created, charting-ready template and has NO chord inside its
    ///     span, so the old <c>else hs.TemplateId = 0</c> discarded it and the
    ///     shape reopened wearing an unrelated chord's frets, fingering and name.
    /// </summary>
    [Fact]
    public void AHandShapeWithNoChordInside_KeepsItsOwnTemplate_AcrossASave()
    {
        var project = new ChartProject();
        var arrangement = NewArrangement();

        // 0 = the chord's shape, 1 = the let-ring run's aggregate shape.
        arrangement.ChordTemplates.Add(NewTemplate(0, "Am", (1, 0), (2, 2)));
        arrangement.ChordTemplates.Add(NewTemplate(1, "Dsus4", (3, 7), (4, 7)));

        var level = arrangement.Levels[0];
        level.Chords.Add(NewChord(new FractionalPosition(0), 0, (1, 0), (2, 2)));
        // A single-note run under the bar — no chord anywhere inside it.
        level.Notes.Add(new GuitarNote { Position = new FractionalPosition(4), StringIndex = 3, Fret = 7 });
        level.Notes.Add(new GuitarNote { Position = new FractionalPosition(5), StringIndex = 4, Fret = 7 });
        level.HandShapes.Add(new HandShape
        {
            Position = new FractionalPosition(4),
            EndPosition = new FractionalPosition(6),
            TemplateId = 1
        });
        project.Arrangements.Add(arrangement);

        var loaded = SaveAndReload(project).Arrangements[0];

        Assert.Equal(1, loaded.Levels[0].HandShapes.Single().TemplateId);
        Assert.Equal("Dsus4", loaded.ChordTemplates[1].Name);
    }

    /// <summary>
    ///     ChartSpacingEnforcer's documented contract: a chord's hand shape keeps
    ///     its own FULL-shape template while the chord itself is repointed at a
    ///     reduced one (the RS hand-shape bar describes the fingering region, not
    ///     the individual strum). The writer used to undo that two passes later by
    ///     forcing the shape onto the chord's template.
    /// </summary>
    [Fact]
    public void AHandShapeOverAChord_IsNotRepointedAtTheChordsReducedTemplate()
    {
        var arrangement = NewArrangement();
        arrangement.ChordTemplates.Add(NewTemplate(0, "Am-reduced", (1, 0)));
        arrangement.ChordTemplates.Add(NewTemplate(1, "Am", (1, 0), (2, 2)));

        var level = arrangement.Levels[0];
        level.Chords.Add(NewChord(new FractionalPosition(0), 0, (1, 0)));
        level.HandShapes.Add(new HandShape
        {
            Position = new FractionalPosition(0),
            EndPosition = new FractionalPosition(2),
            TemplateId = 1
        });

        new RscpTemplateManager().RegenerateTemplates(arrangement);

        Assert.Equal(1, level.HandShapes.Single().TemplateId);
    }

    /// <summary>
    ///     A ChordTemplate is SHARED — never mutate one in place. The old
    ///     <c>SynchronizeHandShapeAndTemplateName</c> copied the hand shape's name
    ///     onto the template, renaming it for every other chord and shape using
    ///     that shape.
    /// </summary>
    [Fact]
    public void AHandShapeName_NeverRenamesTheSharedChordTemplate()
    {
        var arrangement = NewArrangement();
        arrangement.ChordTemplates.Add(NewTemplate(0, "", (1, 0), (2, 2)));

        var level = arrangement.Levels[0];
        level.Chords.Add(NewChord(new FractionalPosition(0), 0, (1, 0), (2, 2)));
        // -1 is the "untemplated" sentinel, so this shape DOES adopt the chord's
        // template — and must still not write its own name onto it.
        level.HandShapes.Add(new HandShape
        {
            Position = new FractionalPosition(0),
            EndPosition = new FractionalPosition(2),
            TemplateId = -1,
            Name = "Cadd9"
        });

        new RscpTemplateManager().RegenerateTemplates(arrangement);

        Assert.Equal(0, level.HandShapes.Single().TemplateId);
        Assert.Equal("", arrangement.ChordTemplates[0].Name);
    }

    /// <summary>
    ///     The untemplated sentinel stays a sentinel: <c>0</c> is a REAL template
    ///     id and must never be invented as a fallback.
    /// </summary>
    [Fact]
    public void AnUntemplatedShapeWithNoChordInside_KeepsTheMinusOneSentinel()
    {
        var arrangement = NewArrangement();
        arrangement.ChordTemplates.Add(NewTemplate(0, "Am", (1, 0), (2, 2)));

        var level = arrangement.Levels[0];
        level.HandShapes.Add(new HandShape
        {
            Position = new FractionalPosition(4),
            EndPosition = new FractionalPosition(6),
            TemplateId = -1
        });

        new RscpTemplateManager().RegenerateTemplates(arrangement);

        Assert.Equal(-1, level.HandShapes.Single().TemplateId);
    }

    /// <summary>
    ///     Find-or-create on the save path matches FRETS within one ARPEGGIO
    ///     flavour — the documented rule. The arpeggio half used to be missing, so
    ///     an untemplated (freshly imported) chord whose shape only an ARPEGGIO
    ///     template described was repointed at it: <c>ChordTemplate.Arpeggio</c>
    ///     sets <c>ChordMask.Arpeggio</c> in the SNG export, picks the arpeggio
    ///     section type in <c>.eof</c> and draws purple arpeggio brackets instead of
    ///     a hand-shape bar, so the chord silently became an arpeggio on save.
    /// </summary>
    [Fact]
    public void AnUntemplatedChord_IsNotRepointedAtAnArpeggioTemplateWithTheSameFrets()
    {
        var arrangement = NewArrangement();
        var arpeggio = NewTemplate(0, "Am", (1, 0), (2, 2));
        arpeggio.Arpeggio = true;
        arrangement.ChordTemplates.Add(arpeggio);

        var level = arrangement.Levels[0];
        level.Chords.Add(NewChord(new FractionalPosition(0), -1, (1, 0), (2, 2)));

        new RscpTemplateManager().RegenerateTemplates(arrangement);

        var chord = level.Chords.Single();
        var assigned = arrangement.ChordTemplates.Single(t => t.Id == chord.TemplateId);
        Assert.False(assigned.Arpeggio);
        Assert.Equal(2, arrangement.ChordTemplates.Count); // a plain sibling was minted
        Assert.True(arpeggio.Arpeggio);                    // and the arpeggio one is untouched
    }

    /// <summary>An AUTHORED arpeggio chord keeps its arpeggio template — the pass
    /// only re-assigns chords whose template is dangling or the wrong shape.</summary>
    [Fact]
    public void AChordDeliberatelyOnAnArpeggioTemplate_KeepsIt()
    {
        var arrangement = NewArrangement();
        var arpeggio = NewTemplate(0, "Am", (1, 0), (2, 2));
        arpeggio.Arpeggio = true;
        arrangement.ChordTemplates.Add(arpeggio);

        var level = arrangement.Levels[0];
        level.Chords.Add(NewChord(new FractionalPosition(0), 0, (1, 0), (2, 2)));

        new RscpTemplateManager().RegenerateTemplates(arrangement);

        Assert.Equal(0, level.Chords.Single().TemplateId);
        Assert.Single(arrangement.ChordTemplates);
    }

    /// <summary>
    ///     The same sentinel, one layer down: <c>RscpTemplateManager</c> kept the
    ///     <c>-1</c> and then the WRITER coerced it to <c>0</c> on the way out —
    ///     "rscharter always serializes templateId", so it wrote a real template
    ///     id where there was none. The bar reopened wearing an unrelated chord's
    ///     frets, fingering and name.
    ///     <para>
    ///         Reachable from the primary authoring gesture: right-clicking an
    ///         empty hand-shape bar leaves
    ///         <c>HandShapeLaneSpan.BuildBar</c> with no
    ///         sounds to build a shape from, so the new shape keeps <c>-1</c>.
    ///     </para>
    /// </summary>
    [Fact]
    public void AnUntemplatedShape_KeepsTheSentinelAcrossASave_AndAdoptsNobodysShape()
    {
        var project = new ChartProject();
        var arrangement = NewArrangement();
        // Template 0 is a REAL chord's shape — what the coercion handed the bar.
        arrangement.ChordTemplates.Add(NewTemplate(0, "Am", (1, 0), (2, 2)));

        var level = arrangement.Levels[0];
        level.Chords.Add(NewChord(new FractionalPosition(0), 0, (1, 0), (2, 2)));
        // Drawn over empty bars, so nothing inside it can supply a template.
        level.HandShapes.Add(new HandShape
        {
            Position = new FractionalPosition(4),
            EndPosition = new FractionalPosition(6),
            TemplateId = -1
        });
        project.Arrangements.Add(arrangement);

        var loaded = SaveAndReload(project).Arrangements[0];

        var shape = loaded.Levels[0].HandShapes.Single();
        Assert.Equal(-1, shape.TemplateId);
        // Stated as the loss, not as a tautology: template 0 still exists, still
        // describes Am's shape, and the bar is still not claiming to be it.
        Assert.Equal("Am", loaded.ChordTemplates[0].Name);
        Assert.Equal(new[] { 0, 2 }, loaded.ChordTemplates[0].Frets.OrderBy(f => f.Key).Select(f => f.Value));
    }

    /// <summary>
    ///     The sentinel is spelled as an ABSENT attribute — which our parser
    ///     already defaults to <c>-1</c> and rscharter's <c>HandShapeConverter</c>
    ///     reads as its own null Integer. A real id is still written exactly as
    ///     before, so every existing chart's bytes are unchanged.
    /// </summary>
    [Fact]
    public void OnlyTheUntemplatedShapeOmitsTheAttribute()
    {
        var project = new ChartProject();
        var arrangement = NewArrangement();
        arrangement.ChordTemplates.Add(NewTemplate(0, "Am", (1, 0), (2, 2)));

        var level = arrangement.Levels[0];
        level.Chords.Add(NewChord(new FractionalPosition(0), 0, (1, 0), (2, 2)));
        level.HandShapes.Add(new HandShape
        {
            Position = new FractionalPosition(0), EndPosition = new FractionalPosition(2), TemplateId = 0
        });
        level.HandShapes.Add(new HandShape
        {
            Position = new FractionalPosition(4), EndPosition = new FractionalPosition(6), TemplateId = -1
        });
        project.Arrangements.Add(arrangement);

        new RscpChartSerializer().Save(ProjectPath, project);

        var shapes = XDocument.Load(ProjectPath).Root!
            .Element("arrangements")!.Element("arrangement")!
            .Element("levels")!.Element("level")!
            .Element("handShapes")!.Elements("handShape").ToList();

        Assert.Equal("0", shapes[0].Attribute("templateId")?.Value);
        Assert.Null(shapes[1].Attribute("templateId"));
    }

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

    private static ChordTemplate NewTemplate(int id, string name, params (int String, int Fret)[] frets)
    {
        var template = new ChordTemplate { Id = id, Name = name };
        foreach (var (stringIndex, fret) in frets) template.Frets[stringIndex] = fret;
        return template;
    }

    private static Chord NewChord(FractionalPosition position, int templateId,
        params (int String, int Fret)[] notes)
    {
        var chord = new Chord { Position = position, TemplateId = templateId };
        foreach (var (stringIndex, fret) in notes)
            chord.ChordNotes.Add(new ChordNote { StringIndex = stringIndex, Fret = fret });
        return chord;
    }
}
