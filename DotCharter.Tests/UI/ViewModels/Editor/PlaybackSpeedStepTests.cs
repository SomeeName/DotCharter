using DotCharter.Services.Editing;
using DotCharter.Services.Editing.Playback;
using DotCharter.Services.Editing.Selection;
using DotCharter.Services.Editing.Targets;
using DotCharter.UI.Constants;
using DotCharter.UI.ViewModels.Editor;

namespace DotCharter.Tests.UI.ViewModels.Editor;

/// <summary>
///     Six keybinds change the playback speed: a normal step, a coarse one and a
///     fine one, up and down. Two things were wrong with them at once.
///     <list type="bullet">
///         <item>
///             The FINE step was 0.01 and <c>PlaybackSpeed</c> quantized to the
///             0.05 grid its gain-slider siblings use, so 1.00 + 0.01 rounded
///             straight back to 1.00 and the setter saw no change. It could not
///             accumulate either — the next press read the snapped field again —
///             so the row did nothing at any speed, forever.
///         </item>
///         <item>
///             The two were CROSSED. The constants were named after the default
///             modifier (<c>…Ctrl</c>, <c>…Shift</c>) rather than the step size,
///             and the registrar matched <c>SpeedUpFast</c> (Ctrl +) to the 0.01
///             one and <c>SpeedUpFine</c> (Shift +) to the 0.25 one — so the
///             settings row labelled "coarse" nudged by a hundredth and the one
///             labelled "fine" jumped by a quarter, in all four directions.
///         </item>
///     </list>
/// </summary>
public sealed class PlaybackSpeedStepTests
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
    public void TheFineStepMovesTheSpeedByOnePercent()
    {
        var audio = new FakeAudioPlaybackService();
        var transport = Build(audio);
        transport.PlaybackSpeed = 1.0;

        transport.IncreaseSpeed(ChartEditorConstants.PlaybackSpeedIncrementFine);

        Assert.Equal(1.01, transport.PlaybackSpeed, 3);
        Assert.Equal(1.01, audio.PlaybackSpeed, 3);
    }

    [Fact]
    public void RepeatedFineSteps_Accumulate()
    {
        var audio = new FakeAudioPlaybackService();
        var transport = Build(audio);
        transport.PlaybackSpeed = 1.0;

        for (var i = 0; i < 5; i++)
            transport.IncreaseSpeed(ChartEditorConstants.PlaybackSpeedIncrementFine);

        Assert.Equal(1.05, transport.PlaybackSpeed, 3);
    }

    [Fact]
    public void TheNormalAndCoarseStepsAreUnchanged()
    {
        var audio = new FakeAudioPlaybackService();
        var transport = Build(audio);

        transport.PlaybackSpeed = 1.0;
        transport.IncreaseSpeed(ChartEditorConstants.PlaybackSpeedIncrementNormal);
        Assert.Equal(1.05, transport.PlaybackSpeed, 3);

        transport.PlaybackSpeed = 1.0;
        transport.DecreaseSpeed(ChartEditorConstants.PlaybackSpeedIncrementCoarse);
        Assert.Equal(0.75, transport.PlaybackSpeed, 3);
    }

    [Fact]
    public void TheStepsStillClampToTheSupportedRange()
    {
        var audio = new FakeAudioPlaybackService();
        var transport = Build(audio);

        transport.PlaybackSpeed = ChartEditorConstants.PlaybackSpeedMax;
        transport.IncreaseSpeed(ChartEditorConstants.PlaybackSpeedIncrementFine);
        Assert.Equal(ChartEditorConstants.PlaybackSpeedMax, transport.PlaybackSpeed, 3);

        transport.PlaybackSpeed = ChartEditorConstants.PlaybackSpeedMin;
        transport.DecreaseSpeed(ChartEditorConstants.PlaybackSpeedIncrementCoarse);
        Assert.Equal(ChartEditorConstants.PlaybackSpeedMin, transport.PlaybackSpeed, 3);
    }

    /// <summary>
    ///     No offered step may be finer than what the property can store — that is
    ///     the shape of the dead keybind, and it is not visible from any one call
    ///     site.
    /// </summary>
    [Fact]
    public void EveryOfferedStep_SurvivesTheStoredResolution()
    {
        var audio = new FakeAudioPlaybackService();
        var transport = Build(audio);

        foreach (var step in new[]
                 {
                     ChartEditorConstants.PlaybackSpeedIncrementFine,
                     ChartEditorConstants.PlaybackSpeedIncrementNormal,
                     ChartEditorConstants.PlaybackSpeedIncrementCoarse
                 })
        {
            transport.PlaybackSpeed = 1.0;
            transport.IncreaseSpeed(step);
            Assert.True(transport.PlaybackSpeed > 1.0,
                $"a {step:0.###} step must actually move the speed, not round away");
        }
    }

    /// <summary>
    ///     …and the FAST keybind must take the COARSE step while the FINE one
    ///     takes the small step. A behavioural test cannot see the pairing — both
    ///     halves compile and run either way round — so this reads the registrar.
    /// </summary>
    [Fact]
    public void TheFastAndFineKeybinds_AreNotCrossed()
    {
        var path = UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "Input", "AppKeybindRegistrar.cs"));
        Assert.True(File.Exists(path), $"Missing source file: {path}");
        var body = SourceText.StripComments(UiSourceTree.ReadAllText(path));

        foreach (var (bind, expected) in new[]
                 {
                     ("SpeedUpFast", "PlaybackSpeedIncrementCoarse"),
                     ("SpeedDownFast", "PlaybackSpeedIncrementCoarse"),
                     ("SpeedUpFine", "PlaybackSpeedIncrementFine"),
                     ("SpeedDownFine", "PlaybackSpeedIncrementFine")
                 })
        {
            var at = body.IndexOf("_projectState.Keybinds." + bind, StringComparison.Ordinal);
            Assert.True(at >= 0, $"{bind} is no longer registered.");
            var window = body[at..Math.Min(body.Length, at + 200)];
            Assert.Contains(expected, window, StringComparison.Ordinal);
        }

        // The old names are what made the crossing look right; they must not come
        // back with a chord in them.
        Assert.DoesNotContain("PlaybackSpeedIncrementCtrl", body, StringComparison.Ordinal);
        Assert.DoesNotContain("PlaybackSpeedIncrementShift", body, StringComparison.Ordinal);
    }
}
