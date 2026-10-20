using DotCharter.Services.Audio.LivePlay;
using DotCharter.Services.Gameplay.Leaderboards;
using DotCharter.Services.Gameplay.Profiles;
using DotCharter.Services.Gameplay.Stats;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.Tone.UI.ViewModels.PlayMode;
using DotCharter.UI.ViewModels.PlayMode;

namespace DotCharter.Tests.UI.ViewModels.PlayMode;

/// <summary>
///     The Amp &amp; FX rig SCREEN is not built on the boot path.
///     <para>
///         Both hosts bind it from markup under a hidden overlay
///         (<c>DataContext="{Binding ToneRig.AmpFxRig}"</c>), and a binding reads its
///         source when it is set up — for the shell that is <c>MainWindow</c>'s
///         bind, before the first frame. A <c>??=</c> getter therefore built the
///         <see cref="PedalboardViewModel" /> singleton and two rig screens on
///         every launch for an overlay nothing shows (~50 ms of the
///         <c>bind=</c> phase, sampled 2026-08-30). <see cref="DeferredRigScreen" />
///         answers null until the overlay opens or the post-first-paint prewarm
///         asks, and the host raises <c>PropertyChanged</c> so the binding
///         re-reads.
///     </para>
///     <para>
///         The first two facts are the gate; the third is the Play Mode host's
///         wiring over a real chain; the last is a source lint that BOTH hosts
///         are on the gate and BOTH prewarm lists want the screen — the shell
///         host's view model only exists inside the real container, which the
///         default gate cannot build.
///     </para>
/// </summary>
public sealed class RigScreenDeferralTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "DotCharterTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Gate_NeverBuildsUntilWanted_ThenBuildsOnce()
    {
        var gate = new DeferredRigScreen();
        var builds = 0;
        AmpFxRigViewModel Build() { builds++; return NewScreen(); }

        // A binding reading the property at bind time gets null and costs nothing.
        Assert.Null(gate.Get(Build));
        Assert.Null(gate.Get(Build));
        Assert.Equal(0, builds);
        Assert.False(gate.IsWanted);

        // The first Want is what makes a screen available — the host's cue to
        // raise the property change.
        Assert.True(gate.Want(Build));
        Assert.Equal(1, builds);
        Assert.True(gate.IsWanted);

        // Everything after that is the same instance, no rebuild.
        var screen = gate.Get(Build);
        Assert.NotNull(screen);
        Assert.Same(screen, gate.Get(Build));
        Assert.False(gate.Want(Build));
        Assert.Equal(1, builds);
    }

    [Fact]
    public void Gate_WantWithNoRig_StaysWanted_SoALaterReadRetries()
    {
        // The shell asks before DI can hand it a chain (a container-less test
        // host): nothing is built, but the want is remembered, so the next read
        // — once a rig exists — builds it without a second cue.
        var gate = new DeferredRigScreen();
        Assert.False(gate.Want(static () => null));
        Assert.True(gate.IsWanted);
        Assert.Null(gate.Get(static () => null));

        var screen = gate.Get(NewScreen);
        Assert.NotNull(screen);
        Assert.Same(screen, gate.Get(static () => null));

        // Reset drops the instance, not the want (UseRigForTests swaps the rig).
        gate.Reset();
        Assert.True(gate.IsWanted);
        Assert.NotSame(screen, gate.Get(NewScreen));
    }

    [Fact]
    public void PlayModeHost_BuildsTheScreenWhenTheOverlayOpens_AndTellsTheBindingFirst()
    {
        var vm = NewVm();
        var rig = TestRigBuilders.Pedalboard(new FakeDspChainService(), new FakeDspTonePresetService(), FakeLocalization.English());
        vm.ToneRig.UseRigForTests(rig, wantScreen: false);

        // The shipped boot shape: the overlay is closed, the binding reads null.
        Assert.False(vm.ToneRig.IsPedalboardOpen);
        Assert.Null(vm.ToneRig.AmpFxRig);

        var raised = new List<string>();
        vm.ToneRig.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? "");

        vm.ToneRig.IsPedalboardOpen = true;

        var screen = vm.ToneRig.AmpFxRig;
        Assert.NotNull(screen);
        Assert.Same(rig, screen!.Rig);
        // The screen's change goes out BEFORE the overlay's, so the panel turns
        // visible over a bound screen rather than over null.
        var screenAt = raised.IndexOf(nameof(PlayModeToneRigViewModel.AmpFxRig));
        var openAt = raised.IndexOf(nameof(PlayModeToneRigViewModel.IsPedalboardOpen));
        Assert.True(screenAt >= 0, "opening the overlay must announce the screen");
        Assert.True(openAt >= 0);
        Assert.True(screenAt < openAt, $"AmpFxRig must be raised before IsPedalboardOpen; got [{string.Join(", ", raised)}]");

        // Closing and reopening keeps the instance — the dock/gear state is the
        // host's, and the binding is not re-read for nothing.
        raised.Clear();
        vm.ToneRig.IsPedalboardOpen = false;
        vm.ToneRig.IsPedalboardOpen = true;
        Assert.Same(screen, vm.ToneRig.AmpFxRig);
        Assert.DoesNotContain(nameof(PlayModeToneRigViewModel.AmpFxRig), raised);
    }

    [Fact]
    public void PlayModeHost_ThePrewarmAsk_BuildsTheScreenWithoutOpeningTheOverlay()
    {
        var vm = NewVm();
        vm.ToneRig.UseRigForTests(TestRigBuilders.Pedalboard(new FakeDspChainService(), new FakeDspTonePresetService(), FakeLocalization.English()),
            wantScreen: false);
        Assert.Null(vm.ToneRig.AmpFxRig);

        var raised = new List<string>();
        vm.ToneRig.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? "");

        vm.ToneRig.EnsureRigScreen();

        Assert.NotNull(vm.ToneRig.AmpFxRig);
        Assert.False(vm.ToneRig.IsPedalboardOpen, "the prewarm must not open the overlay");
        Assert.Contains(nameof(PlayModeToneRigViewModel.AmpFxRig), raised);
    }

    /// <summary>
    ///     Both hosts, both prewarm lists. The shell host's view model needs the
    ///     real container, so its half is a source lint: a <c>??=</c> over the
    ///     rig in either host is the boot-path shape this test exists to keep
    ///     out, and a prewarm list that stops asking prewarms the overlay over
    ///     null.
    /// </summary>
    [Fact]
    public void BothHosts_AreOnTheGate_AndBothPrewarmListsWantTheScreen()
    {
        var root = TestPaths.RepoRoot();
        string Read(string rel) => File.ReadAllText(Path.Combine(root, rel.Replace('\\', Path.DirectorySeparatorChar)));

        foreach (var host in new[]
                 {
                     @"DotCharter.UI\ViewModels\PlayMode\PlayModeToneRigViewModel.cs",
                     @"DotCharter.UI\ViewModels\Shell\ToneRigViewModel.cs"
                 })
        {
            var src = Read(host);
            Assert.DoesNotContain("??= Pedalboard is", src);
            Assert.Contains("_rigScreen.Get(", src);
            Assert.Contains("partial void OnIsPedalboardOpenChanged(bool value)", src);
            Assert.Contains("internal void EnsureRigScreen()", src);
        }

        Assert.Contains("(DataContext as PlayModeViewModel)?.ToneRig.EnsureRigScreen();",
            Read(@"DotCharter.UI\Views\PlayMode\PlayModeView.axaml.cs"));
        Assert.Contains("(_window.DataContext as MainWindowViewModel)?.ToneRig.EnsureRigScreen();",
            Read(@"DotCharter.UI\Views\Shell\MainWindowPrewarm.cs"));

        // …and the gated property IS what the two overlays bind, or the gate
        // guards nothing. Since #194 it is the CONTENT of the host whose template
        // builds the rig view (UI/StartupWindowDeferralTests pins that shape).
        Assert.Contains("Content=\"{Binding ToneRig.AmpFxRig}\"", MainWindowSource.ReadAllText());
        Assert.Contains("Content=\"{Binding ToneRig.AmpFxRig}\"", Read(@"DotCharter.UI\Views\PlayMode\PlayModeView.axaml"));
    }

    private static AmpFxRigViewModel NewScreen() =>
        new(TestRigBuilders.Pedalboard(new FakeDspChainService(), new FakeDspTonePresetService(), FakeLocalization.English()));

    private PlayModeViewModel NewVm()
    {
        var profiles = new PlayerProfileService(FakeLocalization.English(), logger: null,
            filePath: Path.Combine(_dir, "player-profiles.json"), defaultProfileName: "Tester");
        var store = new PracticeProfileService(profiles, logger: null,
            legacyFilePath: Path.Combine(_dir, "legacy.json"));
        return TestRigBuilders.PlayMode(profiles, new GameplayStatsService(store),
            new LeaderboardService(profiles), new ProjectState(), localization: FakeLocalization.English());
    }

    public void Dispose() => BestEffortDelete.Directory(_dir);
}
