using DotCharter.Services.Editing;
using DotCharter.Services.Editing.Playback;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Editing.Targets;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     Play/pause is reached from three doors — the toolbar's Play and Pause
///     buttons, the Space keybind and the command palette — and only the buttons
///     name a direction. The other two TOGGLE, and until this change both branched
///     on <see cref="TransportViewModel.IsPlaying" />: this view model's own cached
///     copy, written by nothing except its own commands.
///     <para>
///         The engine stops itself. <c>JuceAudioPlaybackService</c>'s position tick
///         pauses the transport the moment the playhead reaches the end of the
///         audio, and nothing told the view model — so after any song ran out the
///         cached flag still said "playing", the first Space executed Pause into an
///         already-paused engine, and the user had to press it twice. The same
///         stale <c>true</c> held <c>ChartRenderer</c>'s and
///         <c>Preview3DRenderDriver</c>'s CONTINUOUS render loop open for the rest
///         of the session, because both gate on
///         <c>audioService.IsPlaying || Transport.IsPlaying</c>.
///     </para>
/// </summary>
public sealed class TransportPlayPauseStateTests
{
    private static TransportViewModel Build(FakeAudioPlaybackService audio)
    {
        var state = new ProjectState { TotalAudioLengthMs = 8000 };
        var logger = new FakeAppLogger();
        var config = new FakeConfigurationService();
        var selection = new SelectionService();
        var scope = new EditingScope(state);
        var loop = new EditorLoopService(state, new EditTargetService(selection, scope, config), scope, logger);
        return new TransportViewModel(audio, state, config, FakeLocalization.KeysOnly(), null, logger, loop);
    }

    [Fact]
    public void TogglePlayPause_AfterTheEnginePausedItselfAtTheEnd_Plays()
    {
        var audio = new FakeAudioPlaybackService();
        var transport = Build(audio);

        transport.PlayCommand.Execute(null);
        Assert.True(audio.IsPlaying);

        // The song ran out and the engine's own position tick paused the
        // transport. Nothing wrote the view model's flag, so it still claims
        // to be playing.
        audio.IsPlaying = false;

        transport.TogglePlayPauseCommand.Execute(null);

        Assert.True(audio.IsPlaying);
        Assert.True(transport.IsPlaying);
    }

    [Fact]
    public void TogglePlayPause_WhilePlaying_Pauses()
    {
        var audio = new FakeAudioPlaybackService();
        var transport = Build(audio);

        transport.PlayCommand.Execute(null);
        transport.TogglePlayPauseCommand.Execute(null);

        Assert.False(audio.IsPlaying);
        Assert.False(transport.IsPlaying);
    }

    [Fact]
    public void TogglePlayPause_FromRest_Plays()
    {
        var audio = new FakeAudioPlaybackService();
        var transport = Build(audio);

        transport.TogglePlayPauseCommand.Execute(null);

        Assert.True(audio.IsPlaying);
        Assert.True(transport.IsPlaying);
    }

    [Fact]
    public void SyncPlayingStateFromEngine_ClearsTheFlagTheEngineNoLongerBacks()
    {
        var audio = new FakeAudioPlaybackService();
        var transport = Build(audio);

        transport.PlayCommand.Execute(null);
        Assert.True(transport.IsPlaying);

        audio.IsPlaying = false;
        transport.SyncPlayingStateFromEngine();

        Assert.False(transport.IsPlaying);
    }

    [Fact]
    public void SyncPlayingStateFromEngine_DoesNotInventPlayback()
    {
        var audio = new FakeAudioPlaybackService();
        var transport = Build(audio);

        transport.SyncPlayingStateFromEngine();

        Assert.False(transport.IsPlaying);
    }

    /// <summary>
    ///     Neither toggling door may re-grow its own copy of the decision. A
    ///     behavioural test cannot see a second branch that agrees with the first
    ///     today and drifts tomorrow, so this one reads the source (comments
    ///     stripped first — a note naming the old shape is not the old shape).
    /// </summary>
    [Fact]
    public void NeitherTogglingDoor_BranchesOnTheViewModelsOwnFlag()
    {
        foreach (var relative in new[]
                 {
                     Path.Combine("Input", "AppKeybindRegistrar.cs"),
                     Path.Combine("ViewModels", "CommandPalette", "CommandCatalog.cs")
                 })
        {
            var path = UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", relative));
            Assert.True(File.Exists(path), $"Missing source file: {path}");
            var body = SourceText.StripComments(UiSourceTree.ReadAllText(path));

            Assert.DoesNotContain("Transport.IsPlaying", body);
            Assert.Contains("TogglePlayPauseCommand", body);
        }
    }
}
