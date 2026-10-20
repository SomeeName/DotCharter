// IPackageToneImporter: the seam a plugin fills to read the tones a song package carries; PackageTones is the process-wide holder.
using DotCharter.Tone.Parsing.Psarc;

namespace DotCharter.Services.Plugins;

/// <summary>
///     Reads the tone definitions a song package carries: the Tone Designer's "import tones"
///     and the Play Mode tone playback for a library song. Registered by a plugin that reads
///     packages (<see cref="IPackageFeatures.ReadsGamePackages" />); a build without one has no
///     importer, and both callers then see no package tones.
/// </summary>
public interface IPackageToneImporter
{
    /// <summary>The package's tone definitions; empty when it carries none. Throws on an unreadable package.</summary>
    IReadOnlyList<PsarcToneDefinition> ReadToneDefinitions(string packagePath);
}

/// <summary>
///     The process's <see cref="IPackageToneImporter" />, or none, as <see cref="GameInstall" /> holds
///     the game-install reader: the composition root hands it over once the plugins are registered
///     (<c>AppPlugins.Activate</c>), and the tone coordinator and the Tone Designer read it here.
/// </summary>
public static class PackageTones
{
    private static readonly AsyncLocal<IPackageToneImporter?> TestScope = new();
    private static volatile IPackageToneImporter? _importer;

    /// <summary>The importer in use: a test's scoped one, else the plugin's; null in a build without one.</summary>
    internal static IPackageToneImporter? Importer => TestScope.Value ?? _importer;

    /// <summary>Makes <paramref name="importer" /> the process's (null removes it).</summary>
    public static void Use(IPackageToneImporter? importer) => _importer = importer;

    /// <summary>Test-only: <paramref name="importer" /> for this async flow until disposed; other tests never see it.</summary>
    internal static IDisposable UseForTesting(IPackageToneImporter importer)
    {
        var previous = TestScope.Value;
        TestScope.Value = importer;
        return new Restore(() => TestScope.Value = previous);
    }

    /// <summary>The package's tone definitions; empty without an importer. Throws what the importer throws.</summary>
    public static IReadOnlyList<PsarcToneDefinition> Read(string packagePath) =>
        Importer?.ReadToneDefinitions(packagePath) ?? [];

    private sealed class Restore(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }
}
