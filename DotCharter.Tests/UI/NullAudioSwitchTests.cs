using DotCharter.Tone.Audio;
using DotCharter.Tone.Audio.Abstractions;

namespace DotCharter.Tests.UI;

/// <summary>
///     <c>App.UseNullAudioPlaybackServiceSwitch</c> is how a test that boots the
///     real app session keeps real audio-device enumeration out of the run.
///     <para>
///         It shipped BROKEN: <c>HeadlessSessionFixture</c> set the switch from
///         v0.1x onward, but no production code ever called
///         <see cref="AppContext.TryGetSwitch" /> for it and
///         <see cref="NullAudioPlaybackService" /> was constructed by nothing —
///         so every headless test opened the real engine regardless. A switch
///         that is set but never read is indistinguishable from one that works,
///         which is why this is a lint and not a comment. It matters beyond
///         speed: a hung testhost that opened the engine holds the ASIO device
///         EXCLUSIVELY, and one survived 32 hours doing exactly that.
///     </para>
/// </summary>
public sealed class NullAudioSwitchTests
{

    private static string CompositionRootSource() =>
        string.Concat(UiSourceTree
            .EnumerateFiles(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI"), "App*.cs")
            .OrderBy(p => p, StringComparer.Ordinal)
            .Select(UiSourceTree.ReadAllText));

    [Fact]
    public void TheSwitchIsActuallyREAD_NotJustSet()
    {
        var source = CompositionRootSource();

        Assert.Contains("TryGetSwitch(UseNullAudioPlaybackServiceSwitch", source);
        Assert.Contains("new NullAudioPlaybackService()", source);
    }

    [Fact]
    public void TheHeadlessFixtureSetsItBySYMBOL_NotByString()
    {
        var fixture = UiSourceTree.ReadAllText(Path.Combine(
            TestPaths.RepoRoot(), "DotCharter.Tests", "Perf", "HeadlessAppFixture.cs"));

        Assert.Contains("App.UseNullAudioPlaybackServiceSwitch", fixture);

        // A re-typed literal is how the two halves drifted apart in the first
        // place: renaming the constant would leave the fixture setting a switch
        // nothing reads, silently and with every test still green.
        Assert.DoesNotContain("\"DotCharter.UseNullAudioPlaybackService\"", fixture);
    }

    [Fact]
    public void TheNullServiceStillSatisfiesTheContractTheAppResolves() =>
        // If the null path cannot stand in for the real one, honouring the
        // switch swaps a slow test for a broken one.
        Assert.True(typeof(IAudioPlaybackService).IsAssignableFrom(typeof(NullAudioPlaybackService)));
}
