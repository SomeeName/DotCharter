using DotCharter.Services.Editing;
using DotCharter.Services.Parsing.Rscp;

namespace DotCharter.Tests.Services.Editing.Maintenance;

/// <summary>
///     #520: Squash levels and Generate levels drop the reduced chord templates they leave with no
///     user, inside their own undo node — and nothing a charter made by hand. The rule is
///     <see cref="LevelTemplateOrphans" />: no user afterwards, and either minted by the operation
///     or unnamed and used before it.
/// </summary>
public sealed class LevelTemplatePruningTests
{
    /// <summary>Eleven levels: level k sits at depth k/10 — level 5 plays the barre's lowest two
    /// strings, level 6 its lowest three (<c>LevelGenerationGradedChordTests</c>).</summary>
    private const int ElevenLevels = 11;

    private static readonly Dictionary<int, int> BarreFrets = new() { [0] = 5, [1] = 7, [2] = 7, [3] = 6, [4] = 5, [5] = 5 };
    private static readonly Dictionary<int, int> BarreFingers = new() { [0] = 1, [1] = 3, [2] = 4, [3] = 2, [4] = 1, [5] = 1 };

    [Fact]
    public void GenerateThenSquash_LeavesNoOrphanReducedTemplate_AndOneUndoRestoresThem()
    {
        var h = Prepared(out var barre, beats: [0]);
        var live = h.State.ActiveArrangement!;
        Assert.True(h.Maintenance.GenerateLevels(ElevenLevels));
        Assert.Equal(3, live.ChordTemplates.Count);
        Assert.All(live.ChordTemplates.Skip(1), t => Assert.Equal("", t.Name));
        h.History.Clear();

        var project = h.State.ActiveProject!;
        var before = ChartProjectCloner.Clone(project);
        var instancesBefore = live.ChordTemplates.ToList();

        h.Maintenance.SquashLevels();

        Assert.Same(barre, Assert.Single(live.ChordTemplates));
        AssertEveryChordResolves(live);
        var after = ChartProjectCloner.Clone(project);

        h.History.Undo();
        AssertSame(before, project, "squash undone");
        Assert.Equal(instancesBefore, live.ChordTemplates, ReferenceEqualityComparer.Instance);
        Assert.False(h.History.CanUndo);

        h.History.Redo();
        AssertSame(after, project, "squash redone");
        Assert.Same(barre, Assert.Single(live.ChordTemplates));
    }

    [Fact]
    public void Regenerate_DropsTheReducedTemplatesTheReplacedLevelsPlayed_AndKeepsTheNamedOne()
    {
        var h = Prepared(out var barre, beats: [0]);
        var live = h.State.ActiveArrangement!;
        Assert.True(h.Maintenance.GenerateLevels(ElevenLevels));
        var oldReduced = live.ChordTemplates.Skip(1).ToList();
        Assert.Equal(2, oldReduced.Count);

        // The charter re-chords the hardest level: a C on its own template.
        var c = new ChordTemplate
        {
            Id = 3,
            Name = "C",
            Frets = { [1] = 3, [2] = 2, [3] = 0, [4] = 1, [5] = 0 },
            Fingers = { [1] = 3, [2] = 2, [4] = 1 }
        };
        live.ChordTemplates.Add(c);
        var top = live.Levels.Single(l => l.DifficultyIndex == ElevenLevels - 1);
        var chord = Assert.Single(top.Chords);
        chord.TemplateId = c.Id;
        chord.ChordNotes.Clear();
        foreach (var (s, fret) in c.Frets) chord.ChordNotes.Add(new ChordNote { StringIndex = s, Fret = fret });
        // A let-ring bar over nothing on C: the one user the save cannot re-resolve by its frets.
        var bar = new HandShape
        { Position = new FractionalPosition(6), EndPosition = new FractionalPosition(7), TemplateId = c.Id };
        top.HandShapes.Add(bar);
        h.History.Clear();

        var project = h.State.ActiveProject!;
        var before = ChartProjectCloner.Clone(project);

        Assert.True(h.Maintenance.GenerateLevels(ElevenLevels));

        // The barre lost its last chord but carries a name: the charter's, not the operation's.
        Assert.True(ChordTemplateCollectionOps.ContainsInstance(live.ChordTemplates, barre));
        Assert.True(ChordTemplateCollectionOps.ContainsInstance(live.ChordTemplates, c));
        Assert.All(oldReduced, t => Assert.False(ChordTemplateCollectionOps.ContainsInstance(live.ChordTemplates, t)));
        var used = LevelTemplateOrphans.UsedTemplates(live);
        Assert.All(live.ChordTemplates.Where(t => !ReferenceEquals(t, barre)), t => Assert.Contains(t, used));
        Assert.Equal(4, live.ChordTemplates.Count);
        AssertEveryChordResolves(live);
        AssertIdsArePositions(live);
        var newTop = live.Levels.Single(l => l.DifficultyIndex == ElevenLevels - 1);
        Assert.Same(c, TemplateAt(live, ElevenLevels - 1, 0));
        Assert.Equal(c.Id, Assert.Single(newTop.HandShapes, s => s.Position == new FractionalPosition(6)).TemplateId);
        AssertTheSavedBarIsStillOn(project, c);
        var after = ChartProjectCloner.Clone(project);

        h.History.Undo();
        AssertSame(before, project, "regeneration undone");
        h.History.Redo();
        AssertSame(after, project, "regeneration redone");
    }

    [Fact]
    public void Squash_KeepsTheReducedTemplateASquashedPhraseStillPlays()
    {
        var h = Prepared(out var barre, beats: [0, 4]);
        var live = h.State.ActiveArrangement!;
        Assert.True(h.Maintenance.GenerateLevels(ElevenLevels));
        var twoStrings = TemplateAt(live, 5, 4);
        var threeStrings = TemplateAt(live, 6, 4);
        Assert.Equal(2, twoStrings.Frets.Count);
        Assert.Equal(3, threeStrings.Frets.Count);

        // The second phrase only ever reaches level 5, so the squash takes it from there.
        live.PhraseInfos["easy"].MaxDifficulty = 5;
        h.History.Clear();

        h.Maintenance.SquashLevels();

        Assert.Equal([barre, twoStrings], live.ChordTemplates, ReferenceEqualityComparer.Instance);
        Assert.Same(twoStrings, TemplateAt(live, 0, 4));
        AssertEveryChordResolves(live);
    }

    [Fact]
    public void Squash_NeverDropsANamedTemplate_ASpareOne_OrOneAVoicePlays()
    {
        var h = Prepared(out var barre, beats: [0]);
        var live = h.State.ActiveArrangement!;
        var dsus2 = new ChordTemplate { Id = 1, Name = "Dsus2", Frets = { [3] = 0, [4] = 2, [5] = 3 } };
        var spare = new ChordTemplate { Id = 2, Name = "", Frets = { [0] = 3, [1] = 5 } };
        var voiceOnly = new ChordTemplate { Id = 3, Name = " ", Frets = { [0] = 8, [1] = 10 } };
        live.ChordTemplates.Add(dsus2);
        live.ChordTemplates.Add(spare);
        live.ChordTemplates.Add(voiceOnly);
        var voice = new NotationVoice { VoiceIndex = 1 };
        voice.Chords.Add(new Chord
        {
            Position = new FractionalPosition(2),
            TemplateId = voiceOnly.Id,
            ChordNotes = { new ChordNote { StringIndex = 0, Fret = 8 }, new ChordNote { StringIndex = 1, Fret = 10 } }
        });
        live.NotationVoices.Add(voice);
        Assert.True(h.Maintenance.GenerateLevels(ElevenLevels));
        var reduced = live.ChordTemplates.Where(t => t.Id > voiceOnly.Id).ToList();
        Assert.Equal(2, reduced.Count);

        // A hand-charted lower level: a named grip on a chord, an unnamed one under a bar.
        var handMade = new ChordTemplate { Id = 6, Name = "G5", Frets = { [0] = 3, [1] = 5, [2] = 5 } };
        var barOnly = new ChordTemplate { Id = 7, Name = "", Frets = { [0] = 7, [1] = 9 } };
        live.ChordTemplates.Add(handMade);
        live.ChordTemplates.Add(barOnly);
        var level3 = live.Levels.Single(l => l.DifficultyIndex == 3);
        level3.Chords.Add(new Chord
        {
            Position = new FractionalPosition(2),
            TemplateId = handMade.Id,
            ChordNotes =
            {
                new ChordNote { StringIndex = 0, Fret = 3 },
                new ChordNote { StringIndex = 1, Fret = 5 },
                new ChordNote { StringIndex = 2, Fret = 5 }
            }
        });
        level3.HandShapes.Add(new HandShape
        {
            Position = new FractionalPosition(3), EndPosition = new FractionalPosition(4), TemplateId = barOnly.Id
        });
        h.History.Clear();
        var before = ChartProjectCloner.Clone(h.State.ActiveProject!);
        var instancesBefore = live.ChordTemplates.ToList();

        h.Maintenance.SquashLevels();

        // Same INSTANCES, not just the same values: the restore reconciles index-wise, so a
        // template pulled out of the middle of the snapshot would shift its successors' values
        // into other live instances (the chord panels and the recent-shapes list hold those).
        Assert.Equal([barre, dsus2, spare, voiceOnly, handMade], live.ChordTemplates, ReferenceEqualityComparer.Instance);
        AssertIdsArePositions(live);
        Assert.Equal(voiceOnly.Id, Assert.Single(voice.Chords).TemplateId);
        h.History.Undo();
        AssertSame(before, h.State.ActiveProject!, "squash undone");
        Assert.Equal(instancesBefore, live.ChordTemplates, ReferenceEqualityComparer.Instance);
    }

    [Fact]
    public void ATwinSharingAUsedTemplatesId_IsNotTheOperationsToDrop()
    {
        var arrangement = new Arrangement();
        var barre = new ChordTemplate { Id = 0, Name = "A", Frets = new(BarreFrets) };
        var twin = new ChordTemplate { Id = 0, Name = "", Frets = { [0] = 5, [1] = 7 } };
        arrangement.ChordTemplates.Add(barre);
        arrangement.ChordTemplates.Add(twin);
        var level = new ChartLevel();
        level.Chords.Add(Barre(0));
        arrangement.Levels.Add(level);

        var templates = LevelTemplateOrphans.Capture(arrangement);
        level.Chords.Clear();

        // The twin was never anyone's (the id resolves to the first template), so only a
        // template the operation itself took the last use from is in question — and that
        // one carries a name.
        Assert.Empty(templates.Find(arrangement));
    }

    /// <summary>The <c>.rscp</c> numbers templates by position, so the rule only runs where the ids
    /// ARE positions; a sparse chart keeps every template rather than gain a gap the save would
    /// misread.</summary>
    [Fact]
    public void Apply_OnSparseIds_ChangesNothing()
    {
        var arrangement = new Arrangement();
        var barre = new ChordTemplate { Id = 5, Name = "A", Frets = new(BarreFrets) };
        var reduced = new ChordTemplate { Id = 9, Name = "", Frets = { [0] = 5, [1] = 7 } };
        arrangement.ChordTemplates.Add(barre);
        arrangement.ChordTemplates.Add(reduced);
        var level = new ChartLevel();
        var chord = Barre(0);
        chord.TemplateId = reduced.Id;
        level.Chords.Add(chord);
        arrangement.Levels.Add(level);
        var templates = LevelTemplateOrphans.Capture(arrangement);
        var live = arrangement.ChordTemplates.Select(t => t.Clone()).ToList();
        chord.TemplateId = barre.Id;

        Assert.Single(templates.Find(arrangement));
        Assert.Empty(templates.Apply(arrangement, live));
        Assert.Equal([barre, reduced], arrangement.ChordTemplates, ReferenceEqualityComparer.Instance);
        Assert.Equal((5, 9), (barre.Id, reduced.Id));
    }

    private static ChartPowerToolsTests.Harness Prepared(out ChordTemplate barre, int[] beats)
    {
        var h = new ChartPowerToolsTests.Harness(new LevelSquashService());
        var live = h.State.ActiveArrangement!;
        barre = new ChordTemplate { Id = 0, Name = "A", Frets = new(BarreFrets), Fingers = new(BarreFingers) };
        live.ChordTemplates.Add(barre);
        var level = live.Levels[0];
        level.Notes.Clear();
        string[] phrases = ["verse", "easy"];
        for (var i = 0; i < beats.Length; i++)
        {
            level.Chords.Add(Barre(beats[i]));
            live.EventPoints.Add(new EventPoint { Position = new FractionalPosition(beats[i]), Phrase = phrases[i] });
        }

        return h;
    }

    private static Chord Barre(int beat)
    {
        var chord = new Chord { Position = new FractionalPosition(beat), TemplateId = 0 };
        foreach (var (s, fret) in BarreFrets) chord.ChordNotes.Add(new ChordNote { StringIndex = s, Fret = fret });
        return chord;
    }

    private static ChordTemplate TemplateAt(Arrangement arrangement, int difficulty, int beat)
    {
        var level = arrangement.Levels.Single(l => l.DifficultyIndex == difficulty);
        var chord = Assert.Single(level.Chords, c => c.Position == new FractionalPosition(beat));
        return Assert.Single(arrangement.ChordTemplates, t => t.Id == chord.TemplateId);
    }

    private static void AssertEveryChordResolves(Arrangement arrangement)
    {
        var byId = ChordTemplateResolver.IndexById(arrangement.ChordTemplates);
        foreach (var level in arrangement.Levels)
        {
            Assert.All(level.Chords, c => Assert.True(byId.ContainsKey(c.TemplateId), $"chord at {c.Position} dangles"));
            Assert.All(level.HandShapes.Where(s => s.TemplateId >= 0),
                s => Assert.True(byId.ContainsKey(s.TemplateId), $"hand shape at {s.Position} dangles"));
        }
    }

    private static void AssertIdsArePositions(Arrangement arrangement)
    {
        for (var i = 0; i < arrangement.ChordTemplates.Count; i++) Assert.Equal(i, arrangement.ChordTemplates[i].Id);
    }

    /// <summary>Save and reload: the bar over nothing must still be on C's grip.</summary>
    private static void AssertTheSavedBarIsStillOn(ChartProject project, ChordTemplate expected)
    {
        var directory = Directory.CreateTempSubdirectory("dc-520-");
        try
        {
            var path = Path.Combine(directory.FullName, "pruned.rscp");
            var serializer = new RscpChartSerializer();
            serializer.Save(path, project);
            var reloaded = serializer.Load(path).Arrangements[0];
            var top = reloaded.Levels.Single(l => l.DifficultyIndex == ElevenLevels - 1);
            var bar = Assert.Single(top.HandShapes, s => s.Position == new FractionalPosition(6));
            var template = Assert.Single(reloaded.ChordTemplates, t => t.Id == bar.TemplateId);
            Assert.Equal(expected.Frets, template.Frets);
            Assert.Equal(expected.Name, template.Name);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static void AssertSame(ChartProject expected, ChartProject actual, string when)
    {
        var diffs = DeepCompare.Diff("project", expected, actual, new HashSet<string>());
        Assert.True(diffs.Count == 0, $"{when}: the project differs:\n  " + string.Join("\n  ", diffs.Take(10)));
    }
}
