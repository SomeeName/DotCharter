namespace DotCharter.Services.Infrastructure.Storage;

/// <summary>
///     The project-facing facade over the shared <see cref="FileReferencePolicy" />.
///     Strict resolution confines references to a folder; the media policy also
///     permits local absolute and parent-relative audio that the app writes.
///     Callers validate resolved locations before probing them.
/// </summary>
public static class ProjectRelativePath
{
    public static string? TryResolve(string? baseDirectory, string? declared) =>
        FileReferencePolicy.TryResolve(baseDirectory, declared);

    public static bool IsSafeRelative(string? declared) => FileReferencePolicy.IsSafeRelative(declared);

    public static bool IsReadableMediaReference(string? declared) => FileReferencePolicy.IsReadableMediaReference(declared);

    public static bool IsLocalAbsolute(string? declared) => FileReferencePolicy.IsLocalAbsolute(declared);

    public static bool IsUnder(string root, string path) => FileReferencePolicy.IsUnder(root, path);
}
