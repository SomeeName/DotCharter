namespace DotCharter.Services.Plugins;

/// <summary>
/// What this build can READ besides its own formats. Every reader entry point
///     (the package source of the library, the Tone Designer's package import, the
///     package drop route, the AI tone anchors, the game-table loader, the Files settings
///     rows that only concern packages) asks this instead of assuming. Off means ABSENT,
///     not greyed. The default registration is <see cref="PackageFeatures.None" />; a
///     plugin registers its own (merged in <c>App.Plugins.cs</c>). Writing packages (File ▸ Export Rocksmith, the companion package) never asks.
/// </summary>
public interface IPackageFeatures
{
    /// <summary>Game packages (<c>.psarc</c>) can be read: library, Play Mode, tone import.</summary>
    bool ReadsGamePackages { get; }

    /// <summary>The game install can be read (game tables).</summary>
    bool ReadsGameInstall { get; }
}

/// <summary>The value type behind <see cref="IPackageFeatures" />.</summary>
public sealed record PackageFeatures(bool ReadsGamePackages, bool ReadsGameInstall) : IPackageFeatures
{
    /// <summary>The default: nothing but the app's own formats.</summary>
    public static PackageFeatures None { get; } = new(false, false);

    /// <summary>Everything the package reader can do.</summary>
    public static PackageFeatures All { get; } = new(true, true);

    /// <summary>Feature id a menu row or palette entry is marked with when it reads game packages.</summary>
    public const string GamePackagesId = "game-packages";

    /// <summary>Feature id of what reads the game install.</summary>
    public const string GameInstallId = "game-install";

    /// <summary>Whether the feature <paramref name="id" /> is on; an id that is not a package feature is not this type's to answer (true).</summary>
    public static bool Allows(IPackageFeatures features, string id) => id switch
    {
        GamePackagesId => features.ReadsGamePackages,
        GameInstallId => features.ReadsGameInstall,
        _ => true
    };
}
