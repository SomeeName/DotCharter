using System.Reflection;
using System.Text.RegularExpressions;
using DotCharter.Services.Ai.Generate;
using DotCharter.Services.Audio.Coordination;
using DotCharter.Tone.Online.Tone3000;
using DotCharter.Tone.UI.ViewModels.PlayMode;
using DotCharter.Tone.UI.ViewModels.Settings;
using DotCharter.UI.ViewModels.Shell;

namespace DotCharter.Tests.UI;

/// <summary>
///     The chart editor's tone-rig cluster (the Amp &amp; FX overlay, Preview tones,
///     Generate Rocksmith tones) used to live in two <c>MainWindowViewModel</c>
///     partials that fetched six services from the static <c>App.Services</c>
///     locator — dependencies no constructor, no budget and no DI lint could see.
///     It is <see cref="ToneRigViewModel" /> now, and everything it uses is injected.
/// </summary>
public sealed class ToneRigInjectionTests
{
    /// <summary>A lookup through the locator is a hidden dependency: the view model's
    /// constructor stops telling the truth about what it needs.</summary>
    [Fact]
    public void ToneRigViewModel_NeverReachesForTheServiceLocator()
    {
        var source = SourceText.StripComments(UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(
            TestPaths.RepoRoot(), "DotCharter.UI", "ViewModels", "Shell", "ToneRigViewModel.cs"))));

        Assert.Contains("class ToneRigViewModel", source, StringComparison.Ordinal);
        Assert.False(Regex.IsMatch(source, @"\bApp\s*\.\s*Services\b"),
            "ToneRigViewModel resolves a service through App.Services. Take it through the " +
            "constructor — as a Func<T> factory when it must not be built on the startup path.");
    }

    /// <summary>
    ///     The laziness the locator used to give for free, kept structurally: the
    ///     shell is built before the window exists, so the tone rig, the tone
    ///     coordinator, the AI lane and the per-screen rig parts arrive as
    ///     factories that run on first use, never as instances that DI would
    ///     construct with the shell.
    /// </summary>
    [Theory]
    [InlineData(typeof(PedalboardViewModel))]
    [InlineData(typeof(INamCaptureLibrary))]
    [InlineData(typeof(LiveInputChannelViewModel))]
    [InlineData(typeof(RigReampViewModel))]
    [InlineData(typeof(RsTonePlaybackCoordinator))]
    [InlineData(typeof(IRsToneGenerator))]
    public void ToneRigViewModel_TakesTheToneRigServicesAsFactories(Type service)
    {
        var constructor = Assert.Single(typeof(ToneRigViewModel).GetConstructors(BindingFlags.Instance | BindingFlags.Public));
        var types = constructor.GetParameters().Select(p => p.ParameterType).ToList();

        Assert.Contains(typeof(Func<>).MakeGenericType(service), types);
        Assert.DoesNotContain(service, types);
    }
}
