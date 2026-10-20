using DotCharter.Core.Domain.Vocals;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Screens.Dialogs;
using DotCharter.Services.Infrastructure.Documents;

namespace DotCharter.Tests.Presentation.Dialogs;

/// <summary>
///     Tools ▸ Chart diff loads a WHOLE second project to compare against. The
///     leak-guard lint reads subscriptions, not held object graphs, so nothing
///     else would notice the dialog keeping a song alive: it lets go of the
///     compared project before the next one loads and when it closes.
/// </summary>
public sealed class ChartDiffViewModelTests
{
    private static Arrangement Arrangement(string name, int fret)
    {
        var arrangement = new Arrangement { Name = name };
        var level = new ChartLevel();
        level.Notes.Add(new GuitarNote { StringIndex = 0, Fret = fret });
        arrangement.Levels.Add(level);
        return arrangement;
    }

    private static (ChartDiffViewModel Vm, FakeFileStorageService Storage, OneShotDocuments Documents) Build()
    {
        var other = new ChartProject();
        other.Arrangements.Add(Arrangement("Rhythm", 7));
        other.Arrangements.Add(Arrangement("Lead", 7));
        var state = new ProjectState { ActiveArrangement = Arrangement("Lead", 5) };
        var storage = new FakeFileStorageService { NextOpenFile = @"C:\songs\other.rscp" };
        var documents = new OneShotDocuments(other);
        var vm = new ChartDiffViewModel(storage, documents, state, new FakeConfigurationService(),
            FakeLocalization.English(), new FakeAppLogger());
        return (vm, storage, documents);
    }

    private static Task CompareAsync(ChartDiffViewModel vm) =>
        ((CommunityToolkit.Mvvm.Input.IAsyncRelayCommand)vm.BrowseFileCommand).ExecuteAsync(null);

    [Fact]
    public async Task Comparing_picks_the_same_named_arrangement_and_lists_the_difference()
    {
        var (vm, _, _) = Build();
        Assert.Equal(FakeLocalization.English()["ChartDiff_PickFile"], vm.Status);

        await CompareAsync(vm);

        Assert.Equal("other.rscp", vm.ComparedFileName);
        Assert.Equal(2, vm.ComparableArrangements.Count);
        Assert.Equal(1, vm.SelectedArrangementIndex); // "Lead", not the first
        Assert.True(vm.HasComparableArrangements);
        Assert.True(vm.HasResults); // fret 5 here, fret 7 there
        Assert.Contains(vm.Rows, r => r.IsChanged);
        Assert.Equal("", vm.Status);
    }

    [Fact]
    public async Task Closing_releases_the_compared_project_and_answers_true()
    {
        var (vm, _, _) = Build();
        await CompareAsync(vm);

        vm.CloseCommand.Execute(null);

        Assert.True(ChartEditHarness.Answer(vm.Result));
        AssertReleased(vm);
    }

    [Fact]
    public async Task Esc_releases_it_too()
    {
        var (vm, _, _) = Build();
        await CompareAsync(vm);

        Assert.True(vm.TryCancel());

        Assert.False(ChartEditHarness.Answer(vm.Result));
        AssertReleased(vm);
    }

    /// <summary>Cancelling the second picker keeps the first comparison — nothing was chosen to replace it.</summary>
    [Fact]
    public async Task A_cancelled_second_pick_keeps_the_first_comparison()
    {
        var (vm, storage, _) = Build();
        await CompareAsync(vm);
        var first = vm.SelectedArrangement;

        storage.NextOpenFile = null;
        await CompareAsync(vm);

        Assert.Same(first, vm.SelectedArrangement);
    }

    /// <summary>A second file is loaded only after the first is let go of: two songs are never held at once.</summary>
    [Fact]
    public async Task A_second_pick_lets_go_of_the_first_before_loading()
    {
        var (vm, _, documents) = Build();
        await CompareAsync(vm);
        documents.OnLoad = () => AssertReleased(vm);

        await CompareAsync(vm);

        Assert.Equal(1, documents.LoadsChecked);
        Assert.True(vm.HasResults);
    }

    /// <summary>A file still loading keeps the dialog open: closing would strand the load.</summary>
    [Fact]
    public async Task Close_and_Esc_are_refused_while_a_file_loads()
    {
        var (vm, _, documents) = Build();
        var gate = new ManualResetEventSlim();
        documents.OnLoad = () => gate.Wait(TimeSpan.FromSeconds(60));
        var load = CompareAsync(vm);

        Assert.False(vm.TryCancel());
        vm.CloseCommand.Execute(null);
        Assert.False(vm.Result.IsCompleted);

        gate.Set();
        await load;
        Assert.True(vm.TryCancel());
    }

    [Fact]
    public void Conforms_to_its_contract()
    {
        var (vm, _, _) = Build();
        Assert.Empty(ContractConformanceTests.Violations(typeof(IChartDiffScreen), vm));
    }

    private static void AssertReleased(ChartDiffViewModel vm)
    {
        Assert.Null(vm.SelectedArrangement);
        Assert.Equal(-1, vm.SelectedArrangementIndex);
        Assert.Empty(vm.ComparableArrangements);
        Assert.Empty(vm.Rows);
        Assert.False(vm.HasResults);
        Assert.False(vm.HasComparableArrangements);
        Assert.Equal("", vm.ComparedFileName);
    }

    private sealed class OneShotDocuments(ChartProject project) : IProjectDocumentService
    {
        public Action? OnLoad { get; set; }

        public int LoadsChecked { get; private set; }

        public ChartProject LoadChart(string filePath)
        {
            if (OnLoad is { } check)
            {
                check();
                LoadsChecked++;
            }

            return project;
        }

        public void SaveChart(string filePath, ChartProject p, double? audioLengthMs = null) { }

        public void RefreshArrangementFileManifest(ChartProject p) { }

        public void SaveDlcBuilderXmlBundle(string projectPath, ChartProject p, double? audioLengthMs = null) { }

        public ChartProject AssembleProject(ChartProject? baseProject, double bpm, string audioFileName,
            IEnumerable<LyricPhrase> lyrics) => baseProject ?? new ChartProject();

        public ChartProject CreateNewProject(string title, string artist, string audioFileName) => new();
    }
}
