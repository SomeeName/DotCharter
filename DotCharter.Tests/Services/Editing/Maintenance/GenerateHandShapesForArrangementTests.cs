using DotCharter.Services.Editing.Fixing;
using DotCharter.Services.Editing.Validation.Rules;
using DotCharter.Tests.Services.Editing.HandShapes;

namespace DotCharter.Tests.Services.Editing.Maintenance;

/// <summary>
///     Tools ▸ Hand shapes ▸ Generate hand shapes (arrangement) (#478) and the D11
///     auto-fix (#479) through the real <c>ChartMaintenanceService</c> and history: the
///     bars on every level, the templates they mint and the sentinel chords' repoints
///     are ONE undo node (the <c>HandShapeTemplateMintTests</c> rule).
/// </summary>
public sealed class GenerateHandShapesForArrangementTests
{
    private static readonly (int, int)[] G5 = [(0, 3), (1, 5), (2, 5)];
    private static readonly (int, int)[] A5 = [(0, 5), (1, 7), (2, 7)];

    /// <summary>Two levels: level 0 has a G5 strum pair (one authored bar over the
    /// first) and an A5; level 1 repeats the G5 pair with no bars at all.</summary>
    private static ChartPowerToolsTests.Harness Seeded(IArrangementAutoFixService? autoFix = null)
    {
        var h = new ChartPowerToolsTests.Harness(autoFix: autoFix);
        var arrangement = h.State.ActiveArrangement!;
        arrangement.StringCount = 6;
        var level0 = arrangement.Levels[0];
        level0.Notes.Clear();
        level0.Chords.Add(ChordHandShapePlannerTests.Chord(0, 1, G5));
        level0.Chords.Add(ChordHandShapePlannerTests.Chord(4, 1, G5));
        level0.Chords.Add(ChordHandShapePlannerTests.Chord(8, 0, A5));
        level0.HandShapes.Add(new HandShape
        {
            Position = new FractionalPosition(0), EndPosition = new FractionalPosition(1), TemplateId = -1, Name = "mine"
        });

        var level1 = new ChartLevel { DifficultyIndex = 1 };
        level1.Chords.Add(ChordHandShapePlannerTests.Chord(0, 1, G5));
        level1.Chords.Add(ChordHandShapePlannerTests.Chord(1, 1, G5));
        arrangement.Levels.Add(level1);
        return h;
    }

    private static int Findings(Arrangement arrangement)
    {
        var issues = new List<ArrangementIssue>();
        var beats = Enumerable.Range(0, 32).Select(i => new BeatMarker { Time = i * 500.0, Bpm = 120 }).ToList();
        new ChordOutsideHandShapeRule().Validate(ArrangementValidationContext.Build(arrangement, beats, null), issues);
        return issues.Count;
    }

    [Fact]
    public void Generate_CoversEveryLevel_KeepsTheAuthoredBar_AndOneUndoTakesItAllBack()
    {
        var h = Seeded();
        var arrangement = h.State.ActiveArrangement!;
        var authored = arrangement.Levels[0].HandShapes[0];
        Assert.Equal(4, Findings(arrangement)); // beats 0 (level 1 only), 1, 4 and 8

        h.Maintenance.GenerateHandShapesForArrangement();

        Assert.Equal(0, Findings(arrangement));
        Assert.Equal(3, arrangement.Levels[0].HandShapes.Count); // authored + G5 at 4 + A5 at 8
        Assert.Same(authored, arrangement.Levels[0].HandShapes[0]);
        Assert.Equal(new FractionalPosition(1), authored.EndPosition);
        Assert.Single(arrangement.Levels[1].HandShapes); // the strum pair shares one bar
        Assert.Equal(2, arrangement.ChordTemplates.Count); // G5 minted once for both levels, A5
        // Every chord a new bar holds now names its template; the one under the
        // authored bar was not this verb's to touch.
        var underAuthored = arrangement.Levels[0].Chords[0];
        Assert.Equal(-1, underAuthored.TemplateId);
        Assert.All(arrangement.Levels.SelectMany(l => l.Chords).Where(c => c != underAuthored),
            c => Assert.Contains(arrangement.ChordTemplates, t => t.Id == c.TemplateId));
        Assert.Equal("Added 3 hand shapes over 4 chords.", h.State.MainDisplayMessage);

        h.History.Undo();

        Assert.False(h.History.CanUndo);
        Assert.Same(authored, Assert.Single(arrangement.Levels[0].HandShapes));
        Assert.Empty(arrangement.Levels[1].HandShapes);
        Assert.Empty(arrangement.ChordTemplates);
        Assert.All(arrangement.Levels.SelectMany(l => l.Chords), c => Assert.Equal(-1, c.TemplateId));
        Assert.Equal(4, Findings(arrangement));
    }

    [Fact]
    public void GenerateTwice_AddsNothingTheSecondTime()
    {
        var h = Seeded();
        h.Maintenance.GenerateHandShapesForArrangement();
        var shapes = h.State.ActiveArrangement!.Levels.Sum(l => l.HandShapes.Count);

        h.Maintenance.GenerateHandShapesForArrangement();

        Assert.Equal(shapes, h.State.ActiveArrangement!.Levels.Sum(l => l.HandShapes.Count));
        Assert.Equal("Every chord is already inside a hand shape.", h.State.MainDisplayMessage);
        h.History.Undo();
        Assert.False(h.History.CanUndo); // the no-op pushed no node
    }

    [Fact]
    public void AutoFix_CoversTheChords_AndOneUndoRestoresTheOriginal()
    {
        var h = Seeded(new ArrangementAutoFixService());
        Assert.Equal(4, Findings(h.State.ActiveArrangement!));

        h.Maintenance.AutoFixChart();

        Assert.Equal(0, Findings(h.State.ActiveArrangement!));

        h.History.Undo();

        var restored = h.State.ActiveProject!.Arrangements[0];
        Assert.Equal(4, Findings(restored));
        Assert.Single(restored.Levels[0].HandShapes);
        Assert.Empty(restored.Levels[1].HandShapes);
        Assert.Empty(restored.ChordTemplates);
        Assert.False(h.History.CanUndo);
    }
}
