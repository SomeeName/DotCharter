using System.ComponentModel;
using DotCharter.Tone.Audio.Vst;
using DotCharter.Tone.UI.ViewModels.Editor;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.UI;

/// <summary>
///     <see cref="TransportVstRack" />: the transport's guitar VST3 chain behind
///     the shared plugin rack's <see cref="IVstRack" />. It is an ADAPTER — every
///     verb must land on the transport's own chain logic (persist + whole-chain
///     push), and the two things it translates (property names, the "None"
///     sentinel) must hold, or the shared rack's bindings silently miss.
/// </summary>
public sealed class TransportVstRackTests
{
    [Fact]
    public void TheTransport_HandsOutOneRack_OverItsOwnChain()
    {
        var transport = NewTransport(new FakeAudioPlaybackService());

        Assert.Same(transport.VstRack, transport.VstRack);
        Assert.Same(transport.GuitarVstChain, transport.VstRack.Chain);
    }

    [Fact]
    public async Task TheAddList_IsTheTransportsPlugins_WithoutTheNoneSentinel_AndFollowsARescan()
    {
        var audio = new FakeAudioPlaybackService();
        var transport = NewTransport(audio);
        await transport.VstStartupTask;
        var rack = transport.VstRack;

        audio.VstPluginsOnDisk = [Plugin("TS-9"), Plugin("Archetype")];
        await transport.RescanVstPluginsCommand.ExecuteAsync(null);

        Assert.Contains(transport.AvailableVstPlugins, p => string.IsNullOrEmpty(p.PluginPath)); // the sentinel is still there…
        Assert.Equal(["TS-9", "Archetype"], rack.AvailablePlugins.Select(p => p.Name)); // …and not offered
        // The SAME instances, so a pick writes back something the transport knows.
        Assert.All(rack.AvailablePlugins, p => Assert.Contains(p, transport.AvailableVstPlugins));
    }

    [Fact]
    public async Task APick_AppendsThroughTheTransport_PushesTheChain_AndClearsTheSelection()
    {
        var audio = new FakeAudioPlaybackService();
        var transport = NewTransport(audio);
        await transport.VstStartupTask;
        audio.VstPluginsOnDisk = [Plugin("TS-9")];
        await transport.RescanVstPluginsCommand.ExecuteAsync(null);
        var rack = transport.VstRack;
        var raised = Watch(rack);
        var pushes = audio.PushedGuitarChains.Count;

        rack.AddSelection = rack.AvailablePlugins.Single();

        Assert.Equal("TS-9", Assert.Single(rack.Chain).PluginName);
        Assert.Equal(pushes + 1, audio.PushedGuitarChains.Count);
        Assert.Null(rack.AddSelection);
        Assert.Contains(nameof(IVstRack.AddSelection), raised);
    }

    [Fact]
    public async Task MoveSlot_IsTheTransportsReorder()
    {
        var audio = new FakeAudioPlaybackService();
        var transport = NewTransport(audio);
        await transport.VstStartupTask;
        audio.VstPluginsOnDisk = [Plugin("A"), Plugin("B"), Plugin("C")];
        await transport.RescanVstPluginsCommand.ExecuteAsync(null);
        var rack = transport.VstRack;
        foreach (var p in rack.AvailablePlugins.ToList()) rack.AddSelection = p;
        var pushes = audio.PushedGuitarChains.Count;

        Assert.False(rack.MoveSlot(1, 1));
        Assert.False(rack.MoveSlot(0, 3));
        Assert.Equal(pushes, audio.PushedGuitarChains.Count);

        Assert.True(rack.MoveSlot(2, 0));
        Assert.Equal(["C", "A", "B"], rack.Chain.Select(s => s.PluginName));
        Assert.Equal([1, 2, 3], rack.Chain.Select(s => s.DisplayIndex));
        Assert.Equal(["C", "A", "B"],
            audio.PushedGuitarChains[^1].Select(e => Path.GetFileNameWithoutExtension(e.PluginPath)));
    }

    [Fact]
    public void Scanning_IsReRaisedUnderTheRacksName()
    {
        var transport = NewTransport(new FakeAudioPlaybackService());
        var rack = transport.VstRack;
        var raised = Watch(rack);

        transport.IsRescanningVstPlugins = true;

        Assert.True(rack.IsScanning);
        Assert.Contains(nameof(IVstRack.IsScanning), raised);
        Assert.Same(transport.RescanVstPluginsCommand, rack.RescanCommand);
    }

    private static List<string?> Watch(INotifyPropertyChanged source)
    {
        var raised = new List<string?>();
        source.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        return raised;
    }

    private static TransportViewModel NewTransport(FakeAudioPlaybackService audio) =>
        new(audio, new ProjectState(), new FakeConfigurationService(), FakeLocalization.KeysOnly(), null, new FakeAppLogger());

    private static VstPluginInfo Plugin(string name) =>
        new() { Name = name, PluginPath = $@"C:\VST3\{name}.vst3" };
}
