using DotCharter.Services.Editing.Tones;
using DotCharter.Services.Export.Validation;
using DotCharter.Tone.Audio.Dsp.Tones;

namespace DotCharter.Tests.Services.Editing.Tones;

/// <summary>
///     #195: the repair of an undefined tone key is PROPOSED only when the project leaves no doubt,
///     and it is <see cref="ToneKeyEdits.PlanRename" />'s action — one undo step, the export clean
///     after it. The two broken shapes are exactly what the old partial renames left (#181); every
///     refusal below is a project where a rename would merge two tones the chart keeps apart.
/// </summary>
public sealed class ToneKeyRepairTests
{
    private const string Old = "lead_dist";
    private const string New = "lead_crunch";

    // ---- the two shapes the old renames left: proposed, applied, undone -----------

    /// <summary>The Song tab's old rename moved the tone changes and the starting tone, and left the
    /// definition and the Base/A slots on the old key.</summary>
    [Fact]
    public void TheSongTabShape_IsRepairedByRenamingTheDefinitionOntoTheKeyTheChartPlays()
    {
        var project = Project();
        var lead = project.Arrangements[0];
        lead.ToneChanges[0].ToneName = New;
        lead.StartingTone = New;
        Assert.Equal([New], MissingTones(project));

        var repair = Assert.IsType<ToneKeyRepairProposal>(ToneKeyRepair.Propose(project));
        Assert.Equal((Old, New), (repair.From, repair.To));

        AssertAppliesAsOneUndoStep(project, repair);
    }

    /// <summary>The Tone designer's old key edit moved the definition alone: nothing references it.</summary>
    [Fact]
    public void TheDesignerShape_IsRepairedByRenamingTheOrphanDefinitionBack()
    {
        var project = Project();
        project.ToneDefinitions[0] = Definition(New);
        Assert.Equal([Old], MissingTones(project));

        var repair = Assert.IsType<ToneKeyRepairProposal>(ToneKeyRepair.Propose(project));
        Assert.Equal((New, Old), (repair.From, repair.To));

        AssertAppliesAsOneUndoStep(project, repair);
    }

    [Fact]
    public void TheProposal_IsForTheKeyTheFindingNames_AndNoOther()
    {
        var project = Project();
        project.ToneDefinitions[0] = Definition(New);

        Assert.NotNull(ToneKeyRepair.ProposeFor(project, " LEAD_DIST "));
        Assert.Null(ToneKeyRepair.ProposeFor(project, New));
        Assert.Null(ToneKeyRepair.ProposeFor(project, ""));
    }

    // ---- anything ambiguous stays a report ------------------------------------------

    [Fact]
    public void ACleanProject_NeedsNoRepair() => Assert.Null(ToneKeyRepair.Propose(Project()));

    [Fact]
    public void TwoUndefinedKeys_AreRefused()
    {
        var project = Project();
        project.ToneDefinitions[0] = Definition(New);
        project.Arrangements[0].ToneB = "solo";
        project.Arrangements[0].ToneChanges.Add(new ToneChange { Position = Pos(6000, project), ToneName = "solo" });

        Assert.Null(ToneKeyRepair.Propose(project));
    }

    /// <summary>Two defined tones nothing plays: which one the undefined key meant is a guess.</summary>
    [Fact]
    public void TwoCandidateDefinitions_AreRefused()
    {
        var project = Project();
        project.ToneDefinitions[0] = Definition(New);
        project.ToneDefinitions.Add(Definition("spare"));

        Assert.Null(ToneKeyRepair.Propose(project));
    }

    /// <summary>A defined tone that is PLAYED is its own tone: renaming it would fold two into one.</summary>
    [Fact]
    public void ADefinitionTheChartPlays_IsNoCandidate()
    {
        var project = Project();
        var lead = project.Arrangements[0];
        lead.ToneB = "solo";
        lead.ToneChanges.Add(new ToneChange { Position = Pos(6000, project), ToneName = "solo" });

        Assert.Equal(["solo"], MissingTones(project));
        Assert.Null(ToneKeyRepair.Propose(project));
    }

    /// <summary>A base tone with no starting tone still plays at the start, so it is no candidate.</summary>
    [Fact]
    public void ABaseToneWithNoStartingTone_StillPlays_AndIsNoCandidate()
    {
        var project = Project();
        var lead = project.Arrangements[0];
        lead.StartingTone = "";
        lead.ToneA = "";
        lead.ToneChanges[0].ToneName = "solo";

        Assert.Null(ToneKeyRepair.Propose(project));
    }

    /// <summary>Two slots of one arrangement are two tones even when one of them is not placed yet.</summary>
    [Fact]
    public void ADefinitionInAnotherSlotOfTheSameArrangement_IsNoCandidate()
    {
        var project = Project();
        project.ToneDefinitions.Add(Definition("verse"));
        var lead = project.Arrangements[0];
        lead.ToneB = "verse";
        lead.ToneC = "solo";
        lead.ToneChanges.Add(new ToneChange { Position = Pos(6000, project), ToneName = "solo" });

        Assert.Null(ToneKeyRepair.Propose(project));
    }

    // ---- helpers ---------------------------------------------------------------------

    private static void AssertAppliesAsOneUndoStep(ChartProject project, ToneKeyRepairProposal repair)
    {
        var before = ChartProjectCloner.Clone(project);
        var history = new HistoryService(new FakeAppLogger());

        Assert.True(history.Do(repair.Action));
        Assert.Empty(MissingTones(project));
        Assert.All(ToneKeyEdits.Keys(project), k => Assert.True(k.IsDefined, k.Key + " is still undefined"));
        Assert.Null(ToneKeyRepair.Propose(project));

        history.Undo();
        Assert.False(history.CanUndo, "the repair must be ONE undo step");
        Assert.Empty(DeepCompare.Diff("project", before, project, new HashSet<string>()));
    }

    /// <summary>One defined tone, named by Lead's base and slot A, its starting tone, its declared
    /// names and one tone change; one note so the export check reaches the tones.</summary>
    private static ChartProject Project()
    {
        var project = new ChartProject { Chart = { SongName = "Tone Song", Artist = "DotCharter" } };
        for (var i = 0; i < 16; i++)
            project.Beats.Add(new BeatMarker { Time = i * 500.0, IsMeasure = i % 4 == 0, Bpm = 120, BeatsInMeasure = 4, NoteDenominator = 4 });
        project.ToneDefinitions.Add(Definition(Old));
        var lead = new Arrangement
        {
            ArrangementType = ArrangementType.Lead, Name = "Lead", ToneBase = Old, ToneA = Old, StartingTone = Old
        };
        lead.ToneNames.Add(Old);
        lead.ToneChanges.Add(new ToneChange { Position = Pos(4000, project), ToneName = Old });
        var level = new ChartLevel { DifficultyIndex = 0 };
        level.Notes.Add(new GuitarNote { Position = Pos(2000, project), StringIndex = 0, Fret = 3 });
        lead.Levels.Add(level);
        project.Arrangements.Add(lead);
        return project;
    }

    private static List<string> MissingTones(ChartProject project) =>
    [
        .. new ChartExportValidator().Validate(project)
            .Where(f => f.MessageKey == ChartExportValidator.KeyToneNotDefined)
            .Select(f => f.Detail)
    ];

    private static ToneDefinition Definition(string key) =>
        new() { Key = key, Name = key, Json = RsToneJsonWriter.Write(RsToneAuthoring.CreateDefaultTone(key, key, RsGearCatalog.Shared)) };

    private static FractionalPosition Pos(double ms, ChartProject project) => TimelineMath.PositionFromMs(ms, project.Beats);
}
