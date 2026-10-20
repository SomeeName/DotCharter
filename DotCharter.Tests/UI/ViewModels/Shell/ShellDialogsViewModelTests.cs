using System.Text.RegularExpressions;
using DotCharter.Presentation.Contracts.Dialogs;
using DotCharter.Presentation.Dialogs;
using DotCharter.Tone.Audio.Abstractions;
using DotCharter.Tests.Presentation.Dialogs;
using DotCharter.UI.ViewModels.Shell;

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
        doors.OpenQualityReportCommand.Execute(null);

        Assert.Equal([typeof(IRocksmithExportScreen), typeof(ISetBpmScreen), typeof(IChartQualityReportScreen)], dialogs.Opened);
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

    private static ShellDialogsViewModel Build(ChartEditHarness harness, IDialogs dialogs, ILatencyCalibrationPrompt prompt)
    {
        return new ShellDialogsViewModel(harness.State, harness.Editor, dialogs, prompt, new FakeAppLogger(),
            FakeLocalization.English());
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
