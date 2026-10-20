using System.Text.RegularExpressions;

namespace DotCharter.Tests.UI;

public sealed class RigMidiLifecycleWiringTests
{
    [Fact]
    public void EngineReplacementClosesMidiBeforeDisposal_AndReopensOnThePublishedEngine()
    {
        var source = Read("DotCharter.Tone/Audio/JuceAudioPlaybackService.EngineLifecycle.cs");
        var close = source.IndexOf("CloseRigMidiInputLocked()", StringComparison.Ordinal);
        // Since #345 the outgoing engine is disposed OUTSIDE the playback gate,
        // under the name `outgoingEngine`; the order this pins is unchanged.
        var dispose = source.IndexOf("outgoingEngine?.Dispose()", StringComparison.Ordinal);
        var publish = source.IndexOf("_engine = engine;", StringComparison.Ordinal);
        var restore = source.IndexOf("RestoreRigMidiInputLocked(engine)", StringComparison.Ordinal);
        Assert.True(close >= 0 && dispose > close && publish > dispose && restore > publish);
        Assert.Contains("CloseRigMidiInputLocked()", Read("DotCharter.Tone/Audio/JuceAudioPlaybackService.cs"), StringComparison.Ordinal);
    }

    [Fact]
    public void ConfiguredMidiStartsAfterWindowOpenedWithoutResolvingARigView()
    {
        var source = Read("DotCharter.UI/App.axaml.cs");
        var opened = source.IndexOf("window.Opened +=", StringComparison.Ordinal);
        var configured = source.IndexOf("appConfig.RigMidiInputDevice", opened, StringComparison.Ordinal);
        var start = source.IndexOf("PedalboardViewModel>().MidiControls", configured, StringComparison.Ordinal);
        var background = source.IndexOf("DispatcherPriority.Background", start, StringComparison.Ordinal);
        Assert.True(opened >= 0 && configured > opened && start > configured && background > start);
        Assert.DoesNotContain("AmpFxRigView", source[configured..background], StringComparison.Ordinal);
    }

    private static string Read(string path)
    {
        var source = UiSourceTree.ReadAllText(Path.Combine(TestPaths.RepoRoot(), path));
        return Regex.Replace(source, @"/\*[\s\S]*?\*/|//[^\r\n]*", string.Empty);
    }
}
