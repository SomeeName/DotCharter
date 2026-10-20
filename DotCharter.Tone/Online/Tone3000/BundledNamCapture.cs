using System.Text;
using System.Text.Json;

namespace DotCharter.Tone.Online.Tone3000;

/// <summary>
///     One freely-licensed NAM/AIDA-X capture that ships WITH the app and is
///     installed into the local <see cref="INamCaptureLibrary"/> on first run
///     (see <see cref="BundledNamCaptureInstaller"/>), so a fresh install has a
///     starter capture set in the pedalboard's "NAM Captures → Library" view
///     with no TONE3000 sign-in. Deserialized from the bundle's
///     <c>manifest.json</c>. <see cref="License"/> / <see cref="AttributionUrl"/>
///     are load-bearing: the attribution policy requires every bundled file to
///     keep its provenance (mirrored in the repo's <c>ATTRIBUTION.md</c>, gated
///     by a completeness test).
/// </summary>
public sealed record BundledNamCapture(
    string FileName,
    string Title,
    string Author,
    string GearType,
    string Format,
    string SizeTier,
    string License,
    string LicenseUrl,
    string AttributionUrl)
{
    /// <summary>A stable NEGATIVE synthetic id derived from the bundled file name
    /// (FNV-1a folded into the negative long range), so bundled captures never
    /// collide with TONE3000's positive tone/model ids while still supporting
    /// <see cref="INamCaptureLibrary.IsModelDownloaded"/> / delete by id.</summary>
    public static long SyntheticId(string fileName)
    {
        var name = fileName ?? string.Empty;
        var hash = 1469598103934665603UL; // FNV-1a offset basis
        foreach (var b in Encoding.UTF8.GetBytes(name.ToLowerInvariant()))
        {
            hash ^= b;
            hash *= 1099511628211UL; // FNV prime
        }

        // Fold into a negative long so it can never alias a positive server id.
        var v = (long)(hash & 0x7FFFFFFFFFFFFFFFUL);
        return v == 0 ? -1 : -v;
    }
}

/// <summary>
///     Installs the app's bundled NAM captures into the local
///     <see cref="INamCaptureLibrary"/>. Reads a <c>manifest.json</c> array of
///     <see cref="BundledNamCapture"/> from the bundle directory (shipped as
///     content next to the exe) and imports each sibling capture file that isn't
///     already present. Pure coordinator over the library seam; throw-free and
///     idempotent — safe to call on every startup.
/// </summary>
public static class BundledNamCaptureInstaller
{
    public const string ManifestFileName = "manifest.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>Parses a bundle <c>manifest.json</c> body into its capture list.
    /// Never throws — a malformed / empty body yields an empty list. Pure, so the
    /// manifest shape can be unit-tested without touching the filesystem.</summary>
    public static IReadOnlyList<BundledNamCapture> ParseManifest(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            var list = JsonSerializer.Deserialize<List<BundledNamCapture>>(json, Json);
            if (list is null) return [];
            return list
                .Where(c => c is not null && !string.IsNullOrWhiteSpace(c.FileName))
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    ///     Installs every capture in <paramref name="bundleDirectory"/>'s
    ///     <c>manifest.json</c> into <paramref name="library"/>. Returns the count
    ///     newly imported (0 when the directory / manifest is absent or everything
    ///     is already installed). Throw-free.
    /// </summary>
    public static int InstallFromDirectory(
        INamCaptureLibrary library, string? bundleDirectory, IAppLogger? logger = null)
    {
        if (library is null || string.IsNullOrWhiteSpace(bundleDirectory)) return 0;

        try
        {
            if (!Directory.Exists(bundleDirectory)) return 0;
            var manifestPath = Path.Combine(bundleDirectory, ManifestFileName);
            if (!File.Exists(manifestPath)) return 0;

            var captures = ParseManifest(File.ReadAllText(manifestPath));
            if (captures.Count == 0) return 0;

            // Snapshot what's already installed so the return value counts only
            // NEWLY imported captures (ImportBundled returns true on an idempotent
            // no-op too, so it can't distinguish new from present on its own).
            var alreadyPresent = new HashSet<string>(
                library.GetAll().Select(e => e.Manifest.FileName), StringComparer.OrdinalIgnoreCase);

            var installed = 0;
            foreach (var capture in captures)
            {
                var source = Path.Combine(bundleDirectory, capture.FileName);
                if (!File.Exists(source))
                {
                    logger?.Warning(AppLogCategory.Application,
                        $"Bundled NAM capture file listed but missing: '{capture.FileName}'.");
                    continue;
                }

                if (library.ImportBundled(source, capture) && alreadyPresent.Add(capture.FileName))
                    installed++;
            }

            if (installed > 0)
            {
                logger?.Info(AppLogCategory.Application,
                    $"Installed {installed} bundled NAM capture(s) into the library.");
            }

            return installed;
        }
        catch (Exception ex)
        {
            logger?.Warning(AppLogCategory.Application,
                $"Bundled NAM capture install failed: {ex.GetType().Name}: {ex.Message}");
            return 0;
        }
    }
}
