using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DotCharter.Localization;
using DotCharter.Services.Audio.LivePlay;
using DotCharter.Services.Gameplay.Leaderboards;
using DotCharter.Services.Gameplay.Profiles;
using DotCharter.Services.Gameplay.Stats;
using DotCharter.Services.Audio.Coordination;
using DotCharter.Tone.Audio.Dsp;
using DotCharter.Tone.Audio.Dsp.RsToneConversion;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.Tone.Parsing.Psarc;
using DotCharter.Tone.UI.Views.PlayMode;
using DotCharter.UI.ViewModels.PlayMode;
using DotCharter.UI.Views.PlayMode;

namespace DotCharter.Tests.Perf;

/// <summary>
///     The bar over the rig while a song tone is edited: it constructs, attaches
///     and lays out at a real size with all three actions, and it stays out of
///     the way (hidden) when no tone is being edited — including with no view
///     model at all, which is Play Mode without a tone coordinator.
/// </summary>
[Collection("HeadlessUI")]
public sealed class SongToneEditBarUiPerfSmokeTests
{
    private const string CleanJson =
        """{"Key":"clean","Name":"Clean Tone","GearList":{"Amp":{"Type":"Amps","Key":"Amp_TwinReverb","KnobValues":{}}}}""";

    private static SongToneEditViewModel ActiveEdit(bool hasEdit)
    {
        var chain = new FakeDspChainService(
            new[] { "amp", "cab", "gain" }
                .Select(id => new DspModuleType(id, id, new[] { new DspParamInfo("p", "p", -100f, 100f, 0f) }))
                .ToList());
        var coordinator = new RsTonePlaybackCoordinator(chain, new DcRigApplier(chain), new ProjectState(),
            new FakeConfigurationService())
        {
            ToneDefinitionSource = _ => [new PsarcToneDefinition("clean", "Clean Tone", CleanJson)],
            SongTonesSource = _ => new Dictionary<string, DcRigPreset>()
        };
        coordinator.PrepareForSong("song.psarc", new Arrangement { ToneBase = "Clean Tone" }, "lib:CallMe");
        var vm = new SongToneEditViewModel(coordinator, () => null, () => "lib:CallMe",
            () => Task.CompletedTask, () => { }, LocalizationService.Instance);
        Assert.True(vm.Begin("Clean Tone", hasEdit));
        return vm;
    }

    private static Task WithBar(object? dataContext, Action<SongToneEditBar> body) =>
        HeadlessSessionFixture.Dispatch(() =>
        {
            var bar = new SongToneEditBar { DataContext = dataContext };
            var window = new Window { Width = 1280, Height = 200, Content = bar };
            window.Show();
            try
            {
                bar.Measure(new Size(1280, 200));
                bar.Arrange(new Rect(0, 0, 1280, 200));
                body(bar);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);

    private static Control Named(Control root, string name) =>
        root.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Name == name)
        ?? throw new Xunit.Sdk.XunitException($"'{name}' is not in the visual tree");

    [Fact]
    public async Task AnActiveEdit_LaysOutTheBar_WithItsThreeActions()
    {
        await WithBar(ActiveEdit(hasEdit: true), bar =>
        {
            foreach (var name in new[] { "SongToneEditBarRoot", "SongToneEditName", "SongToneEditReset",
                         "SongToneEditDiscard", "SongToneEditSave" })
            {
                var control = Named(bar, name);
                Assert.True(control.IsEffectivelyVisible, $"'{name}' is not visible");
                Assert.True(control.Bounds.Width > 1 && control.Bounds.Height > 1,
                    $"'{name}' laid out as {control.Bounds} — it rendered nothing");
            }
        });
    }

    [Fact]
    public async Task ResetIsHidden_ForATone_WithNoSavedEdit()
    {
        await WithBar(ActiveEdit(hasEdit: false), bar =>
            Assert.False(Named(bar, "SongToneEditReset").IsVisible));
    }

    [Fact]
    public async Task WithNoEdit_OrNoViewModel_TheBarTakesNoSpace()
    {
        // DesiredSize, not Bounds: the host's row is Auto, so what the bar ASKS
        // for is what it takes from the rig.
        await WithBar(null, bar => Assert.True(bar.DesiredSize.Height < 1,
            $"the bar asked for {bar.DesiredSize.Height} px with no view model"));
    }

    /// <summary>
    ///     The bar as the player sees it: Play Mode on Get Ready with the rig open
    ///     over it and a song tone on the rig. The overlay must be OPAQUE end to
    ///     end — the first build left the host grid without a ground, so the Get
    ///     Ready header showed through between the bar and the rig — and the bar
    ///     must sit flush on the rig. With the capture env vars set it also writes
    ///     <c>song-tone-edit.png</c> to look at.
    /// </summary>
    [Fact]
    public async Task InPlayMode_TheOverlayIsOpaque_AndTheBarSitsFlushOnTheRig()
    {
        var root = Path.Combine(Path.GetTempPath(), "dc-songtone-" + Guid.NewGuid().ToString("N"));
        try
        {
            await HeadlessSessionFixture.Dispatch(() =>
            {
                Directory.CreateDirectory(root);
                var profiles = new PlayerProfileService(FakeLocalization.English(), logger: null,
                    filePath: Path.Combine(root, "player-profiles.json"), defaultProfileName: "Tester");
                var store = new PracticeProfileService(profiles, logger: null,
                    legacyFilePath: Path.Combine(root, "practice-profiles.json"));

                var chain = new FakeDspChainService();
                var coordinator = new RsTonePlaybackCoordinator(chain, new DcRigApplier(chain), new ProjectState(),
                    new FakeConfigurationService())
                {
                    ToneDefinitionSource = _ => [new PsarcToneDefinition("CallMe_synth", "CallMe_synth", CleanJson)],
                    SongTonesSource = _ => new Dictionary<string, DcRigPreset>()
                };
                coordinator.PrepareForSong("song.psarc", new Arrangement { ToneBase = "CallMe_synth" }, "lib:CallMe");

                var vm = TestRigBuilders.PlayMode(profiles, new GameplayStatsService(store),
                    new LeaderboardService(profiles), new ProjectState(), rsToneCoordinator: coordinator, localization: LocalizationService.Instance);
                vm.ToneRig.UseRigForTests(TestRigBuilders.Pedalboard(chain, new FakeDspTonePresetService(), LocalizationService.Instance));

                var view = new PlayModeView { DataContext = vm };
                var window = new Window { Width = 1600, Height = 900, Content = view };
                window.Show();
                try
                {
                    vm.CurrentScreen = PlayModeScreen.TuningCheck;
                    Assert.True(vm.SongToneEdit!.Begin("CallMe_synth", isEdited: false));
                    vm.ToneRig.IsPedalboardOpen = true;
                    Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
                    for (var pass = 0; pass < 3; pass++) window.UpdateLayout();

                    var dir = Environment.GetEnvironmentVariable("DOTCHARTER_RIG_CAPTURE_DIR");
                    if (UiSweepRunner.CaptureEnabled && !string.IsNullOrWhiteSpace(dir))
                    {
                        Directory.CreateDirectory(dir);
                        using var frame = window.CaptureRenderedFrame();
                        Assert.NotNull(frame);
                        frame.Save(Path.Combine(dir, "song-tone-edit.png"), new PngBitmapEncoderOptions());
                    }

                    var bar = view.GetVisualDescendants().OfType<SongToneEditBar>().Single();
                    var host = Assert.IsType<Grid>(bar.Parent);
                    // The rig is the ContentTemplate body of RigScreenHost since #194, so the
                    // host is the grid's child that must sit flush under the bar.
                    var rigView = host.Children.OfType<ContentControl>().Single(c => c.Name == PlayModeView.RigScreenHostName);
                    Assert.Single(rigView.GetVisualDescendants().OfType<AmpFxRigView>());

                    Assert.True(bar.Bounds.Height > 1, $"the bar laid out as {bar.Bounds}");
                    Assert.True(IsOpaque(host.Background),
                        "the rig overlay's host has no opaque ground — the screen under it shows through");
                    Assert.True(Math.Abs(rigView.Bounds.Top - bar.Bounds.Bottom) < 0.5,
                        $"a {rigView.Bounds.Top - bar.Bounds.Bottom:0.#} px gap between the bar and the rig");

                }
                finally
                {
                    vm.SongToneEdit?.End();
                    window.Close();
                }
            }, TestContext.Current.CancellationToken);
        }
        finally
        {
            BestEffortDelete.Directory(root);
        }
    }

    private static bool IsOpaque(IBrush? brush) => brush switch
    {
        ISolidColorBrush solid => solid.Color.A == 255 && solid.Opacity >= 1,
        IGradientBrush gradient => gradient.Opacity >= 1 && gradient.GradientStops.All(s => s.Color.A == 255),
        _ => false
    };
}
