using DotCharter.Tone.Audio.Vst;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     The saved guitar VST chain comes out of <c>appconfig.json</c>, a file a restored
///     backup or a copied config folder can write. The engine only checked that the
///     path EXISTS before loading native code from it, so a chain entry naming a plug-in
///     on a share, or one dropped into a data folder, ran at the next start. The
///     startup restore therefore hands the engine only paths the VST3 scan reported,
///     exactly as the instrument plug-in already was; any other entry stays in the chain
///     as a slot that says why it is not loaded.
/// </summary>
public sealed class TransportVstScanGateTests
{
    private const string Known = @"C:\Program Files\Common Files\VST3\Amp.vst3";
    private const string Foreign = @"\\evil-host\share\payload.vst3";

    [Fact]
    public async Task AChainEntryOutsideTheScan_NeverReachesTheEngine_AndIsShownAsNotLoaded()
    {
        var (vm, audio, _) = await Start(chain: [Entry(Known), Entry(Foreign)]);

        var pushed = Assert.Single(audio.PushedGuitarChains);
        Assert.Equal([Known], pushed.Select(e => e.PluginPath));

        Assert.Equal(2, vm.GuitarVstChain.Count);
        var good = vm.GuitarVstChain[0];
        var bad = vm.GuitarVstChain[1];
        Assert.False(good.IsUnavailable);
        Assert.True(bad.IsUnavailable);
        Assert.False(bad.IsLoading); // not a spinner that never ends
        Assert.False(bad.CanInteract); // bypass / GUI disabled: nothing is loaded behind them
        Assert.False(string.IsNullOrWhiteSpace(bad.UnavailableLabel));
        Assert.False(string.IsNullOrWhiteSpace(bad.UnavailableReason));
    }

    [Fact]
    public async Task WhenNothingInTheChainIsScanned_TheEngineIsNeverToldToLoadAnything()
    {
        var (vm, audio, _) = await Start(chain: [Entry(Foreign)]);

        Assert.Empty(audio.PushedGuitarChains);
        Assert.True(Assert.Single(vm.GuitarVstChain).IsUnavailable);
    }

    [Fact]
    public async Task ALegacySinglePluginPathOutsideTheScan_IsGatedToo()
    {
        var (vm, audio, _) = await Start(chain: [], legacyPath: Foreign);

        Assert.Empty(audio.PushedGuitarChains);
        Assert.True(Assert.Single(vm.GuitarVstChain).IsUnavailable);
    }

    [Fact]
    public async Task LaterEditsNeverPushAnUnavailableSlotToTheEngine_ButKeepItSaved()
    {
        var (vm, audio, config) = await Start(chain: [Entry(Known), Entry(Foreign)]);

        // Any edit re-pushes the whole chain from the view model's slots.
        vm.GuitarVstChain[0].Bypass = true;
        vm.GuitarVstChain[0].PlayerChanged?.Invoke(vm.GuitarVstChain[0], 0);
        vm.GuitarVstChain.Single(s => s.PluginPath == Known).RemoveCommand.Execute(null);

        Assert.All(audio.PushedGuitarChains, c => Assert.DoesNotContain(c, e => e.PluginPath == Foreign));

        // The entry is the user's saved choice: it stays in the config, so installing
        // the plug-in later brings it back on the next start.
        Assert.Contains(config.AppConfig.GuitarVstChain, e => e.PluginPath == Foreign);
    }

    [Fact]
    public async Task ACaseOnlyDifferenceInThePath_IsTheSamePlugin()
    {
        var (vm, audio, _) = await Start(chain: [Entry(Known.ToUpperInvariant())]);

        Assert.False(Assert.Single(vm.GuitarVstChain).IsUnavailable);
        Assert.Single(audio.PushedGuitarChains);
    }

    private static VstChainEntry Entry(string path) => new() { Id = Guid.NewGuid(), PluginPath = path, Player = 0 };

    private static async Task<(TransportViewModel Vm, FakeAudioPlaybackService Audio, FakeConfigurationService Config)> Start(
        List<VstChainEntry> chain, string? legacyPath = null)
    {
        var audio = new FakeAudioPlaybackService
        {
            VstPluginsOnDisk = [new VstPluginInfo { Name = "Amp", PluginPath = Known }]
        };
        var config = new FakeConfigurationService();
        config.AppConfig.GuitarVstChain = chain;
        if (legacyPath != null) config.AppConfig.LastGuitarVstPath = legacyPath;

        var vm = new TransportViewModel(
            audio, new ProjectState(), config, FakeLocalization.KeysOnly(), new TestUiDispatcher(), new FakeAppLogger());
        await vm.VstStartupTask;
        return (vm, audio, config);
    }
}
