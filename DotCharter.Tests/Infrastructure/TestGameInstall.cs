using System.Runtime.CompilerServices;
using DotCharter.Services.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace DotCharter.Tests.Infrastructure;

/// <summary>
///     The optional plugin that reads the game install (#888), for the owner-only tests: found as a declared plugin,
///     the way the app finds it, so a build without it (<c>Invoke-Gate.ps1 -Public</c>, a public
///     clone) simply has no reader and those tests skip. The test process never lets the app's own
///     background load read the developer's install behind the tests' back
///     (<see cref="GameInstall.BackgroundLoadDisabled" />); the reader is installed process-wide so
///     the companion-archive search finds the Steam install as it always did.
/// </summary>
internal static class TestGameInstall
{
    private static readonly Lazy<IGameInstallReader?> Found = new(Find);

    /// <summary>The plugin's reader, or null when this build has no plugin.</summary>
    public static IGameInstallReader? Reader => Found.Value;

    /// <summary>Its table-building half, or null.</summary>
    public static IGameTableBuilder? TableBuilder => Reader as IGameTableBuilder;

    /// <summary>The game install directories the plugin finds; empty without it.</summary>
    public static IReadOnlyList<string> Directories => Reader?.GameDirectories() ?? [];

    [ModuleInitializer]
    internal static void Install()
    {
        GameInstall.BackgroundLoadDisabled = true;
        GameInstall.Use(Reader);
    }

    private static IGameInstallReader? Find()
    {
        var services = new ServiceCollection();
        PluginLoader.Load(services, PluginLoader.Declared(typeof(TestGameInstall).Assembly));
        using var provider = services.BuildServiceProvider();
        return provider.GetService<IGameInstallReader>();
    }
}
