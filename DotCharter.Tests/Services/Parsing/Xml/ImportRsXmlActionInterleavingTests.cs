using DotCharter.Core.Domain.Vocals;
using DotCharter.Services.Editing.Collisions;
using DotCharter.Tests.Services.Editing.Maintenance;

namespace DotCharter.Tests.Services.Parsing.Xml;

/// <summary>Every XML adoption remains an exact inverse after other history actions restore its graph.</summary>
public sealed class ImportRsXmlActionInterleavingTests
{
    public static TheoryData<string, string> AdoptedKindsAndEdits
    {
        get
        {
            var cases = new TheoryData<string, string>();
            foreach (var kind in new[] { "AdoptedBeats", "ExistingBeats", "Vocals", "Showlights" })
            {
                foreach (var edit in new[] { "BeatScale", "ArrangementDelete", "LevelGeneration", "ProjectSpacing" })
                    cases.Add(kind, edit);
            }
            return cases;
        }
    }

    [Theory]
    [MemberData(nameof(AdoptedKindsAndEdits))]
    public void EveryAdoptionAndInterleavedEdit_UndoesAndRedoesTheWholeProjectForThreeCycles(string kind, string edit)
    {
        var h = new ChartPowerToolsTests.Harness();
        var project = h.State.ActiveProject!;
        var baselineArrangement = project.Arrangements[0];
        baselineArrangement.Levels[0].Notes[0].Sustain = new FractionalPosition(1);
        var rhythm = NewArrangement("Existing rhythm");
        project.Arrangements.Add(rhythm);
        project.Showlights.Add(new Showlight { Position = new FractionalPosition(1), Note = 24 });
        project.VocalTracks.Add(NewVocals("Existing vocals"));
        if (kind == "AdoptedBeats") project.Beats.Clear();
        var before = ChartProjectCloner.Clone(project);
        var arrangement = NewArrangement("Imported lead");
        var vocals = NewVocals("Imported vocals");
        var lights = new[]
        {
            new Showlight { Position = new FractionalPosition(2), Note = 42 },
            new Showlight { Position = new FractionalPosition(4), Note = 43 }
        };
        var beats = Enumerable.Range(0, 8).Select(i => new BeatMarker
        {
            Time = i * 500, Bpm = 120, IsMeasure = i % 4 == 0, BeatsInMeasure = 4, NoteDenominator = 4
        }).ToList();
        var plan = new DotCharter.Services.Parsing.Xml.RsXmlImportPlan
        {
            Kind = kind == "Vocals" ? DotCharter.Services.Parsing.Xml.RsXmlImportKind.Vocals :
                kind == "Showlights" ? DotCharter.Services.Parsing.Xml.RsXmlImportKind.Showlights : DotCharter.Services.Parsing.Xml.RsXmlImportKind.Song,
            Arrangement = kind is "AdoptedBeats" or "ExistingBeats" ? arrangement : null,
            VocalTrack = kind == "Vocals" ? vocals : null,
            Beats = kind is "AdoptedBeats" or "ExistingBeats" ? beats : [],
            Showlights = kind == "Showlights" ? lights : []
        };
        using var dirty = new ProjectDirtyTracker(h.History);
        h.History.Do(new DotCharter.Services.Parsing.Xml.ImportRsXmlAction(project, plan, () => { }));
        var imported = ChartProjectCloner.Clone(project);
        Assert.NotEmpty(DeepCompare.Diff("project", before, imported, new HashSet<string>()));
        Assert.True(dirty.IsDirty);
        h.State.ActiveArrangement = plan.Arrangement ?? baselineArrangement;
        h.State.ActiveLevel = h.State.ActiveArrangement.Levels[0];
        ApplyInterleavedEdit(h, edit);
        Assert.Equal(2, h.History.GetUndoSteps().Count);
        var edited = ChartProjectCloner.Clone(project);
        Assert.NotEmpty(DeepCompare.Diff("project", imported, edited, new HashSet<string>()));

        h.History.Undo();
        AssertSame(imported, project);
        AssertAdoptedOwners(project, plan);
        h.History.Undo();
        AssertSame(before, project);
        Assert.False(dirty.IsDirty);
        Assert.False(h.History.CanUndo);
        for (var cycle = 0; cycle < 3; cycle++)
        {
            h.History.Redo();
            AssertSame(imported, project);
            AssertAdoptedOwners(project, plan);
            Assert.True(dirty.IsDirty);
            h.History.Redo();
            AssertSame(edited, project);
            h.History.Undo();
            AssertSame(imported, project);
            AssertAdoptedOwners(project, plan);
            h.History.Undo();
            AssertSame(before, project);
            Assert.False(dirty.IsDirty);
            Assert.False(h.History.CanUndo);
        }
    }

    private static void AssertSame(ChartProject expected, ChartProject actual) =>
        Assert.Empty(DeepCompare.Diff("project", expected, actual, new HashSet<string>()));

    private static void AssertAdoptedOwners(ChartProject project, DotCharter.Services.Parsing.Xml.RsXmlImportPlan plan)
    {
        if (plan.Arrangement != null) Assert.Same(plan.Arrangement, project.Arrangements[^1]);
        if (plan.VocalTrack != null) Assert.Same(plan.VocalTrack, project.VocalTracks[^1]);
        foreach (var light in plan.Showlights) Assert.Contains(light, project.Showlights);
    }

    private static void ApplyInterleavedEdit(ChartPowerToolsTests.Harness h, string edit)
    {
        var project = h.State.ActiveProject!;
        switch (edit)
        {
            case "BeatScale":
                var scale = new BeatScaleService(new FakeAppLogger()).BuildScaleAction(project, 0, project.Beats[^1].Time, 2);
                Assert.NotNull(scale);
                h.History.Do(scale);
                break;
            case "ArrangementDelete":
                h.Maintenance.DeleteActiveArrangement();
                break;
            case "LevelGeneration":
                Assert.True(h.Maintenance.GenerateLevels(4));
                break;
            case "ProjectSpacing":
                h.History.Do(ProjectSpacingEdit.Plan(project, new AppConfig
                {
                    MinNoteDistanceMode = MinNoteDistanceMode.Milliseconds, MinNoteDistanceMs = 250
                }, () => { }));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(edit));
        }
    }

    private static Arrangement NewArrangement(string name)
    {
        var arrangement = new Arrangement { Name = name, StartingTone = "clean", ToneA = "lead" };
        var level = new ChartLevel();
        for (var i = 0; i < 6; i++)
            level.Notes.Add(new GuitarNote { Position = new FractionalPosition(i), Sustain = new FractionalPosition(1), Fret = 5, StringIndex = i });
        level.Anchors.Add(new FretAnchor(new FractionalPosition(0), 5));
        level.Anchors.Add(new FretAnchor(new FractionalPosition(1), 5));
        arrangement.Levels.Add(level);
        arrangement.ToneNames.Add("clean");
        arrangement.ToneNames.Add("lead");
        arrangement.ToneChanges.Add(new ToneChange { Position = new FractionalPosition(2), ToneName = "lead" });
        arrangement.Phrases.Add("verse");
        arrangement.PhraseIterations.Add(new PhraseIteration(new FractionalPosition(2), 0));
        arrangement.EventPoints.Add(new EventPoint { Position = new FractionalPosition(2), Section = "verse", Phrase = "verse" });
        return arrangement;
    }

    private static VocalTrack NewVocals(string name)
    {
        var vocals = new VocalTrack { Name = name };
        var phrase = new LyricPhrase { Position = new FractionalPosition(1), EndPosition = new FractionalPosition(4) };
        phrase.Lyrics.Add(new LyricEvent
        {
            Position = new FractionalPosition(2), EndPosition = new FractionalPosition(3), Text = "word", Note = 60
        });
        vocals.Phrases.Add(phrase);
        return vocals;
    }
}
