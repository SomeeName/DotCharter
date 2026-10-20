// LibraryOrigin: the neutral stock/custom tag on a library entry; ILibraryOriginClassifier is what a plugin fills, LibraryOrigins the process-wide holder.
namespace DotCharter.Services.Library;

/// <summary>Where a library song came from, as far as the browser's origin facet is concerned.</summary>
public enum LibraryOrigin
{
    /// <summary>Nothing settled it either way. Every entry of a build without a plugin.</summary>
    Unknown = 0,

    /// <summary>Authored and released by the original publisher.</summary>
    Stock = 1,

    /// <summary>Made by a charter.</summary>
    Custom = 2
}

/// <summary>
///     Tells a stock library entry from a custom one. The rule belongs to the source that knows
///     its own packages, so this build has none: <see cref="LibraryOrigins" /> answers
///     <see cref="LibraryOrigin.Unknown" /> until a plugin registers one.
/// </summary>
public interface ILibraryOriginClassifier
{
    /// <summary>Classifies <paramref name="song" /> from what the entry already carries (no I/O).</summary>
    LibraryOrigin Classify(LibrarySongEntry song);
}

/// <summary>
///     The process's <see cref="ILibraryOriginClassifier" />, or none, as <c>PackageTones</c> holds the
///     tone importer: the composition root hands it over once the plugins are registered
///     (<c>AppPlugins.Activate</c>) and <see cref="LibrarySongEntry.Origin" /> reads it here.
/// </summary>
public static class LibraryOrigins
{
    private static readonly AsyncLocal<ILibraryOriginClassifier?> TestScope = new();
    private static volatile ILibraryOriginClassifier? _classifier;

    /// <summary>Makes <paramref name="classifier" /> the process's (null removes it).</summary>
    public static void Use(ILibraryOriginClassifier? classifier) => _classifier = classifier;

    /// <summary>Test-only: <paramref name="classifier" /> for this async flow until disposed; other tests never see it.</summary>
    public static IDisposable UseForTesting(ILibraryOriginClassifier classifier)
    {
        var previous = TestScope.Value;
        TestScope.Value = classifier;
        return new Restore(() => TestScope.Value = previous);
    }

    /// <summary>The origin of <paramref name="song" />: the classifier's answer, else <see cref="LibraryOrigin.Unknown" />.</summary>
    public static LibraryOrigin Of(LibrarySongEntry song) =>
        (TestScope.Value ?? _classifier)?.Classify(song) ?? LibraryOrigin.Unknown;

    private sealed class Restore(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }
}
