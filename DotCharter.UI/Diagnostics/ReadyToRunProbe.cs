using System.Reflection;
using DotCharter.Services.Diagnostics;

namespace DotCharter.UI.Diagnostics;

/// <summary>
///     Answers one question about the running build: is its code PRECOMPILED
///     (ReadyToRun), or is every method on the startup path JIT-compiled the
///     first time it runs?
/// </summary>
/// <remarks>
///     <para>
///         <b>Why this exists.</b> <c>DotCharter.UI.csproj</c> turns ReadyToRun on
///         with <c>Condition="'$(RuntimeIdentifier)' == 'win-x64'"</c>, which only
///         holds during <c>dotnet publish -r win-x64</c> — what the release script
///         and CI do. A plain <c>dotnet build -c Release</c>, i.e. the
///         <c>bin/Release/net10.0/DotCharter.exe</c> everyone actually launches
///         while developing, is NOT precompiled. Measured on the same machine,
///         the same shell and the same code, published-R2R against
///         built-Release: window ctor 549 → 222 ms, DataContext bind 774 → 298 ms,
///         first layout 358 → 208 ms. **Over half of a dev build's cold start is
///         JIT that a shipped build does not pay.**
///     </para>
///     <para>
///         A startup timing that does not say which of the two it came from is a
///         trap: it reads as a product problem and gets "optimized" by moving work
///         around. So the <c>StartupPhases:</c> line carries <c>r2r=</c> and the
///         Session Health dashboard shows it.
///     </para>
///     <para>
///         The test is exact rather than a heuristic: a ReadyToRun image is an
///         ordinary managed PE whose COR header points at a native header
///         (<c>ManagedNativeHeaderDirectory</c>). IL-only assemblies leave that
///         directory empty.
///     </para>
/// </remarks>
public static class ReadyToRunProbe
{
    /// <inheritdoc cref="ReadyToRunImage.IsPrecompiledHeader" />
    public static bool IsPrecompiledHeader(int managedNativeHeaderSize) =>
        ReadyToRunImage.IsPrecompiledHeader(managedNativeHeaderSize);

    /// <inheritdoc cref="ReadyToRunImage.IsPrecompiled" />
    public static bool? IsPrecompiled(string? assemblyPath) => ReadyToRunImage.IsPrecompiled(assemblyPath);

    /// <summary>The <c>r2r=</c> value for the app's own assembly: <c>yes</c>,
    /// <c>no</c>, or <c>?</c> when it could not be determined.</summary>
    public static string Describe() => Describe(typeof(ReadyToRunProbe).Assembly);

    internal static string Describe(Assembly assembly) =>
        IsPrecompiled(assembly.Location) switch
        {
            true => "yes",
            false => "no",
            null => "?"
        };
}
