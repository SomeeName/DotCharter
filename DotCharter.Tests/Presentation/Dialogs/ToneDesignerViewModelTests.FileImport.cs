// The Tone designer's imports from outside the project: DLC Builder tone files, and the tone rig's closest Rocksmith tone.
using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.Services.Editing.Tones;
using DotCharter.Tests.Audio.Dsp.Tones;
using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.Audio.Dsp.Tones;

namespace DotCharter.Tests.Presentation.Dialogs;

public sealed partial class ToneDesignerViewModelTests
{
    private string WriteToneFile(string name, string text)
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllText(path, text);
        return path;
    }

    private static Task ImportFile(ToneDesignerViewModel vm) =>
        ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)vm.ImportToneFileCommand).ExecuteAsync(null);

    [Fact]
    public async Task ImportingAProjectFile_AddsItsTonesAsOneUndoStep_AndSkipsKeysTheProjectHas()
    {
        var project = CreateProject();
        project.ToneDefinitions.Add(AuthoredTone("guitar", "Mine"));
        var harness = new Harness(project);
        var vm = harness.Open();
        harness.Storage.NextOpenFile = WriteToneFile("p.rs2dlc",
            ToneFileFixtures.Project(ToneFileFixtures.ToneBody("GUITAR"), ToneFileFixtures.ToneBody("bass"), ToneFileFixtures.ToneBody("lead")));
        var undoDepth = harness.History.GetUndoSteps().Count;

        await ImportFile(vm);

        Assert.Equal(["guitar", "bass", "lead"], project.ToneDefinitions.Select(t => t.Key));
        Assert.Equal("Mine", project.ToneDefinitions[0].Name);
        Assert.Equal(["guitar", "bass", "lead"], vm.ProjectTones.Select(t => t.Key));
        Assert.Equal("lead", vm.SelectedTone?.Key);
        Assert.Contains("bass, lead", vm.ImportStatus, StringComparison.Ordinal);
        Assert.Contains("skipped: GUITAR", vm.ImportStatus, StringComparison.Ordinal);
        Assert.Equal(undoDepth + 1, harness.History.GetUndoSteps().Count);

        harness.History.Undo();
        Assert.Equal(["guitar"], project.ToneDefinitions.Select(t => t.Key));
    }

    [Fact]
    public async Task ImportingAToolkitXml_AddsTheToneAndTheStoredBodyIsRocksmithJson()
    {
        var project = CreateProject();
        var harness = new Harness(project);
        var vm = harness.Open();
        harness.Storage.NextOpenFile = WriteToneFile("t.tone2014.xml", ToneFileFixtures.ToolkitXml);

        await ImportFile(vm);

        var def = Assert.Single(project.ToneDefinitions);
        Assert.Equal(("Test", "Test Tone"), (def.Key, def.Name));
        Assert.True(RsToneJsonParser.TryParse(def.Json, out var parsed));
        Assert.Equal(-12f, parsed!.VolumeDb);
        Assert.Equal("Test", vm.SelectedTone?.Key);
    }

    [Fact]
    public async Task ImportingOnlyDuplicates_AddsNothing_SaysSo_AndLeavesTheHistoryAlone()
    {
        var project = CreateProject();
        project.ToneDefinitions.Add(AuthoredTone("guitar", "Mine"));
        var harness = new Harness(project);
        var vm = harness.Open();
        harness.Storage.NextOpenFile = WriteToneFile("g.tone2014.json", ToneFileFixtures.ToneBody("guitar"));
        var undoDepth = harness.History.GetUndoSteps().Count;

        await ImportFile(vm);

        Assert.Equal("Mine", Assert.Single(project.ToneDefinitions).Name);
        Assert.Contains("skipped: guitar", vm.ImportStatus, StringComparison.Ordinal);
        Assert.Equal(undoDepth, harness.History.GetUndoSteps().Count);
    }

    [Theory]
    [InlineData("not a tone file at all")]
    [InlineData("{ \"Hello\": \"world\" }")]
    [InlineData("<Tone2014><broken>")]
    public async Task AMalformedFile_IsSaidInTheStatusLine_WithoutAChange(string text)
    {
        var project = CreateProject();
        var harness = new Harness(project);
        var vm = harness.Open();
        harness.Storage.NextOpenFile = WriteToneFile("bad.json", text);

        await ImportFile(vm);

        Assert.Empty(project.ToneDefinitions);
        Assert.StartsWith("Could not read tones:", vm.ImportStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMissingFile_IsSaidInTheStatusLine_AndACancelledPickerIsSilent()
    {
        var harness = new Harness(CreateProject());
        var vm = harness.Open();

        harness.Storage.NextOpenFile = Path.Combine(_tempDir, "nope.json");
        await ImportFile(vm);
        Assert.StartsWith("Could not read tones:", vm.ImportStatus, StringComparison.Ordinal);

        harness.Storage.NextOpenFile = null;
        var before = vm.ImportStatus;
        await ImportFile(vm);
        Assert.Equal(before, vm.ImportStatus);
    }

    // ------------------------------------------------------------- from tone rig

    private static RigToneImportReviewViewModel OpenRigImport(ToneDesignerViewModel vm)
    {
        vm.RigImportRunner = work => Task.FromResult(work());
        vm.ImportFromToneRigCommand.Execute(null);
        return Assert.IsType<RigToneImportReviewViewModel>(vm.RigImport);
    }

    private static FakeDspChainService ChainWithAnOverdrive()
    {
        var chain = new FakeDspChainService(RigCatalog());
        chain.SetParam(chain.AddNode("overdrive")!.NodeId, "drive", 0.9f);
        chain.AddNode("amp");
        chain.AddNode("cab");
        return chain;
    }

    [Fact]
    public async Task FromToneRig_AcceptAddsANewToneInAFreeSlot_AsOneUndoStep()
    {
        var project = CreateProject();
        project.ToneDefinitions.Add(AuthoredTone("clean", "Clean"));
        var harness = new Harness(project, ChainWithAnOverdrive());
        var vm = harness.Open();
        var depth = harness.History.GetUndoSteps().Count;

        var review = OpenRigImport(vm);
        await review.LastRun;
        Assert.NotEmpty(review.Rows);
        Assert.Equal(depth, harness.History.GetUndoSteps().Count);
        review.AcceptCommand.Execute(null);

        Assert.Null(vm.RigImport);
        Assert.Equal(depth + 1, harness.History.GetUndoSteps().Count);
        var added = Assert.Single(project.ToneDefinitions, t => t.Key == RigToneImport.NewKeyStem);
        Assert.True(RsToneJsonParser.TryParse(added.Json, out var tone));
        Assert.True(DotCharter.Services.Ai.Generate.RsToneCatalogValidation.IsValid(tone, RsGearCatalog.Shared));
        Assert.Equal(RigToneImport.NewKeyStem, project.Arrangements[0].ToneC);
        Assert.Contains(RigToneImport.NewKeyStem, vm.ImportStatus);

        harness.History.Undo();
        Assert.DoesNotContain(project.ToneDefinitions, t => t.Key == RigToneImport.NewKeyStem);
        Assert.Equal("", project.Arrangements[0].ToneC);
    }

    [Fact]
    public async Task FromToneRig_ReplaceKeepsTheEditedTonesKeyAndName_AsOneUndoStep()
    {
        var project = CreateProject();
        project.ToneDefinitions.Add(AuthoredTone("clean", "Clean"));
        var before = project.ToneDefinitions[0].Json;
        var harness = new Harness(project, ChainWithAnOverdrive());
        var vm = harness.Open();
        vm.SelectedTone = vm.ProjectTones[0];
        var depth = harness.History.GetUndoSteps().Count;

        var review = OpenRigImport(vm);
        await review.LastRun;
        Assert.True(review.CanReplace);
        review.ReplaceEdited = true;
        review.AcceptCommand.Execute(null);

        var replaced = Assert.Single(project.ToneDefinitions);
        Assert.Equal(("clean", "Clean"), (replaced.Key, replaced.Name));
        Assert.NotEqual(before, replaced.Json);
        Assert.Equal(depth + 1, harness.History.GetUndoSteps().Count);
        harness.History.Undo();
        Assert.Equal(before, Assert.Single(project.ToneDefinitions).Json);
    }

    [Fact]
    public async Task FromToneRig_NamesEveryLoss_AndCancelWritesNothing()
    {
        var project = CreateProject();
        var chain = new FakeDspChainService(RigCatalog());
        chain.AddNode("overdrive");
        var harness = new Harness(project, chain);
        var vm = harness.Open();
        var depth = harness.History.GetUndoSteps().Count;

        var review = OpenRigImport(vm);
        await review.LastRun;

        // No amp and no cabinet in this rig: both stand-ins are said, in words.
        Assert.Contains(review.Losses, l => l.StartsWith("The rig has no amp", StringComparison.Ordinal));
        Assert.Contains(review.Losses, l => l.StartsWith("The rig has no cabinet", StringComparison.Ordinal));
        Assert.DoesNotContain(review.Losses, l => l.Contains("RigImport_", StringComparison.Ordinal));
        review.CancelCommand.Execute(null);

        Assert.Null(vm.RigImport);
        Assert.Empty(project.ToneDefinitions);
        Assert.Equal(depth, harness.History.GetUndoSteps().Count);
    }

    [Fact]
    public async Task FromToneRig_UntickingAPedal_LeavesItOut_AndSaysSo()
    {
        var harness = new Harness(CreateProject(), ChainWithAnOverdrive());
        var vm = harness.Open();
        var review = OpenRigImport(vm);
        await review.LastRun;
        var pedal = review.Rows.First(r => r.CanToggle && r.Slot.StartsWith("Pre-pedal", StringComparison.Ordinal));

        pedal.Include = false;
        await review.LastRun;

        Assert.Empty(review.Result!.Tone!.PrePedals);
        Assert.Contains(review.Rows, r => r is { Include: false, Slot: "Left out" });
        Assert.Contains(review.Losses, l => l.EndsWith("is left out.", StringComparison.Ordinal));
    }

    [Fact]
    public void FromToneRig_WithoutANativeRig_SaysSo_AndOpensNothing()
    {
        var vm = new Harness(CreateProject(), new NullDspChainService()).Open();

        vm.ImportFromToneRigCommand.Execute(null);

        Assert.Null(vm.RigImport);
        Assert.Contains("not available", vm.ImportStatus, StringComparison.Ordinal);
    }
}
