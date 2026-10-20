using System.Text.Json;
using System.Text.Json.Serialization;
using DotCharter.UI.ViewModels.Shell;

namespace DotCharter.Tests.UI.Onboarding;

/// <summary>
///     <c>AppConfig.StartupScreen</c> decides what the shell boots into. This
///     pins the pure router (Charter → editor, Player → Play Mode), the
///     back-compat alias the Play Mode menu still writes, that a pre-enum config
///     file keeps its answer, and — by source lint, because
///     <c>MainWindowViewModel</c> takes 26 services — that the shell's
///     constructor routes through <see cref="StartupRouting"/> rather than the
///     old boolean.
/// </summary>
public sealed class StartupScreenRoutingTests
{
    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    /// <summary>A Player sees the Start screen on every launch, one profile or many
    /// (owner, 2026-09-24); a Charter lands in the editor.</summary>
    [Theory]
    [InlineData(StartupScreen.Editor, true, 3, StartupSurface.Editor)]
    [InlineData(StartupScreen.PlayMode, false, 1, StartupSurface.Start)]
    [InlineData(StartupScreen.PlayMode, true, 2, StartupSurface.Start)]
    [InlineData(StartupScreen.PlayMode, true, 1, StartupSurface.Start)]
    public void TheStartScreen_ShowsOnEveryPlayerLaunch(
        StartupScreen screen, bool onboarded, int profiles, StartupSurface expected)
    {
        var config = new AppConfig { StartupScreen = screen, OnboardingCompleted = onboarded };
        Assert.Equal(expected, StartupRouting.SurfaceFor(config, profiles));
    }

    [Fact]
    public void DefaultIsPlayMode_TodaysBehaviour()
    {
        var config = new AppConfig();
        Assert.Equal(StartupScreen.PlayMode, config.StartupScreen);
        Assert.True(config.StartInPlayMode);
        Assert.True(StartupRouting.BootsIntoPlayMode(config));
    }

    [Theory]
    [InlineData(StartupScreen.Editor, false)]
    [InlineData(StartupScreen.PlayMode, true)]
    public void Router_FollowsStartupScreen(StartupScreen screen, bool playMode)
    {
        var config = new AppConfig { StartupScreen = screen };
        Assert.Equal(playMode, StartupRouting.BootsIntoPlayMode(config));
        Assert.Equal(playMode, config.StartInPlayMode);
    }

    [Theory]
    [InlineData(true, StartupScreen.Editor)]
    [InlineData(false, StartupScreen.PlayMode)]
    public void Role_MapsCharterToEditorAndPlayerToPlayMode(bool charter, StartupScreen expected) =>
        Assert.Equal(expected, StartupRouting.ScreenForRole(charter));

    [Fact]
    public void TheAlias_IsTheSameFieldInBothDirections()
    {
        var config = new AppConfig { StartInPlayMode = false };
        Assert.Equal(StartupScreen.Editor, config.StartupScreen);
        config.StartInPlayMode = true;
        Assert.Equal(StartupScreen.PlayMode, config.StartupScreen);
    }

    [Fact]
    public void APreEnumConfigFile_KeepsItsEditorFirstBoot()
    {
        // What appconfig.json held before StartupScreen existed.
        var config = JsonSerializer.Deserialize<AppConfig>("""{"StartInPlayMode":false}""", Json)!;
        Assert.Equal(StartupScreen.Editor, config.StartupScreen);
        Assert.False(StartupRouting.BootsIntoPlayMode(config));
    }

    [Fact]
    public void ASavedConfig_RoundTripsTheScreenAsAString()
    {
        var json = JsonSerializer.Serialize(new AppConfig { StartupScreen = StartupScreen.Editor }, Json);
        Assert.Contains("\"StartupScreen\":\"Editor\"", json);
        var back = JsonSerializer.Deserialize<AppConfig>(json, Json)!;
        Assert.Equal(StartupScreen.Editor, back.StartupScreen);
        Assert.False(back.StartInPlayMode);
    }

    /// <summary>
    ///     The shell must consult the router — not <c>StartInPlayMode</c> — and
    ///     only on the Play Mode answer select the Play Mode tab, which opens on
    ///     its Start screen (Play Mode's own first screen; nothing sets another). The editor is
    ///     tab 0, which needs no action; a stray <c>SelectedMainTabIndex = …</c>
    ///     for the editor case would land in <c>OnSelectedMainTabIndexChanged</c>
    ///     before the window exists.
    /// </summary>
    [Fact]
    public void TheShellConstructor_RoutesThroughStartupRouting()
    {
        var source = UiSourceTree.ReadAllText(UiSourceTree.Resolve(Path.Combine(
            TestPaths.RepoRoot(), "DotCharter.UI", "ViewModels", "Shell", "MainWindowViewModel.cs")));

        var route = source.IndexOf("switch (StartupRouting.SurfaceFor(AppConfig", StringComparison.Ordinal);
        Assert.True(route >= 0, "MainWindowViewModel's constructor must route startup through StartupRouting.SurfaceFor");

        var block = source[route..(route + 400)];
        Assert.DoesNotContain("PlayMode.CurrentScreen =", block);
        Assert.Contains("SelectedMainTabIndex = PlayModeTabIndex", block);
        Assert.Contains("SelectedMainTabIndex = HomeTabIndex", block);

        Assert.DoesNotContain("if (AppConfig.StartInPlayMode)", source);
    }
}
