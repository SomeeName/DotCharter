// The designer's "Create with AI…": the command opens the creator over the selected tone and the designer follows what it accepted.

using CommunityToolkit.Mvvm.Input;
using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.Services.Ai.Generate;
using DotCharter.Services.Editing;
using DotCharter.Tests.Ai.Chart;
using DotCharter.Tests.Ai.Generate;

namespace DotCharter.Tests.Presentation.Dialogs;

public sealed partial class ToneDesignerViewModelTests
{
    [Fact]
    public async Task Create_with_AI_opens_the_creator_over_the_selected_tone_and_lists_what_it_accepted()
    {
        var project = CreateProject();
        project.ToneDefinitions.Add(AuthoredTone("clean", "Clean"));
        var harness = new Harness(project);
        ToneCreatorViewModel? opened = null;
        harness.Dialogs = new ScriptedDialogs(_ =>
        {
            var services = new ChartEditDialogServices(RecordingProxy.Create<IChartEditorSurface>().Proxy, harness.History, harness.State,
                harness.Config, FakeLocalization.English(), new FakeAppLogger());
            var model = new FakeChartModel((_, _) => GenerateFakes.ValidReply());
            opened = new ToneCreatorViewModel(services, new ToneCreator(new LanguageModelRsToneMapper(() => model, "fake"),
                new GenerateFakes.FakeIsolator(), new MeasuredToneEvidenceSource()), RecordingProxy.Create<IRsToneFeedbackLoop>().Proxy);
            return opened;
        }, screen => _ = DriveAsync((ToneCreatorViewModel)screen));
        var vm = harness.Open();
        vm.SelectedTone = vm.ProjectTones[0];
        vm.Editor.VolumeDb = -7;

        await ((IAsyncRelayCommand)vm.CreateWithAiCommand).ExecuteAsync(null);

        Assert.NotNull(opened);
        Assert.True(opened!.CanReplaceEdited);
        Assert.Contains("Clean", opened.ReplaceEditedText, StringComparison.Ordinal);
        Assert.Equal(["clean", "Rhythm"], vm.ProjectTones.Select(t => t.Key));
        Assert.Equal("Rhythm", vm.SelectedTone?.Key);
        Assert.Equal("Rhythm", vm.Arrangements[0].ToneC);
        Assert.Contains("Rhythm", vm.ImportStatus, StringComparison.Ordinal);
        // The buffered volume edit was committed BEFORE the creator wrote, as its own step.
        Assert.Equal(2, harness.History.GetUndoSteps().Count);

        static async Task DriveAsync(ToneCreatorViewModel creator)
        {
            creator.Description = "tight rhythm";
            await ((IAsyncRelayCommand)creator.CreateCommand).ExecuteAsync(null);
            creator.IsSaveAsNew = true;
            creator.NewToneName = "Rhythm";
            creator.AcceptCommand.Execute(null);
        }
    }
}
