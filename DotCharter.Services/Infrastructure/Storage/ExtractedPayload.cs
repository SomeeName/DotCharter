namespace DotCharter.Services.Infrastructure.Storage;

/// <summary>
///     Finds the files an archive was downloaded for once it is expanded. Release
///     archives either hold the files at the top or wrap them in one folder, so
///     the staging root is tried first and then each folder directly below it.
/// </summary>
public static class ExtractedPayload
{
    public static string? FindRoot(string staging, IReadOnlyCollection<string> requiredFiles)
    {
        if (HoldsAll(staging, requiredFiles))
        {
            return staging;
        }

        foreach (var directory in Directory.EnumerateDirectories(staging))
        {
            if (HoldsAll(directory, requiredFiles))
            {
                return directory;
            }
        }

        return null;
    }

    private static bool HoldsAll(string directory, IReadOnlyCollection<string> requiredFiles) => requiredFiles.All(file => File.Exists(Path.Combine(directory, file)));
}
