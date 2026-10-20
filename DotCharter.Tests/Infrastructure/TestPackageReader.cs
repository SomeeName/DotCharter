using DotCharter.Services.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     The package reader's public seams as the app gets them (#889): the optional plugin,
///     found as a declared plugin, the way the app finds it (<see cref="PluginLoader" />), so this project names
///     none of its types. Null when this build has no plugin — the owner-only scans that read
///     a real library then read nothing.
/// </summary>
internal static class TestPackageReader
{
    private static readonly Lazy<IPackageToneImporter?> Importer = new(() =>
    {
        var services = new ServiceCollection();
        PluginLoader.Load(services, PluginLoader.Declared(typeof(TestPackageReader).Assembly));
        // The importer needs nothing else from the container; the provider lives for the process.
        return services.BuildServiceProvider().GetService<IPackageToneImporter>();
    });

    /// <summary>The plugin's tone importer, or null without the plugin.</summary>
    public static IPackageToneImporter? ToneImporter => Importer.Value;
}
