using DotCharter.Services.Editing.Maintenance;
using DotCharter.Services.Editing.Quality;
using DotCharter.Tests.Presentation.Dialogs;
using DotCharter.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Editor.Inspector;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     The inspector's tab strip moving, over real sub-view-models: leaving the Lyrics tab commits its
///     pending draft (a hidden tab keeps its box focused, so nothing else would), as one history entry;
///     moving between other tabs, or onto Lyrics, commits nothing, and a missing riff library is no fault.
/// </summary>
public sealed class InspectorTabActivationTests
{
    [Fact]
    public void LeavingTheLyricsTab_CommitsItsDraftOnce_AndNoOtherMoveDoes()
    {
        var harness = new ChartEditHarness();
        var rig = new LyricDraftRig(harness, "lu");
        var tabs = NewActivation(harness, rig.Inspector);
        var node = harness.History.CurrentNodeId;

        tabs.Activated(InspectorViewModel.LyricsTabIndex);
        Assert.Equal("la", rig.Syllable.Text);
        Assert.Equal(node, harness.History.CurrentNodeId);

        tabs.Activated(InspectorViewModel.SongTabIndex);
        Assert.Equal("lu", rig.Syllable.Text);
        var committed = harness.History.CurrentNodeId;
        Assert.NotEqual(node, committed);

        tabs.Activated(InspectorViewModel.SoundTabIndex);
        tabs.Activated(InspectorViewModel.RiffLibraryTabIndex);
        Assert.Equal(committed, harness.History.CurrentNodeId);
    }

    private static InspectorTabActivation NewActivation(ChartEditHarness harness, LyricsInspectorViewModel lyrics)
    {
        var maintenance = RecordingProxy.Create<IChartMaintenanceService>().Proxy;
        var readiness = new ExportReadinessViewModel(harness.State, new CheckpointsViewModelTests.FakeDocumentService(),
            harness.Transport, harness.Editor, FakeLocalization.KeysOnly(), maintenance,
            new DotCharter.Presentation.Dialogs.DialogsSlot(), new FakeAppLogger(),
            new ChartQualityReporter(new ArrangementValidationService(new FakeAppLogger())), new TestUiDispatcher());
        var issues = new InspectorIssuesViewModel(harness.State, harness.Editor,
            new ArrangementValidationService(new FakeAppLogger()), maintenance, readiness, harness.History,
            FakeLocalization.KeysOnly(), () => null, new GatedInlineDispatcher(), new FakeAppLogger());
        var queue = new SoundQueueViewModel(() => null, () => null, () => [], () => issues.Items,
            harness.Editor.Selection, _ => { }, FakeLocalization.English(),
            _ => (ChordNamingOptions.Default, DoubleStopNameOptions.Default));
        var phrases = new PhrasesInspectorViewModel(() => null, () => [], _ => { }, null, FakeLocalization.English(),
            harness.History);
        return new InspectorTabActivation(lyrics, issues, readiness, queue, phrases, null);
    }
}
