using DotCharter.Services.Editing;
using DotCharter.Services.Editing.Highway;
using DotCharter.Services.Editing.Selection.Patterns;
using System.Text.RegularExpressions;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Dialogs;
using DotCharter.Tone.Audio.Abstractions;
using DotCharter.Tests.Presentation.Dialogs;
using DotCharter.UI.ViewModels.Shell;
using DotCharter.Tests.UI.ViewModels.Editor;

namespace DotCharter.Tests.UI.ViewModels.Shell;

/// <summary>
///     The shell's dialog doors left <c>MainWindowViewModel.Dialogs.cs</c> for
///     <see cref="ShellDialogsViewModel" />, and with them the shell's last
///     <c>App.Services</c> lookup: Tools ▸ Calibrate latency fetched
///     <see cref="ILatencyCalibrationPrompt" /> from the static locator, so the
///     dependency was invisible to the constructor, the budget and every DI lint.
/// </summary>
public sealed class ShellDialogsViewModelTests
{
    [Fact]
    public void CalibrateLatency_OpensTheInjectedPrompt()
    {
        var (prompt, recorder) = RecordingProxy.Create<ILatencyCalibrationPrompt>();
        var doors = Build(new ChartEditHarness(), new RecordingDialogs(), prompt);

        doors.OpenLatencyWizardCommand.Execute(null);

        Assert.Equal(["OpenAsync()"], recorder.Calls);
    }

    [Fact]
    public void AVerbWithNothingToActOn_SaysSo_AndOpensNoDialog()
    {
        var harness = new ChartEditHarness();
        harness.State.ActiveProject = null;
        harness.State.ActiveArrangement = null;
        var dialogs = new RecordingDialogs();
        var (prompt, _) = RecordingProxy.Create<ILatencyCalibrationPrompt>();
        var doors = Build(harness, dialogs, prompt);

        doors.OpenRocksmithExportCommand.Execute(null);
        Assert.Equal(FakeLocalization.English()["Status_Export_NoProject"], harness.State.MainDisplayMessage);

        doors.OpenChartDiffCommand.Execute(null);
        Assert.Equal(FakeLocalization.English()["Status_NoArrangementSelected"], harness.State.MainDisplayMessage);

        doors.OpenQualityReportCommand.Execute(null);
        Assert.Equal(FakeLocalization.English()["Status_NoChartLoaded"], harness.State.MainDisplayMessage);

        Assert.Empty(dialogs.Opened);
    }

    [Fact]
    public void AVerbWithAChart_OpensItsHostedScreen()
    {
        var dialogs = new RecordingDialogs();
        var (prompt, _) = RecordingProxy.Create<ILatencyCalibrationPrompt>();
        var doors = Build(new ChartEditHarness(), dialogs, prompt);

        doors.OpenRocksmithExportCommand.Execute(null);
        doors.OpenSetBpmDialogCommand.Execute(null);
        Assert.Equal([typeof(IRocksmithExportScreen), typeof(ISetBpmScreen)], dialogs.Opened);
    }

    /// <summary>Both package exports read the chart when they open and stay modal: a lyric draft
    /// still in its text box is applied before the dialog opens.</summary>
    [Fact]
    public void ThePackageExports_ApplyALyricDraftBeforeTheyOpen()
    {
        var dialogs = new RecordingDialogs();
        var (prompt, _) = RecordingProxy.Create<ILatencyCalibrationPrompt>();
        var harness = new ChartEditHarness();
        var rig = new LyricDraftRig(harness, "lo");
        var doors = Build(harness, dialogs, prompt);

        doors.OpenRocksmithExportCommand.Execute(null);
        Assert.Equal("lo", rig.Syllable.Text);
        rig.Inspector.SelectedLyricText = "lu";
        doors.OpenDcSongExportCommand.Execute(null);

        Assert.Equal("lu", rig.Syllable.Text);
        Assert.Equal([typeof(IRocksmithExportScreen), typeof(IDcSongExportScreen)], dialogs.Opened);
    }

    /// <summary>The dialog is gone (#497): Tools ▸ Chart quality report brings the Issues tab up instead.</summary>
    [Fact]
    public void TheQualityReportVerb_OpensTheIssuesTab_NotADialog()
    {
        var dialogs = new RecordingDialogs();
        var (prompt, _) = RecordingProxy.Create<ILatencyCalibrationPrompt>();
        var doors = Build(new ChartEditHarness(), dialogs, prompt);
        var shown = 0;
        doors.ShowChartIssues = () => shown++;

        doors.OpenQualityReportCommand.Execute(null);

        Assert.Equal(1, shown);
        Assert.Empty(dialogs.Opened);
    }

    [Fact]
    public void FitTempoToRecording_OnAChartWithoutNotes_SaysWhatItNeeds_AndOpensOnlyOnceTheChartHasANote()
    {
        var harness = new ChartEditHarness();
        var dialogs = new RecordingDialogs();
        var (prompt, _) = RecordingProxy.Create<ILatencyCalibrationPrompt>();
        var doors = Build(harness, dialogs, prompt);

        doors.OpenAlignToRecordingDialogCommand.Execute(null);

        Assert.Equal(FakeLocalization.English()["Dialog_AlignToRecording_NeedsNotes"], harness.State.MainDisplayMessage);
        Assert.Equal(StatusSeverity.Warning, harness.State.MainDisplaySeverity);
        Assert.Empty(dialogs.Opened);

        harness.Project.Arrangements[0].Levels[0].Notes.Add(new GuitarNote());
        doors.OpenAlignToRecordingDialogCommand.Execute(null);

        Assert.Equal([typeof(IAlignToRecordingScreen)], dialogs.Opened);
    }

    /// <summary>The shell and every view model split out of it take what they use through
    /// their constructors; a static lookup would hide a dependency again.</summary>
    [Fact]
    public void NoShellViewModel_ReachesForTheServiceLocator()
    {
        var folder = Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "ViewModels", "Shell");
        var files = UiSourceTree.GetFiles(folder, "*.cs");
        Assert.Contains(files, f => Path.GetFileName(f) == "ShellDialogsViewModel.cs");

        var offenders = files
            .Where(f => Regex.IsMatch(SourceText.StripComments(UiSourceTree.ReadAllText(f)), @"\bApp\s*\.\s*Services\b"))
            .Select(Path.GetFileName)
            .ToList();

        Assert.True(offenders.Count == 0,
            "These shell view models resolve a service through App.Services: " + string.Join(", ", offenders) +
            ". Take it through the constructor — as a Func<T> factory when it must not be built on the startup path.");
    }

    [Fact]
    public void TheFhpDialogFallbackRetainsThePressedRowsTarget()
    {
        var h = new ChartEditHarness();
        var other = new Arrangement { Name = "Rhythm" };
        other.Levels.Add(new ChartLevel());
        h.Project.Arrangements.Add(other);
        var track = h.Editor.TrackContexts.GetOrCreate(other);
        var (dialogs, calls) = RecordingProxy.Create<IDialogs>();
        var doors = Build(h, dialogs, RecordingProxy.Create<ILatencyCalibrationPrompt>().Proxy);
        var request = new FhpRequest(1250, null, new FhpTrackTarget(track, new FractionalPosition(2, new Fraction(1, 2))));
        ((DotCharter.UI.Input.IFhpEditLauncher)doors).EditFhpInPlace(new object(), request);
        var call = Assert.Single(calls.Invocations, c => c.Method == "ShowAsync");
        var initialize = Assert.IsType<Action<IFhpScreen>>(call.Args[0]);
        var editor = new DotCharter.Presentation.Screens.Dialogs.FhpViewModel(h.Services);
        initialize(editor);
        editor.Fret = 9;
        editor.SaveCommand.Execute(null);
        Assert.Equal(request.Target!.Position, Assert.Single(track.Anchors).Position);
        Assert.Empty(h.Editor.Anchors);
        h.History.Undo();
        Assert.Empty(track.Anchors);
    }

    private static ShellDialogsViewModel Build(ChartEditHarness harness, IDialogs dialogs, ILatencyCalibrationPrompt prompt)
    {
        return new ShellDialogsViewModel(harness.State, harness.Editor, dialogs, prompt, new FakeAppLogger(),
            FakeLocalization.English(), RecordingProxy.Create<IPlaybackTransport>().Item1, new DotCharter.Presentation.Shared.ChartIssuesDoor(), SelectionEdits(harness),
            new InlineEditRig().Inline);
    }

    private static SelectionEditsViewModel SelectionEdits(ChartEditHarness harness)
    {
        var (global, _) = RecordingProxy.Create<IGlobalEditingCoordinator>();
        var (highway, _) = RecordingProxy.Create<IHighwayEditingService>();
        return new SelectionEditsViewModel(global, highway, harness.Editor.Selection, harness.State,
            FakeLocalization.English(), new MatchPatternStore(new FakeConfigurationService()));
    }

    /// <summary>Records which contract each open asked for and answers with the default.</summary>
    private sealed class RecordingDialogs : IDialogs
    {
        public List<Type> Opened { get; } = [];

        public bool HasOpenDialog => false;

        public event EventHandler<DialogChange>? Changed
        {
            add { }
            remove { }
        }

        public Task<TResult?> ShowAsync<TContract, TResult>(Action<TContract>? init = null)
            where TContract : class, IDialogScreen<TResult>
        {
            Opened.Add(typeof(TContract));
            return Task.FromResult(default(TResult));
        }

        public Task<bool> MessageAsync(string title, string message, bool isConfirmation = false) => Task.FromResult(false);

        public bool CloseTop() => false;
    }
}
