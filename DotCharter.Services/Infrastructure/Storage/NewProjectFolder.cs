namespace DotCharter.Services.Infrastructure.Storage;

/// <summary>
///     Picks the folder a newly created or imported project is written into —
///     without ever writing it into somebody else's.
/// </summary>
/// <remarks>
///     Every create-project flow derived a folder name from artist + title and
///     then called <c>Directory.CreateDirectory</c>, which is a NO-OP when the
///     folder already exists. The audio copy and the <c>SaveChart</c> that
///     followed then overwrote whatever was in there — so re-importing a song
///     you had already imported, under the same base folder, silently destroyed
///     the earlier import's <c>project.rscp</c> and its working audio. The name
///     is derived, not typed, so hitting the collision takes no mistake at all:
///     the same song produces the same name every time.
///     <para>
///         An EXISTING BUT EMPTY folder is deliberately not a collision. Users
///         pre-create the destination in the folder picker, and disambiguating
///         that into "… (2)" would be a surprise with no data behind it.
///     </para>
/// </remarks>
public static class NewProjectFolder
{
    /// <summary>How far the numeric disambiguation runs before falling back.</summary>
    private const int MaxNumberedAttempts = 99;

    /// <summary>
    ///     Creates — and returns the full path of — a folder under
    ///     <paramref name="baseDir" /> that is free to write a project into.
    /// </summary>
    public static string Create(string baseDir, string folderName)
    {
        var resolved = ResolveName(folderName,
            candidate => IsOccupied(Path.Combine(baseDir, candidate)));

        var projectDir = Path.Combine(baseDir, resolved);
        Directory.CreateDirectory(projectDir);
        return projectDir;
    }

    /// <summary>
    ///     The rule, pure: <paramref name="folderName" /> when it is free, else
    ///     the first free "<c>name (N)</c>" starting at 2. Kept separate from the
    ///     filesystem so the numbering is testable without one.
    /// </summary>
    public static string ResolveName(string folderName, Func<string, bool> isOccupied)
    {
        if (!isOccupied(folderName)) return folderName;

        for (var n = 2; n <= MaxNumberedAttempts; n++)
        {
            var candidate = $"{folderName} ({n})";
            if (!isOccupied(candidate)) return candidate;
        }

        // Practically unreachable (it needs 99 imports of one song into one
        // folder), but the function must still return something FREE rather than
        // fall back to a name it was just told is taken.
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var candidate = $"{folderName} ({Guid.NewGuid().ToString("N")[..8]})";
            if (!isOccupied(candidate)) return candidate;
        }

        throw new IOException($"Could not find a free project folder name for '{folderName}'.");
    }

    /// <summary>
    ///     True when writing a project into <paramref name="path" /> would land on
    ///     top of something. An unreadable directory counts as occupied — the
    ///     whole point is to never overwrite, and "I could not look" is not
    ///     evidence that it is empty.
    /// </summary>
    private static bool IsOccupied(string path)
    {
        try
        {
            return Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any();
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }
}
