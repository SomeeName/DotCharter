using DotCharter.Localization;
using DotCharter.Presentation.Contracts.Settings;
using DotCharter.Presentation.Screens.Settings;
using DotCharter.Presentation.Shared;
using DotCharter.Services.Infrastructure.Input;
using DotCharter.Services.Infrastructure.Shell;
using DotCharter.Services.Update;
using DotCharter.Tone.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Presentation.Settings;

/// <summary>
///     <see cref="SettingsModule" /> registers each migrated tab THREE ways — its
///     screen contract, <see cref="ISettingsTab" /> and <see cref="ISettingsChangeSource" />
///     — and they must be the ONE instance per scope, or the tab the host shows
///     is not the tab the footer saves. Every registered tab has a rail row.
/// </summary>
public sealed class SettingsModuleTests
{
    private sealed class NullDispatch : IKeybindDispatch
    {
        public void RefreshBinds() { }

        public string? DescribeBinding(KeyChord chord) => null;
    }

    private static ServiceProvider Build()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfigurationService>(new FakeConfigurationService());
        services.AddSingleton(new ProjectState());
        services.AddSingleton<ILocalizationService>(LocalizationService.Instance);
        services.AddSingleton<IUiDispatcher>(new TestUiDispatcher());
        services.AddSingleton<IAppLogger>(new FakeAppLogger());
        services.AddSingleton<DotCharter.Presentation.Dialogs.IDialogs>(new DotCharter.Presentation.Dialogs.DialogsSlot());
        services.AddSingleton<IKeybindDispatch>(new NullDispatch());
        services.AddSingleton<IMidiTriggerInput>(new FakeMidiTriggerInput());
        services.AddSingleton<IAppUpdateService>(new StubUpdateService(UpdateCheckStatus.UpToDate));
        services.AddSingleton<IUpdatePrompt>(new StubUpdatePrompt());
        services.AddSingleton<IGitHubAuthService>(new StubGitHubAuth());
        services.AddSingleton<IApplicationCloser>(new StubApplicationCloser());
        services.AddSingleton<DotCharter.Tone.Online.Tone3000.ITone3000AuthService>(new StubTone3000Auth());
        services.AddSingleton(new DotCharter.Services.Audio.StemSeparation.StemModelManager(
            modelsDirectoryOverride: Path.Combine(Path.GetTempPath(), "DotCharterTests", Guid.NewGuid().ToString("N"))));
        services.AddSingleton<UpdateCheckFlow>();
        var engine = new FakeAudioPlaybackService();
        services.AddSingleton<DotCharter.Tone.Audio.Abstractions.IAudioDeviceControl>(engine);
        services.AddSingleton<DotCharter.Tone.Audio.Abstractions.ILiveInputCapture>(engine);
        services.AddSingleton<DotCharter.Services.Infrastructure.Graphics.IGraphicsDeviceSettings>(
            new GraphicsStubs.FakeDevice());
        services.AddSingleton<DotCharter.Tone.Audio.Abstractions.ILatencyCalibrationPrompt>(
            new GraphicsStubs.FakeWizard());
        services.AddSingleton(System.Reflection.DispatchProxy
            .Create<DotCharter.Services.Editing.IChartEditorSurface, GraphicsStubs.RedrawCounter>());
        services.AddSingleton<IFirstRunSetupPrompt>(new SplitTabStubs.FakeSetup());
        services.AddSingleton<DotCharter.Services.Infrastructure.Appearance.IAppearanceTheme>(new AppearanceStubs.FakeTheme());
        services.AddSingleton<DotCharter.Services.Infrastructure.Appearance.IInterfaceScale>(new AppearanceStubs.FakeScale());
        services.AddSingleton<DotCharter.Tone.Infrastructure.Storage.IFileStorageService>(new SplitTabStubs.FakeFiles());
        services.AddSingleton<DotCharter.Services.Infrastructure.Backup.IConfigBackupService>(new SplitTabStubs.FakeBackup());
        services.AddSingleton(SplitTabStubs.Recorder<DotCharter.Services.Gameplay.Profiles.IPlayerProfileService>());
        services.AddSingleton(SplitTabStubs.Recorder<DotCharter.Services.Audio.LivePlay.IPracticeProfileService>());
        services.AddSingleton(SplitTabStubs.Recorder<DotCharter.Services.Editing.Catalog.IPhraseCatalogService>());
        services.AddSingleton(SplitTabStubs.Recorder<DotCharter.Services.Session.IProjectSessionService>());
        services.AddSingleton<DotCharter.Services.Plugins.IPackageFeatures>(DotCharter.Services.Plugins.PackageFeatures.All);
        SettingsModule.Register(services);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    [Fact]
    public void EachMigratedTab_IsOneInstanceAcrossItsThreeRegistrations()
    {
        using var provider = Build();
        using var scope = provider.CreateScope();
        var sp = scope.ServiceProvider;

        var tabs = sp.GetServices<ISettingsTab>().ToList();
        var sources = sp.GetServices<ISettingsChangeSource>().ToList();

        Assert.Equal(
            [SettingsTabKeys.Ai, SettingsTabKeys.Appearance, SettingsTabKeys.Audio, SettingsTabKeys.Editor, SettingsTabKeys.Files, SettingsTabKeys.General,
                SettingsTabKeys.Graphics, SettingsTabKeys.Keybinds, SettingsTabKeys.Play, SettingsTabKeys.StemModels,
                SettingsTabKeys.Tone3000, SettingsTabKeys.Updates],
            tabs.Select(t => t.Key).Order(StringComparer.Ordinal));
        Assert.Same(sp.GetRequiredService<IGeneralSettingsScreen>(), tabs.Single(t => t.Key == SettingsTabKeys.General));
        Assert.Same(sp.GetRequiredService<IEditorSettingsScreen>(), tabs.Single(t => t.Key == SettingsTabKeys.Editor));
        Assert.Same(sp.GetRequiredService<IPlaySettingsScreen>(), tabs.Single(t => t.Key == SettingsTabKeys.Play));
        Assert.Same(sp.GetRequiredService<IAppearanceSettingsScreen>(), tabs.Single(t => t.Key == SettingsTabKeys.Appearance));
        Assert.Same(sp.GetRequiredService<IFilesSettingsScreen>(), tabs.Single(t => t.Key == SettingsTabKeys.Files));
        Assert.Same(sp.GetRequiredService<IGraphicsSettingsScreen>(), tabs.Single(t => t.Key == SettingsTabKeys.Graphics));
        Assert.Same(sp.GetRequiredService<IKeybindsSettingsScreen>(), tabs.Single(t => t.Key == SettingsTabKeys.Keybinds));
        Assert.Same(sp.GetRequiredService<IUpdatesSettingsScreen>(), tabs.Single(t => t.Key == SettingsTabKeys.Updates));
        Assert.Same(sp.GetRequiredService<IAiSettingsScreen>(), tabs.Single(t => t.Key == SettingsTabKeys.Ai));
        Assert.Same(sp.GetRequiredService<ITone3000SettingsScreen>(), tabs.Single(t => t.Key == SettingsTabKeys.Tone3000));
        Assert.Same(sp.GetRequiredService<IStemModelsSettingsScreen>(), tabs.Single(t => t.Key == SettingsTabKeys.StemModels));
        Assert.Equal(tabs.Cast<object>().OrderBy(t => t.GetHashCode()), sources.Cast<object>().OrderBy(s => s.GetHashCode()));
    }

    [Fact]
    public void TheShell_ResolvesOverTheRegisteredTabs_AndEveryTabHasARailRow()
    {
        using var provider = Build();
        using var scope = provider.CreateScope();

        var shell = (SettingsShellViewModel)scope.ServiceProvider.GetRequiredService<ISettingsShellScreen>();

        Assert.Equal(
            [typeof(IGeneralSettingsScreen), typeof(IEditorSettingsScreen), typeof(IPlaySettingsScreen),
                typeof(IAppearanceSettingsScreen), typeof(IGraphicsSettingsScreen), typeof(IAudioSettingsScreen), typeof(IFilesSettingsScreen),
                typeof(IKeybindsSettingsScreen), typeof(IStemModelsSettingsScreen), typeof(ITone3000SettingsScreen),
                typeof(IAiSettingsScreen), typeof(IUpdatesSettingsScreen)],
            shell.TabScreens);
        foreach (var tab in scope.ServiceProvider.GetServices<ISettingsTab>())
            Assert.NotNull(SettingsRail.Find(tab.Key));
        Assert.Same(provider.GetRequiredService<SettingsSession>(), scope.ServiceProvider.GetRequiredService<SettingsSession>());
    }

    [Fact]
    public void ASecondScope_GetsItsOwnTabs()
    {
        using var provider = Build();
        using var first = provider.CreateScope();
        using var second = provider.CreateScope();

        Assert.NotSame(first.ServiceProvider.GetRequiredService<IKeybindsSettingsScreen>(),
            second.ServiceProvider.GetRequiredService<IKeybindsSettingsScreen>());
    }

    /// <summary>
    ///     …except Audio, which is ONE instance app-wide: Play Mode's modal and
    ///     the first-run wizard drive the same rows, so a scope forwards to the
    ///     singleton rather than building a second copy that would disagree.
    /// </summary>
    [Fact]
    public void TheAudioTab_IsTheSameInstanceInEveryScope()
    {
        using var provider = Build();
        using var first = provider.CreateScope();
        using var second = provider.CreateScope();

        var shared = provider.GetRequiredService<AudioSettingsViewModel>();
        Assert.Same(shared, first.ServiceProvider.GetRequiredService<IAudioSettingsScreen>());
        Assert.Same(shared, second.ServiceProvider.GetRequiredService<IAudioSettingsScreen>());
        Assert.Contains(first.ServiceProvider.GetServices<ISettingsTab>(), t => ReferenceEquals(t, shared));
    }
}
