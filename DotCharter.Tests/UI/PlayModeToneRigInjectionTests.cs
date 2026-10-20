using System.Reflection;
using System.Text.RegularExpressions;
using DotCharter.Services.Ai.Match;
using DotCharter.Services.Ai.Match.Model;
using DotCharter.Tone.Audio.Abstractions;
using DotCharter.Services.Audio.LivePlay;
using DotCharter.Services.Gameplay.Leaderboards;
using DotCharter.Services.Gameplay.Profiles;
using DotCharter.Services.Gameplay.Stats;
using DotCharter.Tone.Infrastructure.Storage;
using DotCharter.Tone.Online.Tone3000;
using DotCharter.Tone.UI.ViewModels.PlayMode;
using DotCharter.UI.ViewModels.PlayMode;
using DotCharter.Tone.UI.ViewModels.Settings;

namespace DotCharter.Tests.UI;

/// <summary>
///     Play Mode used to fetch fourteen services from the static <c>App.Services</c>
///     locator — twelve in its rig partial, one for the pedalboard and one for the
///     latency wizard — dependencies no constructor, no budget and no DI lint could
///     see. The rig cluster is <see cref="PlayModeToneRigViewModel" /> now, and
///     everything either type uses is injected, the startup-sensitive parts as
///     factories that run on first use.
/// </summary>
public sealed class PlayModeToneRigInjectionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "DotCharterTests", Guid.NewGuid().ToString("N"));

    /// <summary>The whole class (every <c>PlayModeViewModel*.cs</c> partial) and the
    /// extracted collaborators — the rig, the settings modal, Song Select's list and the results board and listen-back: a lookup
    /// through the locator is a hidden dependency.</summary>
    [Fact]
    public void PlayMode_NeverReachesForTheServiceLocator()
    {
        var folder = UiSourceTree.Resolve(Path.Combine(TestPaths.RepoRoot(), "DotCharter.UI", "ViewModels", "PlayMode"));
        var files = UiSourceTree.GetFiles(folder, "PlayModeViewModel*.cs")
            .Append(Path.Combine(folder, "PlayModeToneRigViewModel.cs"))
            .Append(Path.Combine(folder, "PlayModeSettingsViewModel.cs"))
            .Append(Path.Combine(folder, "SongSelectViewModel.cs"))
            .Append(Path.Combine(folder, "RunResultsViewModel.cs"))
            .Append(Path.Combine(folder, "RunListenBackViewModel.cs"))
            .Append(Path.Combine(folder, "IPlayModeSettingsHost.cs"))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToArray();

        Assert.True(files.Length > 10, $"only {files.Length} Play Mode source files found — this guard is blind");
        Assert.Contains(files, f => Path.GetFileName(f) == "PlayModeViewModel.cs");
        Assert.All(files, f => Assert.True(File.Exists(f), $"{Path.GetFileName(f)} moved — this guard is blind to it"));

        var offenders = files
            .Where(f => Regex.IsMatch(SourceText.StripComments(UiSourceTree.ReadAllText(f)), @"\bApp\s*\.\s*Services\b"))
            .Select(Path.GetFileName)
            .ToList();

        Assert.True(offenders.Count == 0,
            "Play Mode resolves a service through App.Services in " + string.Join(", ", offenders) +
            ". Take it through the constructor — as a Func<T> factory when it must not be built on the startup path.");
    }

    /// <summary>
    ///     The laziness the locator used to give for free, kept structurally: Play
    ///     Mode is built with the shell, so the tone rig, the per-screen rig parts
    ///     and the AI lane arrive as factories, never as instances DI would build
    ///     with it.
    /// </summary>
    [Theory]
    [InlineData(typeof(PedalboardViewModel))]
    [InlineData(typeof(INamCaptureLibrary))]
    [InlineData(typeof(LiveInputChannelViewModel))]
    [InlineData(typeof(RigReampViewModel))]
    [InlineData(typeof(IToneMatcher))]
    [InlineData(typeof(IStemIsolator))]
    [InlineData(typeof(IRigEstimator))]
    [InlineData(typeof(IRigEstimatorModelDownloadService))]
    [InlineData(typeof(IToneSectionProposer))]
    [InlineData(typeof(IToneSetMatcher))]
    [InlineData(typeof(IToneNarrator))]
    public void TheRig_TakesTheToneRigServicesAsFactories(Type service) => AssertTakenAsFactory(typeof(PlayModeToneRigViewModel), service);

    /// <summary>The guest session's latency wizard: a dialog launcher Play Mode
    /// must not build on the startup path for a door most sessions never use.</summary>
    [Fact]
    public void PlayMode_TakesTheLatencyWizardAsAFactory() => AssertTakenAsFactory(typeof(PlayModeViewModel), typeof(ILatencyCalibrationPrompt));

    /// <summary>The rig is one injected collaborator, not its fourteen parts spread
    /// over Play Mode's constructor.</summary>
    [Fact]
    public void PlayMode_TakesTheRigAsOneCollaborator()
    {
        var types = ConstructorParameterTypes(typeof(PlayModeViewModel));

        Assert.Contains(typeof(PlayModeToneRigViewModel), types);
        Assert.DoesNotContain(typeof(Func<PedalboardViewModel>), types);
        Assert.DoesNotContain(typeof(PedalboardViewModel), types);
    }

    /// <summary>The settings modal (#658) is one injected collaborator too, and the
    /// four services only it uses left Play Mode's constructor with it.</summary>
    [Fact]
    public void PlayMode_TakesTheSettingsModalAsOneCollaborator()
    {
        var types = ConstructorParameterTypes(typeof(PlayModeViewModel));

        Assert.Contains(typeof(PlayModeSettingsViewModel), types);
        Assert.DoesNotContain(typeof(DotCharter.Presentation.Screens.Settings.AudioSettingsViewModel), types);
        Assert.DoesNotContain(typeof(DotCharter.UI.Theming.IThemeService), types);
        Assert.DoesNotContain(typeof(DotCharter.Rendering.Vulkan.IVulkanRenderService), types);
        Assert.DoesNotContain(typeof(DotCharter.UI.Input.IKeybindRegistry), types);
    }

    /// <summary>The factories run when the rig is USED, never when the rig — or
    /// Play Mode over it — is built.</summary>
    [Fact]
    public void BuildingPlayMode_RunsNoRigFactory_OpeningTheRigDoes()
    {
        var calls = new List<string>();
        Func<T?> Counted<T>(string name, Func<T?> inner)
            where T : class
        {
            return () =>
            {
                calls.Add(name);
                return inner();
            };
        }

        var chain = TestRigBuilders.Pedalboard(new FakeDspChainService(), new FakeDspTonePresetService(), FakeLocalization.English());
        var rig = new PlayModeToneRigViewModel(
            new ProjectState(), null, null, null, null,
            Counted("pedalboard", () => chain),
            Counted<INamCaptureLibrary>("namCaptures", static () => null),
            Counted<LiveInputChannelViewModel>("inputChannel", static () => null),
            Counted<RigReampViewModel>("reamp", static () => null),
            Counted<IToneMatcher>("toneMatcher", static () => null),
            Counted<IStemIsolator>("stemIsolator", static () => null),
            Counted<IRigEstimator>("rigEstimator", static () => null),
            Counted<IRigEstimatorModelDownloadService>("modelDownloads", static () => null),
            Counted<IToneSectionProposer>("sectionProposer", static () => null),
            Counted<IToneSetMatcher>("toneSetMatcher", static () => null),
            Counted<IToneNarrator>("toneNarrator", static () => null));

        var vm = NewVm(rig);

        Assert.Same(rig, vm.ToneRig);
        Assert.Null(vm.ToneRig.AmpFxRig);
        Assert.Empty(calls);

        vm.ToneRig.OpenPedalboard();

        Assert.True(vm.ToneRig.IsPedalboardOpen);
        Assert.NotNull(vm.ToneRig.AmpFxRig);
        Assert.Same(chain, vm.ToneRig.AmpFxRig!.Rig);
        Assert.Contains("pedalboard", calls);
        Assert.Contains("namCaptures", calls);
        Assert.Contains("inputChannel", calls);
        Assert.Contains("reamp", calls);
    }

    /// <summary>Play Mode's Esc walk-up and its overlay state agree with the rig it
    /// was given: closing through <c>TryCloseOverlay</c> closes the injected rig.</summary>
    [Fact]
    public void TryCloseOverlay_ClosesTheInjectedRig()
    {
        var vm = NewVm(null);
        vm.ToneRig.UseRigForTests(TestRigBuilders.Pedalboard(new FakeDspChainService(), new FakeDspTonePresetService(), FakeLocalization.English()));
        vm.ToneRig.IsPedalboardOpen = true;

        Assert.True(vm.TryCloseOverlay());
        Assert.False(vm.ToneRig.IsPedalboardOpen);
    }

    public void Dispose() => BestEffortDelete.Directory(_dir);

    private static void AssertTakenAsFactory(Type owner, Type service)
    {
        var types = ConstructorParameterTypes(owner);

        Assert.Contains(typeof(Func<>).MakeGenericType(service), types);
        Assert.DoesNotContain(service, types);
    }

    private static List<Type> ConstructorParameterTypes(Type owner)
    {
        var constructor = Assert.Single(owner.GetConstructors(BindingFlags.Instance | BindingFlags.Public));
        return constructor.GetParameters().Select(p => p.ParameterType).ToList();
    }

    private PlayModeViewModel NewVm(PlayModeToneRigViewModel? rig)
    {
        var profiles = new PlayerProfileService(FakeLocalization.English(), logger: null,
            filePath: Path.Combine(_dir, "player-profiles.json"), defaultProfileName: "Tester");
        var store = new PracticeProfileService(profiles, logger: null,
            legacyFilePath: Path.Combine(_dir, "legacy.json"));
        return TestRigBuilders.PlayMode(profiles, new GameplayStatsService(store),
            new LeaderboardService(profiles), new ProjectState(), toneRig: rig, localization: FakeLocalization.English());
    }
}
