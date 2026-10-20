using DotCharter.Tone.Infrastructure.Shell;

namespace DotCharter.Services.Infrastructure.Shell;

/// <summary>
///     Finds a document shipped next to the executable — <c>LICENSE</c> and <c>THIRD-PARTY-NOTICES.md</c> for
///     the About window, the user docs for the Help menu — so the UI can show it,
///     or hand it to <see cref="IShellLauncher" />, without probing the disk itself.
/// </summary>
public interface IBundledDocuments
{
    /// <summary>
    ///     The path of <paramref name="fileName" /> beside the executable, or, in a
    ///     development run, in the nearest of the six parent folders that has it
    ///     (<c>bin/Debug/net10.0</c> up to the repository root). When no folder has
    ///     it, the path beside the executable is returned anyway.
    /// </summary>
    string Resolve(string fileName);

    /// <summary>
    ///     The text of <paramref name="fileName" /> as <see cref="Resolve" /> finds it,
    ///     or empty when it is missing or unreadable — so the UI can SHOW a document
    ///     without depending on the system having an app for its extension.
    /// </summary>
    string ReadText(string fileName);
}

/// <inheritdoc />
public sealed class BundledDocuments : IBundledDocuments
{
    private readonly string _baseDirectory;

    public BundledDocuments() : this(AppContext.BaseDirectory)
    {
    }

    /// <summary>Test seam: resolve from <paramref name="baseDirectory" />.</summary>
    internal BundledDocuments(string baseDirectory) => _baseDirectory = baseDirectory;

    public string Resolve(string fileName)
    {
        var path = Path.Combine(_baseDirectory, fileName);
        if (File.Exists(path)) return path;

        var probe = _baseDirectory;
        for (var i = 0; i < 6 && probe is not null; i++)
        {
            var candidate = Path.Combine(probe, fileName);
            if (File.Exists(candidate)) return candidate;
            probe = Path.GetDirectoryName(probe);
        }

        return path;
    }

    public string ReadText(string fileName)
    {
        try
        {
            var path = Resolve(fileName);
            return File.Exists(path) ? File.ReadAllText(path) : "";
        }
        catch (IOException)
        {
            return "";
        }
        catch (UnauthorizedAccessException)
        {
            return "";
        }
    }
}
