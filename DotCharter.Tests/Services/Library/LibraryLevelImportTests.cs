using DotCharter.Services.Library;
using DotCharter.Services.Editing;

namespace DotCharter.Tests.Services.Library;

/// <summary>#399: opening a Rocksmith song with only its hardest level squashes each laddered
/// arrangement to one level that keeps the hardest level's every note, and leaves a one-level
/// arrangement alone. #634: the chord templates only the dropped levels played go with them, by the
/// editor squash's own rule (<c>LevelTemplateOrphans</c>), and every survivor keeps its users.</summary>
public sealed class LibraryLevelImportTests
{
    [Fact]
    public void Every_laddered_arrangement_keeps_one_level_with_the_hardest_levels_notes()
    {
        var project = new ChartProject();
        var laddered = new Arrangement { Name = "Lead" };
        for (var d = 0; d < 4; d++)
        {
            var level = new ChartLevel { DifficultyIndex = d };
            for (var n = 0; n <= d * 2; n++)
                level.Notes.Add(new GuitarNote { Position = new FractionalPosition(n), StringIndex = 2, Fret = 5 });
            laddered.Levels.Add(level);
        }

        var single = new Arrangement { Name = "Bass" };
        single.Levels.Add(new ChartLevel { Notes = { new GuitarNote { Fret = 3 } } });
        project.Arrangements.Add(laddered);
        project.Arrangements.Add(single);

        Assert.Equal(1, LibraryLevelImport.KeepHardestOnly(project));

        Assert.Equal(7, Assert.Single(laddered.Levels).Notes.Count);
        Assert.Single(Assert.Single(single.Levels).Notes);
    }

    [Fact]
    public void Prepare_drops_a_reduced_template_only_the_easier_level_played_and_remaps_every_user()
    {
        var (project, t) = TwoLevelSong();
        var users = UsersByTemplate(project.Arrangements[0]);

        LibraryLevelImport.PrepareForEditor(project, new AppConfig { PsarcOpenHardestLevelOnly = true });

        var arrangement = Assert.Single(project.Arrangements);
        Assert.Single(arrangement.Levels);
        // The reduced grip had a user only on level 0; the hardest level's templates, a NAMED
        // template the easy level used and a spare nothing used before all stay (#520's rule).
        Assert.Equal([t.Barre, t.D, t.Em, t.Spare], arrangement.ChordTemplates, ReferenceEqualityComparer.Instance);
        Assert.Equal([0, 1, 2, 3], arrangement.ChordTemplates.Select(x => x.Id));
        AssertEveryUserResolvesToItsTemplate(arrangement, users);
    }

    [Fact]
    public void KeepHardestOnly_prunes_by_the_same_rule_as_the_editor_pass()
    {
        var (sequential, _) = TwoLevelSong();
        var (parallel, _) = TwoLevelSong();

        Assert.Equal(1, LibraryLevelImport.KeepHardestOnly(sequential));
        LibraryLevelImport.PrepareForEditor(parallel, new AppConfig { PsarcOpenHardestLevelOnly = true });

        Assert.Equal(
            parallel.Arrangements[0].ChordTemplates.Select(x => (x.Id, x.Name)),
            sequential.Arrangements[0].ChordTemplates.Select(x => (x.Id, x.Name)));
        Assert.Equal(4, sequential.Arrangements[0].ChordTemplates.Count);
    }

    [Fact]
    public void Prepare_with_the_setting_off_keeps_every_level_and_template()
    {
        var (project, t) = TwoLevelSong();

        LibraryLevelImport.PrepareForEditor(project, new AppConfig { PsarcOpenHardestLevelOnly = false });

        var arrangement = Assert.Single(project.Arrangements);
        Assert.Equal(2, arrangement.Levels.Count);
        Assert.Equal([t.Barre, t.Reduced, t.D, t.Em, t.Spare], arrangement.ChordTemplates,
            ReferenceEqualityComparer.Instance);
    }

    private sealed record Templates(ChordTemplate Barre, ChordTemplate Reduced, ChordTemplate D, ChordTemplate Em,
        ChordTemplate Spare);

    /// <summary>Ids by position, as the PSARC/RS XML readers number them. Level 0 plays the reduced
    /// grip (1) and a named Em (3); level 1, the hardest, plays the barre (0) and D (2) — D's users
    /// sit AFTER the pruned slot, so a missed remap points them at the wrong template.</summary>
    private static (ChartProject Project, Templates Templates) TwoLevelSong()
    {
        var t = new Templates(
            new ChordTemplate { Id = 0, Name = "A", Frets = { [0] = 5, [1] = 7, [2] = 7, [3] = 6, [4] = 5, [5] = 5 } },
            new ChordTemplate { Id = 1, Name = "", Frets = { [0] = 5, [1] = 7 } },
            new ChordTemplate { Id = 2, Name = "D", Frets = { [1] = 5, [2] = 7, [3] = 7 } },
            new ChordTemplate { Id = 3, Name = "Em", Frets = { [0] = 0, [1] = 2, [2] = 2 } },
            new ChordTemplate { Id = 4, Name = "", Frets = { [2] = 9, [3] = 9 } });
        var arrangement = new Arrangement { Name = "Lead" };
        foreach (var template in new[] { t.Barre, t.Reduced, t.D, t.Em, t.Spare })
            arrangement.ChordTemplates.Add(template);

        var easy = new ChartLevel { DifficultyIndex = 0 };
        easy.Chords.Add(ChordAt(0, t.Reduced.Id));
        easy.Chords.Add(ChordAt(2, t.Em.Id));
        easy.HandShapes.Add(ShapeAt(0, t.Reduced.Id));
        var hard = new ChartLevel { DifficultyIndex = 1 };
        hard.Chords.Add(ChordAt(0, t.Barre.Id));
        hard.Chords.Add(ChordAt(2, t.D.Id));
        hard.HandShapes.Add(ShapeAt(0, t.Barre.Id));
        hard.HandShapes.Add(ShapeAt(2, t.D.Id));
        arrangement.Levels.Add(easy);
        arrangement.Levels.Add(hard);

        var project = new ChartProject();
        project.Arrangements.Add(arrangement);
        return (project, t);
    }

    private static Chord ChordAt(int beat, int templateId) =>
        new() { Position = new FractionalPosition(beat), TemplateId = templateId };

    private static HandShape ShapeAt(int beat, int templateId) =>
        new() { Position = new FractionalPosition(beat), EndPosition = new FractionalPosition(beat + 1), TemplateId = templateId };

    /// <summary>The template INSTANCE each chord and hand shape of the HARDEST level resolves to,
    /// keyed by kind and position (the squash rebuilds the level, so the user instances change).</summary>
    private static Dictionary<(string, FractionalPosition), ChordTemplate> UsersByTemplate(Arrangement arrangement)
    {
        var byId = ChordTemplateResolver.IndexById(arrangement.ChordTemplates);
        var hardest = arrangement.Levels.MaxBy(l => l.DifficultyIndex)!;
        var users = new Dictionary<(string, FractionalPosition), ChordTemplate>();
        foreach (var chord in hardest.Chords) users[("chord", chord.Position)] = byId[chord.TemplateId];
        foreach (var shape in hardest.HandShapes) users[("shape", shape.Position)] = byId[shape.TemplateId];
        return users;
    }

    private static void AssertEveryUserResolvesToItsTemplate(Arrangement arrangement,
        Dictionary<(string, FractionalPosition), ChordTemplate> before)
    {
        var byId = ChordTemplateResolver.IndexById(arrangement.ChordTemplates);
        var level = Assert.Single(arrangement.Levels);
        Assert.Equal(2, level.Chords.Count);
        Assert.Equal(2, level.HandShapes.Count);
        foreach (var chord in level.Chords)
            Assert.Same(before[("chord", chord.Position)], byId[chord.TemplateId]);
        foreach (var shape in level.HandShapes)
            Assert.Same(before[("shape", shape.Position)], byId[shape.TemplateId]);
    }
}
