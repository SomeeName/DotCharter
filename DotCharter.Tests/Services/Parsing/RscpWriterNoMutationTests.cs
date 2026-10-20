using System.Xml.Linq;
using DotCharter.Services.Parsing.Rscp;

namespace DotCharter.Tests.Services.Parsing;

/// <summary>
/// #304: <c>RscpWriter</c> ran <c>RscpTemplateManager.RegenerateTemplates</c> on the
/// arrangements of the project it was GIVEN, renumbering every chord template
/// and repointing the chords. Saving must not change the project.
/// </summary>
public sealed class RscpWriterNoMutationTests
{
    [Fact]
    public void Writing_leaves_the_project_deep_equal_to_itself()
    {
        var project = BuildProjectWithSparseTemplateIds();
        var before = ChartProjectCloner.Clone(project);

        new RscpWriter().WriteDocument(new XDocument(), project);

        var diff = DeepCompare.Diff("project", before, project, new HashSet<string>());
        Assert.True(diff.Count == 0, "saving changed the project:\n" + string.Join("\n", diff.Take(10)));
        Assert.Equal(5, project.Arrangements[0].ChordTemplates[0].Id);
        Assert.Equal(9, project.Arrangements[0].Levels[0].Chords[1].TemplateId);
    }

    [Fact]
    public void The_written_templates_are_still_regenerated()
    {
        var doc = new XDocument();
        new RscpWriter().WriteDocument(doc, BuildProjectWithSparseTemplateIds());

        // A template's id is its POSITION in <chordTemplates>; the chords point at it.
        var chordIds = doc.Descendants("soundChord").Select(c => (string?)c.Attribute("templateId")).ToList();
        Assert.Equal(["0", "1"], chordIds);
    }

    /// <summary>Two chords on templates with ids 5 and 9: regeneration renumbers them to 0 and 1.</summary>
    private static ChartProject BuildProjectWithSparseTemplateIds()
    {
        var project = new ChartProject();
        for (var i = 0; i < 8; i++) project.Beats.Add(new BeatMarker { Time = i * 500, Bpm = 120, IsMeasure = i % 4 == 0 });

        var arr = new Arrangement { ArrangementType = ArrangementType.Lead, StringCount = 6 };
        for (var s = 0; s < 6; s++) arr.TuningPitches.Add(new TuningOffset(0));

        var em = new ChordTemplate { Id = 5, Name = "Em" };
        em.Frets[1] = 2;
        em.Frets[2] = 2;
        var a = new ChordTemplate { Id = 9, Name = "A" };
        a.Frets[2] = 2;
        a.Frets[3] = 2;
        a.Frets[4] = 2;
        arr.ChordTemplates.Add(em);
        arr.ChordTemplates.Add(a);

        var level = new ChartLevel { DifficultyIndex = 0 };
        level.Chords.Add(Chord(0, 5, (1, 2), (2, 2)));
        level.Chords.Add(Chord(2, 9, (2, 2), (3, 2), (4, 2)));
        arr.Levels.Add(level);
        project.Arrangements.Add(arr);
        return project;
    }

    private static Chord Chord(int beat, int templateId, params (int String, int Fret)[] notes)
    {
        var chord = new Chord { Position = new FractionalPosition(beat), TemplateId = templateId };
        foreach (var (s, f) in notes) chord.ChordNotes.Add(new ChordNote { StringIndex = s, Fret = f });
        return chord;
    }
}
