using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.Services.Parsing.Xml;
using DotCharter.Tone.Infrastructure.Storage;

namespace DotCharter.Tests.Presentation.Dialogs;

/// <summary>
///     The Rocksmith XML import's progress card. The read is
///     <c>RsXmlImporter.Prepare</c> on a background thread against a SNAPSHOT of
///     the host beats; only <c>Apply</c> touches the live project. Before the
///     card existed the parse ran on the UI thread with nothing on screen and
///     every outcome was one status-bar line.
/// </summary>
public sealed class RsXmlImportViewModelTests : IDisposable
{
    [Theory]
    [InlineData("<song><arrangement>Lead</arrangement><ebeats><ebeat time=\"0\" measure=\"0\" /><ebeat time=\"0.5\" /></ebeats></song>")]
    [InlineData("<vocals count=\"1\"><vocal time=\"0.5\" length=\"0.5\" note=\"60\" lyric=\"word\" /></vocals>")]
    [InlineData("<showlights count=\"1\"><showlight time=\"0.5\" note=\"24\" /></showlights>")]
    public async Task Every_import_is_one_dirty_undo_step_and_redo_reuses_the_imported_objects(string xml)
    {
        var edit = new ChartEditHarness();
        edit.Project.Beats.Clear();
        var originalArrangement = edit.Project.Arrangements[0];
        var path = Path.Combine(_tempDir, "import.xml");
        File.WriteAllText(path, xml);
        using var dirty = new ProjectDirtyTracker(edit.History);
        var vm = HistoryTestActivation.Create<RsXmlImportViewModel>(edit.History, edit.State, edit.Editor, FakeLocalization.English(), new FakeAppLogger());

        vm.Configure(new RsXmlImportRequest(path));
        await vm.Import;

        Assert.True(ChartEditHarness.Answer(vm.Result));
        Assert.True(dirty.IsDirty);
        Assert.Single(edit.History.GetUndoSteps());
        var arrangements = edit.Project.Arrangements.ToArray();
        var vocals = edit.Project.VocalTracks.ToArray();
        var lights = edit.Project.Showlights.ToArray();
        var beats = edit.Project.Beats.ToArray();
        edit.History.Undo();
        Assert.False(dirty.IsDirty);
        Assert.Same(originalArrangement, Assert.Single(edit.Project.Arrangements));
        Assert.Empty(edit.Project.VocalTracks);
        Assert.Empty(edit.Project.Showlights);
        Assert.Empty(edit.Project.Beats);
        edit.History.Redo();
        Assert.Equal(arrangements, edit.Project.Arrangements);
        Assert.Equal(vocals, edit.Project.VocalTracks);
        Assert.Equal(lights, edit.Project.Showlights);
        Assert.Equal(beats, edit.Project.Beats);
    }

    private readonly string _tempDir = Directory.CreateTempSubdirectory("dotcharter-rsxmlvm-").FullName;

    public void Dispose() => BestEffortDelete.Directory(_tempDir);

    private static ProjectState HostState(bool withProject = true)
    {
        var state = new ProjectState();
        if (!withProject) return state;
        var project = new ChartProject { AudioFileName = "host.ogg" };
        for (var i = 0; i < 8; i++)
            project.Beats.Add(new BeatMarker { Time = i * 500.0, IsMeasure = i % 4 == 0, Bpm = 120 });
        state.ActiveProject = project;
        return state;
    }

    private static RsXmlImportViewModel NewCard(ProjectState state) =>
        HistoryTestActivation.Create<RsXmlImportViewModel>(new HistoryService(new FakeAppLogger()), state,
            new ChartEditHarness().Editor, FakeLocalization.English(), new FakeAppLogger());

    private string WriteShowlightsXml()
    {
        var path = Path.Combine(_tempDir, $"showlights-{Guid.NewGuid():N}.xml");
        File.WriteAllText(path, """
            <?xml version="1.0" encoding="utf-8"?>
            <showlights count="2">
              <showlight time="0.500" note="24" />
              <showlight time="1.500" note="42" />
            </showlights>
            """);
        return path;
    }

    private string WriteUnknownRootXml()
    {
        var path = Path.Combine(_tempDir, $"bogus-{Guid.NewGuid():N}.xml");
        File.WriteAllText(path, "<?xml version=\"1.0\" encoding=\"utf-8\"?><nonsense />");
        return path;
    }

    /// <summary>The read runs off the UI thread behind <c>IsBusy</c>; a successful merge closes the card.</summary>
    [Fact]
    public async Task A_merge_lands_in_the_project_and_closes_the_card()
    {
        var state = HostState();
        var vm = NewCard(state);
        var busyStates = new List<bool>();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(IRsXmlImportScreen.IsBusy)) busyStates.Add(vm.IsBusy);
        };

        vm.Configure(new RsXmlImportRequest(WriteShowlightsXml()));
        await vm.Import;

        Assert.Equal([true, false], busyStates);
        Assert.Equal(2, state.ActiveProject!.Showlights.Count);
        Assert.True(ChartEditHarness.Answer(vm.Result), "a successful import dismisses its own card");
        Assert.Equal("", vm.ErrorMessage);
    }

    /// <summary>A failure keeps the card up with the reason IN it; Close then answers false.</summary>
    [Fact]
    public async Task A_failed_read_keeps_the_card_up_with_the_reason()
    {
        var state = HostState();
        var vm = NewCard(state);

        vm.Configure(new RsXmlImportRequest(WriteUnknownRootXml()));
        await vm.Import;

        Assert.False(vm.Result.IsCompleted, "a failed import must leave its card on screen");
        Assert.False(string.IsNullOrEmpty(vm.ErrorMessage));
        Assert.False(vm.IsBusy);
        Assert.Empty(state.ActiveProject!.Showlights);

        vm.CloseCommand.Execute(null);
        Assert.False(ChartEditHarness.Answer(vm.Result));
    }

    /// <summary>No project is a refusal before anything is shown — there is nothing to merge into.</summary>
    [Fact]
    public async Task The_launcher_refuses_without_a_project_and_opens_nothing()
    {
        var state = HostState(withProject: false);
        var dialogs = new ScriptedDialogs(_ => NewCard(state));
        var launcher = new RsXmlImportLauncher(dialogs, new FakeFileStorageService(), new FakeConfigurationService(),
            state, new FakeAppLogger(),
            FakeLocalization.English());

        await launcher.OpenAsync(WriteShowlightsXml());

        Assert.Empty(dialogs.Opened);
        Assert.False(string.IsNullOrEmpty(state.MainDisplayMessage));
    }

    /// <summary>Picker → card → merge: the whole door, and the picked folder is remembered.</summary>
    [Fact]
    public async Task The_launcher_merges_the_picked_file_through_the_card()
    {
        var state = HostState();
        var path = WriteShowlightsXml();
        var storage = new FakeFileStorageService { NextOpenFile = path };
        var config = new FakeConfigurationService();
        var dialogs = new ScriptedDialogs(_ => NewCard(state));
        var launcher = new RsXmlImportLauncher(dialogs, storage, config, state, new FakeAppLogger(), FakeLocalization.English());

        await launcher.PickAsync();

        Assert.IsType<RsXmlImportViewModel>(Assert.Single(dialogs.Opened));
        Assert.Equal(2, state.ActiveProject!.Showlights.Count);
        Assert.Equal(_tempDir, config.LoadAppConfig().LastRsXmlDir);
    }

    /// <summary><c>Prepare</c> never touches the host, and <c>Apply</c> produces exactly what the one-call <c>ImportInto</c> did.</summary>
    [Fact]
    public void Prepare_then_Apply_matches_ImportInto_and_Prepare_touches_nothing()
    {
        var path = WriteShowlightsXml();

        var viaSplit = HostState().ActiveProject!;
        var plan = RsXmlImporter.Prepare(path, viaSplit.Beats.ToList());
        Assert.Empty(viaSplit.Showlights);
        var splitResult = new RsXmlImporter().Apply(viaSplit, plan);

        var viaOneCall = HostState().ActiveProject!;
        var oneCallResult = new RsXmlImporter().ImportInto(viaOneCall, path);

        Assert.Equal(oneCallResult.Kind, splitResult.Kind);
        Assert.Equal(oneCallResult.AddedShowlights, splitResult.AddedShowlights);
        Assert.Equal(viaOneCall.Showlights.Count, viaSplit.Showlights.Count);
    }

    [Fact]
    public async Task Conforms_to_its_contract()
    {
        var vm = NewCard(HostState());
        vm.Configure(new RsXmlImportRequest(WriteUnknownRootXml()));
        await vm.Import;
        Assert.Empty(ContractConformanceTests.Violations(typeof(IRsXmlImportScreen), vm));
    }
}
