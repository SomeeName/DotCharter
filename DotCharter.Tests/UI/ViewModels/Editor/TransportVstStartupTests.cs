using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     The ctor's VST scan/restore worker is fire-and-forget. It used to have no
///     try/catch at all, so a stale VST3 path (UnauthorizedAccessException /
///     DirectoryNotFoundException) became an unobserved task exception: the plugin
///     dropdowns stayed empty and the saved chain was never restored for the whole
///     session, with ZERO diagnostics.
/// </summary>
public sealed class TransportVstStartupTests
{
    [Fact]
    public async Task VstScanThrows_IsCaughtAndLogged_AndTheVmStaysUsable()
    {
        var audio = new FakeAudioPlaybackService { ThrowOnScanVstPlugins = true };
        var logger = new FakeAppLogger();

        var vm = new TransportViewModel(audio, new ProjectState(), new FakeConfigurationService(), FakeLocalization.KeysOnly(), null, logger);

        await vm.VstStartupTask; // never faults — the worker swallows + logs

        Assert.True(logger.HasEntryContaining("WARN", "VST plugins"),
            "the failed VST scan must be logged, not silently dropped");
        Assert.Equal(AppLogCategory.Audio, logger.Entries.Single(e => e.Level == "WARN").Category);
        Assert.IsType<UnauthorizedAccessException>(logger.Entries.Single(e => e.Level == "WARN").Exception);

        // The dropdowns still hold their "None" row, so the mixer is usable.
        Assert.Single(vm.AvailableVstPlugins);
        Assert.Single(vm.InstrumentVstPlugins);
    }

    [Fact]
    public async Task VstScanSucceeds_LogsNoWarning()
    {
        var logger = new FakeAppLogger();

        var vm = new TransportViewModel(
            new FakeAudioPlaybackService(), new ProjectState(), new FakeConfigurationService(), FakeLocalization.KeysOnly(), null, logger);

        await vm.VstStartupTask;

        Assert.DoesNotContain(logger.Entries, e => e.Level == "WARN");
    }
}
