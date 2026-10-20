using System.Security.Cryptography;

namespace DotCharter.Services.Update;

/// <summary>Where the picture of the local install came from.</summary>
public enum InstalledPayloadSource
{
    /// <summary>The <c>payload-manifest.json</c> the release itself wrote into the
    /// install, reconciled against what is still on disk.</summary>
    ShippedManifest,

    /// <summary>Hashed here and now, because the install carries no manifest
    /// (older releases shipped none).</summary>
    Hashed
}

/// <summary>
///     What this machine already has, expressed as a <see cref="PayloadManifest" />
///     so the ordinary <see cref="PayloadDelta" /> answers "what is missing".
/// </summary>
/// <param name="Manifest">The local inventory.</param>
/// <param name="Source">How it was obtained. Decides <see cref="CanDeriveDeletions" />.</param>
public sealed record InstalledPayload(PayloadManifest Manifest, InstalledPayloadSource Source)
{
    /// <summary>
    ///     Whether a diff against this may produce a DELETION list.
    ///     <para>
    ///         <b>Only a manifest the release wrote knows what the install is
    ///         supposed to contain.</b> The hashed fallback is deliberately built
    ///         from the RELEASE's own path list, so it can never report a file as
    ///         dropped — and it must not, because on a portable install
    ///         <c>WritablePaths.ConfigRoot</c> IS the install directory. An
    ///         inventory that walked the whole folder would see the user's
    ///         <c>appconfig.json</c>, profiles, keybinds and crash reports, find
    ///         none of them in the release, and hand the updater a
    ///         <c>payload-deletions.txt</c> that erases them. Same footgun,
    ///         same directory, as <c>PayloadReceiptFilter.IsLocalState</c>.
    ///     </para>
    /// </summary>
    public bool CanDeriveDeletions => Source == InstalledPayloadSource.ShippedManifest;
}

/// <summary>
///     Builds an <see cref="InstalledPayload" />. Pure with respect to its injected
///     probes, so the rules are unit-testable without a real install.
/// </summary>
public static class InstalledPayloadReader
{
    /// <summary>
    ///     Resolves what the install has.
    ///     <list type="number">
    ///         <item>
    ///             The shipped manifest, RECONCILED: any file it lists that is now
    ///             missing, a different size, or a different CONTENT is dropped, so
    ///             it gets re-fetched. The stat comes first and is what keeps this
    ///             cheap — a file of the wrong length cannot be the right content,
    ///             so only the survivors are hashed.
    ///         </item>
    ///         <item>
    ///             Otherwise hash — but only the paths the RELEASE lists, and only
    ///             those whose size already matches. A different size cannot be the
    ///             same content, so hashing it would be pure cost, and walking the
    ///             directory instead of the release's path list is what would drag
    ///             user data into the inventory (see
    ///             <see cref="InstalledPayload.CanDeriveDeletions" />).
    ///         </item>
    ///     </list>
    /// </summary>
    /// <param name="release">The release being fetched — the path list for the hashed fallback.</param>
    /// <param name="readShippedManifest">Contents of the install's own manifest, or null.</param>
    /// <param name="sizeOf">Length of an install-relative file, or null when absent.</param>
    /// <param name="sha256Of">Lower-case hex SHA-256 of an install-relative file, or null when unreadable.</param>
    public static InstalledPayload Resolve(
        PayloadManifest release,
        Func<string?> readShippedManifest,
        Func<string, long?> sizeOf,
        Func<string, string?> sha256Of)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(readShippedManifest);
        ArgumentNullException.ThrowIfNull(sizeOf);
        ArgumentNullException.ThrowIfNull(sha256Of);

        var shipped = PayloadManifest.FromJson(readShippedManifest());
        if (shipped != null)
        {
            return new InstalledPayload(
                Reconcile(shipped, sizeOf, sha256Of), InstalledPayloadSource.ShippedManifest);
        }

        var files = new List<PayloadFile>();
        foreach (var file in release.Files)
        {
            if (sizeOf(file.Path) != file.Size) continue;
            var hash = sha256Of(file.Path);
            if (hash == null) continue;
            files.Add(new PayloadFile(file.Path, hash, file.Size));
        }

        return new InstalledPayload(
            new PayloadManifest(string.Empty, release.Layout, files), InstalledPayloadSource.Hashed);
    }

    /// <summary>
    ///     Drops every entry whose file is gone, has changed size, or does not
    ///     hash to what the manifest recorded. Pure.
    ///     <para>
    ///         A manifest describes the install AS SHIPPED, and an install is a
    ///         directory a user can touch — a repaired DLL, a half-finished copy, a
    ///         file an AV tool quarantined. Trusting the manifest for those would
    ///         SKIP the very file that has to be re-fetched, and the verify step
    ///         cannot catch it because it only ever sees the files that WERE
    ///         fetched.
    ///     </para>
    ///     <para>
    ///         <b>Size alone is not enough, and the gap it leaves is PERMANENT.</b>
    ///         One <c>stat</c> per file closes only the size-CHANGING half of that gap.
    ///         A file modified in place at the same length — a targeted swap, an AV
    ///         "repair", bit rot — would be reported as already-present by this update,
    ///         by every later partial update, and forever: nothing in the app would ever
    ///         re-fetch it, and no UI could ask for it. The content check is the
    ///         other half, and it is not expensive: the stat runs FIRST and a
    ///         wrong-length file is dropped without being read, so only files that
    ///         still look right are hashed.
    ///     </para>
    ///     <para>
    ///         An entry whose hash is unreadable (a lock, a permission) is DROPPED
    ///         too — re-fetching a file we cannot read is the safe direction, and it
    ///         costs bandwidth rather than a broken install.
    ///     </para>
    /// </summary>
    public static PayloadManifest Reconcile(
        PayloadManifest manifest, Func<string, long?> sizeOf, Func<string, string?> sha256Of)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(sizeOf);
        ArgumentNullException.ThrowIfNull(sha256Of);

        var kept = new List<PayloadFile>(manifest.Files.Count);
        foreach (var file in manifest.Files)
        {
            // Cheap first: a different length cannot be the same content, and this
            // is what keeps the hashing bounded to files that still look intact.
            if (sizeOf(file.Path) != file.Size) continue;

            // A manifest entry with no recorded digest has nothing to compare
            // against; the size probe is all it ever had.
            if (string.IsNullOrEmpty(file.Sha256)) { kept.Add(file); continue; }

            if (!string.Equals(sha256Of(file.Path), file.Sha256, StringComparison.OrdinalIgnoreCase)) continue;

            kept.Add(file);
        }

        return kept.Count == manifest.Files.Count ? manifest : manifest with { Files = kept };
    }

    /// <summary>Filesystem-backed probes for a real install directory.</summary>
    public static InstalledPayload FromDirectory(PayloadManifest release, string installDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installDirectory);

        return Resolve(
            release,
            // A split install keeps its manifest in app/; the top-level
            // copy is a flat install's, or an old release's left for the cleanup.
            () => TryReadAllText(Path.Combine(installDirectory, PayloadManifest.SplitInstalledPath))
                  ?? TryReadAllText(Path.Combine(installDirectory, PayloadManifest.FileName)),
            path => ResolveFullPath(path) is { } full && File.Exists(full) ? new FileInfo(full).Length : null,
            path =>
            {
                var full = ResolveFullPath(path);
                if (full == null || !File.Exists(full)) return null;
                try
                {
                    using var stream = File.OpenRead(full);
                    return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return null;
                }
            });

        // Manifest paths are forward-slashed and relative BY CONTRACT; anything
        // else in there is not something we resolve against the install directory.
        string? ResolveFullPath(string relative)
        {
            if (ZipCentralDirectory.NormalizeEntryName(relative) == null) return null;
            var full = Path.GetFullPath(Path.Combine(installDirectory, relative));
            var root = Path.GetFullPath(installDirectory);
            var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
            return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? full : null;
        }
    }

    private static string? TryReadAllText(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
