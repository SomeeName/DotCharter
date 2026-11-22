using System.Runtime.CompilerServices;
using DotCharter.Presentation.Screens.Settings;

namespace DotCharter.Tests.UI.ViewModels.Settings;

/// <summary>
///     The device enumeration <c>AudioSettingsViewModel.Open</c> starts, the capture device's jack-count read and the
///     playback device's output-count read end with the view model: once it is disposed, a probe still inside the
///     device seam holds nothing of it, and its late result lands nowhere.
///     <para>
///         The view model is the app's one shared instance and reaches the app's project state, which every screen
///         listens to, so a probe that captured it kept a whole released app reachable for as long as a slow
///         enumeration ran (<c>Perf/IsolatedAppReleaseUiPerfSmokeTests</c> under load). Here the probe is parked
///         inside the enumeration while the view model is disposed and collected.
///     </para>
/// </summary>
public sealed class AudioSettingsProbeReleaseTests
{
    [Fact]
    public async Task ADisposedViewModel_IsCollectable_WhileItsDeviceProbeIsStillRunning()
    {
        using var parked = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var engine = new FakeAudioPlaybackService
        {
            WasapiProbePass = () =>
            {
                parked.Set();
                release.Wait(BoundedWait.DeadlockCap);
            }
        };
        engine.WasapiInputDevices.Add("Line In");
        var ui = new HeldPostsDispatcher();

        var (viewModel, probe) = OpenAndDispose(engine, ui, parked);
        for (var i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        var collectedWhileParked = !viewModel.IsAlive;
        release.Set();
        await BoundedWait.ForAsync(probe, "the parked device probe to finish", TestContext.Current.CancellationToken);

        Assert.True(collectedWhileParked,
            "the disposed Audio settings view model is still reachable while its device probe runs: the probe holds it");
        Assert.Equal(0, ui.HeldCount);
    }

    [Fact]
    public async Task ADisposedViewModel_IsCollectable_WhileItsJackProbeIsStillRunning()
    {
        using var parked = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var engine = new FakeAudioPlaybackService { InputProbePass = () => Park(parked, release) };
        var ui = new HeldPostsDispatcher();

        var (viewModel, probe) = StartAndDispose(engine, ui, parked, StartJackProbe);
        var collectedWhileParked = CollectsWhile(viewModel);
        release.Set();
        await BoundedWait.ForAsync(probe, "the parked jack-count read to finish", TestContext.Current.CancellationToken);

        Assert.True(collectedWhileParked,
            "the disposed Audio settings view model is still reachable while its jack-count read runs: the read holds it");
        Assert.Equal(0, ui.HeldCount);
    }

    [Fact]
    public async Task ADisposedViewModel_IsCollectable_WhileItsOutputPairProbeIsStillRunning()
    {
        using var parked = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var engine = new FakeAudioPlaybackService { OutputProbePass = () => Park(parked, release) };
        var ui = new HeldPostsDispatcher();

        var (viewModel, probe) = StartAndDispose(engine, ui, parked, StartOutputProbe);
        var collectedWhileParked = CollectsWhile(viewModel);
        release.Set();
        await BoundedWait.ForAsync(probe, "the parked output-count read to finish", TestContext.Current.CancellationToken);

        Assert.True(collectedWhileParked,
            "the disposed Audio settings view model is still reachable while its output-count read runs: the read holds it");
        Assert.Equal(0, ui.HeldCount);
    }

    /// <summary>The read finished and its result is waiting on the UI thread when the view model is disposed.</summary>
    [Fact]
    public async Task AJackResult_PostedBeforeDispose_ChangesNoRowsAfterIt()
    {
        var engine = new FakeAudioPlaybackService { InputDeviceChannelCount = 6 };
        var ui = new HeldPostsDispatcher();
        var vm = NewViewModel(engine, ui);
        await StartJackProbe(vm);
        await ui.WaitForHeldAsync(1);
        var (count, rows) = (vm.InputDeviceJackCount, vm.InputJackOptions.Count);

        vm.Dispose();
        ui.RunNewestFirst();

        Assert.Equal((count, rows), (vm.InputDeviceJackCount, vm.InputJackOptions.Count));
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task AnOutputPairResult_PostedBeforeDispose_ChangesNoRowsAfterIt()
    {
        var engine = new FakeAudioPlaybackService { OutputDeviceChannelCount = 8 };
        var ui = new HeldPostsDispatcher();
        var vm = NewViewModel(engine, ui);
        await StartOutputProbe(vm);
        await ui.WaitForHeldAsync(1);
        var (count, rows) = (vm.OutputDeviceChannelCount, vm.OutputPairOptions.Count);

        vm.Dispose();
        ui.RunNewestFirst();

        Assert.Equal((count, rows), (vm.OutputDeviceChannelCount, vm.OutputPairOptions.Count));
        Assert.Equal(0, count);
    }

    private static void Park(ManualResetEventSlim parked, ManualResetEventSlim release)
    {
        parked.Set();
        release.Wait(BoundedWait.DeadlockCap);
    }

    private static bool CollectsWhile(WeakReference viewModel)
    {
        for (var i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        return !viewModel.IsAlive;
    }

    private static AudioSettingsViewModel NewViewModel(FakeAudioPlaybackService engine, HeldPostsDispatcher ui) =>
        new(engine, engine, new FakeConfigurationService(), new ProjectState(),
            new AudioSettingsWindows(new DotCharter.Tests.Presentation.Settings.GraphicsStubs.FakeWizard()), ui,
            FakeLocalization.English());

    /// <summary>Picks the second ASIO capture device (the default input driver): the jack-count read of it starts.</summary>
    private static Task StartJackProbe(AudioSettingsViewModel vm)
    {
        vm.AsioInputDevicesList.Add("Interface A");
        vm.AsioInputDevicesList.Add("Interface B");
        vm.SelectedAsioInputIndex = -1;
        vm.SelectedAsioInputIndex = 1;
        return vm.JackCountProbe;
    }

    /// <summary>Picks the second WASAPI playback device (the default output driver): the output-count read of it starts.</summary>
    private static Task StartOutputProbe(AudioSettingsViewModel vm)
    {
        vm.WasapiDevices.Add("Speakers A");
        vm.WasapiDevices.Add("Speakers B");
        vm.SelectedWasapiOutputIndex = 1;
        return vm.OutputCountProbe;
    }

    /// <summary>Its own frame, so no local of the test method keeps the view model alive.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference ViewModel, Task Probe) StartAndDispose(
        FakeAudioPlaybackService engine, HeldPostsDispatcher ui, ManualResetEventSlim parked,
        Func<AudioSettingsViewModel, Task> start)
    {
        var vm = NewViewModel(engine, ui);
        var probe = start(vm);
        BoundedWait.For(parked, "the read to enter the device seam");
        vm.Dispose();
        return (new WeakReference(vm), probe);
    }

    /// <summary>Its own frame, so no local of the test method keeps the view model alive.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference ViewModel, Task Probe) OpenAndDispose(
        FakeAudioPlaybackService engine, HeldPostsDispatcher ui, ManualResetEventSlim parked)
    {
        var vm = new AudioSettingsViewModel(engine, engine, new FakeConfigurationService(), new ProjectState(),
            new AudioSettingsWindows(new DotCharter.Tests.Presentation.Settings.GraphicsStubs.FakeWizard()), ui,
            FakeLocalization.English());
        vm.Open();
        BoundedWait.For(parked, "the device probe to enter the enumeration");
        vm.Dispose();
        return (new WeakReference(vm), vm.DeviceProbe);
    }
}
