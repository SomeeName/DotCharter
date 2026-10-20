using DotCharter.Localization;
using DotCharter.Presentation.Contracts.Settings;
using DotCharter.Presentation.Shared;
using DotCharter.Tone.Ai;
using DotCharter.Services.Ai.Match;
using DotCharter.Services.Ai.Match.Model;
using DotCharter.Services.Audio.StemSeparation;
using DotCharter.Services.Audio.StemSeparation.Cache;
using DotCharter.Services.Audio.StemSeparation.Gpu;
using DotCharter.Services.Infrastructure.Configuration;
using DotCharter.Services.Infrastructure.Shell;
using DotCharter.Tone.Diagnostics;
using DotCharter.Tone.Infrastructure.Shell;
using DotCharter.Tone.Logging;
using DotCharter.Services.Library.Cache;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Presentation.Screens.Settings;

/// <summary>The Settings area: the shared session, the shell, and one <see cref="RegisterTab{TContract,TViewModel}" /> per migrated tab.</summary>
public static class SettingsModule
{
    public static void Register(IServiceCollection services)
    {
        services.AddSingleton<SettingsSession>();
        services.AddScoped<ISettingsShellScreen, SettingsShellViewModel>();
        services.AddScoped<GitHubSignInViewModel>();
        RegisterTab<IGeneralSettingsScreen, GeneralSettingsViewModel>(services);
        RegisterTab<IEditorSettingsScreen, EditorSettingsViewModel>(services);
        RegisterTab<IPlaySettingsScreen, PlaySettingsViewModel>(services);
        RegisterTab<IAppearanceSettingsScreen, AppearanceSettingsViewModel>(services);
        RegisterTab<IGraphicsSettingsScreen, GraphicsSettingsViewModel>(services);
        services.AddScoped<ImportedConfigStores>();
        services.AddScoped<BackupMigrateViewModel>();
        RegisterTab<IFilesSettingsScreen, FilesSettingsViewModel>(services, sp => new FilesSettingsViewModel(
            sp.GetRequiredService<IConfigurationService>(),
            sp.GetRequiredService<ILocalizationService>(),
            sp.GetRequiredService<IUiDispatcher>(),
            sp.GetRequiredService<BackupMigrateViewModel>(),
            sp.GetRequiredService<DotCharter.Services.Plugins.IPackageFeatures>(),
            sp.GetService<LibraryAudioCacheStore>(),
            sp.GetService<IAppLogger>()));

        // ONE app-wide instance: Play Mode's settings modal and the first-run
        // wizard drive the same rows, so every Settings scope forwards to it.
        services.AddSingleton(sp => new AudioSettingsWindows(
            sp.GetRequiredService<DotCharter.Tone.Audio.Abstractions.ILatencyCalibrationPrompt>(),
            sp.GetService<IMainWindowHandle>()));
        services.AddSingleton<AudioSettingsViewModel>();
        RegisterSharedTab<IAudioSettingsScreen, AudioSettingsViewModel>(services);
        RegisterTab<IKeybindsSettingsScreen, KeybindsSettingsViewModel>(services);
        RegisterTab<IUpdatesSettingsScreen, UpdatesSettingsViewModel>(services);

        // The AI tab's services are asked for with GetService, not
        // GetRequiredService: the provider slice registers them, and a build
        // without that slice must still open Settings — the tab then reports
        // itself unavailable rather than taking the page down with a resolution
        // failure. The estimator is a SUPPLIER, because asking it IsAvailable
        // builds an ONNX session and nothing composing a page may do that.
        services.AddScoped(sp => new MatchModelViewModel(
            sp.GetRequiredService<ILocalizationService>(),
            sp.GetService<IRigEstimatorModelDownloadService>(),
            () => sp.GetService<IRigEstimator>(),
            sp.GetService<IShellLauncher>(),
            sp.GetService<IAppLogger>(),
            sp.GetService<DotCharter.Tone.Diagnostics.IUiDispatcher>()));
        RegisterTab<ITone3000SettingsScreen, Tone3000SettingsViewModel>(services);

        // Stem models: three blocks with their own machine-state probes, each a
        // part of its own. Every service they take is optional for the same
        // reason the AI tab's are — a build without the slice must still open
        // Settings — so they are constructed here rather than guessed at.
        services.AddScoped(sp => new StemBackendViewModel(
            sp.GetRequiredService<StemModelManager>(),
            sp.GetRequiredService<IConfigurationService>(),
            sp.GetRequiredService<ILocalizationService>(),
            sp.GetService<IStemSeparationService>(),
            sp.GetService<IAppLogger>(),
            sp.GetService<IStemGpuEnvironment>()));
        services.AddScoped(sp => new GpuRuntimeViewModel(
            sp.GetRequiredService<ILocalizationService>(),
            sp.GetRequiredService<IUiDispatcher>(),
            sp.GetService<LegacyGpuRuntimeFolder>(),
            sp.GetService<IAppLogger>()));
        services.AddScoped(sp => new StemCacheViewModel(
            sp.GetRequiredService<ILocalizationService>(),
            sp.GetRequiredService<IUiDispatcher>(),
            sp.GetService<StemCacheStore>(),
            sp.GetService<IAppLogger>()));
        RegisterTab<IStemModelsSettingsScreen, StemModelsSettingsViewModel>(services, sp =>
            new StemModelsSettingsViewModel(
                sp.GetRequiredService<StemModelManager>(),
                sp.GetRequiredService<ILocalizationService>(),
                sp.GetRequiredService<IUiDispatcher>(),
                sp.GetRequiredService<StemBackendViewModel>(),
                sp.GetRequiredService<GpuRuntimeViewModel>(),
                sp.GetRequiredService<StemCacheViewModel>(),
                sp.GetService<IAppLogger>()));
        RegisterTab<IAiSettingsScreen, AiSettingsViewModel>(services, sp => new AiSettingsViewModel(
            sp.GetRequiredService<IConfigurationService>(),
            sp.GetRequiredService<ILocalizationService>(),
            sp.GetRequiredService<MatchModelViewModel>(),
            sp.GetService<IAiSecretStore>(),
            sp.GetService<IToneDesignModelFactory>(),
            sp.GetService<IToneDesignService>(),
            sp.GetService<IAppLogger>()));
    }

    /// <summary>A tab is one instance per host scope, reachable as its contract, as a tab and as a change source.</summary>
    /// <param name="services">The container being built.</param>
    /// <param name="create">Only for a view model with optional services the container cannot guess.</param>
    public static void RegisterTab<TContract, TViewModel>(
        IServiceCollection services, Func<IServiceProvider, TViewModel>? create = null)
        where TContract : class
        where TViewModel : class, TContract, ISettingsTab, ISettingsChangeSource
    {
        if (create is null) services.AddScoped<TContract, TViewModel>();
        else services.AddScoped<TContract>(create);
        services.AddScoped<ISettingsTab>(sp => (ISettingsTab)sp.GetRequiredService<TContract>());
        services.AddScoped<ISettingsChangeSource>(sp => (ISettingsChangeSource)sp.GetRequiredService<TContract>());
    }

    /// <summary>A tab whose view model is an app-wide singleton: each scope's three registrations forward to it.</summary>
    public static void RegisterSharedTab<TContract, TViewModel>(IServiceCollection services)
        where TContract : class
        where TViewModel : class, TContract, ISettingsTab, ISettingsChangeSource
    {
        services.AddScoped<TContract>(sp => sp.GetRequiredService<TViewModel>());
        services.AddScoped<ISettingsTab>(sp => sp.GetRequiredService<TViewModel>());
        services.AddScoped<ISettingsChangeSource>(sp => sp.GetRequiredService<TViewModel>());
    }
}
