using DotCharter.Services.Infrastructure.Shell;
using DotCharter.Presentation.Screens.Settings;

namespace DotCharter.Tests.UI.ViewModels.Settings;

/// <summary>
///     An ASIO driver's control panel parents to the main window, and the view
///     model asks <see cref="IMainWindowHandle" /> for it.
///     <para>
///         It used to read <c>Application.Current.ApplicationLifetime</c>
///         itself, wrapped in a bare <c>catch</c>. That works, but it costs this
///         view model the whole UI framework as a dependency — which is the one
///         thing standing between the Audio tab and
///         <c>DotCharter.Presentation</c>, where an <c>Avalonia</c> reference is
///         a build error. The lookup belongs with the other OS adapters in
///         <c>Platform/</c>, and now lives there.
///     </para>
/// </summary>
public sealed class AsioControlPanelParentTests
{
    private sealed class StubHandle(nint value) : IMainWindowHandle
    {
        public IntPtr Value { get; } = value;
    }

    [Fact]
    public void TheDriverPanel_ParentsToTheHandleTheSeamReturns()
    {
        var (vm, engine) = NewVm(new StubHandle(0x4321));

        vm.OpenAsioControlPanelCommand.Execute(null);

        Assert.Equal(new IntPtr(0x4321), Assert.Single(engine.AsioControlPanelParents));
        Assert.Null(vm.AsioControlPanelError);
    }

    /// <summary>
    ///     Zero is a legitimate answer, not a failure: before the window is
    ///     shown there is nothing to parent to and the panel centres on the
    ///     screen, which is what the old best-effort lookup did when it came up
    ///     empty. The panel must still OPEN.
    /// </summary>
    [Fact]
    public void WithNoWindowYet_ThePanelStillOpens_ParentedToNothing()
    {
        var (vm, engine) = NewVm(mainWindow: null);

        vm.OpenAsioControlPanelCommand.Execute(null);

        Assert.Equal(IntPtr.Zero, Assert.Single(engine.AsioControlPanelParents));
    }

    /// <summary>A refusal reaches the row that shows it — the error path is not lost with the lookup.</summary>
    [Fact]
    public void ARefusedPanel_ShowsItsReason()
    {
        var (vm, engine) = NewVm(new StubHandle(1));
        engine.AsioControlPanelError = "the driver has no control panel";

        vm.OpenAsioControlPanelCommand.Execute(null);

        Assert.Equal("the driver has no control panel", vm.AsioControlPanelError);
    }

    /// <summary>
    ///     The payoff, and the half a behavioural test cannot see: this view
    ///     model names no Avalonia type at all. Reintroducing one re-blocks the
    ///     migration silently — it still compiles here, and only fails once the
    ///     file moves.
    /// </summary>
    [Fact]
    public void TheAudioViewModel_NamesNoAvaloniaType()
    {
        var files = Directory.GetFiles(
            Path.Combine(TestPaths.RepoRoot(), "DotCharter.Presentation", "Screens", "Settings"), "AudioSettingsViewModel*.cs");
        Assert.Equal(2, files.Length);
        var source = string.Concat(files.Select(File.ReadAllText));

        Assert.DoesNotContain("Avalonia", source, StringComparison.Ordinal);

        // Non-vacuity: the file is the one this is about and still opens the panel.
        Assert.Contains("TryOpenAsioControlPanel", source, StringComparison.Ordinal);
        Assert.Contains("IMainWindowHandle", source, StringComparison.Ordinal);
    }

    /// <summary>
    ///     …and the same claim for the constructor, which a namespace scan
    ///     cannot make: no parameter is a type from the UI project. A bundle the
    ///     Presentation layer declares for it (<see cref="AudioSettingsWindows" />)
    ///     is opened up, so the seams inside it are what is checked.
    /// </summary>
    [Fact]
    public void TheAudioViewModel_TakesNothingFromTheUiProject()
    {
        var presentation = typeof(AudioSettingsViewModel).Assembly;
        var parameters = typeof(AudioSettingsViewModel)
            .GetConstructors()
            .SelectMany(c => c.GetParameters())
            .Select(p => p.ParameterType)
            .SelectMany(t => t.Assembly == presentation && t.IsClass
                ? t.GetConstructors().SelectMany(c => c.GetParameters()).Select(p => p.ParameterType)
                : [t])
            .SelectMany(Unwrap)
            .Distinct()
            .ToList();

        var fromUi = parameters
            .Where(t => t.Assembly.GetName().Name == "DotCharter.UI")
            .Select(t => t.FullName!)
            .ToList();

        Assert.True(fromUi.Count == 0,
            "the Audio tab's constructor still names a type from DotCharter.UI, which is a build error in "
            + "DotCharter.Presentation:" + Environment.NewLine + "  "
            + string.Join(Environment.NewLine + "  ", fromUi));

        // Non-vacuity: it really does take the seams this is about.
        Assert.Contains(typeof(IMainWindowHandle), parameters);
        Assert.Contains(typeof(DotCharter.Tone.Audio.Abstractions.ILatencyCalibrationPrompt), parameters);
    }

    /// <summary>
    ///     …and it takes the two FOCUSED audio seams rather than the engine.
    ///     <c>IAudioPlaybackService</c> is a banned symbol in
    ///     <c>DotCharter.Presentation</c> by design (<c>BannedSymbols.Presentation.txt</c>)
    ///     — it is the Services-internal view of the engine, and a screen that
    ///     names it can reach the transport, the mixer and the VST chain as
    ///     well as the device it actually wanted.
    /// </summary>
    [Fact]
    public void TheAudioViewModel_TakesTheFocusedSeams_NotTheEngine()
    {
        var parameters = typeof(AudioSettingsViewModel)
            .GetConstructors()
            .SelectMany(c => c.GetParameters())
            .Select(p => p.ParameterType)
            .ToList();

        Assert.DoesNotContain(typeof(DotCharter.Tone.Audio.Abstractions.IAudioPlaybackService), parameters);
        Assert.Contains(typeof(DotCharter.Tone.Audio.Abstractions.IAudioDeviceControl), parameters);
        Assert.Contains(typeof(DotCharter.Tone.Audio.Abstractions.ILiveInputCapture), parameters);
    }

    /// <summary>A generic parameter hides its arguments from a plain assembly check.</summary>
    private static IEnumerable<Type> Unwrap(Type type)
    {
        yield return type;
        if (!type.IsGenericType) yield break;
        foreach (var argument in type.GetGenericArguments())
        {
            foreach (var inner in Unwrap(argument))
                yield return inner;
        }
    }

    private static (AudioSettingsViewModel Vm, FakeAudioPlaybackService Engine) NewVm(IMainWindowHandle? mainWindow)
    {
        var config = new FakeConfigurationService();
        var engine = new FakeAudioPlaybackService();
        var latency = new DotCharter.Tests.Presentation.Settings.GraphicsStubs.FakeWizard();
        var vm = new AudioSettingsViewModel(
            engine, engine, config, new ProjectState(), new AudioSettingsWindows(latency, mainWindow), new GatedInlineDispatcher(),
            FakeLocalization.English());
        return (vm, engine);
    }
}
