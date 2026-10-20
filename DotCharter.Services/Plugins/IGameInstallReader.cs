// IGameInstallReader: the seam a plugin fills to let the app read the player's game install; GameInstall is the process-wide holder.
using DotCharter.Tone.Audio.Dsp.RsToneConversion;

namespace DotCharter.Services.Plugins;

/// <summary>
/// What a plugin that reads the player's game install provides: where the
///     install is, and the amp chains and knob curves built from it
///     (<see cref="RsGameTables.Publish" />). The public build has no implementation: the tables
///     stay empty and the tone converter maps generically. A plugin registers one together
///     with an <see cref="IPackageFeatures" /> that says <see cref="IPackageFeatures.ReadsGameInstall" />.
/// </summary>
public interface IGameInstallReader
{
    /// <summary>Existing game install directories that can be found without asking the user; empty when there are none.</summary>
    IReadOnlyList<string> GameDirectories();

    /// <summary>Loads or builds the game tables on a pool thread and publishes them; once per process. Never throws.</summary>
    Task StartInBackground(IAppLogger? logger);
}

/// <summary>The table-building half of an <see cref="IGameInstallReader" />, for the tests and tools that build tables from a given install or folder.</summary>
internal interface IGameTableBuilder
{
    /// <summary>The tables for <paramref name="installDir" />, from <paramref name="cachePath" /> when it still matches the install, else built (and the cache rewritten). Empty without an install or the keys.</summary>
    (RsGameTables Tables, string Outcome, string? Detail) LoadOrBuild(string? installDir, string cachePath);

    /// <summary>The tables <paramref name="source" /> yields (a folder of extracted game files, a test's synthetic install), and a line per group that failed.</summary>
    RsGameTables Build(IRsGameFileSource source, out IReadOnlyList<string> problems);
}

/// <summary>
///     The process's <see cref="IGameInstallReader" />, or none. The composition root hands it over
///     once the plugins are registered (<c>AppPlugins.Activate</c>); code that has no way to be
///     injected (the companion-archive search) reads it here.
/// </summary>
public static class GameInstall
{
    private static volatile IGameInstallReader? _reader;

    /// <summary>Test-only: the test process never loads the developer's own install behind the tests' back.</summary>
    public static bool BackgroundLoadDisabled { get; set; }

    /// <summary>The reader the plugin registered; null in a build without one.</summary>
    internal static IGameInstallReader? Reader => _reader;

    /// <summary>Makes <paramref name="reader" /> the process's (null removes it).</summary>
    public static void Use(IGameInstallReader? reader) => _reader = reader;

    /// <summary>The game install directories the reader finds; empty without a reader.</summary>
    internal static IReadOnlyList<string> Directories() => _reader?.GameDirectories() ?? [];

    /// <summary>Starts the background table load, unless a test turned it off.</summary>
    public static Task StartInBackground(IGameInstallReader reader, IAppLogger? logger) =>
        BackgroundLoadDisabled ? Task.CompletedTask : reader.StartInBackground(logger);
}
