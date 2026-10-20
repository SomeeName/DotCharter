using System.Xml.Linq;
using DotCharter.Services.Editing;
using DotCharter.Services.Parsing.Rscp;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Services.Parsing.Rscp;

/// <summary>
///     #547: the <c>.rscp</c> is POSITIONAL — a <c>chordTemplate</c> element carries no id and
///     the parser numbers templates by their index — so <c>RscpTemplateManager.RegenerateTemplates</c>
///     renumbers every template to its position on save. It did not remap the users: a chord
///     whose old id no longer validated was re-resolved by its frets, but a hand shape over no
///     chord (a let-ring bar) and a notation-voice chord kept the old number, and on a chart whose
///     ids were not positions that number named ANOTHER template after the reload. Every user is
///     remapped now, from the template its old id resolved to (first template with that id wins,
///     as <see cref="ChordTemplateResolver.IndexById" />) to that template's new position.
/// </summary>
public sealed class RscpTemplateRenumberTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dotcharter_rscp_renumber_").FullName;

    private string ProjectPath => Path.Combine(_dir, "project.rscp");

    public void Dispose() => BestEffortDelete.Directory(_dir);

    /// <summary>
    ///     Ids 0, 2, 2, 5, 6 at positions 0–4: a gap left by removals and a twin that shares an
    ///     earlier template's id. Every chord, hand shape and notation-voice chord must name the
    ///     same fret shape after a save and reload — compared by SHAPE, never by id.
    /// </summary>
    [Fact]
    public void EveryUser_NamesTheSameShape_AfterASaveAndReload_WhenTheIdsAreNotPositions()
    {
        var project = new ChartProject();
        project.Arrangements.Add(GappedArrangement());

        var before = UsersByShape(project.Arrangements[0]);
        var loaded = SaveAndReload(project).Arrangements[0];

        Assert.Equal(before, UsersByShape(loaded));
    }

    /// <summary>
    ///     A reference that resolves to NO template before the save must not come back naming one.
    ///     Id 3 is dangling on the gapped chart, and after renumbering position 3 is a real
    ///     template, so the untouched number would have dressed the bar in that template's frets.
    ///     It becomes the <c>-1</c> sentinel instead, which the writer spells as an absent
    ///     attribute.
    /// </summary>
    [Fact]
    public void ADanglingHandShapeId_StaysUntemplated_InsteadOfLandingOnARenumberedTemplate()
    {
        var project = new ChartProject();
        var arrangement = GappedArrangement();
        arrangement.Levels[0].HandShapes.Add(new HandShape
        {
            Position = new FractionalPosition(40), EndPosition = new FractionalPosition(42), TemplateId = 3
        });
        project.Arrangements.Add(arrangement);

        new RscpChartSerializer().Save(ProjectPath, project);

        var shapes = XDocument.Load(ProjectPath).Root!
            .Element("arrangements")!.Element("arrangement")!
            .Element("levels")!.Elements("level").First()
            .Element("handShapes")!.Elements("handShape").ToList();
        Assert.Null(shapes.Single(s => s.Attribute("p")?.Value == new FractionalPosition(40).ToString())
            .Attribute("templateId"));
        var loaded = new RscpChartSerializer().Load(ProjectPath).Arrangements[0];
        Assert.Equal(-1, loaded.Levels[0].HandShapes.Single(h => h.Position == new FractionalPosition(40)).TemplateId);
    }

    /// <summary>
    ///     The save path renumbers a COPY (#304): the project handed to the writer keeps its own
    ///     ids, including the gap and the twin.
    /// </summary>
    [Fact]
    public void TheSavedProject_KeepsItsOwnIds()
    {
        var project = new ChartProject();
        project.Arrangements.Add(GappedArrangement());

        new RscpChartSerializer().Save(ProjectPath, project);

        var arrangement = project.Arrangements[0];
        Assert.Equal(new[] { 0, 2, 2, 5, 6 }, arrangement.ChordTemplates.Select(t => t.Id));
        Assert.Equal(6, arrangement.Levels[0].HandShapes[0].TemplateId);
        Assert.Equal(2, arrangement.NotationVoices[0].Chords[0].TemplateId);
    }

    /// <summary>
    ///     <c>RegenerateTemplates</c> on its own: ids become positions and every user follows its
    ///     template there, notation voices included.
    /// </summary>
    [Fact]
    public void RegenerateTemplates_RemapsEveryUserToItsTemplatesNewPosition()
    {
        var arrangement = GappedArrangement();
        var before = UsersByShape(arrangement);

        new RscpTemplateManager().RegenerateTemplates(arrangement);

        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, arrangement.ChordTemplates.Select(t => t.Id));
        Assert.Equal(before, UsersByShape(arrangement));
        Assert.Equal(4, arrangement.Levels[0].HandShapes[0].TemplateId);  // D: id 6 -> position 4
        Assert.Equal(1, arrangement.NotationVoices[0].Chords[0].TemplateId); // C: id 2 -> position 1
    }

    /// <summary>
    ///     Two levels, with:
    ///     <list type="bullet">
    ///         <item>let-ring bars (no chord under them) on the templates after the gap — id 6 (D)
    ///         and id 2 (C, whose twin C7 shares the id);</item>
    ///         <item>chords on templates after the gap on BOTH levels, and a bar over one of them
    ///         that keeps its own full shape;</item>
    ///         <item>a notation-voice chord on id 2.</item>
    ///     </list>
    /// </summary>
    private static Arrangement GappedArrangement()
    {
        var arrangement = new Arrangement { Name = "Lead" };
        arrangement.ChordTemplates.Add(NewTemplate(0, "A", (1, 0), (2, 2), (3, 2), (4, 2)));
        arrangement.ChordTemplates.Add(NewTemplate(2, "C", (1, 3), (2, 2), (3, 0), (4, 1)));
        // A twin: same id as C, never resolved by any user.
        arrangement.ChordTemplates.Add(NewTemplate(2, "C7", (1, 3), (2, 2), (3, 3), (4, 1)));
        arrangement.ChordTemplates.Add(NewTemplate(5, "G", (0, 3), (1, 2), (5, 3)));
        arrangement.ChordTemplates.Add(NewTemplate(6, "D", (3, 0), (4, 2), (5, 3)));

        var easy = new ChartLevel();
        easy.Chords.Add(NewChord(0, 5, (0, 3), (1, 2), (5, 3)));
        // Let-ring bars over single notes: no chord inside either span.
        easy.Notes.Add(new GuitarNote { Position = new FractionalPosition(8), StringIndex = 3, Fret = 0 });
        easy.HandShapes.Add(new HandShape
        {
            Position = new FractionalPosition(8), EndPosition = new FractionalPosition(10), TemplateId = 6
        });
        easy.Notes.Add(new GuitarNote { Position = new FractionalPosition(12), StringIndex = 1, Fret = 3 });
        easy.HandShapes.Add(new HandShape
        {
            Position = new FractionalPosition(12), EndPosition = new FractionalPosition(14), TemplateId = 2
        });

        var hard = new ChartLevel();
        hard.Chords.Add(NewChord(0, 2, (1, 3), (2, 2), (3, 0), (4, 1)));
        hard.Chords.Add(NewChord(4, 6, (3, 0), (4, 2), (5, 3)));
        hard.Chords.Add(NewChord(8, 0, (1, 0), (2, 2), (3, 2), (4, 2)));
        // A bar over the D chord that keeps G's full shape.
        hard.HandShapes.Add(new HandShape
        {
            Position = new FractionalPosition(4), EndPosition = new FractionalPosition(6), TemplateId = 5
        });
        hard.HandShapes.Add(new HandShape
        {
            Position = new FractionalPosition(16), EndPosition = new FractionalPosition(18), TemplateId = 2
        });

        arrangement.Levels.Add(easy);
        arrangement.Levels.Add(hard);

        var voice = new NotationVoice { VoiceIndex = 1 };
        voice.Chords.Add(NewChord(20, 2, (1, 3), (2, 2), (3, 0), (4, 1)));
        arrangement.NotationVoices.Add(voice);
        return arrangement;
    }

    /// <summary>
    ///     Every user of a template, in a fixed order, with the SHAPE its id resolves to — name,
    ///     frets, fingers and both arpeggio flags — or <c>none</c>. Never the id itself.
    /// </summary>
    private static List<string> UsersByShape(Arrangement arrangement)
    {
        var byId = ChordTemplateResolver.IndexById(arrangement.ChordTemplates);
        var users = new List<string>();
        for (var l = 0; l < arrangement.Levels.Count; l++)
        {
            var level = arrangement.Levels[l];
            users.AddRange(level.Chords.Select(c => $"L{l} chord@{c.Position}: {Shape(c.TemplateId)}"));
            users.AddRange(level.HandShapes.Select(h => $"L{l} bar@{h.Position}: {Shape(h.TemplateId)}"));
        }

        foreach (var voice in arrangement.NotationVoices)
            users.AddRange(voice.Chords.Select(c => $"V{voice.VoiceIndex} chord@{c.Position}: {Shape(c.TemplateId)}"));

        return users;

        string Shape(int templateId)
        {
            if (!byId.TryGetValue(templateId, out var t)) return "none";
            var frets = string.Join(",", t.Frets.OrderBy(f => f.Key).Select(f => $"{f.Key}={f.Value}"));
            var fingers = string.Join(",", t.Fingers.OrderBy(f => f.Key).Select(f => $"{f.Key}={f.Value}"));
            return $"{t.Name} [{frets}] [{fingers}] arp={t.Arpeggio}/{t.ForceArpeggioInRs}";
        }
    }

    private ChartProject SaveAndReload(ChartProject project)
    {
        new RscpChartSerializer().Save(ProjectPath, project);
        return new RscpChartSerializer().Load(ProjectPath);
    }

    private static ChordTemplate NewTemplate(int id, string name, params (int String, int Fret)[] frets)
    {
        var template = new ChordTemplate { Id = id, Name = name };
        foreach (var (stringIndex, fret) in frets) template.Frets[stringIndex] = fret;
        return template;
    }

    private static Chord NewChord(int position, int templateId, params (int String, int Fret)[] notes)
    {
        var chord = new Chord { Position = new FractionalPosition(position), TemplateId = templateId };
        foreach (var (stringIndex, fret) in notes)
            chord.ChordNotes.Add(new ChordNote { StringIndex = stringIndex, Fret = fret });
        return chord;
    }
}
