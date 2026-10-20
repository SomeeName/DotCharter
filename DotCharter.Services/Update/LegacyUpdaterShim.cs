namespace DotCharter.Services.Update;

/// <summary>
/// The four files a SPLIT release also carries at its TOP, for one
///     reason: the updater inside every v0.62-and-older install.
///     <para>
///         <b>Why they exist.</b> That updater validates the payload it is about to
///         copy with the flat rule — <c>DotCharter.exe</c>,
///         <c>DotCharter.runtimeconfig.json</c> (self-contained),
///         <c>DotCharter.deps.json</c>, <c>hostfxr.dll</c> and <c>coreclr.dll</c>
///         at the payload root — and refuses anything else. A split payload's root
///         has the launcher only, so without these every existing install would
///         refuse the release that moves it into <c>app/</c>, forever. With them,
///         the old updater copies the whole split payload over its flat install in
///         one step and relaunches the top <c>DotCharter.exe</c>, which is now the
///         launcher.
///     </para>
///     <para>
///         They are byte copies of the app's own files in <c>app/</c>, so every
///         release check that reads them (the binary scan, the runtimeconfig trim
///         check) sees real files. Nothing ever LOADS them: the launcher is a
///         NativeAOT exe with no runtime. The current updater never copies them over
///         a split install (<see cref="RemoveFrom" />, <see cref="StripFrom" />),
///         and <see cref="SplitInstallCleanup" /> deletes them from an install the
///         old updater put them in — so they are visible only in a fresh unzip,
///         until its first start.
///     </para>
///     <para>
///         <b>When they can go:</b> once no pre-0.63 install is left to update.
///         Removing them earlier strands every such install on its version.
///     </para>
/// </summary>
public static class LegacyUpdaterShim
{
    /// <summary>The top-level names, besides the launcher, the old updater demands.</summary>
    public static readonly string[] Files =
    [
        AppPayload.RuntimeConfigName,
        AppPayload.DepsName,
        AppPayload.HostFxrName,
        AppPayload.CoreClrName,
    ];

    /// <summary>Whether a payload- or install-relative path is one of the shim files at the TOP.</summary>
    public static bool IsShimPath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return false;
        var p = relativePath.Replace('\\', '/').TrimStart('/');
        return !p.Contains('/') && Files.Contains(p, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     The old updater's own rule, applied to a file set: null when an install
    ///     still running the FLAT validator would accept this payload. The release
    ///     script runs it on every zip (<c>ValidateReleaseZip.cs</c>), because a
    ///     release that fails it updates no pre-0.63 install at all.
    /// </summary>
    public static string? CheckLegacyUpdaterAccepts(Func<string, bool> hasFile, string? rootRuntimeConfigJson)
    {
        ArgumentNullException.ThrowIfNull(hasFile);
        if (!hasFile(AppPayload.ExeName)) return $"it has no {AppPayload.ExeName} at its top";
        foreach (var name in Files)
        {
            if (!hasFile(name))
                return $"it has no {name} at its top, and a v0.62 updater refuses it without one";
        }

        return AppPayload.ClassifyRuntimeConfig(rootRuntimeConfigJson) == InstallLayout.SelfContained
            ? null
            : $"the {AppPayload.RuntimeConfigName} at its top is not self-contained, and a v0.62 updater refuses it";
    }

    /// <summary>A split release manifest without the shim entries, for the current updater's partial fetch.</summary>
    public static PayloadManifest StripFrom(PayloadManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (!manifest.IsSplit) return manifest;
        var kept = manifest.Files.Where(f => !IsShimPath(f.Path)).ToList();
        return kept.Count == manifest.Files.Count ? manifest : manifest with { Files = kept };
    }

    /// <summary>
    ///     Deletes the shim files from a STAGED split payload before it is copied,
    ///     and returns how many it removed. A flat payload is left alone: there the
    ///     same names are the app's own runtime.
    /// </summary>
    public static int RemoveFrom(string payloadDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadDirectory);
        if (!AppPayload.IsSplitDirectory(payloadDirectory)) return 0;

        var removed = 0;
        foreach (var name in Files)
        {
            var path = Path.Combine(payloadDirectory, name);
            if (!File.Exists(path)) continue;
            File.Delete(path);
            removed++;
        }

        return removed;
    }
}
