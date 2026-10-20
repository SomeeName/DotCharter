// Plugins: loading the plugins this build declares, the IPackageFeatures default and the startup notice for a refused plugin.
using DotCharter.Services.Plugins;
using DotCharter.Services.Library;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.UI;

/// <summary>The plugin half of the composition root; <c>App.ConfigureServices</c> calls it first.</summary>
internal static class AppPlugins
{
    /// <summary>Dev switch: <c>DOTCHARTER_PACKAGE_READER=off</c> starts the app without looking for
    /// plugins (every package-reader entry point absent), to see the public build locally.</summary>
    internal const string PackageReaderEnvironmentVariable = "DOTCHARTER_PACKAGE_READER";

    internal static bool PackageReaderFromEnvironment() =>
        !string.Equals(Environment.GetEnvironmentVariable(PackageReaderEnvironmentVariable), "off", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    ///     Registers the plugins, then the <see cref="IPackageFeatures" /> default (none).
    ///     Runs BEFORE the editing services so a package source keeps its place ahead of
    ///     <c>.dcsong</c> in the scan order.
    /// </summary>
    /// <param name="services">The app's collection.</param>
    /// <param name="pluginNames">The plugin assemblies to load (<see cref="PluginLoader.Declared" />).</param>
    /// <param name="resolveModule">Test seam: a plugin assembly's module type.</param>
    /// <remarks>
    ///     A module registers its <see cref="IPackageFeatures" /> as an INSTANCE; the instances of
    ///     every loaded module are merged into the one the app resolves
    ///     (a feature is on when any of them says so).
    /// </remarks>
    internal static PluginLoadReport AddPlugins(IServiceCollection services, IReadOnlyList<string> pluginNames,
        Func<string, Type?>? resolveModule = null)
    {
        var report = PluginLoader.Load(services, pluginNames, resolveModule);
        services.AddSingleton(report);
        MergePackageFeatures(services);
        return report;
    }

    private static void MergePackageFeatures(IServiceCollection services)
    {
        var registered = services.Where(d => d.ServiceType == typeof(IPackageFeatures)).ToList();
        var merged = PackageFeatures.None;
        foreach (var descriptor in registered)
        {
            if (descriptor.ImplementationInstance is IPackageFeatures features)
            {
                merged = new PackageFeatures(
                    merged.ReadsGamePackages || features.ReadsGamePackages,
                    merged.ReadsGameInstall || features.ReadsGameInstall);
            }

            services.Remove(descriptor);
        }

        services.AddSingleton<IPackageFeatures>(merged);
    }

    /// <summary>Hands the plugin's game-install reader, package tone importer and library origin classifier (if registered) to the code that reads them from a process holder; call once the provider is built. Never clears one a test or an earlier call installed.</summary>
    internal static void Activate(IServiceProvider services)
    {
        if (services.GetService<IGameInstallReader>() is { } reader) GameInstall.Use(reader);
        if (services.GetService<IPackageToneImporter>() is { } importer) PackageTones.Use(importer);
        if (services.GetService<ILibraryOriginClassifier>() is { } classifier) LibraryOrigins.Use(classifier);
    }

    /// <summary>Logs every candidate and WARNS about a refused one: the warning reaches the status bar, the app keeps running without it.</summary>
    internal static void ReportPlugins(IServiceProvider services, IAppLogger logger, ILocalizationService localization)
    {
        var report = services.GetService<PluginLoadReport>() ?? PluginLoadReport.Empty;
        foreach (var result in report.Results)
        {
            logger.Info(AppLogCategory.Application,
                $"Plugin '{result.Name}': {result.Status}, {result.Message}.");
        }

        var features = services.GetRequiredService<IPackageFeatures>();
        logger.Info(AppLogCategory.Application,
            $"Package features: gamePackages={features.ReadsGamePackages}, gameInstall={features.ReadsGameInstall}.");
        foreach (var problem in report.Problems)
        {
            var name = problem.Id ?? problem.Name;
            logger.Warning(AppLogCategory.Application, problem.Status == PluginLoadStatus.Outdated
                ? localization.Format("Plugin_Outdated", name, problem.ApiVersion ?? 0, PluginApiVersion.Current)
                : localization.Format("Plugin_Failed", name, problem.Message));
        }
    }
}
