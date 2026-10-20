using System.Reflection.PortableExecutable;

namespace DotCharter.Services.Diagnostics;

/// <summary>
///     Is a managed image ReadyToRun-precompiled? The file half of the UI's
///     <c>ReadyToRunProbe</c>, which asks it about the app's own assembly. Exact
///     rather than heuristic: a ReadyToRun image is an ordinary managed PE whose
///     COR header points at a native header (<c>ManagedNativeHeaderDirectory</c>);
///     IL-only assemblies leave that directory empty.
/// </summary>
public static class ReadyToRunImage
{
    /// <summary>The decision itself, isolated from any file I/O.</summary>
    /// <param name="managedNativeHeaderSize">
    ///     <c>CorHeader.ManagedNativeHeaderDirectory.Size</c> of the image.
    /// </param>
    public static bool IsPrecompiledHeader(int managedNativeHeaderSize) => managedNativeHeaderSize > 0;

    /// <summary>
    ///     Whether the assembly at <paramref name="assemblyPath" /> is a
    ///     ReadyToRun image. <c>null</c> when the question cannot be answered —
    ///     the file is missing, unreadable, or not a managed PE (a single-file
    ///     bundle, for instance). Never throws: this only ever annotates a log
    ///     line, and must not be able to affect startup.
    /// </summary>
    public static bool? IsPrecompiled(string? assemblyPath)
    {
        if (string.IsNullOrEmpty(assemblyPath) || !File.Exists(assemblyPath)) return null;

        try
        {
            using var stream = File.OpenRead(assemblyPath);
            using var pe = new PEReader(stream);
            var cor = pe.PEHeaders.CorHeader;
            return cor is null ? null : IsPrecompiledHeader(cor.ManagedNativeHeaderDirectory.Size);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
