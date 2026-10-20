using System.Reflection;

namespace DotCharter.RigLab;

/// <summary>
///     Values baked in when RigLab is PUBLISHED, not kept in the repository.
///     <c>Publish-RigLab.ps1 -Tone3000ClientId …</c> passes the MSBuild property
///     of the same name, which the csproj turns into an
///     <see cref="AssemblyMetadataAttribute" />; a plain build has none.
/// </summary>
public static class RigLabBuild
{
    /// <summary>The TONE3000 OAuth client id a fresh <c>riglab.json</c> starts
    /// with — a PUBLISHABLE key (<c>t3k_pub_…</c>), which is meant to ship in a
    /// client; never the secret one. Empty when the build carries none.</summary>
    public static string Tone3000ClientId { get; } = Metadata("Tone3000ClientId");

    private static string Metadata(string key) =>
        typeof(RigLabBuild).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == key)?.Value?.Trim() ?? "";
}
